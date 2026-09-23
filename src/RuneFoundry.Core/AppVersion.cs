using System.Reflection;

namespace RuneFoundry.Core;

/// <summary>
/// The RuneFoundry release this build is, as Directory.Build.props sets it. The loader and
/// the editor are built as one, so this assembly's version is the program's.
///
/// A mod carries the version that built it (<see cref="ModManifest.BuiltWith"/>), and the
/// loader compares the two: an older mod is always read, upgraded where its format has
/// moved on; a newer one is refused, because JSON quietly drops fields it does not know and
/// a mod read that way is only partly there.
/// </summary>
public static class AppVersion
{
    /// <summary>The full version, "0.6.2-alpha". No build metadata after a '+'.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
            return typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        // The SDK appends "+<commit>" to the informational version. It says nothing about
        // compatibility and would only make two identical releases look different.
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }

    /// <summary>
    /// Compares two versions on their release number only: "0.6.2-alpha" and "0.6.2" are
    /// the same release for this purpose, since a pre-release tag never changed a format.
    /// Anything that does not parse sorts as 0.0.0, so a mangled field reads as old, and
    /// old is the case that is always accepted.
    /// </summary>
    public static int Compare(string? a, string? b) => Parse(a).CompareTo(Parse(b));

    /// <summary>Whether a mod stamped with <paramref name="builtWith"/> is newer than this build.</summary>
    public static bool IsNewerThanCurrent(string? builtWith) => Compare(builtWith, Current) > 0;

    private static Version Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new Version(0, 0, 0);
        var core = text.Trim();
        var cut = core.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) core = core[..cut];
        if (!Version.TryParse(core, out var parsed)) return new Version(0, 0, 0);
        return new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
    }
}
