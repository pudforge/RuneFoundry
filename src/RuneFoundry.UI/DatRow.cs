using RuneFoundry.Core.Formats;

namespace RuneFoundry.UI;

/// <summary>One flag bit as a checkbox, inside a <see cref="DatRow"/>.</summary>
public sealed class DatFlag : Observable
{
    private readonly DatTable _table;
    private readonly DatField _field;
    private readonly int _record;

    public DatOption Option { get; }

    public DatFlag(DatTable table, DatField field, int record, DatOption option)
    {
        _table = table;
        _field = field;
        _record = record;
        Option = option;
    }

    /// <summary>Names the schema is unsure of are marked, rather than quietly trusted.</summary>
    public string Label => Option.Certain ? Option.Label : Option.Label + " (?)";

    public bool IsCertain => Option.Certain;

    public event Action? Changed;

    public bool IsSet
    {
        get => _table.HasFlag(_field.Key, _record, Option);
        set
        {
            if (value == IsSet) return;
            _table.SetFlag(_field.Key, _record, Option, value);
            Raise();
            Changed?.Invoke();
        }
    }
}

/// <summary>One field of one record changing, with enough to undo it.</summary>
/// <param name="FieldKey">The schema key, which is what writes it back.</param>
/// <param name="FieldLabel">What the field is called on screen, for the menu text.</param>
/// <param name="Component">0 for an ordinary field; size fields have an x and a y.</param>
public sealed record DatFieldEdit(
    string FieldKey, string FieldLabel, int Record, int Component, long Was, long Now);

/// <summary>
/// One editable field of one record, as the .dat editor shows it.
///
/// The control to draw is not decided here — the schema already says, via
/// <see cref="DatField.Kind"/>. This just exposes whichever shape that implies, so adding
/// a field to the schema is enough to get an editor for it.
/// </summary>
public sealed class DatRow : Observable
{
    private readonly DatTable _table;
    private readonly int _record;

    public DatField Field { get; }

    /// <summary>Raised when the value actually changes, so the view can mark itself dirty.</summary>
    public event Action? Edited;

    /// <summary>
    /// Raised with what the value was and what it became, which is everything undo needs.
    ///
    /// Separate from <see cref="Edited"/> because most listeners only want to know that
    /// something happened; this one is for the listener that has to be able to put it back.
    /// </summary>
    public event Action<DatFieldEdit>? Committed;

    public DatRow(DatTable table, DatField field, int record)
    {
        _table = table;
        _record = record;
        Field = field;

        if (field.Kind == DatControlKind.Flags && field.Options is not null)
        {
            Flags = field.Options.Select(option =>
            {
                var flag = new DatFlag(table, field, record, option);
                flag.Changed += () => { Raise(nameof(Summary)); Edited?.Invoke(); };
                return flag;
            }).ToArray();
        }
    }

    public string Display => Field.Display;
    public string? Help => Field.Help;
    private bool _isReadOnly;

    /// <summary>Set while the file is still the stock one; the row stays readable.</summary>
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set { if (Set(ref _isReadOnly, value)) Raise(nameof(IsEditable)); }
    }

    /// <summary>Editable only if the schema allows it and the mod owns the file.</summary>
    public bool IsEditable => Field.Editable && !_isReadOnly;

    public bool IsNumber => Field.Kind == DatControlKind.Number;
    public bool IsToggle => Field.Kind == DatControlKind.Toggle;
    public bool IsChoice => Field.Kind == DatControlKind.Choice;
    public bool IsFlags => Field.Kind == DatControlKind.Flags;

    /// <summary>Only for <see cref="DatControlKind.Flags"/>; empty otherwise.</summary>
    public DatFlag[] Flags { get; } = Array.Empty<DatFlag>();

    public IReadOnlyList<DatOption> Options => Field.Options ?? Array.Empty<DatOption>();

    /// <summary>The x component, or the whole value for the usual single-component field.</summary>
    public long Value
    {
        get => _table.Get(Field.Key, _record);
        set => Assign(value, 0);
    }

    /// <summary>Size fields store an x and a y; this is the second.</summary>
    public long SecondValue
    {
        get => Field.PerRecord > 1 ? _table.Get(Field.Key, _record, 1) : 0;
        set => Assign(value, 1);
    }

    public bool HasSecond => Field.PerRecord > 1;

    private void Assign(long value, int component)
    {
        long was = 0;
        var changed = false;

        try
        {
            was = _table.Get(Field.Key, _record, component);
            if (was == value) return;

            _table.Set(Field.Key, _record, value, component);
            changed = true;
            Error = null;
        }
        catch (Exception ex)
        {
            // Costs are stored in tens, so 605 has nowhere to go. Refusing beats rounding.
            Error = ex.Message;
        }

        Raise(nameof(Value));
        Raise(nameof(SecondValue));
        Raise(nameof(Text));
        Raise(nameof(SecondText));
        Raise(nameof(Error));
        Raise(nameof(HasError));
        Raise(nameof(Summary));
        Edited?.Invoke();

        if (changed)
            Committed?.Invoke(new DatFieldEdit(Field.Key, Field.Display, _record, component, was, value));
    }

    /// <summary>Text-bound so an out-of-range entry can be reported rather than clamped.</summary>
    public string Text
    {
        get => Value.ToString();
        set { if (long.TryParse(value, out var parsed)) Value = parsed; else Raise(); }
    }

    public string SecondText
    {
        get => SecondValue.ToString();
        set { if (long.TryParse(value, out var parsed)) SecondValue = parsed; else Raise(); }
    }

    public bool IsOn
    {
        get => Value != 0;
        set => Value = value ? 1 : 0;
    }

    public DatOption? Choice
    {
        get => Options.FirstOrDefault(o => o.Value == (uint)Value)
               ?? (Options.Count > 0 ? new DatOption((uint)Value, $"({Value})") : null);
        set { if (value is not null) Value = value.Value; }
    }

    public string? Error { get; private set; }
    public bool HasError => Error is not null;

    public long DisplayMax => Field.DisplayMax;
    public long DisplayStep => Field.DisplayStep;

    /// <summary>A short read of the current value, for the collapsed view.</summary>
    public string Summary => Field.Kind switch
    {
        DatControlKind.Toggle => IsOn ? "yes" : "no",
        DatControlKind.Choice => Choice?.Label ?? Value.ToString(),
        DatControlKind.Flags => $"{Flags.Count(f => f.IsSet)} set",
        _ => HasSecond ? $"{Value} x {SecondValue}" : Value.ToString(),
    };
}
