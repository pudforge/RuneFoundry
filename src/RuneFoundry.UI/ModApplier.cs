using System.IO;
using System.Windows;
using RuneFoundry.Core;
using RuneFoundry.Core.Scenarios;
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
    private readonly Func<Window?> _ownerOf;

    public ModApplier(Session session, Window? owner) : this(session, () => owner) { }

    /// <summary>
    /// For a host that is not in a window yet when it builds the applier — a view's
    /// constructor runs before it is parented, so asking for its window there gives null.
    /// </summary>
    public ModApplier(Session session, Func<Window?> owner)
    {
        _session = session;
        _ownerOf = owner;
    }

    private Window? _owner => _ownerOf();

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
        Dictionary<int, ScenarioRules> scenarios;
        try
        {
            var path = _session.Vault.PackagePath(active.Id);
            if (!File.Exists(path)) return;

            using var package = ModPackage.Open(path);
            objectives = new Dictionary<int, int>(package.Manifest.Objectives);
            thresholds = new Dictionary<int, int>(package.Manifest.Thresholds);

            // A mod written against a newer rule shape is refused rather than half read:
            // a condition this build does not know would never be met, and a mission that
            // cannot be finished is worse than one that says why.
            scenarios = package.Manifest.ScenarioVersion <= ScenarioRules.Version
                ? new Dictionary<int, ScenarioRules>(package.Manifest.Scenarios)
                : new Dictionary<int, ScenarioRules>();

            if (package.Manifest.ScenarioVersion > ScenarioRules.Version)
                Say("This mod's own mission rules were written by a newer RuneFoundry, so "
                    + "they are left alone. Everything else is applied.");
        }
        catch
        {
            // A mod that cannot be read here is a problem the apply already reported.
            return;
        }

        // A slot with rules of its own gets the inert objective, so the game stops
        // deciding that mission and waits for the watcher.
        foreach (var slot in scenarios.Keys)
            objectives[slot] = GameAddresses.InertObjective;

        if (objectives.Count == 0 && thresholds.Count == 0) return;

        // Everything else in a mod is file replacement. This one writes into a running,
        // signed-in Blizzard game, so it is the one thing worth asking about first.
        if (CampaignRulesConsent.Ask(_owner, _session.Settings, scenarios.Count > 0)
            != CampaignRulesConsent.Answer.Allow)
        {
            // Saying only "left alone" is true and useless. Without the inert objective the
            // game judges these missions by the rule belonging to whichever campaign slot
            // they sit in — a rule written for a different map, which on this one is
            // routinely unwinnable or already lost. Somebody who declines has to be told
            // that, or they meet it as a mission that ends a second after it starts.
            if (scenarios.Count > 0)
                Say($"The mod is applied, but {scenarios.Count} "
                    + (scenarios.Count == 1 ? "mission" : "missions")
                    + " in it need rules RuneFoundry was not allowed to set. The game will judge "
                    + (scenarios.Count == 1 ? "it" : "them")
                    + " by the original campaign mission's win and lose conditions, which "
                    + "were written for a different map, so the mission may be impossible "
                    + "to win, or end the moment it starts. Apply the mod again and allow "
                    + "campaign rules to play it as its author intended.");
            else
                Say("The mod is applied. Its campaign rules were left alone, as you asked.");

            SetPatch(scenarios.Count > 0
                ? "Campaign rules not applied. These missions use the game's own rules."
                : "Campaign rules not applied, as you asked.", "Warn");
            return;
        }

        Say("Waiting for the game so its victory conditions can be set…");
        SetPatch("Waiting for Warcraft II to start…", "Dim");

        var result = await Task.Run(() =>
            RunningGame.Apply(game, objectives, thresholds, TimeSpan.FromMinutes(3)));

        if (result.Message.Length > 0) Say(result.Message);

        SetPatch(result.Applied
            ? $"Attached to Warcraft II. {result.Changed} campaign "
              + (result.Changed == 1 ? "rule" : "rules") + " written into the running game."
            : "Could not set the campaign rules. "
              + (result.Message.Length > 0 ? result.Message : "The game was not reachable."),
            result.Applied ? "Accent" : "Warn");

        if (scenarios.Count > 0) StartWatching(game, active.Id, scenarios);
    }

    /// <summary>The watcher for this session, or null when nothing is being watched.</summary>
    public ScenarioEngine? Watcher { get; private set; }

    private ScenarioLock? _watchLock;

    /// <summary>Raised whenever the watcher's state changes, for a UI that shows it.</summary>
    public event Action? WatchChanged;

    /// <summary>
    /// What RuneFoundry has done to the running game, in one line, for the status row.
    ///
    /// The campaign rules are the only part of a mod that is not a file on disk: they are
    /// two words written into a running process, and until now the only trace of that was a
    /// message that scrolled past while the mod was applying. Someone whose mission ended
    /// the moment it opened had no way to tell a rule that did not fire from a rule that was
    /// never written, which is the report this came from. So it is kept here and shown for
    /// as long as the session lasts.
    /// </summary>
    public string PatchStatus { get; private set; } = "";

    /// <summary>Dim, Accent or Warn: which brush the status row should use.</summary>
    public string PatchLevel { get; private set; } = "Dim";

    private void SetPatch(string text, string level)
    {
        PatchStatus = text;
        PatchLevel = level;
        WatchChanged?.Invoke();
    }

    /// <summary>
    /// Starts watching the running game for the mod's own rules.
    ///
    /// Held for the session and stopped when the game exits or the window closes. Only one
    /// process may watch, because two would each write the victory pointer and the second
    /// would land after the mission was already over.
    /// </summary>
    private void StartWatching(GameInstall game, string modId,
                               IReadOnlyDictionary<int, ScenarioRules> scenarios)
    {
        StopWatching();

        _watchLock = ScenarioLock.TryAcquire();
        if (!_watchLock.Held)
        {
            _watchLock.Dispose();
            _watchLock = null;
            Say("Another RuneFoundry window is already watching this game.");
            SetPatch("Another RuneFoundry window is watching this game, so this one is not.", "Warn");
            return;
        }

        var process = System.Diagnostics.Process.GetProcessesByName("Warcraft II").FirstOrDefault();
        if (process is null)
        {
            StopWatching();
            Say("The game closed before its rules could be watched.");
            SetPatch("The game closed before this mod's own rules could be watched.", "Warn");
            return;
        }

        Watcher = ScenarioEngine.TryAttach(game, process, scenarios, modId, out var refusal);

        if (Watcher is null)
        {
            StopWatching();
            if (refusal is not null) Say(refusal.Reason);
            SetPatch(refusal?.Reason ?? "This mod's own rules are not being watched.", "Warn");
            return;
        }

        Watcher.Armed += _ => WatchChanged?.Invoke();
        Watcher.Disarmed += _ => WatchChanged?.Invoke();
        Watcher.Refused += _ => WatchChanged?.Invoke();
        Watcher.Fired += _ => WatchChanged?.Invoke();

        Watcher.Start();
        WatchChanged?.Invoke();

        Say("Watching this mod's own mission rules.");
    }

    /// <summary>Stops watching and gives up the lock. Safe to call when not watching.</summary>
    public void StopWatching()
    {
        Watcher?.Dispose();
        Watcher = null;

        _watchLock?.Dispose();
        _watchLock = null;

        WatchChanged?.Invoke();
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
