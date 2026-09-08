using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RuneFoundry.Core.Formats;
using RuneFoundry.Core.Scenarios;
using RuneFoundry.UI;

namespace RuneFoundry.Editor.Views;

/// <summary>A choice in one of the row's dropdowns, with the word the author reads.</summary>
public sealed record RuleChoice(string Label, object? Value)
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
    public static readonly IReadOnlyList<RuleChoice> AllKinds = new[]
    {
        new RuleChoice("Owns at least", ConditionKind.OwnCount),
        new RuleChoice("Enemy has none", ConditionKind.EnemyHasNone),
        new RuleChoice("Player is eliminated", ConditionKind.PlayerEliminated),
        new RuleChoice("A unit is alive", ConditionKind.UnitAlive),
        new RuleChoice("Brought to the Circle", ConditionKind.Delivered),
        new RuleChoice("Rescued", ConditionKind.Rescued),
        new RuleChoice("Resource held", ConditionKind.Resource),
        new RuleChoice("Units killed", ConditionKind.Kills),
        new RuleChoice("Buildings razed", ConditionKind.Razings),
    };

    public static readonly IReadOnlyList<Compare> AllComparisons =
        new[] { Compare.AtLeast, Compare.AtMost, Compare.Exactly };

    /// <summary>The player slots, with the one at the keyboard first because it is the usual answer.</summary>
    public static readonly IReadOnlyList<RuleChoice> AllPlayers =
        new[] { new RuleChoice("You", (object?)null) }
            .Concat(Enumerable.Range(0, GameAddresses.PlayerCount)
                .Select(i => new RuleChoice($"Player {i}", (object?)i)))
            .ToList();

    private RuleChoice _kind = AllKinds[0];
    private RuleChoice? _counter;
    private RuleChoice _player = AllPlayers[0];
    private Compare _op = Compare.AtLeast;
    private int _count = 1;
    private int _heroes;
    private bool _finishedOnly;

    // Instance properties, because a template binds to the row rather than to the type.
    public IReadOnlyList<RuleChoice> Kinds => AllKinds;
    public IReadOnlyList<RuleChoice> Players => AllPlayers;
    public IReadOnlyList<Compare> Comparisons => AllComparisons;

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

            if (Counter is not null && !Counters.Contains(Counter)) Counter = Counters.FirstOrDefault();
            else if (Counter is null) Counter = Counters.FirstOrDefault();

            Raise(nameof(Summary));
        }
    }

    public RuleChoice? Counter
    {
        get => _counter;
        set { if (Set(ref _counter, value)) { Raise(nameof(CanBeFinishedOnly)); Raise(nameof(Summary)); } }
    }

    public RuleChoice Player
    {
        get => _player;
        set { if (Set(ref _player, value)) Raise(nameof(Summary)); }
    }

    public Compare Op
    {
        get => _op;
        set { if (Set(ref _op, value)) Raise(nameof(Summary)); }
    }

    public int Count
    {
        get => _count;
        set { if (Set(ref _count, value)) Raise(nameof(Summary)); }
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

    /// <summary>What this kind can count, which is a different list for a resource.</summary>
    public IReadOnlyList<RuleChoice> Counters =>
        KindValue == ConditionKind.Resource
            ? ResourceCatalog.All.Select(c => new RuleChoice(c.Label, c.Name)).ToList()
            : CounterCatalog.All.Select(c => new RuleChoice(c.Label, c.Name)).ToList();

    public bool NeedsCounter => KindValue is ConditionKind.OwnCount or ConditionKind.EnemyHasNone
        or ConditionKind.UnitAlive or ConditionKind.Resource;

    public bool NeedsCount => KindValue is not (ConditionKind.PlayerEliminated or ConditionKind.UnitAlive
        or ConditionKind.EnemyHasNone);

    public bool NeedsHeroes => KindValue == ConditionKind.Delivered;

    /// <summary>Only where the game keeps a second, finished-only tally.</summary>
    public bool CanBeFinishedOnly =>
        NeedsCounter && CounterCatalog.Find(Counter?.Value as string)?.Active is not null;

    /// <summary>The rule as a sentence, which is the only check most authors will read.</summary>
    public string Summary
    {
        get
        {
            var who = Player.Value is int p ? $"player {p}" : "you";
            var thing = CounterCatalog.Describe(Counter?.Value as string).ToLowerInvariant();
            var how = Op switch
            {
                Compare.AtMost => "at most",
                Compare.Exactly => "exactly",
                _ => "at least",
            };

            return KindValue switch
            {
                ConditionKind.OwnCount =>
                    $"When {who} own {how} {Count} {thing}{(FinishedOnly ? ", finished" : "")}.",
                ConditionKind.EnemyHasNone => $"When no enemy has a {thing} left.",
                ConditionKind.PlayerEliminated => $"When {who} have nothing left.",
                ConditionKind.UnitAlive => $"While {who} still have a {thing}.",
                ConditionKind.Delivered => Heroes > 0
                    ? $"When {who} bring {how} {Count} to the Circle, {Heroes} of them heroes."
                    : $"When {who} bring {how} {Count} to the Circle.",
                ConditionKind.Rescued => $"When {who} rescue {how} {Count}.",
                ConditionKind.Resource => $"When {who} hold {how} {Count} {thing}.",
                ConditionKind.Kills => $"When {who} kill {how} {Count}.",
                ConditionKind.Razings => $"When {who} raze {how} {Count}.",
                _ => "",
            };
        }
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
            _kind = AllKinds.FirstOrDefault(k => (ConditionKind)k.Value! == condition.Kind) ?? AllKinds[0],
            _op = condition.Op,
            _count = condition.Count,
            _heroes = condition.Heroes,
            _finishedOnly = condition.FinishedOnly,
            _player = AllPlayers.FirstOrDefault(p => Equals(p.Value, condition.Player)) ?? AllPlayers[0],
        };

        row._counter = row.Counters.FirstOrDefault(c => (string)c.Value! == condition.Counter)
                       ?? row.Counters.FirstOrDefault();

        return row;
    }
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
