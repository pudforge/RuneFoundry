namespace RuneFoundry.Core.Formats;

/// <summary>
/// Readable names for the script variables.
///
/// The generated tables carry two kinds of name, and neither is much good in a dropdown:
/// aidefs.inc's compiler identifiers (size_land, ptys_water) and AIEd.ini's descriptions,
/// which are phrased as instructions to a compiler ("set # Peasants", "set size of land
/// attack party"). These are written for someone deciding what a script should do.
///
/// Where a unit differs between races the Human name comes first, since one script serves
/// both sides — the game picks the right unit at runtime.
/// </summary>
public static class AiNames
{
    private static readonly Dictionary<byte, string> Friendly = new()
    {
        [0x09] = "Land attack",
        [0x0A] = "Naval attack",
        [0x0B] = "Air attack",
        [0x0C] = "Scattered attacks",

        [0x0D] = "Land party size",
        [0x0E] = "Land party count",
        [0x0F] = "Naval party size",
        [0x10] = "Naval party count",
        [0x11] = "Air party size",
        [0x12] = "Air party count",

        [0x13] = "Peasants / Peons",
        [0x14] = "Footmen / Grunts",
        [0x15] = "Archers / Axethrowers",
        [0x16] = "Ballistas / Catapults",
        [0x17] = "Knights / Ogres",
        [0x18] = "Oil tankers",
        [0x19] = "Destroyers",
        [0x1A] = "Transports",
        [0x1B] = "Battleships / Juggernauts",
        [0x1C] = "Submarines / Turtles",
        [0x1D] = "Mages / Death Knights",
        [0x1E] = "Flying Machines / Zeppelins",
        [0x1F] = "Demo Squads / Sappers",
        [0x20] = "Gryphons / Dragons",

        [0x21] = "Unused ($21)",
        [0x22] = "Build list limit",
    };

    /// <summary>
    /// What a variable number is, in the words the game would use.
    ///
    /// Above the labelled range the numbers are build items: the shipped scripts write
    /// variables as high as 0x4A, and 0x3A through 0x56 are the same codes the build list
    /// uses, so "var 3A = 2" is two Farms. Falling through to "Variable $3A" put a hex
    /// number on screen where a building was meant.
    /// </summary>
    public static string Variable(byte number)
    {
        if (Friendly.TryGetValue(number, out var name)) return name;
        if (AiTables.Items.TryGetValue(number, out var item)) return item;

        return $"Variable ${number:X2}";
    }

    /// <summary>Whether this variable names something the computer builds.</summary>
    public static bool IsBuildItem(byte number) =>
        !Friendly.ContainsKey(number) && AiTables.Items.ContainsKey(number);

    public static IEnumerable<byte> Ordered => Friendly.Keys.OrderBy(k => k);

    /// <summary>Wait conditions, shortened from AIEd.ini's compiler phrasing.</summary>
    private static readonly Dictionary<byte, string> WaitConditions = new()
    {
        [0x01] = "a Keep / Stronghold",
        [0x02] = "a Castle / Fortress",
        [0x03] = "workers are ready",
        [0x04] = "land parties are ready",
        [0x05] = "naval parties are ready",
        [0x06] = "air parties are ready",
    };

    public static string Wait(byte number)
        => WaitConditions.TryGetValue(number, out var name) ? name : $"condition {number}";

    public static IEnumerable<byte> OrderedWaits => WaitConditions.Keys.OrderBy(k => k);

    /// <summary>
    /// What a setting does, for the ones whose name is not enough.
    ///
    /// Everything here was read out of the game rather than guessed, except where the
    /// text says otherwise.
    /// </summary>
    private static readonly Dictionary<byte, string> Notes = new()
    {
        [0x0C] = "A yes or no, not an amount: the shipped scripts only ever set 0 or 1. "
                 + "Set to 1, the computer picks where to attack with a random spread "
                 + "across the map. Set to 0, it takes a direct path.",

        [0x0E] = "Multiplies the land party size beside it. The game waits until it has "
                 + "size times this many. Almost always 1.",
        [0x10] = "Multiplies the sea party size beside it. Almost always 1.",
        [0x12] = "Multiplies the air party size beside it. Almost always 1.",

        [0x09] = "Every wave ends by setting this, almost always right after the land "
                 + "party wait. Nothing in the game appears to read it: the attack "
                 + "machinery runs off other fields ($2B to $2F) that no script can "
                 + "write. Leave it as the shipped scripts have it.",
        [0x0A] = "The sea version of $09, and the same story: written after the wait, "
                 + "with no reader found.",
        [0x0B] = "The air version of $09, and the same story: written after the wait, "
                 + "with no reader found.",

        [0x1F] = "Every shipped script sets this to 0 and nothing in the game reads it.",

        [0x21] = "Written by every shipped script, as 0 or 255, and read by nothing in "
                 + "the game. It has no effect. The name it used to carry here "
                 + "(\"Aggressiveness\") was a guess.",

        [0x22] = "How far down the build list the computer may work, not a position in "
                 + "it. The game starts this at 255, meaning the whole list, and every "
                 + "script's opening block sets it to 0 to hold the list back. Raising "
                 + "it later releases the next few buildings and upgrades. It only "
                 + "covers buildings and upgrades, never units.",
    };

    /// <summary>An explanation of a setting, or null when the name says enough.</summary>
    public static string? Note(byte number) => Notes.GetValueOrDefault(number);
}
