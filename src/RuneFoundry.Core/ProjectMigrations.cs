using System.Text.Json.Nodes;

namespace RuneFoundry.Core;

/// <summary>
/// One structural change to the .w2proj format, and how to bring an older file across it.
///
/// <paramref name="Introduced"/> is the release that first writes the new shape: a project
/// stamped older than it gets the step. <paramref name="Summary"/> is shown to the author
/// when they are asked to upgrade, so it says what changes in their words, not the code's.
/// <paramref name="Apply"/> edits the raw JSON before it is read into a <see cref="ModProject"/>,
/// which is what lets a step rename or restructure a field that no longer has a property.
/// </summary>
public sealed record ProjectMigration(string Introduced, string Summary, Action<JsonObject> Apply);

/// <summary>What opening a project file would involve, found before anything is written.</summary>
public sealed record ProjectUpgradeCheck(
    string FileVersion,
    bool IsNewerThanEditor,
    bool NeedsUpgrade,
    IReadOnlyList<ProjectMigration> Steps)
{
    /// <summary>How the file's release reads to a person: "0.6.2 or earlier" for no stamp.</summary>
    public string FileVersionText =>
        string.IsNullOrWhiteSpace(FileVersion) ? "0.6.2 or earlier" : FileVersion;
}

/// <summary>
/// Every structural change the project format has had, oldest first.
///
/// Projects from any earlier release must keep opening. Changing the shape of the
/// .w2proj — renaming, moving or reinterpreting a field — means adding a step here, a
/// fixture test with a project file as the previous release wrote it, and raising
/// &lt;Version&gt; in Directory.Build.props so the stamp tells the two apart. Adding an
/// optional field with a sensible default needs no step.
/// </summary>
public static class ProjectMigrations
{
    public static IReadOnlyList<ProjectMigration> All { get; } = new ProjectMigration[]
    {
        // Example of the shape, for the first real step:
        //
        // new("0.7.0", "Victory rules move under each mission instead of beside them.",
        //     json => { ... }),
    };

    /// <summary>Reads a project's stamp and works out what upgrading it would take.</summary>
    public static ProjectUpgradeCheck Check(JsonObject json, IReadOnlyList<ProjectMigration>? steps = null)
    {
        var fileVersion = json[ModProject.EditorVersionKey]?.GetValue<string>() ?? "";
        var newer = AppVersion.IsNewerThanCurrent(fileVersion);
        var pending = newer
            ? Array.Empty<ProjectMigration>()
            : (steps ?? All).Where(s => AppVersion.Compare(fileVersion, s.Introduced) < 0).ToArray();

        return new ProjectUpgradeCheck(
            fileVersion,
            IsNewerThanEditor: newer,
            NeedsUpgrade: !newer && AppVersion.Compare(fileVersion, AppVersion.Current) < 0,
            Steps: pending);
    }

    /// <summary>Runs the pending steps, in order, on the raw JSON.</summary>
    public static void Apply(JsonObject json, ProjectUpgradeCheck check)
    {
        foreach (var step in check.Steps) step.Apply(json);
    }
}
