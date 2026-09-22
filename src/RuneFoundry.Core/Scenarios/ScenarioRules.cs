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

    /// <summary>
    /// How many of a named set of unit types a player has alive, found by walking the
    /// unit records. The set is one exact unit (Grom Hellscream) or a group (Farm / Pig
    /// Farm, Any building); either way it is one walk and one count. This is the one kind
    /// the editor offers for "own", and the question the game's own hero rules ask.
    /// <see cref="OwnCount"/> and <see cref="UnitAlive"/> read the scoreboard counters
    /// instead and are kept for mods that already carry them.
    /// </summary>
    OwnUnits,
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
/// means nine times in ten. <see cref="Condition.AnyPlayer"/> means the condition holds as
/// soon as it holds for somebody.
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
    bool Latch = false)
{
    /// <summary>
    /// Stands for "whichever player", in <see cref="Player"/>.
    ///
    /// A negative number because the game's slots are 0 to 15, so it can never collide with
    /// a real one, and it survives JSON without a second field to say which kind of answer
    /// the first field is.
    /// </summary>
    public const int AnyPlayer = -1;

    /// <summary>Whether this condition asks about everybody rather than one player.</summary>
    [JsonIgnore]
    public bool IsAnyPlayer => Player == AnyPlayer;
}

/// <summary>
/// A group of conditions, and groups of those, and how they combine.
///
/// The nesting is what lets a rule say "O1 and (O2 or O3)": the outer set is All over one
/// condition and one group, and the group is Any over two. One <see cref="Match"/> covers
/// everything in a set, its own conditions and its groups alike, which is the shape a
/// person draws when they bracket something.
///
/// <para>
/// <see cref="Groups"/> defaults to empty, so every rule written before nesting existed
/// still reads as exactly what it was.
/// </para>
/// </summary>
public sealed record ConditionSet(
    Match Match,
    IReadOnlyList<Condition> Conditions,
    IReadOnlyList<ConditionSet>? Groups = null)
{
    public static ConditionSet Empty { get; } = new(Match.All, Array.Empty<Condition>());

    /// <summary>The nested groups, never null.</summary>
    [JsonIgnore]
    public IReadOnlyList<ConditionSet> Nested => Groups ?? Array.Empty<ConditionSet>();

    /// <summary>An empty set asks nothing, so it can never be met.</summary>
    [JsonIgnore]
    public bool IsEmpty => Conditions.Count == 0 && Nested.All(g => g.IsEmpty);

    /// <summary>Every condition in here and in everything under it.</summary>
    public IEnumerable<Condition> Flatten() =>
        Conditions.Concat(Nested.SelectMany(g => g.Flatten()));
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
    public const int Version = 2;   // 2: TypeAlive. A build without it would read the kind
                                    // as a number it has no case for, and the rule would
                                    // never fire; refusing the mod outright is kinder.

    public static ScenarioRules Empty { get; } =
        new(ConditionSet.Empty, ConditionSet.Empty);

    /// <summary>Whether the author actually asked for anything.</summary>
    [JsonIgnore]
    public bool IsEmpty => Victory.IsEmpty && Defeat.IsEmpty;
}
