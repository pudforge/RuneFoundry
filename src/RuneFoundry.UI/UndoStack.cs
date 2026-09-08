namespace RuneFoundry.UI;

/// <summary>
/// One change, and how to take it back.
///
/// Deliberately not a snapshot: an edit is "field X of record Y went from A to B", so undo
/// is setting it back to A. Snapshots of the project would be simpler to write and much
/// worse to use — they cannot say what changed, and they cannot put you back where it
/// happened.
/// </summary>
/// <param name="Describe">
/// What was done, in the words the menu will show: "Footman hit points 60 to 90". Written
/// from the person's point of view, not the code's.
/// </param>
/// <param name="Apply">Does the change. Called for redo, and by the caller to do it first.</param>
/// <param name="Revert">Puts it back.</param>
/// <param name="Show">
/// Puts the change back in front of the person — switch to its screen, select its row. An
/// undo you cannot see is indistinguishable from one that did not work. Each screen supplies
/// its own, because only it knows how to get there.
/// </param>
public sealed record Edit(
    string Describe,
    Action Apply,
    Action Revert,
    Action? Show = null);

/// <summary>
/// What the editor has done, and what it has taken back.
///
/// One stack for the whole editor rather than one per screen. Ctrl+Z means "undo the last
/// thing I did", and people do not keep a separate mental history per tab — so undoing a
/// change made on another screen switches to that screen and shows it, which is honest
/// about what just happened.
///
/// File replacements go through <see cref="OverrideUndo"/>, which pushes onto this stack but
/// keeps its copies on disk. Holding a fifty-megabyte sheet in memory per step is what kept
/// them off the stack originally; keeping them beside it costs nothing until someone
/// replaces the same file twice, because the first replacement is undone by dropping the
/// override.
/// </summary>
public sealed class UndoStack
{
    private readonly List<Edit> _done = new();
    private readonly List<Edit> _undone = new();

    /// <summary>How much history to hold. Long enough to cover a session's mistakes.</summary>
    public int Limit { get; init; } = 200;

    /// <summary>Raised whenever undo or redo becomes available, or stops being.</summary>
    public event Action? Changed;

    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;

    public string UndoLabel => CanUndo ? $"Undo {_done[^1].Describe}" : "Undo";
    public string RedoLabel => CanRedo ? $"Redo {_undone[^1].Describe}" : "Redo";

    /// <summary>
    /// Records a change that has already happened.
    ///
    /// Redo history is dropped, because it described a future that no longer follows from
    /// here — the standard rule, and the only one that cannot produce a nonsense state.
    /// </summary>
    public void Push(Edit edit)
    {
        _done.Add(edit);
        _undone.Clear();

        if (_done.Count > Limit) _done.RemoveRange(0, _done.Count - Limit);

        Changed?.Invoke();
    }

    /// <summary>Applies a change and records it, for callers that have not done it yet.</summary>
    public void Do(Edit edit)
    {
        edit.Apply();
        Push(edit);
    }

    /// <summary>Takes back the last change and returns it, so the caller can show where.</summary>
    public Edit? Undo()
    {
        if (_done.Count == 0) return null;

        var edit = _done[^1];
        _done.RemoveAt(_done.Count - 1);

        edit.Revert();

        _undone.Add(edit);
        Changed?.Invoke();
        return edit;
    }

    public Edit? Redo()
    {
        if (_undone.Count == 0) return null;

        var edit = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);

        edit.Apply();

        _done.Add(edit);
        Changed?.Invoke();
        return edit;
    }

    /// <summary>
    /// Forgets everything, for when the history no longer describes what is on screen —
    /// closing a project, or opening another one.
    /// </summary>
    public void Clear()
    {
        if (_done.Count == 0 && _undone.Count == 0) return;

        _done.Clear();
        _undone.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// Runs an action without recording anything.
    ///
    /// Undo and redo change the same fields the person does, through the same handlers, and
    /// those handlers push to this stack. Without this they would push while unwinding and
    /// the stack would fight itself.
    /// </summary>
    public bool Suspended { get; private set; }

    public IDisposable Quiet() => new Pause(this);

    private sealed class Pause : IDisposable
    {
        private readonly UndoStack _stack;
        private readonly bool _was;

        public Pause(UndoStack stack)
        {
            _stack = stack;
            _was = stack.Suspended;
            stack.Suspended = true;
        }

        public void Dispose() => _stack.Suspended = _was;
    }
}
