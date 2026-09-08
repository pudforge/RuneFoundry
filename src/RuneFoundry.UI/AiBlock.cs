using RuneFoundry.Core.Formats;

namespace RuneFoundry.UI;

/// <summary>A pick in one of the block editor's dropdowns: a number with a readable name.</summary>
public sealed record AiChoice(byte Number, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Somewhere a goto can aim: a named section of the script, and its byte offset.</summary>
public sealed record AiJumpTarget(int Offset, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One line of an AI script, as the block editor sees it.
///
/// The bytecode is only seven fixed-width instructions, so a "block" is just a kind plus
/// at most two numbers. That keeps the editor honest: every block maps to exactly one
/// instruction, and anything the parser did not understand is carried as Raw rather than
/// being dropped or guessed at.
/// </summary>
public sealed class AiBlock : Observable
{
    /// <summary>
    /// What a row does, phrased as an action rather than as the byte it writes.
    ///
    /// Most instructions are "var X = n", but that is an implementation detail: queueing a
    /// building, setting a unit target and launching an attack are different acts that all
    /// happen to be variable writes. Splitting them means the second dropdown can offer the
    /// right things — buildings for a queue, units for a target — instead of one list of 26
    /// variables the reader has to interpret.
    /// </summary>
    public enum AiAction
    {
        QueueBuilding,
        BuildUnits,
        AttackParty,
        LaunchAttack,
        RearmAttack,
        Wait,
        Sleep,
        GoTo,
        SetVariable,
        Other,
    }

    public sealed record AiActionChoice(AiAction Action, string Label)
    {
        public override string ToString() => Label;
    }

    public static readonly AiActionChoice[] Actions =
    {
        new(AiAction.QueueBuilding, "Queue building"),
        new(AiAction.BuildUnits, "Build units"),
        new(AiAction.AttackParty, "Set attack party"),
        new(AiAction.LaunchAttack, "Launch attack"),
        new(AiAction.RearmAttack, "Rearm attack"),
        new(AiAction.Wait, "Wait for"),
        new(AiAction.Sleep, "Sleep"),
        new(AiAction.GoTo, "Go to"),
        new(AiAction.SetVariable, "Set variable"),
    };

    /// <summary>Unit-count variables, offered when the action is "build units".</summary>
    public static readonly AiChoice[] UnitChoices =
        AiNames.Ordered.Where(n => n >= 0x13 && n <= 0x20)
            .Select(n => new AiChoice(n, AiNames.Variable(n))).ToArray();

    /// <summary>Party size and count variables, for "set attack party".</summary>
    public static readonly AiChoice[] PartyChoices =
        AiNames.Ordered.Where(n => n >= 0x0D && n <= 0x12)
            .Select(n => new AiChoice(n, AiNames.Variable(n))).ToArray();

    /// <summary>The three attack triggers, for "launch attack".</summary>
    public static readonly AiChoice[] AttackChoices =
        AiNames.Ordered.Where(n => n is 0x09 or 0x0A or 0x0B)
            .Select(n => new AiChoice(n, AiNames.Variable(n))).ToArray();

    public static readonly AiChoice[] Kinds =
    {
        new((byte)AiOpcode.Var, "Set variable"),
        new((byte)AiOpcode.Wait, "Wait for"),
        new((byte)AiOpcode.Sleep, "Sleep"),
        new((byte)AiOpcode.Goto, "Go to"),
    };

    /// <summary>Variables in readable form, not the compiler's identifiers.</summary>
    public static readonly AiChoice[] VariableChoices =
        AiNames.Ordered.Select(n => new AiChoice(n, AiNames.Variable(n))).ToArray();

    public static readonly AiChoice[] WaitChoices =
        AiNames.OrderedWaits.Select(n => new AiChoice(n, AiNames.Wait(n))).ToArray();

    private byte _opcode;
    private byte _target;
    private uint _value;

    /// <summary>Bytes of an instruction we could not decode; null for everything else.</summary>
    public byte[]? RawOperands { get; private set; }

    public bool IsRaw => RawOperands is not null;

    public byte Opcode
    {
        get => _opcode;
        set
        {
            if (!Set(ref _opcode, value)) return;
            RaiseShape();
        }
    }

    /// <summary>Bound to the kind dropdown.</summary>
    public AiChoice? Kind
    {
        get => Kinds.FirstOrDefault(k => k.Number == _opcode);
        set { if (value is not null) Opcode = value.Number; }
    }

    /// <summary>The variable, wait condition or item this block acts on.</summary>
    public byte Target
    {
        get => _target;
        set
        {
            if (!Set(ref _target, value)) return;
            Raise(nameof(TargetChoice));
            Raise(nameof(Summary));
            Raise(nameof(Warning));
            Raise(nameof(HasWarning));
            Raise(nameof(Explanation));
            Raise(nameof(ValueIsBuildItem));
            Raise(nameof(ValueIsNumber));
            Raise(nameof(ValueChoices));
            Raise(nameof(ValueChoice));
        }
    }

    /// <summary>Bound to the second dropdown, where the block has one.</summary>
    public AiChoice? TargetChoice
    {
        get => TargetChoices.FirstOrDefault(c => c.Number == _target)
               ?? (TargetChoices.Length > 0 ? new AiChoice(_target, $"${_target:X2}") : null);
        set { if (value is not null) Target = value.Number; }
    }

    public uint Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value)) return;
            Raise(nameof(Summary));
            Raise(nameof(ValueChoice));
            Raise(nameof(ValueText));
            Raise(nameof(GotoTargets));
            Raise(nameof(GotoTarget));
        }
    }

    /// <summary>Editable as text so hex and out-of-range input can be rejected rather than clamped.</summary>
    public string ValueText
    {
        get => _value.ToString();
        set
        {
            if (uint.TryParse(value, out var parsed)) Value = parsed;
            Raise();
        }
    }

    public AiChoice[] TargetChoices => (AiOpcode)_opcode switch
    {
        AiOpcode.Var => VariableChoices,
        AiOpcode.Wait => WaitChoices,
        _ => Array.Empty<AiChoice>(),
    };

    public bool HasTarget => (AiOpcode)_opcode is AiOpcode.Var or AiOpcode.Wait;

    /// <summary>Var and Wait both have names worth showing rather than numbers.</summary>
    public bool TargetIsList => TargetChoices.Length > 0;
    public bool TargetIsNumber => HasTarget && !TargetIsList;

    public bool HasValue => (AiOpcode)_opcode is AiOpcode.Var or AiOpcode.Sleep
                            or AiOpcode.Goto;

    /// <summary>True for the build cursor on a script whose list we can read.</summary>
    public bool ValueIsBuildItem =>
        (AiOpcode)_opcode == AiOpcode.Var && _target == 0x22 && ValueChoices.Length > 0;

    public bool ValueIsNumber => HasValue && !ValueIsBuildItem;

    public AiChoice[] ValueChoices =>
        (AiOpcode)_opcode == AiOpcode.Var && _target == 0x22 && BuildListChoices is not null
            ? BuildListChoices()
            : Array.Empty<AiChoice>();

    /// <summary>Bound to the build-item dropdown.</summary>
    public AiChoice? ValueChoice
    {
        get
        {
            var choices = ValueChoices;
            return choices.FirstOrDefault(c => c.Number == (byte)_value)
                   ?? (choices.Length > 0
                       ? new AiChoice((byte)_value, "everything in the list")
                       : null);
        }
        set { if (value is not null) Value = value.Number; }
    }

    /// <summary>
    /// Checks this row against the rest of its script. Supplied by the editor, which is
    /// what holds the script; a row on its own cannot tell whether a wait can be met.
    /// </summary>
    public Func<AiInstruction, string?>? Advise { get; set; }

    /// <summary>The same, for what only looks wrong because the map is not visible here.</summary>
    public Func<AiInstruction, string?>? Caution { get; set; }

    /// <summary>
    /// What is wrong with this row, or null. Recomputed on demand rather than stored,
    /// because editing a row above can make a row below wrong.
    /// </summary>
    public string? Warning
    {
        get
        {
            if (IsRaw || Advise is null) return null;

            try { return Advise(ToInstruction()); }
            catch (Exception) { return null; }
        }
    }

    public bool HasWarning => Warning is not null;

    /// <summary>
    /// What the setting behind this row does, for hovering over. Kept out of the summary
    /// column, which stays quiet for rows whose own controls already say everything.
    /// </summary>
    public string? Explanation
    {
        get
        {
            if (!IsRaw && Caution is not null)
            {
                try
                {
                    if (Caution(ToInstruction()) is { } care) return care;
                }
                catch (Exception)
                {
                    // A row mid-edit cannot be checked; it will be once it parses.
                }
            }

            return HasTarget ? AiNames.Note(_target) : null;
        }
    }

    /// <summary>
    /// Resolves a build-list position to an item name. Supplied by the editor, which knows
    /// which script the row belongs to; "item 12" means nothing without it.
    /// </summary>
    public Func<int, string?>? BuildListLookup { get; set; }

    /// <summary>The script's build list as pickable entries, supplied the same way.</summary>
    public Func<AiChoice[]>? BuildListChoices { get; set; }

    /// <summary>
    /// Where this instruction sits in the file. Shown because goto takes an absolute byte
    /// offset — without these numbers there is no way to aim one.
    /// </summary>
    private int _offset;

    public int Offset
    {
        get => _offset;
        set { if (Set(ref _offset, value)) Raise(nameof(OffsetLabel)); }
    }

    public string OffsetLabel => _offset.ToString();

    /// <summary>Bytes this instruction occupies, for laying out the offsets.</summary>
    public int Length => 1 + (IsRaw ? RawOperands!.Length : (AiOpcode)_opcode switch
    {
        AiOpcode.Var or AiOpcode.Goto => 2,
        AiOpcode.Sleep => 4,
        AiOpcode.Wait => 1,
        _ => 0,
    });

    /// <summary>Which action this row represents, derived from the opcode and variable.</summary>
    public AiAction Action => (AiOpcode)_opcode switch
    {
        AiOpcode.Wait => AiAction.Wait,
        AiOpcode.Sleep => AiAction.Sleep,
        AiOpcode.Goto => AiAction.GoTo,
        AiOpcode.Var => _target switch
        {
            0x22 => AiAction.QueueBuilding,
            >= 0x13 and <= 0x20 => AiAction.BuildUnits,
            >= 0x0D and <= 0x12 => AiAction.AttackParty,
            // Same variable, different act: 1 launches, 0 rearms the trigger.
            0x09 or 0x0A or 0x0B => _value == 0 ? AiAction.RearmAttack : AiAction.LaunchAttack,
            _ => AiAction.SetVariable,
        },
        _ => AiAction.Other,
    };

    public AiActionChoice? ActionChoice
    {
        get => Actions.FirstOrDefault(a => a.Action == Action);
        set { if (value is not null) ApplyAction(value.Action); }
    }

    /// <summary>Rewrites the row as a different action, with a sensible starting value.</summary>
    private void ApplyAction(AiAction action)
    {
        if (action == Action) return;

        switch (action)
        {
            case AiAction.QueueBuilding: SetVar(0x22, 0); break;
            case AiAction.BuildUnits: SetVar(UnitChoices[0].Number, 1); break;
            case AiAction.AttackParty: SetVar(PartyChoices[0].Number, 1); break;
            case AiAction.LaunchAttack: SetVar(AttackChoices[0].Number, 1); break;
            case AiAction.RearmAttack: SetVar(AttackChoices[0].Number, 0); break;
            case AiAction.SetVariable: SetVar(0x0C, 1); break;

            case AiAction.Wait:
                _opcode = (byte)AiOpcode.Wait;
                _target = WaitChoices.Length > 0 ? WaitChoices[0].Number : (byte)1;
                break;

            case AiAction.Sleep:
                _opcode = (byte)AiOpcode.Sleep;
                _value = 500;
                break;

            case AiAction.GoTo:
                _opcode = (byte)AiOpcode.Goto;
                break;
        }

        RaiseShape();

        void SetVar(byte variable, uint value)
        {
            _opcode = (byte)AiOpcode.Var;
            _target = variable;
            _value = value;
        }
    }

    /// <summary>The second dropdown's contents, narrowed to what the action can act on.</summary>
    public AiChoice[] SubjectChoices => Action switch
    {
        AiAction.BuildUnits => UnitChoices,
        AiAction.AttackParty => PartyChoices,
        AiAction.LaunchAttack or AiAction.RearmAttack => AttackChoices,
        AiAction.Wait => WaitChoices,
        AiAction.SetVariable => VariableChoices,
        _ => Array.Empty<AiChoice>(),
    };

    public AiChoice? Subject
    {
        get => SubjectChoices.FirstOrDefault(c => c.Number == _target);
        set { if (value is not null) Target = value.Number; }
    }

    public bool HasSubject => SubjectChoices.Length > 0;

    /// <summary>Queueing shows the building list; launching and rearming need no value.</summary>
    public bool ShowsBuilding => Action == AiAction.QueueBuilding && ValueChoices.Length > 0;

    public bool ShowsNumber => Action is AiAction.BuildUnits or AiAction.AttackParty
                               or AiAction.Sleep or AiAction.SetVariable
                               || (Action == AiAction.GoTo && !ShowsGoto);

    private AiJumpTarget[] _jumpTargets = Array.Empty<AiJumpTarget>();

    /// <summary>
    /// Where a goto in this script may aim. Supplied by the editor, which is what works
    /// out the sections. A goto takes an absolute byte offset, which reads as nothing and
    /// moves whenever a row above it changes length; picking the wave by name and letting
    /// the editor resolve the offset is the same edit without the arithmetic.
    /// </summary>
    public AiJumpTarget[] JumpTargets
    {
        get => _jumpTargets;
        set
        {
            // Only on a real change: every regroup assigns this, and a spurious
            // notification would be read as an edit.
            if (_jumpTargets.SequenceEqual(value)) return;
            _jumpTargets = value;
            Raise(nameof(GotoTargets));
            Raise(nameof(GotoTarget));
            Raise(nameof(ShowsGoto));
            Raise(nameof(ShowsNumber));
        }
    }

    /// <summary>
    /// The pickable targets, plus the current offset when the jump aims somewhere unnamed.
    /// A combo cannot show a selection absent from its list, and leaving that entry out
    /// would silently retarget the jump to whatever the box happened to land on.
    /// </summary>
    public AiJumpTarget[] GotoTargets =>
        _jumpTargets.Length == 0 || _jumpTargets.Any(t => t.Offset == _value)
            ? _jumpTargets
            : _jumpTargets.Prepend(new AiJumpTarget((int)_value, $"byte {_value}")).ToArray();

    public AiJumpTarget? GotoTarget
    {
        get => GotoTargets.FirstOrDefault(t => t.Offset == _value);
        set { if (value is not null) Value = (uint)value.Offset; }
    }

    public bool ShowsGoto => Action == AiAction.GoTo && _jumpTargets.Length > 0;

    /// <summary>Queueing with an unreadable list still needs the raw position.</summary>
    public bool ShowsPosition => Action == AiAction.QueueBuilding && ValueChoices.Length == 0;

    /// <summary>
    /// Set while the file being shown is still the stock game file — the table stays
    /// browsable but nothing can be typed into it.
    /// </summary>
    private bool _isReadOnly;

    public bool IsReadOnly
    {
        get => _isReadOnly;
        set { if (Set(ref _isReadOnly, value)) { Raise(nameof(CanEdit)); Raise(nameof(CanChangeAction)); } }
    }

    /// <summary>
    /// Whether this row's controls accept input. Locking the individual controls rather
    /// than disabling the list keeps a stock script scrollable, selectable and legible —
    /// you can read it and copy from it, you just cannot change it.
    /// </summary>
    public bool CanEdit => !_isReadOnly;

    /// <summary>An unrecognised instruction keeps its bytes whatever the file's state.</summary>
    public bool CanChangeAction => !_isReadOnly && !IsRaw;

    /// <summary>Set by the editor when this goto leaves the script it belongs to.</summary>
    public bool JumpsAway { get; set; }

    private bool _isLoopTarget;

    /// <summary>
    /// Whether a jump in this script comes back to this row.
    ///
    /// Unlike the wave headings, this is not our guess. The script states it: the goto
    /// holds the byte it returns to, so this row is where the repeat really begins. Only
    /// 40 of the 83 shipped scripts loop to a sleep, which is where we start a wave, so
    /// the two genuinely differ and the file's own answer is the one worth showing.
    /// </summary>
    public bool IsLoopTarget
    {
        get => _isLoopTarget;
        set { if (Set(ref _isLoopTarget, value)) Raise(nameof(LoopNote)); }
    }

    public string LoopNote => _isLoopTarget ? "repeats from here" : "";

    /// <summary>Which part of the script this row belongs to; set by the editor.</summary>
    private string _section = "";

    public string Section
    {
        get => _section;
        set => Set(ref _section, value);
    }

    /// <summary>True for an instruction that launches an attack, which is what ends a wave.</summary>
    /// <summary>
    /// Whether this row launches an attack. No longer used to divide waves: the scripts
    /// put their loop boundary at the sleep, and this is the row an author removes.
    /// </summary>
    public bool IsAttackLaunch => Action == AiAction.LaunchAttack;

    /// <summary>
    /// Extra reading for the row, shown after the controls.
    ///
    /// Deliberately empty for most rows. Once the action and its subject are dropdowns,
    /// "Build units / Footmen / 6" needs no sentence saying "build up to 6 Footmen" — the
    /// row already says it. What is left is what the controls cannot show: a tick count as
    /// a duration, an unrecognised instruction, and the generic variable case.
    /// </summary>
    /// <summary>
    /// The row in words, shown while it is not the row being edited. Same sentence the
    /// source view uses, so the two views of a script say the same thing.
    /// </summary>
    public string Line
    {
        get
        {
            try { return AiText.Describe(ToInstruction(), BuildListLookup); }
            catch (Exception ex) { return ex.Message; }
        }
    }

    public string Summary
    {
        get
        {
            if (IsRaw) return $"Unrecognised instruction ({_opcode:X2}), kept as-is";

            try
            {
                return Action switch
                {
                    AiAction.Sleep => "≈ " + AiText.DescribeTicks(_value),
                    // A goto inside the script is a loop; one that leaves it is a jump to
                    // code held somewhere else, which is a different thing to tell someone.
                    // A plain loop repeats what the value box already shows; a jump out of
                    // the script does not, so only that one earns a label.
                    AiAction.GoTo => JumpsAway ? $"jumps to shared code at {_value}" : "",
                    AiAction.SetVariable => AiText.Describe(ToInstruction(), BuildListLookup),
                    AiAction.QueueBuilding when ValueChoices.Length == 0
                        => "this script's build list is not readable",
                    _ => "",
                };
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    private void RaiseShape()
    {
        Raise(nameof(Kind));
        Raise(nameof(TargetChoices));
        Raise(nameof(TargetChoice));
        Raise(nameof(HasTarget));
        Raise(nameof(TargetIsList));
        Raise(nameof(TargetIsNumber));
        Raise(nameof(HasValue));
        Raise(nameof(ValueIsBuildItem));
        Raise(nameof(ValueIsNumber));
        Raise(nameof(ValueChoices));
        Raise(nameof(ValueChoice));
        Raise(nameof(Action));
        Raise(nameof(ActionChoice));
        Raise(nameof(SubjectChoices));
        Raise(nameof(Subject));
        Raise(nameof(HasSubject));
        Raise(nameof(ShowsBuilding));
        Raise(nameof(ShowsNumber));
        Raise(nameof(ShowsGoto));
        Raise(nameof(GotoTargets));
        Raise(nameof(GotoTarget));
        Raise(nameof(Summary));
    }

    public static AiBlock From(AiInstruction instruction)
    {
        var block = new AiBlock { _opcode = instruction.Opcode };

        if (instruction.IsUnknown)
        {
            block.RawOperands = instruction.Operands;
            return block;
        }

        switch ((AiOpcode)instruction.Opcode)
        {
            case AiOpcode.Var:
                block._target = instruction.Operands[0];
                block._value = instruction.Operands[1];
                break;
            case AiOpcode.Wait:
                block._target = instruction.Operands[0];
                break;
            case AiOpcode.Goto:
                block._value = AiFile.ReadWord(instruction);
                break;
            case AiOpcode.Sleep:
                block._value = AiFile.ReadDword(instruction);
                break;
        }
        return block;
    }

    /// <summary>Turns the block back into an instruction, or explains why it cannot.</summary>
    public AiInstruction ToInstruction()
    {
        if (IsRaw)
            return new AiInstruction { Opcode = _opcode, Operands = RawOperands!, IsUnknown = true };

        switch ((AiOpcode)_opcode)
        {
            case AiOpcode.Var:
                Check(_target <= AiFile.MaxVariable,
                    $"${_target:X2} writes into another player's settings. Use $09 to $2F.");
                Check(_value <= byte.MaxValue, "value must be 0-255");
                return new AiInstruction { Opcode = _opcode, Operands = new[] { _target, (byte)_value } };

            case AiOpcode.Wait:
                return new AiInstruction { Opcode = _opcode, Operands = new[] { _target } };

            case AiOpcode.Goto:
            {
                Check(_value <= ushort.MaxValue, "goto must be 0-65535");
                var operands = new byte[2];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(operands, (ushort)_value);
                return new AiInstruction { Opcode = _opcode, Operands = operands };
            }

            case AiOpcode.Sleep:
            {
                var operands = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(operands, _value);
                return new AiInstruction { Opcode = _opcode, Operands = operands };
            }


            default:
                throw new InvalidOperationException($"Opcode {_opcode:X2} cannot be written.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static AiBlock NewVar() => new()
    {
        _opcode = (byte)AiOpcode.Var,
        _target = VariableChoices.Length > 0 ? VariableChoices[0].Number : (byte)0,
    };
}
