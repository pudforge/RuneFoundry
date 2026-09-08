using System.Text.Json.Serialization;

namespace RuneFoundry.Core.Scenarios;

/// <summary>Whether every condition has to hold, or any one of them.</summary>
public enum Match
{
    All,
    Any,
}

/// <summary>
/// What a condition looks at.
///
/// Each one is a plain read from the running game. Nothing here needs the game to be
/// patched: the counters are arrays the game maintains for its own scoreboard, and the
/// research behind each is in CUSTOM-SCENARIOS.md §3.
/// </summary>
public enum ConditionKind
{
    /// <summary>How many of a counted thing a player owns.</summary>
    OwnCount,

    /// <summary>No player hostile to the local one owns any of a counted thing.</summary>
    EnemyHasNone,

    /// <summary>A player is out: no buildings, and no units that count towards defeat.</summary>
    PlayerEliminated,

    /// <summary>A named unit type is still alive somewhere.</summary>
    UnitAlive,

    /// <summary>Units brought to the Circle of Power, heroes counted separately.</summary>
    Delivered,

    /// <summary>Units rescued.</summary>
    Rescued,

    /// <summary>Gold, lumber or oil held.</summary>
    Resource,

    /// <summary>Units killed.</summary>
    Kills,

    /// <summary>Buildings razed.</summary>
    Razings,
}

/// <summary>How a count is compared with the number the author asked for.</summary>
public enum Compare
{
    AtLeast,
    AtMost,
    Exactly,
}

/// <summary>
/// One thing that has to be true.
///
/// Deliberately one flat record rather than a class per kind. The set is small, the fields
/// a kind ignores are simply left at their defaults, and a flat record is what survives a
/// round trip through JSON without a custom converter.
/// </summary>
/// <param name="Player">
/// Whose things to count. Null means the player at the keyboard, which is what an author
/// means nine times in ten.
/// </param>
/// <param name="Counter">A name from <see cref="CounterCatalog"/>.</param>
/// <param name="Heroes">For <see cref="ConditionKind.Delivered"/>: how many must be heroes.</param>
/// <param name="FinishedOnly">
/// Count only what is built. The game keeps two tables, one counting a building from the
/// moment its foundation is laid and one only when it is finished, so "owns a Castle" and
/// "owns a finished Castle" are different questions and both are askable.
/// </param>
/// <param name="Invert">Satisfied when the test does not hold.</param>
/// <param name="Latch">
/// Once true, stays true for the rest of the mission. Only safe on values the game itself
/// never decreases; the editor does not offer it yet.
/// </param>
public sealed record Condition(
    ConditionKind Kind,
    int? Player = null,
    string? Counter = null,
    Compare Op = Compare.AtLeast,
    int Count = 1,
    int Heroes = 0,
    bool FinishedOnly = false,
    bool Invert = false,
    bool Latch = false);

/// <summary>A group of conditions and how they combine.</summary>
public sealed record ConditionSet(Match Match, IReadOnlyList<Condition> Conditions)
{
    public static ConditionSet Empty { get; } = new(Match.All, Array.Empty<Condition>());

    /// <summary>An empty set asks nothing, so it can never be met.</summary>
    [JsonIgnore]
    public bool IsEmpty => Conditions.Count == 0;
}

/// <summary>
/// What a mission is won and lost by, as the author wrote it.
///
/// Held per mission slot on the project and carried in the mod. Nothing here knows about a
/// running game: this is the author's intent, and reading it back out is somebody else's
/// job.
/// </summary>
public sealed record ScenarioRules(ConditionSet Victory, ConditionSet Defeat)
{
    /// <summary>
    /// Bumped when the shape changes in a way an older build cannot read. A mod carrying a
    /// version this build does not know is refused rather than half understood.
    /// </summary>
    public const int Version = 1;

    public static ScenarioRules Empty { get; } =
        new(ConditionSet.Empty, ConditionSet.Empty);

    /// <summary>Whether the author actually asked for anything.</summary>
    [JsonIgnore]
    public bool IsEmpty => Victory.IsEmpty && Defeat.IsEmpty;
}
