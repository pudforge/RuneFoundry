namespace RuneFoundry.Core.Formats;

/// <summary>
/// How a field wants to be edited. The point of carrying this in the schema is that a
/// view can build a real control per field — a spinner, a checkbox, a combo box, a bank
/// of checkboxes — instead of dropping the user into a hex grid. Nothing in here is
/// WPF-specific; it is a description of the value, not of the widget.
/// </summary>
public enum DatControlKind
{
    /// <summary>A plain integer. Bounds come from the storage width and <see cref="DatField.DisplayScale"/>.</summary>
    Number,

    /// <summary>Stored as 0 or 1. A checkbox.</summary>
    Toggle,

    /// <summary>One of <see cref="DatField.Options"/>. A combo box.</summary>
    Choice,

    /// <summary>A bit set drawn from <see cref="DatField.Options"/>. A list of checkboxes.</summary>
    Flags,
}

/// <summary>One named value of a <see cref="DatControlKind.Choice"/> or <see cref="DatControlKind.Flags"/> field.</summary>
/// <param name="Value">The stored value, or the single bit for a flag.</param>
/// <param name="Label">What to show the user.</param>
/// <param name="Certain">
/// False where the name is inherited from the community format notes but the retail data
/// disagrees with it, or where nobody has established a meaning. A view should still let
/// the bit be set — it just should not promise the label is right.
/// </param>
public sealed record DatOption(uint Value, string Label, bool Certain = true);

/// <summary>
/// One segment of a .dat file. These files are struct-of-arrays: every field is a
/// contiguous run covering all records, so a single record's values are scattered across
/// the file and only <see cref="DatTable"/> should be computing offsets.
/// </summary>
/// <param name="Key">Stable identifier, used for lookup and for mod-manifest diffs.</param>
/// <param name="Display">Human-facing label.</param>
/// <param name="Width">Bytes per element: 1, 2 or 4.</param>
/// <param name="Count">Total elements in the segment.</param>
/// <param name="PerRecord">
/// Elements belonging to one record. Normally 1; the size fields store an x and a y, so
/// they are 2 and are addressed with a component index.
/// </param>
/// <param name="DisplayScale">
/// Multiplier between stored and shown. The three unit costs are held in a byte and
/// scaled by ten, which is why a Footman reads 60 on disk and 600 in game — and why the
/// most a unit can cost is 2550.
/// </param>
/// <param name="Editable">
/// False for the sprite frame tables. They are indices into art the editor does not
/// repack, so exposing them as numbers invites corruption for no gain.
/// </param>
public sealed record DatField(
    string Key,
    string Display,
    int Width,
    int Count,
    int PerRecord = 1,
    DatControlKind Kind = DatControlKind.Number,
    IReadOnlyList<DatOption>? Options = null,
    int DisplayScale = 1,
    string? Help = null,
    bool Editable = true)
{
    /// <summary>Number of records this segment covers.</summary>
    public int Records => Count / PerRecord;

    /// <summary>Largest value the storage can hold, before scaling.</summary>
    public uint RawMax => Width switch
    {
        1 => byte.MaxValue,
        2 => ushort.MaxValue,
        4 => uint.MaxValue,
        _ => throw new InvalidOperationException($"Field '{Key}' has an impossible width of {Width}."),
    };

    /// <summary>Largest value to show in a spinner, with <see cref="DisplayScale"/> applied.</summary>
    public long DisplayMax => (long)RawMax * DisplayScale;

    /// <summary>Step for a spinner: costs move in tens because that is their storage granularity.</summary>
    public int DisplayStep => DisplayScale;
}

/// <summary>
/// Layout of the unit and upgrade tables.
///
/// The segment lists and the field names match PUDForge's `kUdtaSegments` / `kUgrdSegments`
/// (MIT), which is the same layout the PUD `UDTA` and `UGRD` sections carry — a loose .dat
/// is that section's payload without its leading two-byte `useDefaultData` word. Sizes line
/// up exactly: `unitdato.dat` is 5694 and PUDForge's default UDTA payload is 5696.
///
/// The remaster appends `swampFrames` to the unit table, so `unitdata.dat` is 5948 and
/// `unitdato.dat` — the pre-expansion table, which has no swamp tileset — is 5694.
/// <see cref="UnitDataFile"/> accepts either and remembers which it read.
/// </summary>
public static class DatSchema
{
    // ---- option tables -------------------------------------------------------------

    /// <summary>`unitType`. Which movement domain the unit lives in.</summary>
    public static readonly DatOption[] UnitTypeOptions =
    {
        new(0, "Land"),
        new(1, "Air"),
        new(2, "Sea"),
    };

    /// <summary>
    /// `missileWeapon`. The projectile drawn for the attack, not its damage. 0x1d is the
    /// absence of a missile, which is what every melee unit stores.
    /// </summary>
    public static readonly DatOption[] MissileOptions =
    {
        new(0x00, "Lightning"),
        new(0x01, "Griffon Hammer"),
        new(0x02, "Dragon Breath"),
        new(0x03, "Flame Shield"),
        new(0x07, "Big Cannon"),
        new(0x0a, "Touch of Death"),
        new(0x0d, "Catapult Rock"),
        new(0x0e, "Ballista Bolt"),
        new(0x0f, "Arrow"),
        new(0x10, "Axe"),
        new(0x11, "Submarine Missile"),
        new(0x12, "Turtle Missile"),
        new(0x18, "Small Cannon"),
        new(0x1b, "Demon Fire"),
        new(0x1d, "None"),
    };

    /// <summary>`secondMouseButton`. What right-clicking the unit onto a target does.</summary>
    public static readonly DatOption[] MouseOptions =
    {
        new(0, "None"),
        new(1, "Attack"),
        new(2, "Move"),
        new(3, "Harvest"),
        new(4, "Haul oil"),
        new(5, "Demolish"),
        new(6, "Sail"),
    };

    /// <summary>
    /// `canTarget`. Which domains this unit's attack may reach. Guard towers store 7 and
    /// cannon towers 3, which is the in-game rule and a good check that the field is read
    /// the right way round. A unit with no weapon still stores a value here; it is only
    /// meaningful alongside the "Can attack" flag.
    /// </summary>
    public static readonly DatOption[] CanTargetOptions =
    {
        new(1, "Land"),
        new(2, "Sea"),
        new(4, "Air"),
    };

    /// <summary>
    /// The `flags` long.
    ///
    /// Names follow PUDForge, which took them from the community PUD format notes. Three
    /// are marked uncertain because the retail membership fits them badly:
    ///
    ///   bit 2  "Explodes when killed" is set on Ballista and Catapult only — not on the
    ///          Demolition Squad or Sappers, which are the units that actually explode.
    ///   bit 14 "Can attack ground" is set on four units, while forty-nine have attacks.
    ///          Those four are exactly the splash-damage siege weapons.
    ///   bit 25 "Killed by invisibility" is set on exactly the Demolition Squad and
    ///          Sappers, which looks like bit 2's description.
    ///
    /// Bit 13 is listed as unknown upstream. In retail data it is set on exactly the five
    /// corpse slots (unit ids 105-109), so it is named here as the corpse marker.
    /// Bits 28-31 are unused by every unit in the retail table and are absent rather than
    /// guessed at; <see cref="DatTable"/> still round-trips them.
    /// </summary>
    public static readonly DatOption[] UnitFlagOptions =
    {
        new(1u << 0, "Land unit"),
        new(1u << 1, "Air unit"),
        new(1u << 2, "Explodes when killed", Certain: false),
        new(1u << 3, "Sea unit"),
        new(1u << 4, "Critter"),
        new(1u << 5, "Building"),
        new(1u << 6, "Submarine"),
        new(1u << 7, "Sees submarines"),
        new(1u << 8, "Peon"),
        new(1u << 9, "Tanker"),
        new(1u << 10, "Transport"),
        new(1u << 11, "Gives oil"),
        new(1u << 12, "Stores gold"),
        new(1u << 13, "Corpse", Certain: false),
        new(1u << 14, "Can attack ground", Certain: false),
        new(1u << 15, "Undead"),
        new(1u << 16, "Shore building"),
        new(1u << 17, "Can cast spells"),
        new(1u << 18, "Stores lumber"),
        new(1u << 19, "Can attack"),
        new(1u << 20, "Tower"),
        new(1u << 21, "Oil patch"),
        new(1u << 22, "Gold mine"),
        new(1u << 23, "Hero"),
        new(1u << 24, "Stores oil"),
        new(1u << 25, "Killed by invisibility", Certain: false),
        new(1u << 26, "Flees when attacked"),
        new(1u << 27, "Organic"),
    };

    // ---- unit table ----------------------------------------------------------------

    /// <summary>Units addressed by the unit table.</summary>
    public const int UnitCount = 110;

    /// <summary>Bytes of a unit table that stops before the swamp frames — `unitdato.dat`.</summary>
    public const int UnitSizeWithoutSwamp = 5694;

    /// <summary>Bytes of the remaster's unit table, which appends them — `unitdata.dat`.</summary>
    public const int UnitSizeWithSwamp = 5948;

    /// <summary>Key of the trailing segment the two sizes differ by.</summary>
    public const string SwampFramesKey = "swampFrames";

    /// <summary>
    /// The unit table in file order. Every entry before <see cref="SwampFramesKey"/> is
    /// present in both file sizes.
    /// </summary>
    public static readonly DatField[] UnitFields =
    {
        new("overlapFrames", "Overlap frames", 2, 110, Editable: false,
            Help: "Sprite frame index. Art data, not balance."),
        new("obsoleteFrames", "Tileset frames", 2, 508, Editable: false,
            Help: "Four 127-entry frame tables, one per original tileset."),
        new("sight", "Sight range", 4, 110,
            Help: "Tiles revealed around the unit."),
        new("hitPoints", "Hit points", 2, 110),
        new("hasMagic", "Has mana", 1, 110, Kind: DatControlKind.Toggle,
            Help: "Draws a mana bar and lets the unit hold spell points."),
        new("buildTime", "Build time", 1, 110),
        new("goldCost", "Gold cost", 1, 110, DisplayScale: 10,
            Help: "Stored in tens, so costs move in steps of 10 and stop at 2550."),
        new("lumberCost", "Lumber cost", 1, 110, DisplayScale: 10),
        new("oilCost", "Oil cost", 1, 110, DisplayScale: 10),
        new("unitSize", "Unit size", 2, 220, PerRecord: 2,
            Help: "Width and height in pixels."),
        new("boxSize", "Selection box", 2, 220, PerRecord: 2,
            Help: "Width and height of the selection rectangle, in pixels."),
        new("attackRange", "Attack range", 1, 110),
        new("reactRangeComputer", "React range (CPU)", 1, 110,
            Help: "How far a computer-controlled unit looks for something to attack."),
        new("reactRangeHuman", "React range (player)", 1, 110),
        new("armor", "Armor", 1, 110),
        new("selectableViaRectangle", "Box-selectable", 1, 110, Kind: DatControlKind.Toggle),
        new("priority", "Selection priority", 1, 110,
            Help: "Higher wins when a drag-select covers several units."),
        new("basicDamage", "Basic damage", 1, 110,
            Help: "Reduced by the target's armor."),
        new("piercingDamage", "Piercing damage", 1, 110,
            Help: "Ignores armor."),
        new("weaponsUpgradable", "Weapons upgradable", 1, 110, Kind: DatControlKind.Toggle),
        new("armorUpgradable", "Armor upgradable", 1, 110, Kind: DatControlKind.Toggle),
        new("missileWeapon", "Missile", 1, 110, Kind: DatControlKind.Choice, Options: MissileOptions),
        new("unitType", "Movement domain", 1, 110, Kind: DatControlKind.Choice, Options: UnitTypeOptions),
        new("decayRate", "Decay rate", 1, 110,
            Help: "How quickly the corpse fades."),
        new("annoyComputerFactor", "Annoy factor", 1, 110,
            Help: "How strongly the computer is drawn to attack this unit."),
        new("secondMouseButton", "Right-click action", 1, 58, Kind: DatControlKind.Choice, Options: MouseOptions,
            Help: "Only the first 58 units carry one."),
        new("pointValue", "Point value", 2, 110,
            Help: "Score awarded for killing it."),
        new("canTarget", "Can target", 1, 110, Kind: DatControlKind.Flags, Options: CanTargetOptions),
        new("flags", "Flags", 4, 110, Kind: DatControlKind.Flags, Options: UnitFlagOptions),
        new(SwampFramesKey, "Swamp frames", 2, 127, Editable: false,
            Help: "Added by the remaster. Absent from unitdato.dat."),
    };

    // ---- upgrade table -------------------------------------------------------------

    /// <summary>Researches and spells addressed by the upgrade table.</summary>
    public const int UpgradeCount = 52;

    /// <summary>Bytes of `upgrades.dat`.</summary>
    public const int UpgradeSize = 780;

    /// <summary>Upgrade ids at or above this are spells; below it, researches.</summary>
    public const int FirstSpellUpgrade = 34;

    /// <summary>
    /// The upgrade table in file order.
    ///
    /// `flags` holds exactly one bit per upgrade — the slot the research occupies in the
    /// player's researched mask — so it is a number to be preserved, not a set of
    /// independent switches, and is exposed read-only. Human and orc counterparts share a
    /// slot: Sword 1 and Axe 1 are both bit 2. Retail has one long-standing quirk, Arrow 2
    /// storing bit 0 alongside Arrow 1 where Throwing Axe 2 correctly stores bit 1; it
    /// round-trips untouched.
    /// </summary>
    public static readonly DatField[] UpgradeFields =
    {
        new("upgradeTime", "Research time", 1, 52),
        new("goldCost", "Gold cost", 2, 52),
        new("lumberCost", "Lumber cost", 2, 52),
        new("oilCost", "Oil cost", 2, 52,
            Help: "Only the four ship attack researches spend oil."),
        new("icon", "Icon", 2, 52,
            Help: "Index into the icon sheet."),
        new("group", "Group", 2, 52,
            Help: "Research category. For spells this is the spell id, and flags is 1 << group."),
        new("flags", "Researched bit", 4, 52, Editable: false,
            Help: "The upgrade's slot in the player's researched mask. Changing it desynchronises saves and maps."),
    };

    /// <summary>Total bytes a segment list occupies.</summary>
    public static int SizeOf(IEnumerable<DatField> fields) => fields.Sum(f => f.Count * f.Width);

    /// <summary>Byte offset of a segment within its table, or -1 if the key is not present.</summary>
    public static int OffsetOf(IReadOnlyList<DatField> fields, string key)
    {
        var at = 0;
        foreach (var field in fields)
        {
            if (field.Key == key) return at;
            at += field.Count * field.Width;
        }
        return -1;
    }
}
