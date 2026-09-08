using RuneFoundry.Core.Formats;

namespace RuneFoundry.Core;

/// <summary>One page of a mission briefing: the text the game shows, and the voice line.</summary>
public sealed class BriefingPage
{
    public required int Number { get; init; }

    /// <summary>Its key in the language file, e.g. <c>human_1_2</c>.</summary>
    public required string TextKey { get; init; }

    /// <summary>The recording, as a game-relative path, e.g. <c>Speech/Hum_1_2.wav</c>.</summary>
    public required string SpeechPath { get; init; }
}

/// <summary>
/// One mission, gathered from the three places the game keeps it: the map, the language
/// file and the speech folder.
/// </summary>
public sealed class CampaignMission
{
    public required Campaign Campaign { get; init; }
    public required int Number { get; init; }

    /// <summary>The map, e.g. <c>Campaign/Human/HUMAN01.PUD</c>.</summary>
    public required string MapPath { get; init; }

    public required IReadOnlyList<BriefingPage> Pages { get; init; }

    /// <summary>
    /// This mission's index in the executable's 52-slot campaign table, which interleaves
    /// the two races: Human01, Orc01, Human02, Orc02 … then the expansion the same way.
    /// </summary>
    public int ExeSlot => Campaign.ExeSlotBase + (Number - 1) * 2;

    public string NameKey => $"{Campaign.KeyPrefix}_{Number}_name";
    public string ObjectivesKey => $"{Campaign.KeyPrefix}_{Number}_objectives";

    /// <summary>The one-paragraph recap shown on the mission list, not in the briefing.</summary>
    public string SummaryKey => $"{Campaign.KeyPrefix}_{Number}_summary";

    /// <summary>Every game file this mission is made of, for counting a mod's changes.</summary>
    public IEnumerable<string> Files()
    {
        yield return MapPath;
        foreach (var page in Pages) yield return page.SpeechPath;
    }

    /// <summary>
    /// Every string-table entry this mission owns.
    ///
    /// The sibling of <see cref="Files"/>, and kept beside it for the same reason: anything
    /// that needs to know what a mission is made of should ask here rather than keep its
    /// own list that quietly drifts.
    /// </summary>
    public IEnumerable<string> TextKeys()
    {
        yield return NameKey;
        yield return ObjectivesKey;
        yield return SummaryKey;
        foreach (var page in Pages) yield return page.TextKey;
    }

    public override string ToString() => $"{Campaign.Title} {Number}";
}

/// <summary>
/// A campaign, and how to find its parts.
///
/// The four campaigns each name their files a little differently — the expansion drops the
/// underscore from its speech files and prefixes its maps with "2X" — which is exactly the
/// sort of thing nobody should have to work out to replace a mission.
/// </summary>
public sealed class Campaign
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    /// <summary>What the game calls it: the campaign this belongs to, for grouping.</summary>
    public required string Collection { get; init; }

    /// <summary>The prefix its keys share in the language files, e.g. <c>xhuman</c>.</summary>
    public required string KeyPrefix { get; init; }

    public required int MissionCount { get; init; }

    /// <summary>The most briefing pages any of its missions has.</summary>
    public required int MaxPages { get; init; }

    /// <summary>Where this campaign's mission 1 sits in the executable's 52-slot table.</summary>
    public required int ExeSlotBase { get; init; }

    public required string MapFormat { private get; init; }
    public required string SpeechFormat { private get; init; }

    public string MapPath(int mission) => string.Format(MapFormat, mission);
    public string SpeechPath(int mission, int page) => string.Format(SpeechFormat, mission, page);

    /// <summary>
    /// The pages shown after the last mission is won, before the ending movie.
    ///
    /// Built at <c>0x52ab81</c>, which assembles the key as the campaign's own prefix plus
    /// <c>_finale_</c> plus a page number counting from one, then walks up until a key is
    /// missing. So <c>human_finale_1</c>, and <c>xorc_finale_1</c> to <c>_3</c>.
    ///
    /// The recordings are named on a different scheme again, and not the mission one:
    /// <c>Speech/Humvict.wav</c> for Tides of Darkness, where the expansion numbers them
    /// <c>Speech/War2x/Humvict1.wav</c> and up. The page counts and the recording counts
    /// agree exactly, one to one, which is what says they are pages of one screen rather
    /// than separate things.
    /// </summary>
    public IReadOnlyList<BriefingPage> Epilogue(GameStrings? strings)
    {
        var pages = new List<BriefingPage>();

        for (var page = 1; ; page++)
        {
            var key = $"{KeyPrefix}_finale_{page}";
            if (strings is null || !strings.Has(key)) break;

            pages.Add(new BriefingPage
            {
                Number = page,
                TextKey = key,
                SpeechPath = EpilogueSpeechPath(page),
            });

            // Nothing in the game reads more than three, and an unbounded loop over a file
            // a mod can write is not a loop to leave open.
            if (page >= MaxEpiloguePages) break;
        }

        return pages;
    }

    /// <summary>More than the game ships, and few enough to stop a bad language file looping.</summary>
    private const int MaxEpiloguePages = 8;

    /// <summary>
    /// The recording for one epilogue page.
    ///
    /// Tides of Darkness has a single unnumbered file per race; the expansion numbers them
    /// from one. Both are spelled out here rather than derived, because they are two schemes
    /// rather than one with a parameter.
    /// </summary>
    public string EpilogueSpeechPath(int page)
    {
        var race = KeyPrefix.EndsWith("orc", StringComparison.Ordinal) ? "Orc" : "Hum";

        return KeyPrefix.StartsWith("x", StringComparison.Ordinal)
            ? $"Speech/War2x/{race}vict{page}.wav"
            : $"Speech/{race}vict.wav";
    }

    /// <summary>
    /// The missions, with the briefing pages each one actually has.
    ///
    /// Which pages exist is read from the language file rather than assumed: Human 6 has
    /// one page where Human 5 has two, and offering an empty second page would be inviting
    /// someone to write text the game will not show.
    /// </summary>
    public IReadOnlyList<CampaignMission> Missions(GameStrings? strings)
    {
        var missions = new List<CampaignMission>(MissionCount);

        for (var number = 1; number <= MissionCount; number++)
        {
            var pages = new List<BriefingPage>();

            for (var page = 1; page <= MaxPages; page++)
            {
                var key = $"{KeyPrefix}_{number}_{page}";
                if (strings is not null && !strings.Has(key)) continue;

                pages.Add(new BriefingPage
                {
                    Number = page,
                    TextKey = key,
                    SpeechPath = SpeechPath(number, page),
                });
            }

            missions.Add(new CampaignMission
            {
                Campaign = this,
                Number = number,
                MapPath = MapPath(number),
                Pages = pages,
            });
        }

        return missions;
    }

    public static readonly IReadOnlyList<Campaign> All = new[]
    {
        new Campaign
        {
            Id = "human",
            ExeSlotBase = 0,
            Title = "Human",
            Collection = "Tides of Darkness",
            KeyPrefix = "human",
            MissionCount = 14,
            MaxPages = 2,
            MapFormat = "Campaign/Human/HUMAN{0:00}.PUD",
            SpeechFormat = "Speech/Hum_{0}_{1}.wav",
        },
        new Campaign
        {
            Id = "orc",
            ExeSlotBase = 1,
            Title = "Orc",
            Collection = "Tides of Darkness",
            KeyPrefix = "orc",
            MissionCount = 14,
            MaxPages = 2,
            MapFormat = "Campaign/Orc/ORC{0:00}.PUD",
            SpeechFormat = "Speech/Orc_{0}_{1}.wav",
        },
        new Campaign
        {
            Id = "xhuman",
            ExeSlotBase = 28,
            Title = "Human",
            Collection = "Beyond the Dark Portal",
            KeyPrefix = "xhuman",
            MissionCount = 12,
            MaxPages = 3,
            MapFormat = "Campaign/XHuman/2XHUM{0:00}.PUD",
            SpeechFormat = "Speech/War2x/Hum{0}_{1}.wav",
        },
        new Campaign
        {
            Id = "xorc",
            ExeSlotBase = 29,
            Title = "Orc",
            Collection = "Beyond the Dark Portal",
            KeyPrefix = "xorc",
            MissionCount = 12,
            MaxPages = 3,
            MapFormat = "Campaign/XOrc/2XORC{0:00}.PUD",
            SpeechFormat = "Speech/War2x/Orc{0}_{1}.wav",
        },
    };

    public override string ToString() => $"{Collection}: {Title}";
}

/// <summary>The language files the campaign text lives in.</summary>
public static class CampaignText
{
    public const string Folder = "Strings";

    /// <summary>The one to edit unless told otherwise.</summary>
    public const string Default = "Strings/enUS.json";

    /// <summary>
    /// The language files present in an install. Credits is left out: it is a strings file
    /// by shape, but it holds no campaign text and editing it here would be an accident.
    /// </summary>
    public static IReadOnlyList<string> Available(GameInstall game)
    {
        var folder = Path.Combine(game.DataRoot, Folder);
        if (!Directory.Exists(folder)) return Array.Empty<string>();

        return Directory.EnumerateFiles(folder, "*.json")
            .Select(Path.GetFileName)
            .Where(name => name is not null && !name.Equals("credits.json", StringComparison.OrdinalIgnoreCase))
            .Select(name => $"{Folder}/{name}")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// One scenario rule: how a mission is won, how it is lost, and anything else it changes.
///
/// Calling these "victory conditions" undersells them. Each id is a bundle — the win test,
/// a defeat clause the same function enforces, and in eight cases a change the dispatcher
/// makes to the map before play starts: turning player 4's Peasants into Attack Peasants,
/// converting Knights to Paladins and granting their upgrades, adding or removing units
/// from the rescue pool, recharging Runestones. Choosing an id chooses all of it, and
/// <see cref="Also"/> is where the part nobody expects gets said out loud.
///
/// These are the game's own conditions, not ours. Every campaign slot names one by id, and
/// pointing a slot at a different id makes that mission use that condition — the game then
/// runs its own code to check it. So the whole list below is available to any mission, for
/// free, and nothing new has to be written to offer it.
/// </summary>
public sealed record CampaignObjective(
    int Id, string Label, string? Lose = null, string? Also = null, string? Requires = null)
{
    public override string ToString() => Label;

    /// <summary>
    /// The defeat every condition enforces, before its own clause. Stated on each rule
    /// rather than once in a corner: someone comparing two rules is comparing how they are
    /// lost as much as how they are won, and a line that is missing reads as "this one
    /// cannot be lost that way".
    /// </summary>
    public const string UniversalLose = "you have no units and no producing building";

    /// <summary>
    /// The rule's fine print, one point per line.
    ///
    /// A list rather than a sentence: two defeat clauses joined by "or" read as one long
    /// condition, and the whole point of showing them is that someone can compare two
    /// rules at a glance.
    /// </summary>
    public IReadOnlyList<string> Details
    {
        get
        {
            var lines = new List<string> { $"Lose if {UniversalLose}" };
            if (Lose is { Length: > 0 }) lines.Add($"Lose if {Lose}");
            if (Also is { Length: > 0 }) lines.Add($"Also {Also}");
            return lines;
        }
    }

    public bool HasLose => Lose is { Length: > 0 };
    public bool HasAlso => Also is { Length: > 0 };
    public bool HasRequires => Requires is { Length: > 0 };
}

/// <summary>
/// The conditions decoded so far, from W2R-RE-NOTES.md §5f.
///
/// Only the decoded ones are offered. The rest are real and selectable, but describing one
/// wrongly would be worse than leaving it out — this text is what somebody designs a
/// mission against.
///
/// <c>Requires</c> is the part that is easy to get wrong: a condition tests what is on the
/// map, so pointing a mission at "destroy the Dark Portal" when the map has no Portal
/// gives a mission that can never be won, and pointing it at "leave no enemy Refinery"
/// when the map has none gives one that is won on the first tick.
/// </summary>
public static class CampaignObjectiveNames
{
    /// <summary>
    /// Every label is an instruction to the player, verb first — Destroy, Build, Bring,
    /// Capture, Rescue. The game's own phrasing is inconsistent ("leave no enemy Refinery
    /// standing" beside "destroy the Dark Portal") and these are read as a list, where the
    /// difference reads as meaning rather than as style.
    ///
    /// Where a condition names particular units, they are named here too: the types are
    /// compiled into the condition and cannot be changed, so an author needs to know which
    /// ones before choosing. What those units *are* is still theirs to decide — the type's
    /// name, art and stats are all data this loader edits.
    ///
    /// Every id the dispatcher knows is offered. Each one's win branch was read to the
    /// end of the function, not to its first return — three of them keep their win path
    /// after an early return and look like defeat-only conditions if you stop there.
    /// </summary>
    /// <summary>
    /// Every rule the dispatcher knows. <c>Label</c> is how the mission is won;
    /// <c>Lose</c> is the defeat clause this rule adds on top of the universal one;
    /// <c>Also</c> is what it changes about the map before play starts.
    ///
    /// The universal defeat is not repeated on each entry: every condition first calls the
    /// common check, which loses when you have no units and no producing building, or a
    /// producing building and under 400 gold.
    /// </summary>
    public static readonly IReadOnlyList<CampaignObjective> Choices = new[]
    {
        // --- destroy ---
        new CampaignObjective(0x0100, "Destroy all enemy units and structures"),
        new CampaignObjective(2, "Destroy all enemy Refineries",
            Requires: "an enemy Refinery, or it is won on the first tick"),
        new CampaignObjective(6, "Destroy all enemy Transports, Oil Platforms and Shipyards",
            Requires: "at least one of those, or it is won on the first tick"),
        new CampaignObjective(8, "Destroy the Dark Portal",
            Requires: "a Dark Portal owned by player 15"),
        new CampaignObjective(9, "Capture a Runestone and destroy all enemy Castles",
            Requires: "a Runestone to capture and enemy Castles"),
        new CampaignObjective(16, "Destroy all Daemons",
            Also: "turns player 1's Knights into Paladins and grants their upgrades",
            Requires: "at least one Daemon"),
        new CampaignObjective(18, "Destroy everything player 3 owns",
            Requires: "player 3 as an opponent"),
        new CampaignObjective(27, "Destroy player 5's Castles and player 4's Runestones",
            Also: "recharges every Runestone on the map to 400",
            Requires: "those buildings, owned by those players"),
        new CampaignObjective(7, "Destroy all enemy units and structures (Lothar cannot be rescued)",
            Also: "takes Lothar out of the rescue pool, so he cannot be delivered or counted"),
        new CampaignObjective(21, "Destroy all enemy units and structures (Danath must survive)",
            Lose: "Danath dies", Requires: "Danath"),
        new CampaignObjective(13, "Destroy all enemy units and structures (Teron Gorefiend must survive)",
            Lose: "Teron Gorefiend dies", Requires: "Teron Gorefiend"),
        new CampaignObjective(12, "Destroy all enemy units and structures (Kargath and Dentarg must survive)",
            Lose: "fewer than 2 of Kargath Bladefist and Dentarg are alive",
            Requires: "both of them"),
        new CampaignObjective(29, "Destroy all enemy units and structures (Khadgar, Turalyon and Alleria must survive)",
            Lose: "fewer than 3 of Khadgar, Turalyon and Alleria are alive",
            Also: "turns your and player 0's Knights into Paladins, and grants upgrades",
            Requires: "all three of them"),
        new CampaignObjective(28, "Destroy all enemy units and structures (Danath, Khadgar, Turalyon and Alleria must survive)",
            Lose: "fewer than 4 of Danath, Khadgar, Turalyon and Alleria are alive",
            Requires: "all four of them"),
        new CampaignObjective(25, "Destroy everything player 4 owns",
            Lose: "fewer than 2 of Turalyon and Danath are alive",
            Requires: "player 4 as an opponent, and both heroes"),
        new CampaignObjective(26, "Kill Deathwing",
            Lose: "fewer than 3 of Alleria, Khadgar and Kurdran are alive",
            Also: "turns your and player 3's Knights into Paladins, and grants upgrades",
            Requires: "Deathwing, and all three heroes"),
        new CampaignObjective(11, "Destroy every Death Knight and their Temple (Grom must survive)",
            Lose: "Grommash Hellscream dies",
            Requires: "Death Knights and a Temple of the Damned to destroy, and Grom"),
        new CampaignObjective(30, "Destroy the Dark Portal (Khadgar must survive)",
            Lose: "Khadgar dies",
            Requires: "a Dark Portal owned by player 15, and Khadgar"),
        new CampaignObjective(19, "Destroy all enemy units and structures, while you hold a Dark Portal",
            Lose: "no Dark Portal is left, yours or player 15's",
            Also: "adds every Dark Portal to the rescue pool",
            Requires: "a Dark Portal you own"),

        // --- build ---
        new CampaignObjective(0, "Build 4 Farms and a Barracks"),
        new CampaignObjective(1, "Build 4 Oil Platforms", Requires: "oil patches to build on"),
        new CampaignObjective(3, "Build a Castle or Fortress, or destroy all enemies",
            Also: "turns player 4's Peasants into Attack Peasants, and changes diplomacy"),
        new CampaignObjective(10, "Build a Castle and Shipyard in the marked region",
            Requires: "a Circle of Power standing on the target region"),
        new CampaignObjective(24, "Build 3 Shipyards and destroy every enemy Shipyard",
            Requires: "enemy Shipyards, or it is won as soon as you have three"),
        new CampaignObjective(15, "Build 5 Shipyards and destroy every enemy ship",
            Requires: "an enemy navy: Shipyards, Tankers, Destroyers, Battleships or Submarines"),
        new CampaignObjective(23, "Build a Castle or Fortress, and defeat every player but player 7",
            Requires: "player 7 left out of the fight"),
        new CampaignObjective(14, "Own a Dragon Roost and destroy player 2's air units",
            Lose: "neither you nor player 2 has a Gryphon Aviary or Dragon Roost",
            Requires: "a Dragon Roost, and player 2 with air units"),

        // --- bring to the Circle of Power ---
        new CampaignObjective(0x0200, "Bring units to the Circle of Power",
            Lose: "the units to deliver are killed, so delivered plus still alive falls below the number required",
            Requires: "a Circle of Power, and units owned by a rescue-passive or rescue-active player"),
        new CampaignObjective(22, "Bring Turalyon to the Circle of Power",
            Lose: "Turalyon dies", Requires: "Turalyon, and a Circle of Power"),
        new CampaignObjective(20, "Bring Turalyon, Danath and Alleria to the Circle of Power",
            Lose: "fewer than 3 of them are alive",
            Requires: "all three, and a Circle of Power"),

        // --- other ---
        new CampaignObjective(17, "Defeat players 0, 2 and 6",
            Lose: "no Mage of yours or player 4's is alive",
            Requires: "a Mage, and those three as opponents"),
        new CampaignObjective(5, "Rescue anyone, then destroy all enemies",
            Requires: "rescuable units"),
    };

    /// <summary>
    /// Ids that are another rule by the time the mission runs.
    ///
    /// Human10 ships objective 4, and the dispatcher's first act is to rewrite
    /// <c>OBJ_STATE</c> to <c>0x200</c> (`0x004F43E0`, notes §5h) — so it *is* the
    /// counter-threshold rule, and describing it as anything else would be describing the
    /// id rather than the mission. It is not offered separately in <see cref="Choices"/>,
    /// because a second entry reading the same as <c>0x0200</c> is a choice nobody can make
    /// meaningfully.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, int> Aliases = new Dictionary<int, int>
    {
        [4] = 0x0200,
    };

    /// <summary>A readable description, or an honest admission that we do not know.</summary>
    public static string Describe(int objectiveId)
        => Find(objectiveId)?.Label ?? $"Objective {objectiveId}, not yet decoded";

    public static bool IsKnown(int objectiveId) => Find(objectiveId) is not null;

    public static CampaignObjective? Find(int objectiveId)
    {
        var id = Aliases.TryGetValue(objectiveId, out var actual) ? actual : objectiveId;
        return Choices.FirstOrDefault(c => c.Id == id);
    }
}
