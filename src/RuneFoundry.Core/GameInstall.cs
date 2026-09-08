using Microsoft.Win32;

namespace RuneFoundry.Core;

/// <summary>
/// A located Warcraft II Remastered installation.
///
/// The moddable content is loose files under &lt;root&gt;\x86\Data — 1,900-odd png, pcx,
/// grp, pud, wav, json, tbl and bin files. The &lt;root&gt;\Data folder is Battle.net's
/// CASC storage for the launcher and patch data; we never touch it.
/// </summary>
public sealed class GameInstall
{
    public string Root { get; }
    public string DataRoot => Path.Combine(Root, "x86", "Data");
    public string ExecutablePath => Path.Combine(Root, "x86", "Warcraft II.exe");

    private GameInstall(string root) => Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>Accepts either the install root or the x86\Data folder itself, and normalises to the root.</summary>
    public static bool TryOpen(string path, out GameInstall? install, out string error)
    {
        install = null;
        error = "";
        if (string.IsNullOrWhiteSpace(path)) { error = "No folder given."; return false; }

        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        // Tolerate the user pointing at x86, or x86\Data, instead of the root.
        for (var i = 0; i < 2; i++)
        {
            if (Directory.Exists(Path.Combine(candidate, "x86", "Data"))) break;
            var parent = Path.GetDirectoryName(candidate);
            if (parent is null) break;
            candidate = parent;
        }

        if (!Directory.Exists(candidate)) { error = $"Folder does not exist: {candidate}"; return false; }
        if (!Directory.Exists(Path.Combine(candidate, "x86", "Data")))
        {
            error = "That folder is not a Warcraft II Remastered install (no x86\\Data inside).";
            return false;
        }
        if (!File.Exists(Path.Combine(candidate, "x86", "Warcraft II.exe")))
        {
            error = "Found x86\\Data but no x86\\Warcraft II.exe. Is the install complete?";
            return false;
        }

        install = new GameInstall(candidate);
        return true;
    }

    private static readonly string[] CommonPaths =
    {
        @"C:\Program Files (x86)\Warcraft II Remastered",
        @"C:\Program Files\Warcraft II Remastered",
    };

    /// <summary>Best-effort autodetect: Battle.net's uninstall registry keys first, then the usual paths.</summary>
    public static GameInstall? Detect()
    {
        foreach (var root in RegistryCandidates().Concat(CommonPaths))
        {
            if (TryOpen(root, out var install, out _)) return install;
        }
        return null;
    }

    private static IEnumerable<string> RegistryCandidates()
    {
        var keys = new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, subKey) in keys)
        {
            RegistryKey? uninstall = null;
            try
            {
                uninstall = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(subKey);
            }
            catch
            {
                // Reading the registry is a convenience; a locked-down machine just falls back to CommonPaths.
            }
            if (uninstall is null) continue;

            using (uninstall)
            {
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    string? location = null;
                    try
                    {
                        using var app = uninstall.OpenSubKey(name);
                        var display = app?.GetValue("DisplayName") as string;
                        if (display is null || !display.Contains("Warcraft II", StringComparison.OrdinalIgnoreCase)) continue;
                        location = app?.GetValue("InstallLocation") as string;
                    }
                    catch
                    {
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(location)) yield return location!;
                }
            }
        }
    }

    /// <summary>Relative-to-DataRoot paths of every loose game file, in canonical form.</summary>
    public IEnumerable<string> EnumerateDataFiles()
    {
        var root = DataRoot;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            yield return PathSafety.Normalize(Path.GetRelativePath(root, file));
        }
    }

    public string ResolveDataPath(string relativePath)
    {
        if (!PathSafety.TryResolveUnder(DataRoot, relativePath, out var full, out var error))
            throw new InvalidOperationException($"Unsafe game path '{relativePath}': {error}");
        return full;
    }

    /// <summary>
    /// Writing into Program Files needs elevation. We test for real rather than
    /// inspecting the token, because ACLs on a given install can go either way.
    /// </summary>
    public bool CanWrite(out string error)
    {
        error = "";
        var probe = Path.Combine(DataRoot, $".war2mod-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "No write access to the game folder. Run RuneFoundry as administrator.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Cannot write to the game folder: {ex.Message}";
            return false;
        }
    }
}
