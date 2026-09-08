namespace RuneFoundry.Core.Formats;

/// <summary>
/// Fallback names for the rows of the unit and upgrade tables.
///
/// An install's own `Data/Rez/stat_txt.tbl` is the better source and is preferred wherever
/// one is available — it is localised, and a text mod may have renamed things. These are
/// for the cases where it is not: a table opened on its own, a stripped install, or a slot
/// the string table leaves blank. Transcribed from retail `stat_txt.tbl`, with the empty
/// slots named after the identifiers in the engine's own unit enum.
/// </summary>
public static class DatNames
{
    /// <summary>`stat_txt.tbl` entry holding unit id 0. Unit names run one behind their id.</summary>
    public const int FirstUnitString = 1;

    /// <summary>`stat_txt.tbl` entry holding upgrade id 0.</summary>
    public const int FirstUpgradeString = 106;

    /// <summary>Ids the retail table defines nothing for. Free for a mod; the exe still indexes them.</summary>
    public static readonly int[] EmptyUnitIds = { 34, 36, 37, 48, 54 };

    private static readonly string[] Units =
    {
        "Footman", "Grunt", "Peasant", "Peon",
        "Ballista", "Catapult", "Knight", "Ogre",
        "Elven Archer", "Troll Axethrower", "Mage", "Death Knight",
        "Paladin", "Ogre-Mage", "Dwarven Demolition Squad", "Goblin Sappers",
        "Attack Peasant", "Attack Peon", "Ranger", "Berserker",
        "Alleria", "Teron Gorefiend", "Kurdran and Sky'ree", "Dentarg",
        "Khadgar", "Grom Hellscream", "Human Oil Tanker", "Orc Oil Tanker",
        "Human Transport", "Orc Transport", "Elven Destroyer", "Troll Destroyer",
        "Battleship", "Ogre Juggernaught", "(unused 34)", "Deathwing",
        "(unused 36)", "(unused 37)", "Gnomish Submarine", "Giant Turtle",
        "Gnomish Flying Machine", "Goblin Zeppelin", "Gryphon Rider", "Dragon",
        "Turalyon", "Eye of Kilrogg", "Arthas", "Korgath Bladefist",
        "(unused 48)", "Cho'gall", "Lothar", "Gul'dan",
        "Uther Lightbringer", "Zuljin", "(unused 54)", "Skeleton",
        "Daemon", "Critter", "Farm", "Pig Farm",
        "Human Barracks", "Orc Barracks", "Church", "Altar of Storms",
        "Scout Tower", "Watch Tower", "Stables", "Ogre Mound",
        "Gnomish Inventor", "Goblin Alchemist", "Gryphon Aviary", "Dragon Roost",
        "Human Shipyard", "Orc Shipyard", "Town Hall", "Great Hall",
        "Elven Lumber Mill", "Troll Lumber Mill", "Human Foundry", "Orc Foundry",
        "Mage Tower", "Temple of the Damned", "Human Blacksmith", "Orc Blacksmith",
        "Human Refinery", "Orc Refinery", "Human Oil Platform", "Orc Oil Platform",
        "Keep", "Stronghold", "Castle", "Fortress",
        "Gold Mine", "Oil Patch", "Human Start Location", "Orc Start Location",
        "Human Guard Tower", "Orc Guard Tower", "Human Cannon Tower", "Orc Cannon Tower",
        "Circle of Power", "Dark Portal", "Runestone", "Human Wall",
        "Orc Wall", "Dead body", "Destroyed 1x1 building", "Destroyed 2x2 building",
        "Destroyed 3x3 building", "Destroyed 4x5 building",
    };

    private static readonly string[] Upgrades =
    {
        "Sword Strength 1", "Sword Strength 2", "Axe Strength 1", "Axe Strength 2",
        "Arrow Strength 1", "Arrow Strength 2", "Spear Strength 1", "Spear Strength 2",
        "Human Shield Strength 1", "Human Shield Strength 2",
        "Orc Shield Strength 1", "Orc Shield Strength 2",
        "Human Ship Attack 1", "Human Ship Attack 2",
        "Orc Ship Attack 1", "Orc Ship Attack 2",
        "Human Ship Armor 1", "Human Ship Armor 2",
        "Orc Ship Armor 1", "Orc Ship Armor 2",
        "Catapult Strength 1", "Catapult Strength 2",
        "Ballista Strength 1", "Ballista Strength 2",
        "Elven Ranger Training", "Research Longbow", "Ranger Scouting", "Ranger Marksmanship",
        "Troll Berserker Training", "Research Lighter Axes", "Berserker Scouting", "Berserker Regeneration",
        "Upgrade Ogres to Ogre-Mages", "Upgrade Knights to Paladins",
        "Spell - Holy Vision", "Spell - Healing", "Spell - Exorcism", "Spell - Flame Shield",
        "Spell - Fireball", "Spell - Slow", "Spell - Invisibility", "Spell - Polymorph",
        "Spell - Blizzard", "Spell - Eye of Kilrogg", "Spell - Bloodlust", "Spell - Raise Dead",
        "Spell - Death Coil", "Spell - Whirlwind", "Spell - Haste", "Spell - Unholy Armor",
        "Spell - Runes", "Spell - Death and Decay",
    };

    /// <summary>
    /// What each bit of the researched mask stands for, indexed by bit position.
    ///
    /// Human and orc versions of a research share a slot, so most entries name both.
    /// Fifteen of the thirty-two bits go unused. Null where there is no upgrade.
    /// Matches PUDForge's `kAlowUpgradeBits`, which is the naming a map's ALOW section uses.
    /// </summary>
    public static readonly string?[] ResearchedBitNames =
    {
        "Arrow 1 / Throwing Axe 1",
        "Arrow 2 / Throwing Axe 2",
        "Sword 1 / Axe 1",
        "Sword 2 / Axe 2",
        "Human Shield 1 / Orc Shield 1",
        "Human Shield 2 / Orc Shield 2",
        "Ship Cannon 1",
        "Ship Cannon 2",
        "Ship Armor 1",
        "Ship Armor 2",
        null, null,
        "Catapult 1 / Ballista 1",
        "Catapult 2 / Ballista 2",
        null, null,
        "Train Rangers / Berserkers",
        "Longbow / Lighter Axes",
        "Ranger / Berserker Scouting",
        "Marksmanship / Regeneration",
        "Train Paladins / Ogre-Mages",
        null, null, null, null, null, null, null, null, null, null, null,
    };

    /// <summary>Name for a unit id, or a placeholder for one outside the table.</summary>
    public static string UnitName(int unit) =>
        unit >= 0 && unit < Units.Length ? Units[unit] : $"Unit {unit}";

    /// <summary>Name for an upgrade id, or a placeholder for one outside the table.</summary>
    public static string UpgradeName(int upgrade) =>
        upgrade >= 0 && upgrade < Upgrades.Length ? Upgrades[upgrade] : $"Upgrade {upgrade}";

    /// <summary>True for a slot retail defines nothing for.</summary>
    public static bool IsEmptyUnitId(int unit) => Array.IndexOf(EmptyUnitIds, unit) >= 0;
}
