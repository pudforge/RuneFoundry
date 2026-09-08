namespace RuneFoundry.Core.Formats;

/// <summary>
/// One replaceable picture belonging to a campaign.
///
/// Two shapes. Most are a frame inside a shared sheet, where replacing means drawing into a
/// rectangle and leaving the rest of the sheet alone. The act backgrounds are whole files of
/// their own, where replacing means replacing the file — so <see cref="Frame"/> is null and
/// <see cref="Atlas"/> with it.
/// </summary>
/// <param name="Label">What it is, in the words the screen uses.</param>
/// <param name="Image">The file: either a sheet to draw into, or the picture itself.</param>
/// <param name="Frame">Its name in the sheet, or null when the file *is* the picture.</param>
/// <param name="Atlas">The JSON describing the sheet, or null for a whole file.</param>
public sealed record CampaignPicture(string Label, string Image, string? Frame = null, string? Atlas = null)
{
    /// <summary>Whether this is a rectangle of a shared sheet rather than a file of its own.</summary>
    public bool InSheet => Frame is not null && Atlas is not null;
}

/// <summary>
/// The pictures the game shows for a campaign: the sketch on the campaign chooser, and the
/// map behind each of its four acts.
///
/// Both live in sheets under <c>Data/skins</c>, and both name their frames after the campaign,
/// which is why this is a mapping rather than a search. The act sheet uses the campaign's own
/// id (<c>human_act1</c>, <c>xorc_act4</c>). The sketch sheet numbers the two campaigns of a
/// race instead, and not consistently between the races. See <see cref="SketchStem"/>.
///
/// Both sheets are plain RGBA, so replacing a frame is drawing into them and writing them back
/// at the size they arrived — no palette to preserve, unlike the terrain sheets.
/// </summary>
public static class CampaignArt
{
    public const string SketchAtlas = "skins/Modern_Graphics.json";
    public const string SketchImage = "skins/Modern_Graphics.png";

    public const string MapsAtlas = "skins/Maps.json";
    public const string MapsImage = "skins/Maps.png";

    /// <summary>Acts per campaign. All four campaigns have four.</summary>
    public const int Acts = 4;

    /// <summary>
    /// The sketch sheet's name for a campaign.
    ///
    /// The numbering is not what it looks like. Human runs the obvious way: <c>sketch_human1</c>
    /// is Lothar over fallen orcs, which is Tides of Darkness, and <c>sketch_human2</c> is
    /// Khadgar casting, which is Beyond the Dark Portal. The orc pair is the other way round.
    /// <c>sketch_orc1</c> is an orc enthroned, which is the expansion, and <c>sketch_orc2</c> is
    /// the axe-bearing warrior of Tides of Darkness.
    ///
    /// Established by looking at the eight frames, after this was first written from the
    /// numbering alone and got the orc pair backwards. Nothing in the executable names a
    /// campaign beside a frame: each sketch is drawn by its own small function, and those
    /// sit in a table ordered by screen layout rather than by campaign.
    /// </summary>
    private static string? SketchStem(string campaignId) => campaignId switch
    {
        "human" => "sketch_human1",
        "xhuman" => "sketch_human2",
        "orc" => "sketch_orc2",
        "xorc" => "sketch_orc1",
        _ => null,
    };

    /// <summary>The picture on the campaign chooser, or null for a campaign with none.</summary>
    public static CampaignPicture? Sketch(string campaignId) =>
        SketchStem(campaignId) is { } stem
            ? new CampaignPicture("Campaign picture", SketchImage, stem, SketchAtlas)
            : null;

    /// <summary>
    /// The same picture as the pointer rests on it — a different drawing in the game's art,
    /// not a tint, which is why replacing one and not the other is visible.
    /// </summary>
    public static CampaignPicture? SketchHovered(string campaignId) =>
        SketchStem(campaignId) is { } stem
            ? new CampaignPicture("Campaign picture (pointed at)", SketchImage,
                                  stem + "_hovered", SketchAtlas)
            : null;

    /// <summary>
    /// Which act each mission belongs to, as the game decides it.
    ///
    /// Read from the table at <c>0x8caec0</c>, which the act screen looks up at
    /// <c>0x5470a0</c> to pick the title card and the act name. Fourteen entries per
    /// campaign, indexed by the mission within it.
    ///
    /// The routine indexes that table by <c>expansion + race * 2</c>, so it holds four rows.
    /// Two of them are duplicates: the acts fall in the same place for the human and orc
    /// campaigns of a release, and differ between the releases. Hence two rows here rather
    /// than four.
    ///
    /// Worth having rather than guessing, because the split is not even. Tides of Darkness
    /// gives act 1 four missions and act 2 only three.
    /// </summary>
    private static readonly int[] TidesActs = { 1, 1, 1, 1, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4 };
    private static readonly int[] PortalActs = { 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 4, 4 };

    /// <summary>Missions per campaign. Every campaign has fourteen.</summary>
    public const int Missions = 14;

    private static int[] ActTable(string campaignId) =>
        campaignId.StartsWith("x", StringComparison.Ordinal) ? PortalActs : TidesActs;

    /// <summary>
    /// The act a mission belongs to, counting missions and acts from 1. Zero when the
    /// mission is outside the campaign.
    /// </summary>
    public static int ActOf(string campaignId, int mission) =>
        mission >= 1 && mission <= Missions ? ActTable(campaignId)[mission - 1] : 0;

    /// <summary>The missions in one act, in order. Empty for an act that has none.</summary>
    public static IReadOnlyList<int> MissionsInAct(string campaignId, int act)
    {
        var table = ActTable(campaignId);

        return Enumerable.Range(1, Missions)
            .Where(mission => table[mission - 1] == act)
            .ToArray();
    }

    /// <summary>
    /// How an act's span reads on screen: "missions 5 to 7", or "mission 3" for an act of one.
    /// </summary>
    public static string ActRange(string campaignId, int act)
    {
        var missions = MissionsInAct(campaignId, act);
        if (missions.Count == 0) return "no missions";

        return missions.Count == 1
            ? $"mission {missions[0]}"
            : $"missions {missions[0]} to {missions[^1]}";
    }

    /// <summary>
    /// The language-file key holding an act's name — "The Shores of Lordaeron" for the first
    /// human act. Note the underscore before the number, which the frame names do not have:
    /// the map is <c>human_act1</c> and its title is <c>human_act_1</c>.
    /// </summary>
    public static string ActTitleKey(string campaignId, int act) => $"{campaignId}_act_{act}";

    /// <summary>
    /// The card an act shows in the campaign's mission list — the drawn map of the region the
    /// act is fought over. Acts are numbered from 1.
    /// </summary>
    public static CampaignPicture? Act(string campaignId, int act) =>
        act is >= 1 and <= Acts
            ? new CampaignPicture("Campaign mission list card", MapsImage,
                                  $"{campaignId}_act{act}", MapsAtlas)
            : null;

    /// <summary>
    /// The full-screen card the game holds on when an act begins, with the act's title
    /// lettered over it.
    ///
    /// Loose files rather than a sheet, and named on a different scheme again: Tides of
    /// Darkness uses <c>umaact1</c> and <c>orcact1</c>, the expansion <c>2xhact1</c> and
    /// <c>2xoact1</c>. All sixteen are 3840x2160 RGB — no alpha, unlike the sheets.
    /// </summary>
    private static string? BackgroundStem(string campaignId) => campaignId switch
    {
        "human" => "umaact",
        "orc" => "orcact",
        "xhuman" => "2xhact",
        "xorc" => "2xoact",
        _ => null,
    };

    public static CampaignPicture? ActBackground(string campaignId, int act) =>
        act is >= 1 and <= Acts && BackgroundStem(campaignId) is { } stem
            ? new CampaignPicture("Act title card", $"Backgrounds/{stem}{act}.png")
            : null;

    /// <summary>
    /// The picture shown while an act's briefing is read out.
    ///
    /// Only eight of these ship — <c>splash_briefing_human01</c> to <c>04</c> and the orc
    /// four — so they are per race and per act, and the two campaigns of a race share them.
    /// Replacing the human act 1 splash changes it for Tides of Darkness and Beyond the Dark
    /// Portal alike, which the screen says out loud.
    /// </summary>
    public static CampaignPicture? Briefing(string campaignId, int act)
    {
        if (act is < 1 or > Acts) return null;

        var race = campaignId switch
        {
            "human" or "xhuman" => "human",
            "orc" or "xorc" => "orc",
            _ => null,
        };

        return race is null
            ? null
            : new CampaignPicture("Briefing splash", $"Backgrounds/splash_briefing_{race}{act:00}.png");
    }

    /// <summary>Everything replaceable for one campaign, in the order the screen shows it.</summary>
    public static IReadOnlyList<CampaignPicture> For(string campaignId)
    {
        var all = new List<CampaignPicture>();

        if (Sketch(campaignId) is { } sketch) all.Add(sketch);
        if (SketchHovered(campaignId) is { } hovered) all.Add(hovered);

        for (var act = 1; act <= Acts; act++)
        {
            if (Act(campaignId, act) is { } map) all.Add(map);
            if (ActBackground(campaignId, act) is { } title) all.Add(title);
            if (Briefing(campaignId, act) is { } briefing) all.Add(briefing);
        }

        return all;
    }
}
