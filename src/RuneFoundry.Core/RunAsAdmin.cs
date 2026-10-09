using Microsoft.Win32;

namespace RuneFoundry.Core;

/// <summary>
/// Finds "Run this program as an administrator" on the game and the Battle.net client.
///
/// RuneFoundry runs as the desktop user, and Windows will not let an ordinary process read
/// an elevated one. With that box ticked on Warcraft II.exe, or on the client that starts
/// it, the mod's files still apply but its rules cannot be watched and its campaign values
/// cannot be set. With it ticked on the client, asking the client to start the game can
/// also stall. The setting is invisible from inside the program, so it is looked up where
/// the Compatibility tab stores it and named, rather than leaving a player to guess.
/// </summary>
public static class RunAsAdmin
{
    private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    /// <summary>
    /// Whether a Layers value turns elevation on. The value is a space-separated list of
    /// flags, optionally led by "~" or "$", for example "~ RUNASADMIN HIGHDPIAWARE".
    /// </summary>
    public static bool RequestsElevation(string? layers) =>
        layers is not null
        && layers.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                 .Any(flag => flag.Equals("RUNASADMIN", StringComparison.OrdinalIgnoreCase));

    /// <summary>The executables among <paramref name="paths"/> that are set to run as administrator.</summary>
    public static IReadOnlyList<string> Flagged(IEnumerable<string> paths)
    {
        var wanted = paths.Select(Path.GetFullPath).ToList();
        var found = new List<string>();

        // The Compatibility tab writes per-user settings to HKCU, and "Change settings for
        // all users" to HKLM. Either is enough to elevate.
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(LayersKey);
                if (key is null) continue;

                foreach (var name in key.GetValueNames())
                {
                    var match = wanted.FirstOrDefault(path => path.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (match is null || found.Contains(match, StringComparer.OrdinalIgnoreCase)) continue;
                    if (RequestsElevation(key.GetValue(name) as string)) found.Add(match);
                }
            }
            catch (Exception)
            {
                // A key this account may not read says nothing either way.
            }
        }
        return found;
    }

    /// <summary>The game and Battle.net executables, wherever they are on this machine.</summary>
    public static IReadOnlyList<string> Flagged(GameInstall game)
    {
        var paths = new List<string> { game.ExecutablePath };

        if (GameLauncher.FindBattleNet() is { } client)
        {
            paths.Add(client);
            if (GameLauncher.FindBattleNetLauncher() is { } launcher) paths.Add(launcher);
        }
        return Flagged(paths);
    }

    /// <summary>
    /// What to tell a player when the game could not be opened, naming the programs set to
    /// run as administrator when there are any. Empty when none are, so callers can append it.
    /// </summary>
    public static string Advice(GameInstall game)
    {
        var flagged = Flagged(game);
        if (flagged.Count == 0) return "";

        var names = string.Join(" and ", flagged.Select(Path.GetFileName));
        return $"{names} {(flagged.Count == 1 ? "is" : "are")} set to run as administrator, so Windows "
               + "will not let RuneFoundry read the game. Untick \"Run this program as an administrator\" "
               + "in the Compatibility tab of their Properties (also under \"Change settings for all users\"), "
               + "close Battle.net completely, and press Play again.";
    }

    /// <summary>
    /// The same advice when the game is known to be unreadable but no setting was found: the
    /// game can also be elevated by a client started as administrator some other way.
    /// </summary>
    public static string AdviceForDeniedAccess(GameInstall game)
    {
        var advice = Advice(game);
        return advice.Length > 0
            ? advice
            : "Windows refused access to the game, which usually means it is running as administrator. "
              + "Check that neither Warcraft II.exe nor Battle.net.exe has \"Run this program as an "
              + "administrator\" ticked in its Compatibility tab, close Battle.net completely, and press Play again.";
    }
}
