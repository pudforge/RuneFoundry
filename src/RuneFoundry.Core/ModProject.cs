using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuneFoundry.Core;

/// <summary>
/// A packager project (.w2proj).
///
/// The project is a folder, not a database: alongside the .w2proj file sits a
/// "content" directory that mirrors the game's x86\Data tree. Any file present there
/// is an override. That makes the whole thing diffable, syncable and editable with
/// ordinary tools — you can drop a PNG in with Explorer and the packager picks it up.
/// </summary>
public sealed class ModProject
{
    public const int CurrentSchema = 1;
    public const string Extension = ".w2proj";

    [JsonPropertyName("schema")] public int Schema { get; set; } = CurrentSchema;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("website")] public string Website { get; set; } = "";

    /// <summary>Content folder, relative to the project file. Kept configurable but rarely changed.</summary>
    [JsonPropertyName("contentFolder")] public string ContentFolder { get; set; } = "content";

    /// <summary>Optional cover image, relative to the project file.</summary>
    [JsonPropertyName("previewImage")] public string PreviewImage { get; set; } = "";

    /// <summary>Remembered so reopening a project does not mean re-finding the game.</summary>
    [JsonPropertyName("lastGameRoot")] public string LastGameRoot { get; set; } = "";

    /// <summary>
    /// Campaign slots this mod wants won by destroying every enemy instead of by the
    /// mission's own objective. Stored as the executable's slot numbers, 0-51, because
    /// that is what the change is actually addressed by.
    /// </summary>
    [JsonPropertyName("objectives")] public Dictionary<int, int> Objectives { get; set; } = new();

    /// <summary>The objective this mod wants for a slot, or null to leave the game's own.</summary>
    public int? ObjectiveFor(int slot) => Objectives.TryGetValue(slot, out var id) ? id : null;

    /// <summary>Returns true if this changed anything, so the caller can save.</summary>
    public bool SetObjective(int slot, int? objectiveId)
    {
        if (ObjectiveFor(slot) == objectiveId) return false;

        if (objectiveId is null) Objectives.Remove(slot);
        else Objectives[slot] = objectiveId.Value;

        return true;
    }

    /// <summary>
    /// Threshold words this mod sets, by slot. Only meaningful for a counter objective;
    /// see <see cref="Formats.CampaignObjectives.ThresholdTableAddress"/>.
    /// </summary>
    [JsonPropertyName("thresholds")] public Dictionary<int, int> Thresholds { get; set; } = new();

    public int? ThresholdFor(int slot) => Thresholds.TryGetValue(slot, out var v) ? v : null;

    public bool SetThreshold(int slot, int? threshold)
    {
        if (ThresholdFor(slot) == threshold) return false;

        if (threshold is null) Thresholds.Remove(slot);
        else Thresholds[slot] = threshold.Value;

        return true;
    }

    public bool WantsDestroyAll(int slot) => ObjectiveFor(slot) == 0x0100;

    public bool SetDestroyAll(int slot, bool on) => SetObjective(slot, on ? 0x0100 : null);

    [JsonIgnore] public string ProjectPath { get; private set; } = "";
    [JsonIgnore] public string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;
    [JsonIgnore] public string ContentRoot => Path.GetFullPath(Path.Combine(ProjectDirectory, ContentFolder));

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ModProject Create(string projectPath, string name)
    {
        var full = Path.GetFullPath(projectPath);
        var project = new ModProject
        {
            ProjectPath = full,
            Name = name,
            Id = PathSafety.SuggestModId(name),

            // A folder of its own, named after the project file. Every project used to
            // default to "content" beside itself, so two projects in one directory shared
            // every replaced file: a new mod opened already holding the previous one's
            // work, and deleting a file from one deleted it from both. Projects that
            // already say "content" in their own file keep saying it.
            ContentFolder = Path.GetFileNameWithoutExtension(full) + "-content",
        };
        Directory.CreateDirectory(project.ContentRoot);
        project.Save();
        return project;
    }

    public static ModProject Load(string projectPath)
    {
        var project = JsonSerializer.Deserialize<ModProject>(File.ReadAllText(projectPath), Options)
            ?? throw new InvalidDataException("Project file is empty or malformed.");
        project.ProjectPath = Path.GetFullPath(projectPath);
        Directory.CreateDirectory(project.ContentRoot);
        return project;
    }

    public void Save()
    {
        var temp = ProjectPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, ProjectPath, overwrite: true);
    }

    public string ResolveContentPath(string relativePath)
    {
        if (!PathSafety.TryResolveUnder(ContentRoot, relativePath, out var full, out var error))
            throw new InvalidOperationException($"Unsafe project path '{relativePath}': {error}");
        return full;
    }

    public bool HasOverride(string relativePath) => File.Exists(ResolveContentPath(relativePath));

    /// <summary>Every override in the project, as canonical game-relative paths.</summary>
    public IReadOnlyList<string> EnumerateOverrides()
    {
        if (!Directory.Exists(ContentRoot)) return Array.Empty<string>();
        return Directory.EnumerateFiles(ContentRoot, "*", SearchOption.AllDirectories)
            .Select(f => PathSafety.Normalize(Path.GetRelativePath(ContentRoot, f)))
            .Where(p => PathSafety.IsSafeRelativePath(p, out _))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Brings an external file in as the override for a game path.</summary>
    public void ImportOverride(string relativePath, string sourceFile)
    {
        var target = ResolveContentPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(sourceFile, target, overwrite: true);
    }

    /// <summary>Puts bytes in as the override for a game path, creating it if need be.</summary>
    public void WriteOverride(string relativePath, byte[] bytes)
    {
        var target = ResolveContentPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
    }

    /// <summary>
    /// Seeds an override with the stock file, so the user can edit a real asset in place
    /// instead of having to produce one from nothing.
    /// </summary>
    /// <summary>
    /// Takes the game's own copy of a file into this mod.
    ///
    /// The vault is preferred over the game folder whenever it has the file: while a mod is
    /// applied the game folder holds *that* mod's version, and starting from it would quietly
    /// build one mod on top of another's work.
    /// </summary>
    public void SeedFromGame(string relativePath, GameInstall game, BackupVault? vault = null)
    {
        var source = vault?.OriginalFile(relativePath) ?? game.ResolveDataPath(relativePath);
        if (!File.Exists(source)) throw new FileNotFoundException($"'{relativePath}' is not in the game folder.", source);
        ImportOverride(relativePath, source);
    }

    public void RemoveOverride(string relativePath)
    {
        var target = ResolveContentPath(relativePath);
        if (File.Exists(target)) File.Delete(target);

        var directory = Path.GetDirectoryName(target)!;
        while (directory.StartsWith(ContentRoot, StringComparison.OrdinalIgnoreCase) &&
               !directory.Equals(ContentRoot, StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(directory) &&
               !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory)!;
        }
    }

    /// <summary>An override that is byte-identical to the stock file ships no change; worth telling the user.</summary>
    /// <summary>
    /// Overrides whose bytes match the game's own, and which therefore change nothing.
    ///
    /// The comparison must not be against whatever is in the game folder right now. Once a
    /// mod is applied, the game folder holds that mod's files — so comparing an override
    /// against it compares the file to itself, and every override is reported as identical
    /// to stock. That is the most misleading thing this can say: it told a user their
    /// replaced campaign maps "will change nothing in game" while they were live.
    ///
    /// So the vault's stored original is the reference wherever there is one, and a file
    /// the loader has applied but the vault has no original for is one the mod added —
    /// which is by definition not identical to anything stock.
    /// </summary>
    /// <summary>
    /// Hashes already taken, keyed by the file's path, length and write time.
    ///
    /// The overrides list is redrawn on every lens switch and every replaced file, and
    /// hashing a 5 MB image to decide whether one line of it says "identical to stock" is
    /// most of that work. A file whose length and timestamp are unchanged cannot have
    /// different bytes for our purposes.
    /// </summary>
    [JsonIgnore]
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,
        (long Length, DateTime Written, string Sha)> _hashes = new();

    private string HashOf(string path)
    {
        var info = new FileInfo(path);
        if (_hashes.TryGetValue(path, out var seen)
            && seen.Length == info.Length && seen.Written == info.LastWriteTimeUtc)
            return seen.Sha;

        var sha = Hashing.Sha256File(path);
        _hashes[path] = (info.Length, info.LastWriteTimeUtc, sha);
        return sha;
    }

    public IReadOnlyList<string> FindUnchangedOverrides(
        GameInstall game, BackupVault? vault = null, ISet<string>? appliedPaths = null)
    {
        var unchanged = new List<string>();

        // Warm the cache in parallel first. Everything below then hits it, and the answer is
        // the same list in the same order as when each file was hashed in turn.
        Warm(EnumerateOverrides().Select(ResolveContentPath));

        foreach (var relativePath in EnumerateOverrides())
        {
            var applied = appliedPaths?.Contains(relativePath) == true;

            string stockSha;
            if (vault is not null && vault.HasOriginal(relativePath))
            {
                // Present but unreadable is possible — an elevated run can leave the vault
                // owned by Administrators — and "cannot tell" is not "identical".
                if (vault.OriginalSha(relativePath) is not { } hash) continue;
                stockSha = hash;
            }
            else if (applied)
            {
                // Applied, and nothing was displaced to make room for it: the mod added it.
                continue;
            }
            else
            {
                var gamePath = game.ResolveDataPath(relativePath);
                if (!File.Exists(gamePath)) continue;
                stockSha = HashOf(gamePath);
            }

            if (stockSha == HashOf(ResolveContentPath(relativePath)))
                unchanged.Add(relativePath);
        }

        return unchanged;
    }

    /// <summary>
    /// Fills the hash cache for a set of files at once. The same values as asking for them
    /// one at a time, which is what the callers still do — this only gets there sooner.
    /// </summary>
    private void Warm(IEnumerable<string> paths)
    {
        var wanted = paths.Where(File.Exists).ToList();
        if (wanted.Count < 2) return;

        var infos = wanted.ToDictionary(path => path, path => new FileInfo(path), StringComparer.OrdinalIgnoreCase);

        // Only what the cache does not already have, on the same length-and-timestamp test
        // HashOf uses, so warming never costs more than it saves.
        var stale = wanted.Where(path =>
            !_hashes.TryGetValue(path, out var seen)
            || seen.Length != infos[path].Length
            || seen.Written != infos[path].LastWriteTimeUtc).ToList();

        foreach (var (path, sha) in Hashing.Sha256Files(stale))
            _hashes[path] = (infos[path].Length, infos[path].LastWriteTimeUtc, sha);
    }

    public ModManifest BuildManifest(GameInstall? game)
    {
        var manifest = new ModManifest
        {
            Id = Id,
            Name = Name,
            Version = Version,
            Author = Author,
            Description = Description,
            Website = Website,
            CreatedUtc = DateTime.UtcNow,
            BuiltAgainst = game?.Root ?? LastGameRoot,
            Objectives = new Dictionary<int, int>(Objectives),
            Thresholds = new Dictionary<int, int>(Thresholds),
        };

        var stock = game is null
            ? new Dictionary<string, string>()
            : Hashing.Sha256Files(EnumerateOverrides().Select(game.ResolveDataPath).Where(File.Exists));

        foreach (var relativePath in EnumerateOverrides())
        {
            var entry = new ModFileEntry { Path = relativePath };
            if (game is not null)
            {
                var gamePath = game.ResolveDataPath(relativePath);
                if (File.Exists(gamePath))
                    entry.BaseSha256 = stock.GetValueOrDefault(gamePath) ?? Hashing.Sha256File(gamePath);
                else entry.IsNew = true;
            }
            manifest.Files.Add(entry);
        }
        return manifest;
    }

    /// <summary>Writes the distributable .w2mod. Hashes and sizes are filled in by the packager.</summary>
    public ModManifest Build(string destinationPath, GameInstall? game, IProgress<string>? progress = null)
    {
        var overrides = EnumerateOverrides();
        if (overrides.Count == 0)
            throw new InvalidOperationException("This project has no files to package. Add at least one override first.");

        var files = overrides.ToDictionary(p => p, ResolveContentPath, StringComparer.OrdinalIgnoreCase);

        byte[]? preview = null;
        if (!string.IsNullOrWhiteSpace(PreviewImage))
        {
            var previewPath = Path.GetFullPath(Path.Combine(ProjectDirectory, PreviewImage));
            if (File.Exists(previewPath)) preview = File.ReadAllBytes(previewPath);
        }

        return ModPackage.Write(destinationPath, BuildManifest(game), files, preview, progress);
    }
}
