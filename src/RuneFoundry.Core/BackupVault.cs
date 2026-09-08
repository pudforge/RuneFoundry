namespace RuneFoundry.Core;

/// <summary>
/// Holds the stock copy of every game file a mod has displaced, plus the packages
/// themselves so a mod can be re-applied after being disabled without the user
/// still having the original .w2mod lying around.
///
/// The vault mirrors the game's folder structure, which makes it browsable and
/// hand-recoverable: if every one of these tools vanished, copying vault\originals
/// over x86\Data restores a clean install.
/// </summary>
public sealed class BackupVault
{
    public string Root { get; }
    public string OriginalsRoot => Path.Combine(Root, "originals");
    public string PackagesRoot => Path.Combine(Root, "packages");
    public string StatePath => Path.Combine(Root, "state.json");

    public BackupVault(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Directory.CreateDirectory(OriginalsRoot);
        Directory.CreateDirectory(PackagesRoot);
    }

    /// <summary>The folder this app was called before it was RuneFoundry. A name on disk,
    /// not an identifier: it must keep saying War2ModLoader whatever the code is called.</summary>
    public const string FormerFolderName = "War2ModLoader";

    public const string FolderName = "RuneFoundry";

    public static string DefaultRoot => ChooseRoot(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Directory.Exists);

    /// <summary>
    /// Which folder to keep the vault in. A vault made under the old name holds the only
    /// copies of the game files an applied mod displaced, so an install that has one keeps
    /// using it; moving it would risk those, and using it costs nothing.
    ///
    /// Split out from <see cref="DefaultRoot"/> so the rule can be tested without a real
    /// ProgramData to point it at.
    /// </summary>
    public static string ChooseRoot(string sharedFolder, Func<string, bool> exists)
    {
        var current = Path.Combine(sharedFolder, FolderName);
        var former = Path.Combine(sharedFolder, FormerFolderName);
        return !exists(current) && exists(former) ? former : current;
    }

    public static BackupVault Default() => new(DefaultRoot);

    /// <summary>
    /// A pristine copy of the game executable, kept from before the loader first edited a
    /// campaign objective in it.
    ///
    /// The objective change is two bytes and the loader remembers what they were, so this
    /// is not how the edit is undone. It is here so the editor can show what the campaign's
    /// own objectives are while a mod that changes them is applied — reading the live
    /// executable then would show the mod's choice as if it were the game's.
    /// </summary>
    public string ExecutablePath => Path.Combine(Root, "executable", "Warcraft II.exe");

    public bool HasExecutable => File.Exists(ExecutablePath);

    /// <summary>Stores the executable if it has not been stored already. Never overwrites.</summary>
    public void StoreExecutable(string source)
    {
        if (HasExecutable || !File.Exists(source)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(ExecutablePath)!);
        File.Copy(source, ExecutablePath);
    }

    /// <summary>
    /// Whether the loader can still write its own vault.
    ///
    /// It can lose the ability. Everything created under ProgramData while running as
    /// administrator belongs to Administrators and is read-only to everyone else, so a
    /// vault built by an elevated run is one the ordinary app can read but never update —
    /// and putting a mod away means deleting the stored original, not just reading it.
    ///
    /// Creating a file is not the test: ProgramData lets anyone add one. What fails is
    /// rewriting what is already there, so that is what this probes.
    /// </summary>
    public bool CanWrite(out string error)
    {
        error = "";

        foreach (var path in Probes())
        {
            try
            {
                using var _ = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }
            catch (UnauthorizedAccessException)
            {
                error = $"'{path}' cannot be written. The vault was created by a run with "
                        + "administrator rights, so this app can no longer update it.";
                return false;
            }
            catch (IOException)
            {
                // In use, or something else transient. Not a rights problem.
            }
        }

        return true;
    }

    /// <summary>The state file and one stored original — where the loss of rights bites.</summary>
    private IEnumerable<string> Probes()
    {
        if (File.Exists(StatePath)) yield return StatePath;

        if (!Directory.Exists(OriginalsRoot)) yield break;

        var original = Directory
            .EnumerateFiles(OriginalsRoot, "*", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (original is not null) yield return original;
    }

    private string OriginalPath(string relativePath)
    {
        if (!PathSafety.TryResolveUnder(OriginalsRoot, relativePath, out var full, out var error))
            throw new InvalidOperationException($"Unsafe vault path '{relativePath}': {error}");
        return full;
    }

    public bool HasOriginal(string relativePath) => File.Exists(OriginalPath(relativePath));

    /// <summary>
    /// The stock copy of a file, for reading. Worth preferring over the game folder while
    /// a mod is applied: the game folder then holds the mod's version, so comparing
    /// against it would say nothing had changed.
    /// </summary>
    /// <summary>
    /// The game's own copy of a file: what the vault kept if a mod displaced it, else what
    /// is in the game folder.
    ///
    /// This is the rule for everything that means "as the game shipped it" — showing a file
    /// no mod includes, comparing a table against stock, seeding a file into a mod. Reading
    /// the game folder directly would show one mod's work while another is applied.
    /// </summary>
    public string StockFile(GameInstall game, string relativePath) =>
        OriginalFile(relativePath) ?? game.ResolveDataPath(relativePath);

    /// <summary>
    /// The file a mod actually reads: its own copy if it has one, otherwise the game's.
    ///
    /// Beside <see cref="StockFile"/> because it is the same rule with one step in front,
    /// and because six screens had each written it for themselves under five different
    /// names. Two of those ended in a fallback that could only run when there was no game
    /// and then dereferenced the game, so the guard threw in the case it was written for.
    /// </summary>
    public string EffectiveFile(ModProject? project, GameInstall game, string relativePath) =>
        project?.HasOverride(relativePath) == true
            ? project.ResolveContentPath(relativePath)
            : StockFile(game, relativePath);

    public string? OriginalFile(string relativePath)
    {
        var path = OriginalPath(relativePath);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Copies a stock file into the vault if we have not already stored it. Idempotent:
    /// a second mod displacing the same file must not overwrite the stock copy with the
    /// first mod's output.
    /// </summary>
    public void StoreOriginal(string relativePath, string gameFilePath)
    {
        var target = OriginalPath(relativePath);
        if (File.Exists(target)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";
        File.Copy(gameFilePath, temp, overwrite: true);
        File.Move(temp, target, overwrite: true);
    }

    public void RestoreOriginal(string relativePath, string gameFilePath)
    {
        var source = OriginalPath(relativePath);
        if (!File.Exists(source))
            throw new FileNotFoundException($"No backup of '{relativePath}' in the vault, so it cannot be restored.", source);

        Directory.CreateDirectory(Path.GetDirectoryName(gameFilePath)!);
        var temp = gameFilePath + ".w2mod-restore";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, gameFilePath, overwrite: true);
    }

    public string? OriginalSha(string relativePath)
    {
        var path = OriginalPath(relativePath);
        return File.Exists(path) ? Hashing.Sha256File(path) : null;
    }

    /// <summary>Drops a vault copy once no mod displaces that path any more.</summary>
    public void ForgetOriginal(string relativePath)
    {
        var path = OriginalPath(relativePath);
        if (!File.Exists(path)) return;
        File.Delete(path);
        PruneEmptyDirectories(Path.GetDirectoryName(path)!);
    }

    private void PruneEmptyDirectories(string directory)
    {
        var current = directory;
        while (current.StartsWith(OriginalsRoot, StringComparison.OrdinalIgnoreCase) &&
               !current.Equals(OriginalsRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.EnumerateFileSystemEntries(current).Any()) return;
            Directory.Delete(current);
            current = Path.GetDirectoryName(current)!;
        }
    }

    public string PackagePath(string modId)
    {
        if (!PathSafety.IsValidModId(modId))
            throw new InvalidOperationException($"Invalid mod id '{modId}'.");
        return Path.Combine(PackagesRoot, modId + ModPackage.Extension);
    }

    /// <summary>Takes the loader's own copy of a package so the user's file can move or be deleted.</summary>
    public string ImportPackage(string modId, string sourcePackagePath)
    {
        var target = PackagePath(modId);
        if (!string.Equals(Path.GetFullPath(sourcePackagePath), target, StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePackagePath, target, overwrite: true);
        return target;
    }

    public void RemovePackage(string modId)
    {
        var path = PackagePath(modId);
        if (File.Exists(path)) File.Delete(path);
    }

    public long OriginalsSizeBytes()
        => Directory.Exists(OriginalsRoot)
            ? Directory.EnumerateFiles(OriginalsRoot, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;
}
