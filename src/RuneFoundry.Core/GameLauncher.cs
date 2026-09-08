using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace RuneFoundry.Core;

public enum LaunchMethod
{
    /// <summary>Let the Battle.net client start the game, exactly as its Play button does.</summary>
    BattleNet,

    /// <summary>Start the executable ourselves with the client's own arguments. An escape hatch.</summary>
    Direct,
}

/// <summary>
/// Starts Warcraft II Remastered, by asking the Battle.net client to do it.
///
/// Everything here was measured on a real install rather than inferred. A native launch
/// looks like:
///
///     "…\Warcraft II.exe" -uid w2r      (parent: Battle.net.exe --from-launcher)
///
/// The -uid argument is how the game identifies itself to the client; without it the game
/// loads and then reports "Lost connection to Battle.net". The shipped Start Menu shortcut
/// passes no arguments at all, so it is not the guide it appears to be.
///
/// Rather than reproduce that command line, the client is asked to launch the game, which
/// gets the environment right by construction. See TryLaunch for the two rules that makes
/// necessary — uppercase uid, and client-must-already-be-running.
///
/// Underneath all of it sits elevation. This app runs as administrator so it can write
/// into Program Files, and a child process inherits that token. An elevated game cannot
/// reach the ordinary-user Battle.net session — reported, confusingly, as the same "Lost
/// connection" message — so launching is handed to Explorer, which runs as the user. If
/// that cannot be done the launch is refused: a game that will not sign in is worse than
/// no launch at all.
/// </summary>
public static class GameLauncher
{
    /// <summary>Blizzard's product code for Warcraft II Remastered.</summary>
    public const string ProductCode = "w2r";

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Locates Battle.net.exe from its registered protocol handler, then the usual paths.</summary>
    public static string? FindBattleNet()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"battlenet\shell\open\command");
            if (key?.GetValue(null) is string command)
            {
                // Stored as: "C:\...\Battle.net.exe" --uri="%1"
                var quoted = command.StartsWith('"') ? command[1..].Split('"')[0] : command.Split(' ')[0];
                if (File.Exists(quoted)) return quoted;
            }
        }
        catch
        {
            // A missing or unreadable handler just means we fall through to the known paths.
        }

        foreach (var path in new[]
                 {
                     @"C:\Program Files (x86)\Battle.net\Battle.net.exe",
                     @"C:\Program Files\Battle.net\Battle.net.exe",
                 })
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static bool IsBattleNetAvailable => FindBattleNet() is not null;

    /// <summary>
    /// "Battle.net Launcher.exe", the bootstrapper that brings the client up. Starting it
    /// is what produces the "Battle.net.exe --from-launcher" process a real launch runs
    /// under; starting Battle.net.exe directly is not the same thing.
    /// </summary>
    public static string? FindBattleNetLauncher()
    {
        var client = FindBattleNet();
        if (client is null) return null;

        var launcher = Path.Combine(Path.GetDirectoryName(client)!, "Battle.net Launcher.exe");
        return File.Exists(launcher) ? launcher : null;
    }

    public static bool IsBattleNetRunning
        => Process.GetProcessesByName("Battle.net").Length > 0;

    /// <summary>How the last launch was actually started. Shown to the user; never guessed at.</summary>
    public static string LastLaunchDetail { get; private set; } = "";

    /// <summary>
    /// Starts the game, by default by asking the Battle.net client to do it.
    ///
    /// Both halves of this were measured rather than guessed:
    ///
    ///   * With the client already running, "Battle.net.exe --exec=&quot;launch W2R&quot;" starts
    ///     the game within a few seconds. The uid must be UPPERCASE here; "launch w2r"
    ///     silently does nothing, which is what made an earlier attempt look broken.
    ///   * From cold, the same argument passed to Battle.net Launcher.exe is swallowed by
    ///     the bootstrap: the client comes up, the game does not. So the client has to be
    ///     started first and waited for, and only then asked to launch.
    ///
    /// Letting the client do it means the game gets exactly the environment it expects —
    /// it ends up as "Warcraft II.exe -uid w2r" parented to Battle.net.exe, the same as a
    /// launch from the client's own Play button.
    /// </summary>
    /// <summary>
    /// The scenario to open straight into, or null for the game's own front end.
    ///
    /// The executable takes "title tigerlily &lt;scenario&gt;" on its command line, which skips
    /// the menus. Only the direct route can carry it: going through Battle.net means the
    /// client builds the command line, not us.
    /// </summary>
    public static string? Scenario { get; set; }

    public static bool TryLaunch(
        GameInstall game, LaunchMethod method, out string error, Action<string>? progress = null)
    {
        error = "";
        LastLaunchDetail = "";

        var executable = game.ExecutablePath;
        if (!File.Exists(executable))
        {
            error = $"{executable} does not exist.";
            return false;
        }

        if (IsElevated() && !CanStartAsDesktopUser(out var why))
        {
            error =
                "The studio is running as administrator, and Windows would give the game the same " +
                "administrator rights. The game cannot reach your Battle.net session that way, so it " +
                $"shows \"Lost connection to Battle.net\".\n\nReason: {why}\n\n" +
                "The mod is applied. Start Warcraft II from Battle.net as you normally would " +
                "and it will run with the mod.";
            return false;
        }

        // Asking for a mission means starting the executable ourselves: going through
        // Battle.net means the client writes the command line, and a scenario cannot be
        // put on it. So a chosen mission takes the direct route whatever the setting says.
        return method == LaunchMethod.Direct || !string.IsNullOrWhiteSpace(Scenario)
            ? TryLaunchDirectly(game, out error)
            : TryLaunchViaClient(out error, progress);
    }

    private static bool TryLaunchViaClient(out string error, Action<string>? progress)
    {
        error = "";

        var client = FindBattleNet();
        if (client is null)
        {
            error = "Battle.net was not found on this machine. Switch the launch method to " +
                    "\"directly\" in Settings, or start the game from Battle.net yourself.";
            return false;
        }

        if (!IsBattleNetRunning)
        {
            progress?.Invoke("Starting Battle.net\u2026");

            var launcher = FindBattleNetLauncher() ?? client;
            if (!TryStart(launcher, "", Path.GetDirectoryName(launcher)!, out var clientError))
            {
                error = clientError + "\n\nStart Battle.net yourself, then press Play again. " +
                        "The mod is already applied.";
                return false;
            }

            if (!WaitFor(() => IsBattleNetRunning, TimeSpan.FromSeconds(90),
                    progress, "Waiting for Battle.net\u2026 sign in if it asks."))
            {
                error = "Battle.net did not come up within 90 seconds.\n\n" +
                        "Once it is running and signed in, press Play again. The mod is already applied.";
                return false;
            }

            // The client needs a moment after appearing before it will accept commands.
            Thread.Sleep(5000);
        }

        progress?.Invoke("Asking Battle.net to start the game\u2026");

        // Uppercase uid: lowercase is accepted and ignored.
        if (!TryStart(client, $"--exec=\"launch {ProductCode.ToUpperInvariant()}\"",
                Path.GetDirectoryName(client)!, out var execError))
        {
            error = execError;
            return false;
        }

        // Confirm the game actually appeared rather than reporting success because a
        // process started — asking the client to launch always "succeeds" by itself.
        if (!WaitFor(() => IsGameRunning, TimeSpan.FromSeconds(60), progress, "Waiting for the game\u2026"))
        {
            error = "Battle.net was asked to start the game but it has not appeared.\n\n" +
                    "If Battle.net is showing a sign-in or an update, finish that and press Play again. " +
                    "The mod is already applied.";
            return false;
        }

        LastLaunchDetail = "launched by Battle.net";
        return true;
    }

    private static bool TryLaunchDirectly(GameInstall game, out string error)
    {
        // The command line the client itself uses, for when going through it is not wanted.
        var arguments = $"-uid {ProductCode}";

        if (!string.IsNullOrWhiteSpace(Scenario))
            arguments += $" title tigerlily {Scenario}";

        if (!TryStart(game.ExecutablePath, arguments, game.Root, out error)) return false;

        LastLaunchDetail = string.IsNullOrWhiteSpace(Scenario)
            ? "started directly"
            : $"started directly, into {Scenario}";
        return true;
    }

    public static bool IsGameRunning
        => Process.GetProcessesByName("Warcraft II").Length > 0;

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout, Action<string>? progress, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        var announced = false;

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;

            if (!announced)
            {
                progress?.Invoke(message);
                announced = true;
            }
            Thread.Sleep(500);
        }
        return false;
    }

    /// <summary>
    /// Checks that Explorer can be asked to launch on our behalf, without launching
    /// anything. Everything about this route is silent when it fails, so it is proven
    /// up front rather than discovered as a game that will not connect.
    /// </summary>
    public static bool CanStartAsDesktopUser(out string why)
    {
        why = "";

        if (!IsBuiltInComSupported())
        {
            why = "COM support is unavailable in this build, so Explorer cannot be asked to launch it.";
            return false;
        }

        object? shell = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                why = "Shell.Application is not registered.";
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                why = "Explorer's shell object could not be created. Explorer may not be running.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            why = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }

    private static bool IsBuiltInComSupported()
    {
        // The runtime turns IDispatch late binding off in some publish configurations.
        // AppContext is how that switch is exposed.
        if (AppContext.TryGetSwitch("System.Runtime.InteropServices.BuiltInComInterop.IsSupported", out var enabled))
            return enabled;
        return OperatingSystem.IsWindows();
    }

    /// <summary>
    /// Starts something, dropping administrator rights when we hold them.
    ///
    /// There is deliberately no fallback to an elevated start: that is what produced a
    /// game and a second Battle.net client both running as administrator, unable to reach
    /// the user's real session. Failing loudly is the useful behaviour.
    /// </summary>
    private static bool TryStart(string file, string arguments, string workingDirectory, out string error)
    {
        error = "";

        if (IsElevated())
        {
            if (TryStartAsDesktopUser(file, arguments, workingDirectory)) return true;
            error = "Could not hand the launch to Explorer, so it would have run as administrator.";
            return false;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo(file)
            {
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = workingDirectory,
            });

            if (process is null)
            {
                error = "Windows did not start a process for " + file;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Asks Explorer to do the launching, so the new process gets the desktop user's
    /// token rather than this process's administrator one. Explorer exposes this through
    /// the Shell.Application COM object; there is no plain Win32 call that does it.
    ///
    /// Invoked by reflection rather than `dynamic` to keep the binding explicit and avoid
    /// dragging in the C# runtime binder for one call.
    /// </summary>
    private static bool TryStartAsDesktopUser(string file, string arguments, string workingDirectory)
    {
        object? shell = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return false;

            shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;

            // ShellExecute(File, Arguments, Directory, Operation, Show)
            shellType.InvokeMember(
                "ShellExecute",
                BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { file, arguments, workingDirectory, "open", 1 });

            return true;
        }
        catch
        {
            // Explorer may not be running, or the call may be blocked. The caller reports
            // that rather than launching something that cannot sign in.
            return false;
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }
}
