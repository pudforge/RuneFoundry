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

        return findings;
    }

    private static void Check(List<Finding> findings, ConditionSet set, PudFile? map,
                              int localPlayer, string what)
    {
        for (var i = 0; i < set.Conditions.Count; i++)
        {
            var condition = set.Conditions[i];
            var where = set.Conditions.Count == 1 ? $"The {what} rule" : $"{what} rule {i + 1}";

            foreach (var finding in CheckOne(condition, map, localPlayer, where))
                findings.Add(finding);
        }

        // Every condition met at the start means the mission ends on its first tick.
        if (map is not null && !set.IsEmpty && set.Match == Match.All
            && set.Conditions.All(c => MetAtStart(c, map, localPlayer) == true))
        {
            findings.Add(new Finding(FindingLevel.Problem,
                $"Every {what} condition is already true when the mission starts, so it ends "
                + "immediately."));
        }
    }

    private static IEnumerable<Finding> CheckOne(Condition condition, PudFile? map,
                                                 int localPlayer, string where)
    {
        // A counter the build does not know is a mod written by a newer version, or a typo.
        if (NeedsCounter(condition.Kind))
        {
            var known = condition.Kind == ConditionKind.Resource
                ? ResourceCatalog.Find(condition.Counter) is not null
                : CounterCatalog.Find(condition.Counter) is not null;

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

        if (condition.Player is { } player && (player < 0 || player > NeutralPlayer))
            yield return new Finding(FindingLevel.Problem,
                $"{where} names player {player}. The game has 0 to {NeutralPlayer}.");

        if (map is null) yield break;

        // What the map actually starts with, which is the only thing knowable here.
        if (MetAtStart(condition, map, localPlayer) == true && !condition.Invert)
        {
            yield return new Finding(FindingLevel.Warning,
                $"{where} is already true when the mission starts.");
        }

        foreach (var finding in CheckReachable(condition, map, localPlayer, where))
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
                                                       int localPlayer, string where)
    {
        if (condition.Kind is not ConditionKind.Delivered)
        {
            if (CounterCatalog.Find(condition.Counter) is not { } known) yield break;
            if (known.UnitTypes.Count == 0) yield break;
        }

        var counter = CounterCatalog.Find(condition.Counter);

        if (condition.Kind == ConditionKind.EnemyHasNone && counter is not null)
        {
            var enemies = map.Units.Count(u => u.Player != localPlayer
                                               && u.Player != NeutralPlayer
                                               && counter.UnitTypes.Contains(u.Type));

            if (enemies == 0)
                yield return new Finding(FindingLevel.Warning,
                    $"{where} waits for the enemy to lose every {counter.Label}, and the map "
                    + "starts with none. It is true from the first tick.");
        }

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

    /// <summary>
    /// Whether a condition already holds on the map's opening position.
    ///
    /// Null when it cannot be decided from a map alone, which is most kinds: a mission is
    /// not won at the start by anybody's kill count.
    /// </summary>
    private static bool? MetAtStart(Condition condition, PudFile map, int localPlayer)
    {
        var player = condition.Player ?? localPlayer;

        bool? met = condition.Kind switch
        {
            ConditionKind.OwnCount => CounterCatalog.Find(condition.Counter) is { } c && c.UnitTypes.Count > 0
                ? Compare(map.Units.Count(u => u.Player == player && c.UnitTypes.Contains(u.Type)),
                          condition.Op, condition.Count)
                : null,

            ConditionKind.EnemyHasNone => CounterCatalog.Find(condition.Counter) is { } e && e.UnitTypes.Count > 0
                ? !map.Units.Any(u => u.Player != localPlayer && u.Player != NeutralPlayer
                                      && e.UnitTypes.Contains(u.Type))
                : null,

            ConditionKind.UnitAlive => CounterCatalog.Find(condition.Counter) is { } a && a.UnitTypes.Count > 0
                ? map.Units.Any(u => a.UnitTypes.Contains(u.Type)
                                     && (condition.Player is null || u.Player == player))
                : null,

            // Nobody has killed, razed, delivered or rescued anything yet.
            ConditionKind.Kills or ConditionKind.Razings
                or ConditionKind.Delivered or ConditionKind.Rescued
                => Compare(0, condition.Op, condition.Count),

            _ => null,
        };

        return met is null ? null : met.Value ^ condition.Invert;
    }

    private static bool Compare(int actual, Compare op, int wanted) => op switch
    {
        Scenarios.Compare.AtLeast => actual >= wanted,
        Scenarios.Compare.AtMost => actual <= wanted,
        _ => actual == wanted,
    };

    private static bool NeedsCounter(ConditionKind kind) => kind switch
    {
        ConditionKind.OwnCount or ConditionKind.EnemyHasNone
            or ConditionKind.UnitAlive or ConditionKind.Resource => true,
        _ => false,
    };
}
