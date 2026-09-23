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

    /// <summary>Which section of a dropdown this belongs in, where one has sections.</summary>
    public string Group { get; init; } = "";

    /// <summary>The unit's race, for grouping the unit list. Empty where it does not apply.</summary>
    public string Race { get; init; } = "";
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
        // Everything below this line is generated from the game's own table at
        // 0x008C0B80, which maps a unit type to the counter it increments. The labels name
        // every type that feeds a counter, because several share one: asking for Knights
        // also counts Ogres, Paladins, Ogre-Mages, Dentarg and Turalyon, and an author who
        // cannot see that will write a rule that fires early and never know why.
        //
        // The two aggregates come first; they are not in the type table because they count
        // whatever is not the other one.
        new Counter("units", "Any unit", 0x0091B38C, null, None),
        new Counter("buildings", "Any building", 0x0091B3AC, null, None) { IsBuilding = true },

        new Counter("oilplatform", "Oil Platform",
            0x0091B3EC, null, new byte[] { 0x56, 0x57 }) { IsBuilding = true },
        new Counter("tower", "Scout Tower / Watch Tower / Guard Tower / Cannon Tower",
            0x0091B40C, null, new byte[] { 0x40, 0x41, 0x60, 0x61, 0x62, 0x63 }) { IsBuilding = true },
        new Counter("lumbermill", "Elven Lumber Mill / Troll Lumber Mill",
            0x0091B42C, null, new byte[] { 0x4C, 0x4D }) { IsBuilding = true },
        new Counter("blacksmith", "Blacksmith",
            0x0091B44C, null, new byte[] { 0x52, 0x53 }) { IsBuilding = true },
        new Counter("stables", "Stables / Ogre Mound",
            0x0091B46C, null, new byte[] { 0x42, 0x43 }) { IsBuilding = true },
        new Counter("farm", "Farm / Pig Farm",
            0x0091B48C, null, new byte[] { 0x3A, 0x3B }) { IsBuilding = true },
        new Counter("altar", "Church / Altar of Storms",
            0x0091B4AC, null, new byte[] { 0x3E, 0x3F }) { IsBuilding = true },
        new Counter("barracks", "Barracks",
            0x0091B4CC, null, new byte[] { 0x3C, 0x3D }) { IsBuilding = true },
        new Counter("townhall", "Town Hall / Great Hall",
            0x0091B52C, null, new byte[] { 0x4A, 0x4B }) { IsBuilding = true },
        new Counter("keep", "Keep / Stronghold",
            0x0091B54C, null, new byte[] { 0x58, 0x59 }) { IsBuilding = true },
        new Counter("castle", "Castle / Fortress",
            0x0091B56C, null, new byte[] { 0x5A, 0x5B }) { IsBuilding = true },
        new Counter("refinery", "Refinery",
            0x0091B58C, null, new byte[] { 0x54, 0x55 }) { IsBuilding = true },
        new Counter("foundry", "Foundry",
            0x0091B5AC, null, new byte[] { 0x4E, 0x4F }) { IsBuilding = true },
        new Counter("inventor", "Gnomish Inventor / Goblin Alchemist",
            0x0091B5CC, null, new byte[] { 0x44, 0x45 }) { IsBuilding = true },
        new Counter("aviary", "Gryphon Aviary / Dragon Roost",
            0x0091B5EC, null, new byte[] { 0x46, 0x47 }) { IsBuilding = true },
        new Counter("shipyard", "Shipyard",
            0x0091B60C, null, new byte[] { 0x48, 0x49 }) { IsBuilding = true },
        new Counter("portal", "Dark Portal",
            0x0091B62C, null, new byte[] { 0x65 }) { IsBuilding = true },
        new Counter("runestone", "Runestone",
            0x0091B64C, null, new byte[] { 0x66 }) { IsBuilding = true },
        new Counter("peon", "Peasant / Peon",
            0x0091B66C, null, new byte[] { 0x02, 0x03, 0x10, 0x11 }),
        new Counter("tanker", "Oil Tanker",
            0x0091B68C, null, new byte[] { 0x1A, 0x1B }),
        new Counter("npc", "Skeleton / Daemon / Critter",
            0x0091B6AC, null, new byte[] { 0x37, 0x38, 0x39 }),
        new Counter("magus", "Mage / Death Knight / Teron Gorefiend / Khadgar",
            0x0091B6CC, 0x0091B96C, new byte[] { 0x0A, 0x0B, 0x15, 0x18 }),
        new Counter("grunt", "Footman / Grunt / Grommash Hellscream / Danath / Kargath Bladefist",
            0x0091B6EC, 0x0091B86C, new byte[] { 0x00, 0x01, 0x19, 0x2E, 0x2F }),
        new Counter("archer", "Elven Archer / Troll Axethrower / Alleria",
            0x0091B70C, 0x0091B88C, new byte[] { 0x08, 0x09, 0x14 }),
        new Counter("ranger", "Ranger / Berserker",
            0x0091B72C, null, new byte[] { 0x12, 0x13 }),
        new Counter("knight", "Knight / Ogre / Paladin / Ogre-Mage / Dentarg / Turalyon",
            0x0091B74C, 0x0091B8CC, new byte[] { 0x06, 0x07, 0x0C, 0x0D, 0x17, 0x2C }),
        new Counter("catapult", "Ballista / Catapult",
            0x0091B76C, 0x0091B8AC, new byte[] { 0x04, 0x05 }),
        new Counter("transport", "Transport",
            0x0091B78C, 0x0091B90C, new byte[] { 0x1C, 0x1D }),
        new Counter("destroyer", "Elven Destroyer / Troll Destroyer",
            0x0091B7AC, 0x0091B8EC, new byte[] { 0x1E, 0x1F }),
        new Counter("battleship", "Battleship / Ogre Juggernaught",
            0x0091B7CC, 0x0091B92C, new byte[] { 0x20, 0x21 }),
        new Counter("submarine", "Gnomish Submarine / Giant Turtle",
            0x0091B7EC, 0x0091B94C, new byte[] { 0x26, 0x27 }),
        new Counter("flyer", "Flying Machine / Goblin Zeppelin",
            0x0091B80C, 0x0091B98C, new byte[] { 0x28, 0x29 }),
        new Counter("dragon", "Kurdran and Sky'ree / Deathwing / Gryphon Rider / Dragon",
            0x0091B82C, 0x0091B9CC, new byte[] { 0x16, 0x23, 0x2A, 0x2B }),
        new Counter("sappers", "Dwarven Demo Squad / Goblin Sappers",
            0x0091B84C, 0x0091B9AC, new byte[] { 0x0E, 0x0F }),
    };

    private static readonly Dictionary<string, Counter> ByName =
        All.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    public static Counter? Find(string? name) =>
        name is not null && ByName.TryGetValue(name, out var counter) ? counter : null;

    /// <summary>The label to show for a stored name, or the name itself if it is unknown.</summary>
    public static string Describe(string? name) => Find(name)?.Label ?? name ?? "";
}

/// <summary>
/// Everything "own" can count: every unit by its own name, and every group the game's
/// counters know, each as a set of unit type ids. One list, because the walk that counts
/// them does not care whether the set has one member or six.
///
/// The key stored in a mod is the type id in decimal for a unit, and the counter's name
/// for a group, so a rule written against the old counter kinds keeps its key.
/// </summary>
public static class UnitTypeCatalog
{
    public const string UnitsGroup = "Units";
    public const string GroupsGroup = "Groups";

    public const string Human = "Human";
    public const string Orc = "Orc";
    public const string Neutral = "Neutral";

    /// <summary>Race of a unit id. The paired units alternate even Human, odd Orc; the
    /// three wildlife/summon types are Neutral.</summary>
    public static string RaceOf(int id) => id is 55 or 56 or 57 ? Neutral : id % 2 == 0 ? Human : Orc;

    /// <summary>Units first, then groups, in the order the dropdown shows them.</summary>
    public static readonly IReadOnlyList<Counter> All = BuildAll();

    private static IReadOnlyList<Counter> BuildAll()
    {
        var list = new List<Counter>();

        for (var id = 0; id < 110; id++)
        {
            if (Formats.DatNames.IsEmptyUnitId(id)) continue;
            var name = Formats.DatNames.UnitName(id);
            if (name.StartsWith("Unit ", StringComparison.Ordinal) || name.StartsWith("(", StringComparison.Ordinal)) continue;
            list.Add(new Counter(id.ToString(), name, 0, null, new[] { (byte)id }) { Group = UnitsGroup, Race = RaceOf(id) });
        }

        var buildings = new HashSet<byte>();
        foreach (var counter in CounterCatalog.All)
            if (counter.IsBuilding)
                foreach (var type in counter.UnitTypes) buildings.Add(type);

        var everything = list.Select(c => c.UnitTypes[0]).ToList();

        foreach (var counter in CounterCatalog.All)
        {
            // The two aggregates have no type list of their own: they are every building,
            // and everything that is not one.
            var types = counter.UnitTypes.Count > 0 ? counter.UnitTypes
                : counter.Name == "buildings" ? everything.Where(buildings.Contains).ToList()
                : everything.Where(t => !buildings.Contains(t)).ToList();
            list.Add(new Counter(counter.Name, counter.Label, 0, null, types) { IsBuilding = counter.IsBuilding, Group = GroupsGroup });
        }

        return list;
    }

    private static readonly Dictionary<string, Counter> ByName =
        All.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    public static Counter? Find(string? name) =>
        name is not null && ByName.TryGetValue(name, out var counter) ? counter : null;

    /// <summary>The type ids behind a stored key, or null.</summary>
    public static IReadOnlyList<byte>? TypesOf(string? name) => Find(name)?.UnitTypes;
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
