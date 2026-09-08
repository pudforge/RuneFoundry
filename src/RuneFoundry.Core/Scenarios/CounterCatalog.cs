namespace RuneFoundry.Core.Scenarios;

/// <summary>
/// One thing a rule can count, and where the game keeps the tally.
/// </summary>
/// <param name="Name">The key stored in a mod. Stable: renaming one breaks saved rules.</param>
/// <param name="Label">What the editor shows.</param>
/// <param name="Total">
/// The TOTAL counter: every one the player owns, a building from the moment its foundation
/// is laid.
/// </param>
/// <param name="Active">
/// The ACTIVE counter, where the game keeps one: finished and able to act. Null where it
/// does not, and a rule asking for finished-only on such a counter falls back to TOTAL.
/// </param>
/// <param name="UnitTypes">
/// The unit type ids that feed the counter. The validator uses these to look at a map's
/// starting units, and the editor uses them to explain what a counter covers.
/// </param>
public sealed record Counter(
    string Name,
    string Label,
    uint Total,
    uint? Active,
    IReadOnlyList<byte> UnitTypes)
{
    public bool IsBuilding { get; init; }
}

/// <summary>
/// Everything a condition can count.
///
/// The game keeps a <c>word[16]</c> per counted thing, on a 0x20 stride, written at
/// 0x004B5300 whenever a unit is registered or removed, indexed by owner. Two parallel
/// tables exist: TOTAL at 0x008C0B80 counts everything owned, ACTIVE at 0x008C0D38 counts
/// only what is finished. Both are listed here so "owns a Castle" and "owns a finished
/// Castle" are separate questions. See W2R-RE-NOTES.md §5b and CUSTOM-SCENARIOS.md §3.1.
///
/// <para>
/// One table, read by the editor's dropdown and by the evaluator alike. A counter that is
/// in one and not the other is a bug waiting to happen.
/// </para>
/// </summary>
public static class CounterCatalog
{
    private static readonly byte[] None = Array.Empty<byte>();

    /// <summary>Everything countable, in the order the editor should offer it.</summary>
    public static readonly IReadOnlyList<Counter> All = new[]
    {
        // --- the two totals, which most rules want ---
        new Counter("units", "Units alive", 0x0091B38C, null, None),
        new Counter("buildings", "Buildings standing", 0x0091B3AC, null, None) { IsBuilding = true },

        // --- town tiers ---
        new Counter("townhall", "Town Hall / Great Hall", 0x0091B52C, null, new byte[] { 0x4A, 0x4B }) { IsBuilding = true },
        new Counter("keep", "Keep / Stronghold", 0x0091B54C, null, new byte[] { 0x58, 0x59 }) { IsBuilding = true },
        new Counter("castle", "Castle / Fortress", 0x0091B56C, null, new byte[] { 0x5A, 0x5B }) { IsBuilding = true },

        // --- the buildings an objective usually names ---
        new Counter("farm", "Farm / Pig Farm", 0x0091B48C, null, new byte[] { 0x3A, 0x3B }) { IsBuilding = true },
        new Counter("barracks", "Barracks", 0x0091B4CC, null, new byte[] { 0x3C, 0x3D }) { IsBuilding = true },
        new Counter("shipyard", "Shipyard", 0x0091B60C, null, new byte[] { 0x48, 0x49 }) { IsBuilding = true },
        new Counter("refinery", "Refinery", 0x0091B58C, null, new byte[] { 0x54, 0x55 }) { IsBuilding = true },
        new Counter("oilplatform", "Oil Platform", 0x0091B3EC, null, new byte[] { 0x56, 0x57 }) { IsBuilding = true },
        new Counter("tower", "Tower", 0x0091B40C, null, new byte[] { 0x40, 0x41, 0x60, 0x61, 0x62, 0x63 }) { IsBuilding = true },
        new Counter("lumbermill", "Lumber Mill", 0x0091B42C, null, new byte[] { 0x4C, 0x4D }) { IsBuilding = true },
        new Counter("blacksmith", "Blacksmith", 0x0091B44C, null, new byte[] { 0x52, 0x53 }) { IsBuilding = true },
        new Counter("stables", "Stables / Ogre Mound", 0x0091B46C, null, new byte[] { 0x42, 0x43 }) { IsBuilding = true },
        new Counter("altar", "Church / Altar of Storms", 0x0091B4AC, null, new byte[] { 0x3E, 0x3F }) { IsBuilding = true },
        new Counter("foundry", "Foundry", 0x0091B5AC, null, new byte[] { 0x4E, 0x4F }) { IsBuilding = true },
        new Counter("inventor", "Inventor / Alchemist", 0x0091B5CC, null, new byte[] { 0x44, 0x45 }) { IsBuilding = true },
        new Counter("aviary", "Aviary / Dragon Roost", 0x0091B5EC, null, new byte[] { 0x46, 0x47 }) { IsBuilding = true },
        new Counter("portal", "Dark Portal", 0x0091B62C, null, new byte[] { 0x65 }) { IsBuilding = true },
        new Counter("runestone", "Runestone", 0x0091B64C, null, new byte[] { 0x66 }) { IsBuilding = true },

        // --- units. ACTIVE exists only for the combat types, and its order is not the
        //     order of this list: grunt, archer, catapult, knight, destroyer, transport,
        //     battleship, submarine, magus, flyer, sappers, dragon, on a 0x20 stride from
        //     0x0091B86C. Two of them were transposed here, which read a submarine tally
        //     for a mage. ---
        new Counter("peon", "Peasant / Peon", 0x0091B66C, null, new byte[] { 0x02, 0x03, 0x10, 0x11 }),
        new Counter("tanker", "Oil Tanker", 0x0091B68C, null, new byte[] { 0x1A, 0x1B }),
        new Counter("grunt", "Footman / Grunt", 0x0091B6EC, 0x0091B86C, new byte[] { 0x00, 0x01 }),
        new Counter("archer", "Archer / Axethrower", 0x0091B70C, 0x0091B88C, new byte[] { 0x08, 0x09 }),
        new Counter("ranger", "Ranger / Berserker", 0x0091B72C, null, new byte[] { 0x12, 0x13 }),
        new Counter("knight", "Knight / Ogre", 0x0091B74C, 0x0091B8CC, new byte[] { 0x06, 0x07, 0x0C, 0x0D }),
        new Counter("catapult", "Ballista / Catapult", 0x0091B76C, 0x0091B8AC, new byte[] { 0x04, 0x05 }),
        new Counter("magus", "Mage / Death Knight", 0x0091B6CC, 0x0091B96C, new byte[] { 0x0A, 0x0B }),
        new Counter("magetower", "Mage Tower / Temple", 0x0091B4EC, null, new byte[] { 0x50, 0x51 }) { IsBuilding = true },
        new Counter("sappers", "Demo Squad / Sappers", 0x0091B84C, 0x0091B9AC, new byte[] { 0x0E, 0x0F }),
        new Counter("transport", "Transport", 0x0091B78C, 0x0091B90C, new byte[] { 0x1C, 0x1D }),
        new Counter("destroyer", "Destroyer", 0x0091B7AC, 0x0091B8EC, new byte[] { 0x1E, 0x1F }),
        new Counter("battleship", "Battleship / Juggernaut", 0x0091B7CC, 0x0091B92C, new byte[] { 0x20, 0x21 }),
        new Counter("submarine", "Submarine / Turtle", 0x0091B7EC, 0x0091B94C, new byte[] { 0x26, 0x27 }),
        new Counter("flyer", "Flying Machine / Zeppelin", 0x0091B80C, 0x0091B98C, new byte[] { 0x28, 0x29 }),
        new Counter("dragon", "Gryphon / Dragon", 0x0091B82C, 0x0091B9CC, new byte[] { 0x2A, 0x2B }),
    };

    private static readonly Dictionary<string, Counter> ByName =
        All.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    public static Counter? Find(string? name) =>
        name is not null && ByName.TryGetValue(name, out var counter) ? counter : null;

    /// <summary>The label to show for a stored name, or the name itself if it is unknown.</summary>
    public static string Describe(string? name) => Find(name)?.Label ?? name ?? "";
}

/// <summary>
/// The resources a rule can read, as <see cref="Condition.Counter"/> names for
/// <see cref="ConditionKind.Resource"/>. Kept apart from the unit counters because they are
/// dwords in different arrays. CUSTOM-SCENARIOS.md §3.3.
/// </summary>
public static class ResourceCatalog
{
    public static readonly IReadOnlyList<Counter> All = new[]
    {
        new Counter("gold", "Gold", 0x00919128, null, Array.Empty<byte>()),
        new Counter("lumber", "Lumber", 0x009190E8, null, Array.Empty<byte>()),
        new Counter("oil", "Oil", 0x00919168, null, Array.Empty<byte>()),
    };

    private static readonly Dictionary<string, Counter> ByName =
        All.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    public static Counter? Find(string? name) =>
        name is not null && ByName.TryGetValue(name, out var counter) ? counter : null;
}
