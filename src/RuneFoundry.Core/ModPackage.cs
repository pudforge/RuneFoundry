using System.IO.Compression;
using System.Text;

namespace RuneFoundry.Core;

/// <summary>
/// A .w2mod file: an ordinary zip laid out as
///   mod.json           the manifest
///   preview.png        optional cover image
///   files/&lt;path&gt;       payload, mirroring the game's x86\Data tree
/// Renaming one to .zip and poking around by hand is a supported thing to do.
/// </summary>
public sealed class ModPackage : IDisposable
{
    public const string Extension = ".w2mod";
    public const string ManifestEntry = "mod.json";
    public const string PreviewEntry = "preview.png";
    public const string FilePrefix = "files/";

    /// <summary>A single payload file is capped so a malformed or hostile package cannot exhaust the disk.</summary>
    public const long MaxEntrySize = 2L * 1024 * 1024 * 1024;

    private readonly ZipArchive _archive;

    public ModManifest Manifest { get; }
    public string SourcePath { get; }

    private ModPackage(string sourcePath, ZipArchive archive, ModManifest manifest)
    {
        SourcePath = sourcePath;
        _archive = archive;
        Manifest = manifest;
    }

    public static ModPackage Open(string path)
    {
        var archive = ZipFile.OpenRead(path);
        try
        {
            var manifestEntry = archive.GetEntry(ManifestEntry)
                ?? throw new InvalidDataException("Not a valid mod package: mod.json is missing.");

            string json;
            using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
                json = reader.ReadToEnd();

            var manifest = ModManifest.FromJson(json);
            var problems = manifest.Validate();
            if (problems.Count > 0)
                throw new InvalidDataException("Mod package is invalid:\n  - " + string.Join("\n  - ", problems));

            return new ModPackage(path, archive, manifest);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    public bool HasPreview => _archive.GetEntry(PreviewEntry) is not null;

    public byte[]? ReadPreview()
    {
        var entry = _archive.GetEntry(PreviewEntry);
        if (entry is null) return null;
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Extracts one payload file to disk, verifying its hash as it goes. The write
    /// lands on a temp file first so a mid-copy failure cannot leave a half-written
    /// file where the game expects real content.
    /// </summary>
    public void ExtractFile(ModFileEntry file, string destinationPath)
    {
        var entryName = FilePrefix + PathSafety.Normalize(file.Path);
        var entry = _archive.GetEntry(entryName)
            ?? throw new InvalidDataException($"Package is missing payload for '{file.Path}'.");

        if (entry.Length > MaxEntrySize)
            throw new InvalidDataException($"Payload '{file.Path}' is implausibly large ({entry.Length} bytes).");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temp = destinationPath + ".w2mod-tmp";

        try
        {
            string actualHash;
            using (var source = entry.Open())
            using (var target = File.Create(temp))
            {
                source.CopyTo(target);
                target.Position = 0;
                actualHash = Hashing.Sha256Stream(target);
            }

            if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Payload '{file.Path}' failed its integrity check. The package is corrupt or was tampered with.");

            File.Move(temp, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort cleanup */ }
    }

    /// <summary>Checks every payload against its manifest hash without writing anything.</summary>
    public IReadOnlyList<string> VerifyPayload()
    {
        var problems = new List<string>();
        foreach (var file in Manifest.Files)
        {
            var entry = _archive.GetEntry(FilePrefix + PathSafety.Normalize(file.Path));
            if (entry is null) { problems.Add($"Missing payload for '{file.Path}'."); continue; }
            if (entry.Length != file.Size) { problems.Add($"'{file.Path}' is {entry.Length} bytes, manifest says {file.Size}."); continue; }

            using var stream = entry.Open();
            var hash = Hashing.Sha256Stream(stream);
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"'{file.Path}' does not match its manifest hash.");
        }
        return problems;
    }

    public void Dispose() => _archive.Dispose();

    /// <summary>
    /// Writes a package from a set of (in-game relative path -&gt; source file on disk) pairs.
    /// Hashes are computed here so the manifest and payload can never disagree.
    /// </summary>
    public static ModManifest Write(
        string destinationPath,
        ModManifest manifest,
        IReadOnlyDictionary<string, string> filesByRelativePath,
        byte[]? previewPng = null,
        IProgress<string>? progress = null)
    {
        var entries = new List<ModFileEntry>();
        var byPath = manifest.Files.ToDictionary(f => PathSafety.Normalize(f.Path), StringComparer.OrdinalIgnoreCase);

        var temp = destinationPath + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);

        using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var pair in filesByRelativePath.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = PathSafety.Normalize(pair.Key);
                if (!PathSafety.IsSafeRelativePath(relativePath, out var pathError))
                    throw new InvalidOperationException($"Refusing to package unsafe path '{pair.Key}': {pathError}");

                progress?.Report(relativePath);

                var info = new FileInfo(pair.Value);
                if (!info.Exists) throw new FileNotFoundException($"Source file for '{relativePath}' is missing.", pair.Value);

                var entry = archive.CreateEntry(FilePrefix + relativePath, CompressionLevel.Optimal);
                using (var target = entry.Open())
                using (var source = File.OpenRead(pair.Value))
                    source.CopyTo(target);

                byPath.TryGetValue(relativePath, out var existing);
                entries.Add(new ModFileEntry
                {
                    Path = relativePath,
                    Sha256 = Hashing.Sha256File(pair.Value),
                    Size = info.Length,
                    IsNew = existing?.IsNew ?? false,
                    BaseSha256 = existing?.BaseSha256,
                });
            }

            manifest.Files = entries;
            manifest.Schema = ModManifest.CurrentSchema;

            var problems = manifest.Validate();
            if (problems.Count > 0)
                throw new InvalidOperationException("Cannot package this mod:\n  - " + string.Join("\n  - ", problems));

            var manifestEntry = archive.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
                writer.Write(manifest.ToJson());

            if (previewPng is { Length: > 0 })
            {
                var previewEntry = archive.CreateEntry(PreviewEntry, CompressionLevel.Optimal);
                using var previewStream = previewEntry.Open();
                previewStream.Write(previewPng);
            }
        }

        File.Move(temp, destinationPath, overwrite: true);
        return manifest;
    }
}
