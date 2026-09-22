using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RuneFoundry.Core.Formats;
using RuneFoundry.Core.Scenarios;
using RuneFoundry.UI;

namespace RuneFoundry.Editor.Views;

/// <summary>A choice in one of the row's dropdowns, with the word the author reads.</summary>
public sealed record RuleChoice(string Label, object? Value, string Group = "")
{
    public override string ToString() => Label;
}

/// <summary>
/// One condition, as the editor shows it.
///
/// The row decides which of its own fields make sense: a kill count needs no counter, and
/// a delivery needs a hero count nothing else does. Hiding what does not apply beats
/// greying it, because a greyed box still asks the reader what it would have meant.
/// </summary>
public sealed class ConditionRow : Observable
{
    // The verb of the sentence, and only the verb. "Owns at least" used to be one of
    // these, which put the words "at least" on screen twice: once here and again in the
    // comparison beside it. A kind says what is counted, a comparison says how many, and
    // neither says the other's half.
    //
    // Two lists, because "You owns" is the kind of thing that makes a form feel like a
    // form. The row offers the list that agrees with its player, and swaps when the
    // player changes without losing which kind was chosen.
    public static readonly IReadOnlyList<RuleChoice> AllKinds = new[]
    {
        new RuleChoice("own unit", ConditionKind.OwnUnits, "unit"),
        new RuleChoice("own unit category", ConditionKind.OwnUnits, "category"),
        new RuleChoice("face no enemy", ConditionKind.EnemyHasNone),
        new RuleChoice("are eliminated", ConditionKind.PlayerEliminated),
        new RuleChoice("have delivered", ConditionKind.Delivered),
        new RuleChoice("have rescued", ConditionKind.Rescued),
        new RuleChoice("hold", ConditionKind.Resource),
        new RuleChoice("have killed", ConditionKind.Kills),
        new RuleChoice("have razed", ConditionKind.Razings),
    };

    public static readonly IReadOnlyList<RuleChoice> AllKindsThirdPerson = new[]
    {
        new RuleChoice("owns unit", ConditionKind.OwnUnits, "unit"),
        new RuleChoice("owns unit category", ConditionKind.OwnUnits, "category"),
        new RuleChoice("faces no enemy", ConditionKind.EnemyHasNone),
        new RuleChoice("is eliminated", ConditionKind.PlayerEliminated),
        new RuleChoice("has delivered", ConditionKind.Delivered),
        new RuleChoice("has rescued", ConditionKind.Rescued),
        new RuleChoice("holds", ConditionKind.Resource),
        new RuleChoice("has killed", ConditionKind.Kills),
        new RuleChoice("has razed", ConditionKind.Razings),
    };

    private static IReadOnlyList<RuleChoice> KindsFor(RuleChoice player) =>
        player.Value is null ? AllKinds : AllKindsThirdPerson;

    private static RuleChoice KindFor(ConditionKind kind, RuleChoice player, bool category = false) =>
        KindsFor(player).FirstOrDefault(k => (ConditionKind)k.Value! == Upgraded(kind)
            && (Upgraded(kind) != ConditionKind.OwnUnits || (k.Group == "category") == category))
        ?? KindsFor(player).FirstOrDefault(k => (ConditionKind)k.Value! == Upgraded(kind))
        ?? KindsFor(player)[0];

    /// <summary>
    /// The kind a stored rule is edited as. The two counter kinds and the old keep-alive
    /// kind all become "own": same keys, one walk. A mod is only rewritten when its author
    /// saves the mission, so nothing changes under a mod that is merely installed.
    /// </summary>
    private static ConditionKind Upgraded(ConditionKind kind) => kind switch
    {
        ConditionKind.OwnCount or ConditionKind.UnitAlive => ConditionKind.OwnUnits,
        _ => kind,
    };

    /// <summary>
    /// Worded, because the enum's own names went to the screen unchanged: a dropdown
    /// reading "AtLeast" is a field name, not something a person would say.
    /// </summary>
    public static readonly IReadOnlyList<RuleChoice> AllComparisons = new[]
    {
        new RuleChoice("at least", Compare.AtLeast),
        new RuleChoice("at most", Compare.AtMost),
        new RuleChoice("exactly", Compare.Exactly),
    };

    /// <summary>
    /// Whose things a condition counts.
    ///
    /// The player at the keyboard comes first because it is the usual answer, and "anyone"
    /// second because it is the next most useful: "anyone has four farms" is a race, and
    /// naming the slot means knowing which slot the map gave the opponent.
    /// </summary>
    public static readonly IReadOnlyList<RuleChoice> AllPlayers =
        new[]
            {
                new RuleChoice("You", (object?)null),
                new RuleChoice("Any player", RuneFoundry.Core.Scenarios.Condition.AnyPlayer),
            }
            .Concat(Enumerable.Range(0, GameAddresses.PlayerCount)
                .Select(i => new RuleChoice(PlayerColors.Label(i), (object?)i)))
            .ToList();

    private RuleChoice _kind = AllKinds[0];
    private RuleChoice? _counter;
    private RuleChoice _player = AllPlayers[0];
    private Compare _op = Compare.AtLeast;
    private int _count = 1;
    private int _heroes;
    private bool _finishedOnly;

    /// <summary>The two words a set can be joined by, as the gutter dropdown offers them.</summary>
    public static readonly IReadOnlyList<RuleChoice> AllJoins = new[]
    {
        new RuleChoice("and", Match.All),
        new RuleChoice("or", Match.Any),
    };

    public static RuleChoice JoinFor(Match match) => AllJoins[match == Match.Any ? 1 : 0];

    public IReadOnlyList<RuleChoice> Joins => AllJoins;

    /// <summary>
    /// The word joining this row to the one above it, and the control for it.
    ///
    /// The set's all-or-any used to be a pair of chips above the rows it governed, which is
    /// the shape of a setting rather than of a sentence. The word now sits in a column down
    /// the left, so the rows read straight down: this, and this, and this. It is a dropdown
    /// rather than a label because it is the one place a reader would reach to change it,
    /// and it is only shown from the second row, since a single row is joined to nothing.
    ///
    /// The row does not own the set's match; choosing a word calls back to whoever does.
    /// </summary>
    private RuleChoice _joinChoice = AllJoins[0];
    private bool _showJoin;

    public RuleChoice JoinChoice
    {
        get => _joinChoice;
        set
        {
            if (value is null || !Set(ref _joinChoice, value)) return;
            SetMatch?.Invoke((Match)value.Value!);
        }
    }

    public bool ShowJoin
    {
        get => _showJoin;
        set => Set(ref _showJoin, value);
    }

    /// <summary>Told the set's new match when the gutter word is changed. Set by the view.</summary>
    public Action<Match>? SetMatch { get; set; }

    /// <summary>
    /// A new row starts with a counter chosen, not blank. The kind's setter picks one when
    /// the kind changes, but nothing ran it for the first kind, so a freshly added condition
    /// opened already failing validation for a reason the author had not caused yet.
    /// </summary>
    public ConditionRow()
    {
        _counter = Counters.FirstOrDefault();
    }

    // Instance properties, because a template binds to the row rather than to the type.
    public IReadOnlyList<RuleChoice> Kinds => KindsFor(Player);
    public IReadOnlyList<RuleChoice> Players => AllPlayers;
    public IReadOnlyList<RuleChoice> Comparisons => AllComparisons;

    public RuleChoice Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value)) return;

            // The counter list depends on the kind, so a kind that no longer fits its
            // counter drops it rather than keeping a name that means nothing here.
            Raise(nameof(Counters));
            Raise(nameof(NeedsCounter));
            Raise(nameof(NeedsCount));
            Raise(nameof(NeedsHeroes));
            Raise(nameof(CanBeFinishedOnly));

            if (Counter is null || !Counters.Contains(Counter))
                Counter = Counters.FirstOrDefault(c => (string?)c.Value == "units")
                          ?? Counters.FirstOrDefault(c => c.Group != Heading);

            Raise(nameof(Summary));
        }
    }

    public RuleChoice? Counter
    {
        get => _counter;
        set
        {
            if (value is { Group: Heading }) return;   // a heading is not a choice
            if (Set(ref _counter, value)) { Raise(nameof(CanBeFinishedOnly)); Raise(nameof(Summary)); }
        }
    }

    public RuleChoice Player
    {
        get => _player;
        set
        {
            if (!Set(ref _player, value)) return;

            // The verbs change person with the player, so the list is swapped and the same
            // kind picked again out of the new one, so nothing the author chose is lost.
            var kind = KindValue;
            Raise(nameof(Kinds));
            _kind = KindFor(kind, value);
            Raise(nameof(Kind));
            Raise(nameof(Summary));
        }
    }

    /// <summary>The comparison, as the worded choice the dropdown shows.</summary>
    public RuleChoice OpChoice
    {
        get => AllComparisons.FirstOrDefault(c => (Compare)c.Value! == _op) ?? AllComparisons[0];
        set
        {
            if (value is null || (Compare)value.Value! == _op) return;
            _op = (Compare)value.Value!;
            Raise(nameof(OpChoice));
            Raise(nameof(Summary));
        }
    }

    /// <summary>The comparison itself, for everything that is not the dropdown.</summary>
    public Compare Op => _op;

    public int Count
    {
        get => _count;
        set { if (Set(ref _count, value)) { Raise(nameof(Summary)); } }
    }

    public int Heroes
    {
        get => _heroes;
        set { if (Set(ref _heroes, value)) Raise(nameof(Summary)); }
    }

    public bool FinishedOnly
    {
        get => _finishedOnly;
        set { if (Set(ref _finishedOnly, value)) Raise(nameof(Summary)); }
    }

    public ConditionKind KindValue => (ConditionKind)Kind.Value!;

    /// <summary>Never a heading now; kept so older callers still compile.</summary>
    public const string Heading = "heading";

    /// <summary>Whether the chosen verb is "own unit category" rather than "own unit".</summary>
    private bool OwnsCategory => KindValue == ConditionKind.OwnUnits && Kind.Group == "category";

    /// <summary>
    /// What this kind can count. For the walk, one flat list per verb: every exact unit for
    /// "own unit", every category for "own unit category". Splitting the verb is what lets
    /// each list stay flat and short instead of one long list under two headings.
    /// </summary>
    public IReadOnlyList<RuleChoice> Counters => KindValue switch
    {
        ConditionKind.Resource => ResourceCatalog.All.Select(c => new RuleChoice(c.Label, c.Name)).ToList(),
        ConditionKind.OwnUnits => UnitTypeCatalog.All
            .Where(c => c.Group == (OwnsCategory ? UnitTypeCatalog.GroupsGroup : UnitTypeCatalog.UnitsGroup))
            .Select(c => new RuleChoice(c.Label, c.Name)).ToList(),
        _ => CounterCatalog.All.Select(c => new RuleChoice(c.Label, c.Name)).ToList(),
    };

    public bool NeedsCounter => KindValue is ConditionKind.OwnCount or ConditionKind.EnemyHasNone
        or ConditionKind.UnitAlive or ConditionKind.Resource or ConditionKind.OwnUnits;

    public bool NeedsCount => KindValue is not (ConditionKind.PlayerEliminated or ConditionKind.UnitAlive
        or ConditionKind.EnemyHasNone);

    public bool NeedsHeroes => KindValue == ConditionKind.Delivered;

    /// <summary>
    /// Never offered.
    ///
    /// There was a "finished only" box here. The game's second tally does not mean that:
    /// it covers the combat types alone, so no building has one, and it reads zero for
    /// units that are plainly on the map. The field stays on the condition so an older mod
    /// still loads, and nothing sets it any more.
    /// </summary>
    public bool CanBeFinishedOnly => false;

    /// <summary>
    /// The condition as a clause, with no capital and no full stop, so the view can join
    /// several into one sentence: "you own at least 4 Farms, and either ... or ...".
    ///
    /// A rule read back as English is the only check most authors will ever make on it,
    /// and it is the check that catches the mistakes a form cannot show: a comparison the
    /// wrong way round, a count of the wrong thing, a bracket around the wrong pair.
    /// </summary>
    public string Summary
    {
        get
        {
            var who = Player.Value switch
            {
                RuneFoundry.Core.Scenarios.Condition.AnyPlayer => "any player",
                int p => PlayerColors.Of(p) is { } colour ? $"player {p + 1} ({colour})" : $"player {p + 1}",
                _ => "you",
            };
            var second = Player.Value is null;
            var verb = Kind.Label;
            var thing = Thing();
            var how = Op switch
            {
                Compare.AtMost => "at most",
                Compare.Exactly => "exactly",
                _ => "at least",
            };

            return KindValue switch
            {
                ConditionKind.OwnCount => $"{who} {verb} {how} {Count} {thing}",
                ConditionKind.EnemyHasNone => $"no enemy has a {thing} left",
                ConditionKind.PlayerEliminated => $"{who} {verb}",
                ConditionKind.UnitAlive => $"{who} still {(second ? "have" : "has")} a {thing}",
                ConditionKind.Delivered => Heroes > 0
                    ? $"{who} {verb} {how} {Count} to the Circle of Power, {Heroes} of them heroes"
                    : $"{who} {verb} {how} {Count} to the Circle of Power",
                ConditionKind.Rescued => $"{who} {verb} {how} {Count}",
                ConditionKind.Resource => $"{who} {verb} {how} {Count} {thing}",
                ConditionKind.Kills => $"{who} {verb} {how} {Count}",
                ConditionKind.Razings => $"{who} {verb} {how} {Count}",
                ConditionKind.OwnUnits => $"{who} {(second ? "own" : "owns")} {how} {Count} {thing}",
                _ => "",
            };
        }
    }

    /// <summary>The counted thing as it reads in a sentence. The two aggregates take the number.</summary>
    private string Thing()
    {
        var label = KindValue == ConditionKind.OwnUnits
            ? UnitTypeCatalog.Find(Counter?.Value as string)?.Label ?? ""
            : CounterCatalog.Describe(Counter?.Value as string);
        var one = Count == 1;
        return label switch
        {
            "Any unit" => one ? "unit" : "units",
            "Any building" => one ? "building" : "buildings",
            _ => label,
        };
    }

    public RuneFoundry.Core.Scenarios.Condition ToCondition() => new(
        KindValue,
        Player.Value as int?,
        Counter?.Value as string,
        Op,
        Count,
        Heroes,
        FinishedOnly);

    public static ConditionRow From(RuneFoundry.Core.Scenarios.Condition condition)
    {
        var row = new ConditionRow
        {
            _player = AllPlayers.FirstOrDefault(p => Equals(p.Value, condition.Player)) ?? AllPlayers[0],
            _op = condition.Op,
            _count = condition.Count,
            _heroes = condition.Heroes,
            _finishedOnly = condition.FinishedOnly,
        };

        var storedIsCategory = UnitTypeCatalog.Find(condition.Counter)?.Group == UnitTypeCatalog.GroupsGroup
                               || condition.Kind is ConditionKind.OwnCount or ConditionKind.UnitAlive;
        row._kind = KindFor(condition.Kind, row._player, storedIsCategory);
        if (condition.Kind == ConditionKind.UnitAlive) { row._op = Compare.AtLeast; row._count = 1; }
        // Keep an unknown key rather than silently resampling to some other unit: a rule
        // that counts a thing this build does not know should read blank and be flagged by
        // the validator, not quietly become "own 0 Peasants". Only fall back when there is
        // no key at all.
        row._counter = row.Counters.FirstOrDefault(c => (string?)c.Value == condition.Counter)
                       ?? (string.IsNullOrEmpty(condition.Counter)
                            ? row.Counters.FirstOrDefault(c => c.Group != Heading)
                            : new RuleChoice($"(unknown: {condition.Counter})", condition.Counter));

        return row;
    }
}

/// <summary>
/// A bracketed set of conditions, with its own all-or-any.
///
/// One level of nesting is offered, which is what "this and (that or the other)" needs. A
/// group inside a group is expressible in the file and simply is not drawn: the shapes
/// people actually write stop at one bracket, and a tree editor is a great deal of UI for
/// the second one.
/// </summary>
public sealed class GroupRow : Observable
{
    public List<ConditionRow> Rows { get; } = new();

    /// <summary>The word joining this group to what is above it. See ConditionRow.JoinChoice.</summary>
    private RuleChoice _joinChoice = ConditionRow.AllJoins[0];
    private bool _showJoin;

    public IReadOnlyList<RuleChoice> Joins => ConditionRow.AllJoins;

    public RuleChoice JoinChoice
    {
        get => _joinChoice;
        set
        {
            if (value is null || !Set(ref _joinChoice, value)) return;
            SetMatch?.Invoke((Match)value.Value!);
        }
    }

    public bool ShowJoin
    {
        get => _showJoin;
        set => Set(ref _showJoin, value);
    }

    /// <summary>Told the parent set's new match when this group's gutter word changes.</summary>
    public Action<Match>? SetMatch { get; set; }

    private Match _match = Match.Any;

    public Match Match
    {
        get => _match;
        set { if (Set(ref _match, value)) { Raise(nameof(IsAll)); Raise(nameof(IsAny)); } }
    }

    // Two booleans rather than one, because a pair of radio buttons binds to two.
    public bool IsAll
    {
        get => Match == Match.All;
        set { if (value) Match = Match.All; }
    }

    public bool IsAny
    {
        get => Match == Match.Any;
        set { if (value) Match = Match.Any; }
    }

    public ConditionSet ToSet() =>
        new(Match, Rows.Select(r => r.ToCondition()).ToList());

    public static GroupRow From(ConditionSet set, Action<ConditionRow> track)
    {
        var group = new GroupRow { Match = set.Match };

        foreach (var condition in set.Conditions)
        {
            var row = ConditionRow.From(condition);
            track(row);
            group.Rows.Add(row);
        }

        return group;
    }
}

/// <summary>
/// True to visible, false to hidden rather than collapsed.
///
/// A row hides the fields its kind does not use. Collapsing them takes their width away as
/// well, so every row of a different kind sat at a different set of positions and no two
/// lined up. Hidden keeps the space, and the rows read down the page as a table.
/// </summary>
public sealed class BoolToSpaceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Hidden;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Paints a finding by how much it matters.</summary>
public sealed class FindingColourConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is FindingLevel.Problem ? "Bad"
            : value is FindingLevel.Warning ? "Warn"
            : "TextDim";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
