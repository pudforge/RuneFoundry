namespace RuneFoundry.Core.Formats;

/// <summary>
/// Which icon each unit draws, and what the icons are called.
///
/// The mapping is not guesswork any more. The HUD builds an atlas key out of a tileset
/// prefix and a number — the strings <c>"forest_"</c>, <c>"ice_"</c>, <c>"swamp_"</c> and
/// <c>"xswamp_"</c> live at <c>0x008429E0</c> — and the number comes from a table the
/// portrait code indexes by unit type:
///
/// <code>
/// movzx eax, byte [ecx + 0x27]        ; the unit's type
/// shl   eax, 4                        ; 16 bytes per type
/// movzx eax, word [eax + 0x008C6308]  ; its icon id
/// </code>
///
/// So <c>word[0x008C6308 + type * 16]</c> is the answer, and the table below is that array
/// read out of build 1.0.2.2818. A test reads it back from the installed executable and
/// fails if a patch ever moves it, which is the only way this can quietly go wrong.
/// </summary>
public static class IconNames
{
    /// <summary>Where the table sits, and how far apart its entries are.</summary>
    public const uint TableAddress = 0x008C6308;

    public const int TableStride = 16;

    /// <summary>Icon id per unit type, indexed by unit id.</summary>
    private static readonly ushort[] Icons =
    {
        2, 3, 0, 1,           // Footman, Grunt, Peasant, Peon
        16, 17, 8, 9,         // Ballista, Catapult, Knight, Ogre
        4, 5, 14, 15,         // Elven Archer, Troll Axethrower, Mage, Death Knight
        10, 11, 12, 13,       // Paladin, Ogre-Mage, Dwarven Demolition Squad, Goblin Sappers
        0, 1, 6, 7,           // Attack Peasant, Attack Peon, Ranger, Berserker
        187, 189, 191, 194,   // Alleria, Teron Gorefiend, Kurdran and Sky'ree, Dentarg
        193, 190, 18, 19,     // Khadgar, Grommash Hellscream, Oil Tankers
        20, 21, 22, 23,       // Transports, Destroyers
        24, 25, 0, 192,       // Battleship, Juggernaught, empty slot 34, Deathwing
        0, 0, 26, 27,         // empty slots 36 and 37, Submarine, Giant Turtle
        28, 29, 30, 31,       // Flying Machine, Zeppelin, Gryphon Rider, Dragon
        195, 111, 188, 186,   // Turalyon, Eye of Kilrogg, Danath, Korgath Bladefist
        0, 36, 32, 33,        // empty slot 48, Cho'gall, Lothar, Gul'dan
        34, 35, 0, 114,       // Uther Lightbringer, Zuljin, empty slot 54, Skeleton
        37, 115, 38, 39,      // Daemon, Critter, Farm, Pig Farm
        42, 43, 62, 63,       // Barracks, Church, Altar of Storms
        60, 61, 56, 57,       // Scout Towers, Town Hall, Great Hall
        58, 59, 72, 73,       // Lumber Mills, Stables, Ogre Mound
        48, 49, 40, 41,       // Inventor, Alchemist, Aviary, Roost
        44, 45, 52, 53,       // Shipyards, Refineries
        64, 65, 46, 47,       // Foundries, Mage Tower, Temple of the Damned
        50, 51, 54, 55,       // Blacksmiths, the second pair of shipyards
        66, 67, 70, 71,       // Keep, Stronghold, Castle, Fortress
        74, 79, 0, 0,         // Gold Mine, Oil Patch, the two oil wells (no icon)
        75, 77, 76, 78,       // Guard Towers, Cannon Towers
        81, 80, 82, 92,       // Circle of Power, Dark Portal, Runestone, Human Wall
        93,                   // Orc Wall
    };

    /// <summary>How many unit types the table covers.</summary>
    public static int UnitCount => Icons.Length;

    /// <summary>
    /// The icon a unit draws. Null outside the table, and for the handful of slots that
    /// have no icon of their own — the empty unit ids and the two oil wells all read 0,
    /// which is the Peasant, not a portrait of their own.
    /// </summary>
    public static int? ForUnit(int unit) =>
        unit >= 0 && unit < Icons.Length ? Icons[unit] : null;

    /// <summary>Every unit that draws a given icon, in id order.</summary>
    public static IEnumerable<int> UnitsUsing(int icon)
    {
        for (var unit = 0; unit < Icons.Length; unit++)
            if (Icons[unit] == icon) yield return unit;
    }

    /// <summary>
    /// A name for every icon a unit or an upgrade uses, in the words the game uses for
    /// them. Built from the tables rather than from a list here, so a mod that renames a
    /// unit sees its own name against the icon.
    /// </summary>
    public static Dictionary<int, string> Label(
        Func<int, string> unitName,
        Func<int, string> upgradeName,
        Func<int, int>? upgradeIcon = null,
        int upgradeCount = 0)
    {
        var labels = new Dictionary<int, string>();

        // Units first: an icon shared by a unit and an upgrade belongs to the unit, and
        // the first unit to claim it wins, so the Peasant names icon 0 rather than the
        // Attack Peasant that follows it.
        for (var unit = 0; unit < Icons.Length; unit++)
        {
            var icon = Icons[unit];
            if (!labels.ContainsKey(icon)) labels[icon] = unitName(unit);
        }

        if (upgradeIcon is null) return labels;

        for (var upgrade = 0; upgrade < upgradeCount; upgrade++)
        {
            var icon = upgradeIcon(upgrade);
            if (!labels.ContainsKey(icon)) labels[icon] = upgradeName(upgrade);
        }

        return labels;
    }
}
