using System.IO;
using System.Windows;
using RuneFoundry.Core;
using RuneFoundry.Core.Formats;

namespace RuneFoundry.UI;

/// <summary>
/// Installing a mod, putting it on disk, and starting the game.
///
/// This is the whole of what the loader does, and the editor needs every step of it too:
/// "save and test" is build, install, apply, launch. Keeping it in one place is what lets
/// the two programs be separate without the editor growing a second, quietly different
/// version of the most destructive code in the project — the part that writes into
/// Program Files and moves the player's files into the vault.
///
/// It reports through events rather than touching controls, so the loader can drive a
/// progress bar with it and the editor can put the same words in its status line.
/// </summary>
public sealed class ModApplier
{
    private readonly Session _session;
    private readonly Window? _owner;

    public ModApplier(Session session, Window? owner)
    {
        _session = session;
        _owner = owner;
    }

    /// <summary>Something worth saying in a status line.</summary>
    public event Action<string>? Status;

    /// <summary>Work is or is not in progress; hosts disable their buttons on it.</summary>
    public event Action<bool>? Busy;

    /// <summary>done, total, indeterminate. Fired only while something is running.</summary>
    public event Action<double, double, bool>? Progressed;

    /// <summary>Whether a progress indicator should be on screen at all.</summary>
    public event Action<bool>? ProgressVisible;

    /// <summary>The library on disk changed, so any list of it is now stale.</summary>
    public event Action? LibraryChanged;

    private void Say(string message) => Status?.Invoke(message);

    /// <summary>
    /// Takes a built package into the library and returns its id, or null when it could not
    /// be installed — which is reported to the person rather than thrown.
    /// </summary>
    public string? Install(string packagePath)
    {
        var installer = _session.Installer;
        if (installer is null)
        {
            Ui.Error(_owner, "No game folder", "Set your Warcraft II Remastered folder in Settings first.");
            return null;
        }

        try
        {
            var mod = installer.AddPackage(packagePath);
            installer.Save();
            LibraryChanged?.Invoke();
            return mod.Id;
        }
        catch (Exception ex)
        {
            Ui.Failed(_owner, "install the mod", $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Makes one mod — or none, which is vanilla — the thing on disk.
    ///
    /// Asking for administrator rights up front would cost the ability to launch the game
    /// afterwards, so when something is out of reach (the game folder, or a vault an earlier
    /// elevated run left owned by Administrators) the work is handed to a short-lived
    /// elevated helper and Windows asks at the moment it is actually needed.
    /// </summary>
    public async Task<bool> Apply(string? modId, bool launch, bool silent = false)
    {
        var installer = _session.Installer;
        if (installer is null) return false;

        // Two programs, one vault: an apply must not interleave with another program's.
        using var guard = VaultLock.TryAcquire(TimeSpan.FromSeconds(20));
        if (!guard.Acquired)
        {
            Ui.Error(_owner, "Busy", VaultLock.BusyMessage);
            return false;
        }

        var needsElevation = _session.NeedsElevationToApply(out _);

        Busy?.Invoke(true);
        ProgressVisible?.Invoke(true);
        Progressed?.Invoke(0, 1, false);

        ApplyResult result;

        if (needsElevation)
        {
            // The helper is a separate process, so there is no per-file progress to show.
            Progressed?.Invoke(0, 1, true);
            Say("Waiting for permission to change the game folder…");

            var gameRoot = _session.Game!.Root;
            var vaultRoot = _session.Vault.Root;

            ElevatedApply.Response? response;
            try
            {
                response = await Task.Run(() => ElevatedApply.Run(gameRoot, vaultRoot, modId));
            }
            catch (Exception ex)
            {
                response = new ElevatedApply.Response
                {
                    Succeeded = false,
                    FailureMessage = $"{ex.GetType().Name}: {ex.Message}",
                };
            }

            Progressed?.Invoke(0, 1, false);

            // A different process did the work, so this one's idea of disk is stale.
            installer.Reload();

            if (response is null)
            {
                Busy?.Invoke(false);
                ProgressVisible?.Invoke(false);
                Say("Permission declined. Nothing was changed.");
                LibraryChanged?.Invoke();
                return false;
            }

            result = response.ToResult();
        }
        else
        {
            var progress = new Progress<ApplyProgress>(p =>
            {
                Progressed?.Invoke(p.Done, Math.Max(p.Total, 1), false);
                Say($"{p.Done}/{p.Total}  {p.CurrentPath}");
            });

            try
            {
                installer.SetOnlyEnabled(modId);
                result = await Task.Run(() => installer.Apply(progress));
            }
            catch (Exception ex)
            {
                Busy?.Invoke(false);
                ProgressVisible?.Invoke(false);
                Ui.Failed(_owner, "apply the mod", $"{ex.GetType().Name}: {ex.Message}");
                LibraryChanged?.Invoke();
                return false;
            }
        }

        Busy?.Invoke(false);
        ProgressVisible?.Invoke(false);

        if (!result.Succeeded)
        {
            Ui.Error(_owner, "Nothing was changed",
                result.FailureMessage + "\n\nThe game folder was left as it was.");
            LibraryChanged?.Invoke();
            return false;
        }

        // Whatever just moved on disk, the editor is previewing those same files.
        if (!result.DidNothing) _session.NotifyGameFilesChanged();

        LibraryChanged?.Invoke();

        if (result.Warnings.Count > 0 && !silent)
            Ui.Info(_owner, "Applied with warnings",
                Summarise(result) + "\n\n" + string.Join("\n", result.Warnings));
        else if (!silent)
            Say(Summarise(result));

        if (launch) await Launch();
        return true;
    }

    /// <summary>Starts the game, then sets what only a running game can be told.</summary>
    public async Task<bool> Launch()
    {
        var game = _session.Game;
        if (game is null) return false;

        var progress = new Progress<string>(Say);
        var method = _session.Settings.LaunchMethod;

        Busy?.Invoke(true);
        string error;
        bool started;
        try
        {
            var result = await Task.Run(() =>
            {
                var ok = GameLauncher.TryLaunch(game, method, out var why, m => ((IProgress<string>)progress).Report(m));
                return (ok, why);
            });
            started = result.ok;
            error = result.why;
        }
        finally
        {
            Busy?.Invoke(false);
        }

        if (started)
        {
            Say($"Started the game. Playing {_session.Installer?.ActiveMod?.Name ?? "Vanilla"}.");
            await ApplyObjectivesToRunningGame(game);
            return true;
        }

        // The mod is applied either way, so this is a launch problem, not a mod problem.
        Ui.Failed(_owner, "start the game", error);
        Say("The mod is applied, but the game could not be started from here.");
        return false;
    }

    /// <summary>
    /// Sets the active mod's campaign victory conditions in the game that has just started.
    ///
    /// This cannot be done when the mod is installed, because the conditions live in the
    /// executable and this build will not run if its file is edited. So it happens here,
    /// once, while the game is loading and before any mission can be started — which is the
    /// only moment that works, since the game copies the value out of the table when a
    /// mission loads.
    /// </summary>
    private async Task ApplyObjectivesToRunningGame(GameInstall game)
    {
        var installer = _session.Installer;
        var active = installer?.ActiveMod;
        if (installer is null || active is null) return;

        Dictionary<int, int> objectives;
        Dictionary<int, int> thresholds;
        try
        {
            var path = _session.Vault.PackagePath(active.Id);
            if (!File.Exists(path)) return;

            using var package = ModPackage.Open(path);
            objectives = new Dictionary<int, int>(package.Manifest.Objectives);
            thresholds = new Dictionary<int, int>(package.Manifest.Thresholds);
        }
        catch
        {
            // A mod that cannot be read here is a problem the apply already reported.
            return;
        }

        if (objectives.Count == 0 && thresholds.Count == 0) return;

        // Everything else in a mod is file replacement. This one writes into a running,
        // signed-in Blizzard game, so it is the one thing worth asking about first.
        if (CampaignRulesConsent.Ask(_owner, _session.Settings) != CampaignRulesConsent.Answer.Allow)
        {
            Say("The mod is applied. Its campaign rules were left alone, as you asked.");
            return;
        }

        Say("Waiting for the game so its victory conditions can be set…");

        var result = await Task.Run(() =>
            RunningGame.Apply(game, objectives, thresholds, TimeSpan.FromMinutes(3)));

        if (result.Message.Length > 0) Say(result.Message);
    }

    public static string Summarise(ApplyResult result)
    {
        if (result.DidNothing) return "Already up to date. Nothing to change.";

        var parts = new List<string>();
        if (result.FilesWritten > 0) parts.Add($"{result.FilesWritten} replaced");
        if (result.FilesRestored > 0) parts.Add($"{result.FilesRestored} restored");
        if (result.FilesDeleted > 0) parts.Add($"{result.FilesDeleted} removed");
        return string.Join(", ", parts) + ".";
    }
}
