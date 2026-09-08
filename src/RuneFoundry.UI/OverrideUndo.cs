using System.IO;
using RuneFoundry.Core;

namespace RuneFoundry.UI;

/// <summary>
/// Undo for the things a mod replaces whole: sheets, pictures, movies, music, recordings.
///
/// The trick that makes this affordable is that most of the time there is nothing to store.
/// Replacing a file the mod had not touched yet is undone by dropping the override, which
/// costs nothing and is what Reset already does. Only replacing your own earlier work needs
/// the previous copy kept, and that is the rarer half of the job.
///
/// It matters most for sprite art, where Reset is not a substitute: Reset goes back to the
/// game's own frames, so someone who imported twice would lose both versions rather than
/// stepping back one.
///
/// Copies live outside the project, under the user's local application data, and go when the
/// project closes. Undo is a safety net for the session you are in. Reset is the durable way
/// back, and keeping a gigabyte of history between sessions would be a surprise rather than
/// a feature.
/// </summary>
public sealed class OverrideUndo : IDisposable
{
    /// <summary>
    /// How many steps of replaced files to keep.
    ///
    /// Ten rather than the stack's own two hundred: an entry here can be fifty megabytes,
    /// and nobody steps back ten replacements of the same sheet.
    /// </summary>
    public const int Steps = 10;

    private readonly UndoStack _undo;
    private readonly string _root;
    private readonly List<string> _kept = new();

    private int _next;

    public OverrideUndo(UndoStack undo)
    {
        _undo = undo;

        var store = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RuneFoundry", "undo");

        _root = Path.Combine(store, Guid.NewGuid().ToString("N")[..8]);

        SweepOldSessions(store);
    }

    /// <summary>
    /// Deletes what earlier sessions left behind.
    ///
    /// A session that is killed, crashes, or is closed by the window's X never reaches
    /// Dispose, so its folder stays. Three of them had reached 320 MB before this existed.
    /// Anything that is not ours is fair game, because two editors are not expected to run
    /// at once and the worst case is one losing an undo it had not been asked for.
    /// </summary>
    private void SweepOldSessions(string store)
    {
        try
        {
            if (!Directory.Exists(store)) return;

            foreach (var folder in Directory.EnumerateDirectories(store))
            {
                if (string.Equals(folder, _root, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception)
                {
                    // In use by another editor, most likely. Leave it.
                }
            }
        }
        catch (Exception)
        {
            // Housekeeping is not worth failing a startup over.
        }
    }

    /// <summary>
    /// Throws away every kept copy.
    ///
    /// Called when a project closes, beside the stack's own Clear. The entries pointing at
    /// these files go at the same moment, so keeping the files would be keeping bytes
    /// nothing can reach.
    /// </summary>
    public void Clear()
    {
        _kept.Clear();
        _next = 0;

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Swept at the start of the next session.
        }
    }

    /// <summary>What one file looked like: the copy we kept, or nothing if it was not ours.</summary>
    private sealed record Was(string Path, string? Kept);

    /// <summary>
    /// Records what these files are now, makes the change, and offers it back.
    ///
    /// The change runs whether or not undo is available, so a failure to keep a copy costs
    /// the undo and nothing else.
    /// </summary>
    /// <param name="refresh">
    /// Redraws whatever showed the old state. Without it undo would change the files and
    /// leave the screen claiming otherwise, which is worse than no undo at all.
    /// </param>
    public void Record(ModProject project, string describe, IReadOnlyList<string> paths,
                       Action change, Action? refresh = null)
    {
        if (_undo.Suspended)
        {
            change();
            return;
        }

        var before = Capture(project, paths);
        change();

        // Captured lazily on the way back: keeping the "after" state now would double the
        // disk for a redo most people never ask for.
        List<Was>? after = null;

        _undo.Push(new Edit(
            describe,
            Apply: () =>
            {
                if (after is null) return;

                Restore(project, after);
                refresh?.Invoke();
            },
            Revert: () =>
            {
                after ??= Capture(project, paths);

                Restore(project, before);
                refresh?.Invoke();
            }));

        Prune();
    }

    private List<Was> Capture(ModProject project, IReadOnlyList<string> paths)
    {
        var state = new List<Was>();

        foreach (var path in paths)
        {
            if (!project.HasOverride(path))
            {
                // Not ours yet, so going back means dropping it. Nothing to keep.
                state.Add(new Was(path, null));
                continue;
            }

            try
            {
                var kept = Path.Combine(_root, (_next++).ToString("D4") + Path.GetExtension(path));
                Directory.CreateDirectory(_root);
                File.Copy(project.ResolveContentPath(path), kept, overwrite: true);

                _kept.Add(kept);
                state.Add(new Was(path, kept));
            }
            catch (Exception)
            {
                // A copy we could not keep is an undo we cannot offer for that file. The
                // edit itself still stands, which is the part that matters.
                state.Add(new Was(path, null));
            }
        }

        return state;
    }

    private static void Restore(ModProject project, IReadOnlyList<Was> state)
    {
        foreach (var (path, kept) in state)
        {
            if (kept is null || !File.Exists(kept))
            {
                if (project.HasOverride(path)) project.RemoveOverride(path);
                continue;
            }

            project.ImportOverride(path, kept);
        }
    }

    /// <summary>Drops the oldest copies once there are more than <see cref="Steps"/> of them.</summary>
    private void Prune()
    {
        while (_kept.Count > Steps)
        {
            var oldest = _kept[0];
            _kept.RemoveAt(0);

            try
            {
                if (File.Exists(oldest)) File.Delete(oldest);
            }
            catch (Exception)
            {
                // A file we cannot delete costs disk until the folder goes. Not worth stopping for.
            }
        }
    }

    public void Dispose() => Clear();
}
