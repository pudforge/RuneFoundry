namespace RuneFoundry.Core.Formats;

/// <summary>
/// Names for the things inside ai.bin.
///
/// Generated from WarDraft's own data files — AIEd.ini for the 84 AI slots and the
/// wait conditions, aidefs.inc for the variable numbers. WarDraft is the editor the
/// Warcraft II community has used for these scripts since 1997, so its naming is the
/// naming people expect to see. Transcribed by script rather than by hand.
/// </summary>
public static class AiTables
{
    /// <summary>The 84 AI slots, in the order ai.bin stores them.</summary>
    public static readonly string[] AiNames =
    {
        "Land Attack",
        "Passive",
        "Orc 3",
        "Human 4",
        "Orc 4",
        "Human 5",
        "Orc 5",
        "Human 6",
        "Orc 6",
        "Human 7",
        "Orc 7",
        "Human 8",
        "Orc 8",
        "Human 9",
        "Orc 9",
        "Human 10",
        "Orc 10",
        "Human 11",
        "Orc 11",
        "Human 12",
        "Orc 12",
        "Human 13",
        "Orc 13",
        "Human 14 (Orange)",
        "Orc 14 (Blue)",
        "Sea Attack",
        "Air Attack",
        "Human 14 (Red)",
        "Human 14 (White)",
        "Human 14 (Black)",
        "Orc 14 (Green)",
        "Orc 14 (White)",
        "Orc Exp. 4",
        "Orc Exp. 5",
        "Orc Exp. 7a",
        "Orc Exp. 9",
        "Orc Exp.10",
        "Orc Exp.12",
        "Orc Exp. 6a",
        "Orc Exp. 6b",
        "Orc Exp.11a",
        "Orc Exp.11b",
        "Hum Exp. 2a (Red)",
        "Hum Exp. 2b (Black)",
        "Hum Exp. 2c (Yellow)",
        "Hum Exp. 3a (Orange)",
        "Hum Exp. 3b (Red)",
        "Hum Exp. 3c (Violet)",
        "Hum Exp. 4a (Black)",
        "Hum Exp. 4b (Red)",
        "Hum Exp. 4c (White)",
        "Hum Exp. 5a (Green)",
        "Hum Exp. 5b (Orange)",
        "Hum Exp. 5c (Violet)",
        "Hum Exp. 5d (Yellow)",
        "Hum Exp. 6a (Green)",
        "Hum Exp. 6b (Black)",
        "Hum Exp. 6c (Orange)",
        "Hum Exp. 6d (Red)",
        "Hum Exp. 8a (White)",
        "Hum Exp. 8b (Yellow)",
        "Hum Exp. 8c (Violet)",
        "Hum Exp. 9a (Black)",
        "Hum Exp. 9b (Red)",
        "Hum Exp. 9c (Green)",
        "Hum Exp. 9d (White)",
        "Hum Exp.10a (Violet)",
        "Hum Exp.10b (Green)",
        "Hum Exp.10c (Black)",
        "Hum Exp.11a",
        "Hum Exp.11b",
        "Hum Exp.12a",
        "Orc Exp. 5b",
        "Hum Exp. 7a",
        "Hum Exp. 7b",
        "Hum Exp. 7c",
        "Orc Exp.12a",
        "Orc Exp.12b",
        "Orc Exp.12c",
        "Orc Exp.12d",
        "Orc Exp. 2",
        "Orc Exp. 7b",
        "Orc Exp. 3",
        "Custom (compiled) AI",
    };

    /// <summary>Script variable numbers to the names WarDraft uses for them.</summary>
    public static readonly IReadOnlyDictionary<byte, string> Variables =
        new Dictionary<byte, string>
        {
            [0x09] = "land_attack",
            [0x0A] = "naval_attack",
            [0x0B] = "air_attack",
            [0x0C] = "strategy",
            [0x0D] = "size_land",
            [0x0E] = "ptys_land",
            [0x0F] = "size_water",
            [0x10] = "ptys_water",
            [0x11] = "size_air",
            [0x12] = "ptys_air",
            [0x13] = "peasants",
            [0x14] = "footmen",
            [0x15] = "archers",
            [0x16] = "ballistas",
            [0x17] = "knights",
            [0x18] = "tankers",
            [0x19] = "destroyers",
            [0x1A] = "transports",
            [0x1B] = "battleships",
            [0x1C] = "submarines",
            [0x1D] = "mages",
            [0x1E] = "flyers",
            [0x1F] = "demosquads",
            [0x20] = "gryphons",
            [0x21] = "aggression",
            [0x22] = "items",
        };

    /// <summary>What each wait condition waits for.</summary>
    public static readonly IReadOnlyDictionary<byte, string> Waits =
        new Dictionary<byte, string>
        {
            [0x01] = "wait until you have a Keep / Stronghold",
            [0x02] = "wait until you have a Castle / Fortress",
            [0x03] = "wait until peasants are complete / wait until peons are complete",
            [0x04] = "wait until land attack parties are complete",
            [0x05] = "wait until naval attack parties are complete",
            [0x06] = "wait until air attack parties are complete",

            // 0x4ca200 walks all eight players: it answers yes as soon as it finds one
            // this player is not allied with (0x919578, the same table that decides
            // whether two units may fight) whose worker count is above zero. Used once
            // in the shipped file.
            [0x07] = "wait until an enemy still has workers",
        };

    /// <summary>Readable labels, with Human and Orc wordings merged.</summary>
    public static readonly IReadOnlyDictionary<byte, string> Labels =
        new Dictionary<byte, string>
        {
            [0x09] = "start land attack",
            [0x0A] = "start naval attack",
            [0x0B] = "start air attack",
            [0x0D] = "size of land attack party",
            [0x0E] = "number of land attack parties",
            [0x0F] = "size of naval attack party",
            [0x10] = "number of naval attack parties",
            [0x11] = "size of air attack party",
            [0x12] = "number of air attack parties",
            [0x13] = "Peasants / Peons",
            [0x14] = "Footmen / Grunts",
            [0x15] = "Archers / Axethrowers",
            [0x16] = "Ballistas / Catapults",
            [0x17] = "Knights / Ogres",
            [0x18] = "Tankers",
            [0x19] = "Destroyers",
            [0x1A] = "Transports",
            [0x1B] = "Battleships / Juggernaughts",
            [0x1C] = "Submarines / Giant Turtles",
            [0x1D] = "Mages / Death Knights",
            [0x1E] = "Flying Machines / Zeppelins",
            [0x1F] = "Dwarven Demo Squads / Goblin Sappers",
            [0x20] = "Gryphon Riders / Dragons",
            [0x21] = "aggressiveness:",
        };

    /// <summary>Build-list item codes: buildings and upgrades the AI can queue.</summary>
    public static readonly IReadOnlyDictionary<byte, string> Items =
        new Dictionary<byte, string>
        {
            [0x3A] = "Farm",
            [0x3C] = "Barracks",
            [0x3E] = "Church / Altar of Storms",
            [0x40] = "Scout Tower",
            [0x42] = "Stables / Ogre Mound",
            [0x44] = "Gnomish Inventor / Goblin Alchemist",
            [0x46] = "Gryphon Aviary / Dragon Roost",
            [0x48] = "Shipyard",
            [0x4A] = "Town Hall / Great Hall",
            [0x4C] = "Lumber mill",
            [0x4E] = "Foundry",
            [0x50] = "Mage Tower / Temple of the Damned",
            [0x52] = "Blacksmith",
            [0x54] = "Refinery",
            [0x56] = "Oil well",
            [0x80] = "Arrow upgrade (1/2) / Axe upgrade (1/2)",
            [0x81] = "Arrow upgrade (2/2) / Axe upgrade (2/2)",
            [0x82] = "upgrade Archers to Rangers / upgrade Axethrowers to Berserkers",
            [0x83] = "Ranger upgrade A / Berserker upgrade A",
            [0x84] = "Ranger upgrade B / Berserker upgrade B",
            [0x85] = "Ranger upgrade C / Berserker upgrade C",
            [0x86] = "Footman,Knight upgrade A (1/2) / Grunt,Ogre upgrade A (1/2)",
            [0x87] = "Footman,Knight upgrade A (2/2) / Grunt,Ogre upgrade A (2/2)",
            [0x88] = "Footman,Knight upgrade B (1/2) / Grunt,Ogre upgrade B (1/2)",
            [0x89] = "Footman,Knight upgrade B (2/2) / Grunt,Ogre upgrade B (2/2)",
            [0x8A] = "Ballista upgrade (1/2) / Catapult upgrade (1/2)",
            [0x8B] = "Ballista upgrade (1/2) / Catapult upgrade (1/2)",
            [0x8C] = "Ship cannons upgrade (1/2)",
            [0x8D] = "Ship cannons upgrade (2/2)",
            [0x8E] = "Ship armor upgrade (1/2)",
            [0x8F] = "Ship armor upgrade (2/2)",
            [0x90] = "upgrade Knights to Paladins / upgrade Ogres to Ogre-Mages",
            [0x91] = "Paladin spell A / Ogre-Mage spell A",
            [0x92] = "Paladin spell B / Ogre-Mage spell B",
            [0x93] = "Mage spell A / Death Knight spell A",
            [0x94] = "Mage spell B / Death Knight spell B",
            [0x95] = "Mage spell C / Death Knight spell C",
            [0x96] = "Mage spell D / Death Knight spell D",
            [0x97] = "Mage spell E / Death Knight spell E",
            [0x98] = "upgrade Townhall to Keep / upgrade Great Hall to Stronghold",
            [0x99] = "upgrade Keep to Castle / upgrade Stronghold to Fortress",
        };

    /// <summary>
    /// Whether a byte is a plausible build-list entry. Item codes occupy two narrow bands
    /// — $3A-$56 for buildings, $80-$99 for upgrades — so a run of bytes outside them is a
    /// sign the pointer is not aimed at a build list at all.
    /// </summary>
    public static bool IsPlausibleItem(byte code)
        => code == 0x00 || code == 0xFF || Items.ContainsKey(code);

    public static string ItemName(byte code) =>
        code == 0x00 ? "nothing"
        : code == 0xFF ? "end of group"
        : Items.TryGetValue(code, out var name) ? name : $"item ${code:X2}";

    public static string Label(byte number)
        => Labels.TryGetValue(number, out var text) ? text : VariableName(number);

    public static string AiName(int index)
        => index >= 0 && index < AiNames.Length ? AiNames[index] : $"AI ${index:X2}";

    public static string VariableName(byte number)
        => Variables.TryGetValue(number, out var name) ? name : $"${number:X2}";

    public static string WaitName(byte number)
        => Waits.TryGetValue(number, out var name) ? name : $"condition {number}";
}
