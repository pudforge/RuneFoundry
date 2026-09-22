using RuneFoundry.Core.Formats;

namespace RuneFoundry.Core.Scenarios;

/// <summary>How much a finding matters.</summary>
public enum FindingLevel
{
    /// <summary>Worth saying. The mission still works.</summary>
    Note,

    /// <summary>The mission is probably not what the author meant.</summary>
    Warning,

    /// <summary>The mission cannot be played as written.</summary>
    Problem,
}

/// <summary>One thing wrong, or worth knowing, about a set of rules.</summary>
public sealed record Finding(FindingLevel Level, string Message)
{
    public override string ToString() => Message;
}

/// <summary>
/// Reads a mission's rules against the map it will run on, and says what will go wrong.
///
/// This runs when the author writes the rule and again when the mod is built, which is the
/// only time anyone can act on it. The alternative is finding out by playing the mission,
/// and a rule that is met at the first tick looks exactly like a mission that is broken.
///
/// <para>
/// It reads the map's starting units, so it can only speak about the opening position. A
/// rule about a building the author expects to be built later is unknowable here, and this
/// says nothing about it rather than guessing.
/// </para>
/// </summary>
public static class ScenarioValidator
{
    /// <summary>Player 15 is the neutral rescue/passive slot rather than an opponent.</summary>
    public const int NeutralPlayer = 15;

    /// <summary>The Circle of Power, which is what a delivery is delivered to.</summary>
    public const byte CircleOfPower = 0x64;

    public static IReadOnlyList<Finding> Validate(ScenarioRules rules, PudFile? map, int localPlayer = 0)
    {
        var findings = new List<Finding>();

        if (rules.Victory.IsEmpty)
            findings.Add(new Finding(FindingLevel.Problem,
                "This mission has no way to be won. Add a victory condition, or use one of "
                + "the game's own rules."));

        Check(findings, rules.Victory, map, localPlayer, "won");
        Check(findings, rules.Defeat, map, localPlayer, "lost");

        foreach (var finding in CheckOpeningSurvives(map))
            findings.Add(finding);

        return findings;
    }

    /// <summary>Unit types that stand on the map as a building, gathered from the counters.</summary>
    private static readonly HashSet<byte> BuildingTypes = CollectBuildings();

    private static HashSet<byte> CollectBuildings()
    {
        var types = new HashSet<byte>();
        foreach (var counter in CounterCatalog.All)
            if (counter.IsBuilding)
                foreach (var type in counter.UnitTypes)
                    types.Add(type);

        // Not a counter of its own, but it stands on the map like a building. Counting it
        // here can only make the check below quieter, which is the safe direction.
        types.Add(CircleOfPower);
        return types;
    }

    /// <summary>
    /// The three kinds the game subtracts before asking whether you still have an army:
    /// Flying Machines and Zeppelins, Transports, and Oil Tankers. The same three
    /// <see cref="ScenarioEvaluator"/> subtracts when mirroring the check at 0x004F4316.
    /// </summary>
    private static readonly HashSet<byte> UncountedTypes = new()
    {
        0x28, 0x29,  // Flying Machine / Goblin Zeppelin
        0x1C, 0x1D,  // Transports
        0x1A, 0x1B,  // Oil Tankers
    };

    /// <summary>
    /// Whether the game will end the mission in defeat before a rule of the author's is
    /// ever consulted.
    ///
    /// <para>
    /// Every condition function calls the common defeat check first, and the inert
    /// objective RuneFoundry installs is literally a jump to it (CUSTOM-SCENARIOS.md §2.4).
    /// So the game's own "you are out" rule stays live under custom rules and cannot be
    /// switched off: no buildings, and nothing left that counts as an army once flyers,
    /// transports and tankers are taken off, and the mission is lost on the first
    /// evaluation — about a second in, whatever the author wrote.
    /// </para>
    ///
    /// <para>
    /// This is the one failure that looks exactly like the rules never running, so it is
    /// worth saying at the only point anybody can act on it. The slot is read from the map
    /// rather than passed in, and only when there is exactly one human slot: guessing wrong
    /// would mean crying wolf over a sound mission.
    /// </para>
    /// </summary>
    private static IEnumerable<Finding> CheckOpeningSurvives(PudFile? map)
    {
        if (map is null) yield break;

        var humans = CampaignTech.HumanPlayers(map.PlayerOwners);
        if (humans.Count != 1) yield break;

        var mine = map.Units.Where(u => u.Player == humans[0]).ToList();

        if (mine.Count == 0)
        {
            yield return new Finding(FindingLevel.Problem,
                "This map gives your side nothing to start with, so the game calls the "
                + "mission lost about a second in, before any rule here is looked at.");
            yield break;
        }

        if (mine.Any(u => BuildingTypes.Contains(u.Type))) yield break;
        if (mine.Any(u => !UncountedTypes.Contains(u.Type))) yield break;

        yield return new Finding(FindingLevel.Problem,
            "Your side starts with no buildings, and transports, tankers and flying machines "
            + "do not count as an army. The game's own defeat check still runs under custom "
            + "rules and cannot be switched off, so it ends this mission about a second in. "
            + "Give your side a building, or any one other unit, to hold the mission open.");
    }

    private static void Check(List<Finding> findings, ConditionSet set, PudFile? map,
                              int localPlayer, string what)
    {
        for (var i = 0; i < set.Conditions.Count; i++)
        {
            var condition = set.Conditions[i];
            var where = set.Conditions.Count == 1 && set.Nested.Count == 0
                ? $"The {what} rule"
                : $"{what} rule {i + 1}";

            foreach (var finding in CheckOne(condition, map, localPlayer, where))
                findings.Add(finding);
        }

        // Each group is checked as itself, so a fault inside brackets is reported where it
        // is rather than attributed to the rule around it.
        for (var i = 0; i < set.Nested.Count; i++)
            Check(findings, set.Nested[i], map, localPlayer, $"{what} group {i + 1}");

    }

    private static IEnumerable<Finding> CheckOne(Condition condition, PudFile? map,
                                                 int localPlayer, string where)
    {
        // A counter the build does not know is a mod written by a newer version, or a typo.
        if (NeedsCounter(condition.Kind))
        {
            var known = condition.Kind switch
            {
                ConditionKind.Resource => ResourceCatalog.Find(condition.Counter) is not null,
                ConditionKind.OwnUnits => UnitTypeCatalog.Find(condition.Counter) is not null,
                _ => CounterCatalog.Find(condition.Counter) is not null,
            };

            if (!known)
            {
                yield return new Finding(FindingLevel.Problem,
                    $"{where} counts \"{condition.Counter}\", which this version does not know.");
                yield break;
            }
        }

        if (condition.Count < 0)
            yield return new Finding(FindingLevel.Problem, $"{where} asks for a count below zero.");

        if (condition.Kind == ConditionKind.Delivered && condition.Heroes > condition.Count)
            yield return new Finding(FindingLevel.Problem,
                $"{where} wants {condition.Heroes} heroes among {condition.Count} delivered.");

        if (condition.Player is { } player && !condition.IsAnyPlayer
            && (player < 0 || player > NeutralPlayer))
            yield return new Finding(FindingLevel.Problem,
                $"{where} names {PlayerColors.Label(player)}. The game has players 1 to {NeutralPlayer + 1}.");

        if (map is null) yield break;

        foreach (var finding in CheckReachable(condition, map, where))
            yield return finding;
    }

    /// <summary>
    /// Whether the map holds what a rule needs for the rule to ever be satisfiable.
    ///
    /// Only says something when the answer is certain. "Destroy every enemy Refinery" on a
    /// map with no enemy Refinery is met the moment the mission opens, which is a different
    /// mistake from one that can never be met, and both are worth saying out loud.
    /// </summary>
    private static IEnumerable<Finding> CheckReachable(Condition condition, PudFile map,
                                                       string where)
    {
        if (condition.Kind == ConditionKind.OwnUnits)
        {
            // Only for one exact unit: a group can be built up to later, and saying so
            // about every Farm rule would be noise. A hero cannot be trained, so a map
            // that does not start with one is worth a note.
            if (UnitTypeCatalog.Find(condition.Counter) is not { } unit || unit.UnitTypes.Count != 1) yield break;

            var present = map.Units.Any(u => u.Type == unit.UnitTypes[0]
                                             && (condition.Player is null || condition.IsAnyPlayer
                                                 || u.Player == condition.Player));
            if (!present)
                yield return new Finding(FindingLevel.Note,
                    $"{where} counts {unit.Label}, and the map does not start with one.");
            yield break;
        }

        if (condition.Kind is not ConditionKind.Delivered)
        {
            if (CounterCatalog.Find(condition.Counter) is not { } known) yield break;
            if (known.UnitTypes.Count == 0) yield break;
        }

        var counter = CounterCatalog.Find(condition.Counter);

        if (condition.Kind == ConditionKind.Delivered)
        {
            // A delivery is counted when a unit reaches a Circle of Power. A map without
            // one leaves the counter at zero for the whole mission, so the rule cannot be
            // met and nothing in the game says why.
            if (!map.Units.Any(u => u.Type == CircleOfPower))
                yield return new Finding(FindingLevel.Problem,
                    $"{where} waits for units brought to a Circle of Power, and this map has "
                    + "none. Place one in the map editor, or use a different rule.");
        }

        if (condition.Kind == ConditionKind.UnitAlive && counter is not null)
        {
            var alive = map.Units.Count(u => counter.UnitTypes.Contains(u.Type)
                                             && (condition.Player is null || u.Player == condition.Player));

            if (alive == 0)
                yield return new Finding(FindingLevel.Problem,
                    $"{where} watches a {counter.Label}, and the map has none to watch.");
        }
    }

    private static bool NeedsCounter(ConditionKind kind) => kind switch
    {
        ConditionKind.OwnCount or ConditionKind.EnemyHasNone
            or ConditionKind.UnitAlive or ConditionKind.Resource
            or ConditionKind.OwnUnits => true,
        _ => false,
    };
}
