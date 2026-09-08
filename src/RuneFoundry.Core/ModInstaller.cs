using RuneFoundry.Core.Formats;
namespace RuneFoundry.Core;

public sealed record ApplyProgress(int Done, int Total, string CurrentPath);

public sealed class ApplyResult
{
    public int FilesWritten { get; set; }
    public int FilesRestored { get; set; }
    public int FilesDeleted { get; set; }

    /// <summary>Campaign slots whose victory condition this apply left changed.</summary>
    public int ObjectivesChanged { get; set; }
    public List<string> Warnings { get; } = new();
    public bool Succeeded { get; set; } = true;
    public string? FailureMessage { get; set; }

    public bool DidNothing => FilesWritten == 0 && FilesRestored == 0 && FilesDeleted == 0;
}

/// <summary>
/// Applies the mod library to the game folder.
///
/// Rather than tracking install and uninstall as separate operations, the installer
/// reconciles: it works out which file every enabled mod should own given the current
/// load order, compares that to what is actually on disk, and moves the difference.
/// Enabling, disabling, reordering, adding and removing a mod are then all the same
/// operation, which is what keeps the "restore the originals exactly" promise honest.
/// </summary>
public sealed class ModInstaller
{
    private readonly GameInstall _game;
    private readonly BackupVault _vault;

    public InstallState State { get; private set; }

    public ModInstaller(GameInstall game, BackupVault vault)
    {
        _game = game;
        _vault = vault;
        State = InstallState.Load(vault.StatePath);
        if (string.IsNullOrEmpty(State.GameRoot)) State.GameRoot = game.Root;
    }

    public void Save() => State.Save(_vault.StatePath);

    /// <summary>
    /// Re-reads the state file. Needed after the elevated helper has applied something,
    /// since it is a different process and this one's copy no longer describes disk.
    /// </summary>
    public void Reload() => State = InstallState.Load(_vault.StatePath);

    /// <summary>
    /// Registers a package in the library. Does not touch the game folder — call
    /// <see cref="Apply"/> for that, so the user can stage several changes and commit once.
    /// </summary>
    public InstalledMod AddPackage(string packagePath)
    {
        using var package = ModPackage.Open(packagePath);
        var manifest = package.Manifest;

        var payloadProblems = package.VerifyPayload();
        if (payloadProblems.Count > 0)
            throw new InvalidDataException("Package failed verification:\n  - " + string.Join("\n  - ", payloadProblems));

        var existing = State.Find(manifest.Id);
        _vault.ImportPackage(manifest.Id, packagePath);

        var record = existing ?? new InstalledMod { Id = manifest.Id };
        record.Name = manifest.Name;
        record.Version = manifest.Version;
        record.Author = manifest.Author;
        record.Description = manifest.Description;
        record.ProvidedPaths = manifest.Files.Select(f => PathSafety.Normalize(f.Path)).ToList();
        if (existing is null)
        {
            record.AddedUtc = DateTime.UtcNow;
            State.Mods.Add(record);
        }
        return record;
    }

    /// <summary>Drops a mod from the library. Its files come off the game folder on the next Apply.</summary>
    public void RemoveMod(string modId)
    {
        var mod = State.Find(modId);
        if (mod is null) return;
        State.Mods.Remove(mod);
    }

    // SetEnabled and Move (load order) were removed along with the multi-mod library they
    // served. One mod is enabled at a time — see SetOnlyEnabled — and that is not a UI
    // preference: it is what makes "uninstalling restores the original bytes exactly" simple
    // enough to be sure of. With a load order, which mod owns a file depends on what else is
    // enabled and in what order, and restoring means unwinding that.

    private sealed record DesiredFile(string ModId, ModFileEntry Entry);

    private Dictionary<string, DesiredFile> BuildDesiredState(Dictionary<string, ModPackage> packages)
    {
        var desired = new Dictionary<string, DesiredFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in State.Mods.Where(m => m.Enabled))
        {
            if (!packages.TryGetValue(mod.Id, out var package)) continue;
            foreach (var entry in package.Manifest.Files)
                desired[PathSafety.Normalize(entry.Path)] = new DesiredFile(mod.Id, entry);
        }
        return desired;
    }

    /// <summary>Undo record for a single filesystem change, so a failed Apply can be wound back.</summary>
    private sealed record Journal(string RelativePath, string GamePath, bool CreatedFile, bool HadOriginal);

    /// <summary>
    /// Brings the game folder in line with the enabled mods, in load order.
    /// On failure, every change made during this call is wound back before returning.
    /// </summary>
    public ApplyResult Apply(IProgress<ApplyProgress>? progress = null, CancellationToken cancel = default)
    {
        var result = new ApplyResult();

        if (!_game.CanWrite(out var writeError))
        {
            result.Succeeded = false;
            result.FailureMessage = writeError;
            return result;
        }

        var packages = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        var journal = new List<Journal>();

        try
        {
            foreach (var mod in State.Mods.Where(m => m.Enabled))
            {
                var path = _vault.PackagePath(mod.Id);
                if (!File.Exists(path))
                {
                    result.Warnings.Add($"'{mod.Name}' is enabled but its package is missing from the vault, so it was skipped.");
                    continue;
                }
                packages[mod.Id] = ModPackage.Open(path);
            }

            var desired = BuildDesiredState(packages);
            var stale = State.Applied.Keys.Where(p => !desired.ContainsKey(p)).ToList();

            var work = stale.Count + desired.Count;
            var done = 0;

            // 1. Take back everything no enabled mod claims any more.
            foreach (var relativePath in stale)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report(new ApplyProgress(done++, work, relativePath));

                var applied = State.Applied[relativePath];
                var gamePath = _game.ResolveDataPath(relativePath);

                if (applied.HadOriginal)
                {
                    if (_vault.HasOriginal(relativePath))
                    {
                        _vault.RestoreOriginal(relativePath, gamePath);
                        _vault.ForgetOriginal(relativePath);
                        result.FilesRestored++;
                    }
                    else
                    {
                        result.Warnings.Add($"No vault copy of '{relativePath}'. Left the modded file in place rather than deleting game content.");
                        continue;
                    }
                }
                else
                {
                    if (File.Exists(gamePath)) File.Delete(gamePath);
                    result.FilesDeleted++;
                }

                State.Applied.Remove(relativePath);
                State.Owners.Remove(relativePath);
            }

            // 2. Write everything the enabled mods claim, where disk does not already match.
            foreach (var (relativePath, want) in desired.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report(new ApplyProgress(done++, work, relativePath));

                var gamePath = _game.ResolveDataPath(relativePath);
                State.Applied.TryGetValue(relativePath, out var prior);
                State.Owners.TryGetValue(relativePath, out var currentOwner);

                var upToDate =
                    prior is not null &&
                    string.Equals(currentOwner, want.ModId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(prior.ModdedSha256, want.Entry.Sha256, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(gamePath) &&
                    string.Equals(Hashing.Sha256File(gamePath), prior.ModdedSha256, StringComparison.OrdinalIgnoreCase);

                if (upToDate) continue;

                bool hadOriginal;
                string? originalSha;

                if (prior is not null)
                {
                    // Already under our control; the stock copy is in the vault from an earlier apply.
                    hadOriginal = prior.HadOriginal;
                    originalSha = prior.OriginalSha256;
                }
                else
                {
                    hadOriginal = File.Exists(gamePath);
                    if (hadOriginal)
                    {
                        _vault.StoreOriginal(relativePath, gamePath);
                        originalSha = _vault.OriginalSha(relativePath);

                        var onDisk = Hashing.Sha256File(gamePath);
                        if (want.Entry.BaseSha256 is { Length: 64 } expected &&
                            !string.Equals(onDisk, expected, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Warnings.Add(
                                $"'{relativePath}' differs from the copy this mod was built against. The game may have been patched since. Backed it up anyway.");
                        }
                    }
                    else
                    {
                        originalSha = null;
                    }
                }

                journal.Add(new Journal(relativePath, gamePath, CreatedFile: !hadOriginal, HadOriginal: hadOriginal));
                packages[want.ModId].ExtractFile(want.Entry, gamePath);

                State.Applied[relativePath] = new AppliedFile
                {
                    Path = relativePath,
                    ModdedSha256 = want.Entry.Sha256,
                    OriginalSha256 = originalSha,
                    HadOriginal = hadOriginal,
                };
                State.Owners[relativePath] = want.ModId;
                result.FilesWritten++;
            }

            progress?.Report(new ApplyProgress(work, work, "Campaign objectives"));
            progress?.Report(new ApplyProgress(work, work, "Saving state"));
            Save();
            return result;
        }
        catch (Exception ex)
        {
            RollBack(journal, result);
            result.Succeeded = false;
            result.FailureMessage = ex is OperationCanceledException
                ? "Cancelled. The game folder was put back as it was."
                : ex.Message;
            // The rollback restored the files; State was not saved, so it still describes disk.
            State = InstallState.Load(_vault.StatePath);
            return result;
        }
        finally
        {
            foreach (var package in packages.Values) package.Dispose();
        }
    }

    /// <summary>
    /// Campaign victory conditions are not applied here.
    ///
    /// They were, by editing the executable, until measuring showed this build refuses to
    /// start if its file is modified at all. They are applied to the running game instead —
    /// see <see cref="RunningGame"/> — which means they belong to launching, not
    /// installing. Anything an earlier version wrote is forgotten here so the state file
    /// does not keep claiming an edit that no longer exists.
    /// </summary>
    private void RollBack(List<Journal> journal, ApplyResult result)
    {
        for (var i = journal.Count - 1; i >= 0; i--)
        {
            var step = journal[i];
            try
            {
                if (step.HadOriginal && _vault.HasOriginal(step.RelativePath))
                    _vault.RestoreOriginal(step.RelativePath, step.GamePath);
                else if (step.CreatedFile && File.Exists(step.GamePath))
                    File.Delete(step.GamePath);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Could not roll back '{step.RelativePath}': {ex.Message}");
            }
        }
    }

    /// <summary>Reports anything on disk that no longer matches what the loader wrote.</summary>
    public IReadOnlyList<string> Verify()
    {
        var problems = new List<string>();

        // Hashed up front and in parallel. The loop below still walks the mod's files in
        // their own order, so what it reports, and the order it reports it in, are the same.
        var hashes = Hashing.Sha256Files(State.Applied.Keys
            .Select(relativePath =>
            {
                try { return _game.ResolveDataPath(relativePath); }
                catch { return null; }
            })
            .Where(path => path is not null && File.Exists(path))
            .Select(path => path!));

        foreach (var (relativePath, applied) in State.Applied)
        {
            string gamePath;
            try { gamePath = _game.ResolveDataPath(relativePath); }
            catch (Exception ex) { problems.Add($"{relativePath}: {ex.Message}"); continue; }

            if (!File.Exists(gamePath)) { problems.Add($"{relativePath}: missing from the game folder."); continue; }
            if (!string.Equals(hashes.GetValueOrDefault(gamePath) ?? Hashing.Sha256File(gamePath),
                    applied.ModdedSha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{relativePath}: changed outside RuneFoundry (a game patch or a manual edit).");
            if (applied.HadOriginal && !_vault.HasOriginal(relativePath))
                problems.Add($"{relativePath}: its backup is missing from the vault, so uninstall cannot restore it.");
        }
        return problems;
    }

    /// <summary>
    /// Makes one mod the only enabled one — or none of them, which is vanilla.
    ///
    /// The studio loads a single mod at a time, so this is how a selection becomes the
    /// thing on disk: set it, then Apply, and the reconciler takes the previous mod's
    /// files back out and puts this one's in.
    /// </summary>
    public void SetOnlyEnabled(string? modId)
    {
        foreach (var mod in State.Mods)
            mod.Enabled = modId is not null && mod.Id.Equals(modId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The mod currently enabled, or null when the game is vanilla.</summary>
    public InstalledMod? ActiveMod => State.Mods.FirstOrDefault(m => m.Enabled);

    /// <summary>
    /// Puts the game back to stock: disables everything and applies. The library is kept,
    /// so the user can switch a mod back on afterwards.
    /// </summary>
    public ApplyResult RestoreVanilla(IProgress<ApplyProgress>? progress = null)
    {
        SetOnlyEnabled(null);
        return Apply(progress);
    }
}
