namespace RuneFoundry.Core.Formats;

/// <summary>Something the test button can start: the game, or one mission of it.</summary>
public sealed record LaunchTarget(string Label, string? Scenario)
{
    /// <summary>
    /// A record prints its own fields by default, and a themed combo box does not always
    /// honour DisplayMemberPath, so the list read "LaunchTarget { Label = ... }".
    /// </summary>
    public override string ToString() => Label;
}

/// <summary>
/// The missions the executable will open straight into.
///
/// "Warcraft II.exe title tigerlily human01" skips the menus and loads that mission, which
/// turns a test of one AI script from several minutes of clicking into one launch.
/// </summary>
public static class LaunchTargets
{
    private static readonly (string Id, string Name)[] Campaigns =
    {
        ("human", "Tides of Darkness, Human"),
        ("orc", "Tides of Darkness, Orc"),
        ("xhuman", "Beyond the Dark Portal, Human"),
        ("xorc", "Beyond the Dark Portal, Orc"),
    };

    /// <summary>The game's own front end, then every mission.</summary>
    public static IReadOnlyList<LaunchTarget> All()
    {
        var list = new List<LaunchTarget> { new("Main menu", null) };

        foreach (var (id, name) in Campaigns)
            for (var mission = 1; mission <= CampaignArt.Missions; mission++)
                list.Add(new LaunchTarget($"{name} {mission}  ({id}{mission:00})",
                                          $"{id}{mission:00}"));

        return list;
    }
}
