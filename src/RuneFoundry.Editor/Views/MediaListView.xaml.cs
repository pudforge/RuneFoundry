using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RuneFoundry.Core;
using RuneFoundry.Core.Formats;
using RuneFoundry.UI;

namespace RuneFoundry.Editor.Views;

/// <summary>One file behind a cue, as this panel shows it.</summary>
public sealed class MediaFileRow : Observable
{
    public required MediaFile File { get; init; }

    public string Label => File.Label;
    public string Path => File.Path;
    public bool IsAudio => File.IsAudio;

    /// <summary>How long it runs and how big it is — blank until it has been read.</summary>
    public required string Measure { get; init; }

    private bool _changed;

    public bool Changed
    {
        get => _changed;
        set { if (Set(ref _changed, value)) Raise(nameof(State)); }
    }

    /// <summary>The one line under the path: what it is now, and how long it runs.</summary>
    public string State =>
        (_changed ? "replaced by this mod" : "the game's own")
        + (Measure.Length > 0 ? " · " + Measure : "");
}

/// <summary>One cue and its files, as this panel shows it.</summary>
public sealed class MediaCueRow
{
    public required string Label { get; init; }
    public required string Note { get; init; }
    public required IReadOnlyList<MediaFileRow> Files { get; init; }
}

/// <summary>
/// The movies and sound cues behind a screen, with a Replace and a Reset for each file.
///
/// One control used from two places — a campaign's own cues and the game's — so that a movie
/// is replaced the same way wherever it is met. The alternative was two panels that drift
/// apart, which is how the art screens started before they were pulled together.
///
/// A cue is usually one file — a movie, sound and all. Where a cue does have more than one,
/// each is listed and replaced on its own, since they fail separately.
/// </summary>
public partial class MediaListView : UserControl
{
    private Session _session = null!;
    private ModProject? _project;
    private IReadOnlyList<MediaCue> _cues = Array.Empty<MediaCue>();

    public MediaListView() => InitializeComponent();

    public void Attach(Session session) => _session = session;

    /// <summary>
    /// What to supply for a movie. One string, because two screens say it and rule 6 of the
    /// content design allows a concept only one wording.
    /// </summary>
    public const string MovieAdvice = "Supply a WebM with its sound inside it.";

    /// <summary>Reports progress to whatever is hosting this.</summary>
    public event Action<string>? Status;

    /// <summary>Raised when a file was added to or dropped from the mod.</summary>
    public event Action? OverridesChanged;

    private Window? Owner => Window.GetWindow(this);

    /// <summary>Names the section, since the same control heads two different lists.</summary>
    public void Describe(string title, string blurb)
    {
        Title.Text = title;
        Blurb.Text = blurb;
    }

    public void Refresh(ModProject? project, IReadOnlyList<MediaCue> cues)
    {
        _project = project;
        _cues = cues;
        Build();
    }

    /// <summary>
    /// Reads every file's size, and every sound's length, off the thread that draws the window.
    ///
    /// A campaign's cues come to about 80 MB of video, and the two hashes per file that decide
    /// whether it counts as replaced read all of it. Doing that where the window is drawn is
    /// the difference between a section that appears and one that appears to hang.
    /// </summary>
    private async void Build()
    {
        var generation = ++_generation;

        CueList.ItemsSource = null;
        if (_session?.Game is null) return;

        // Resolve paths on this thread: the project and session are not ours to touch from
        // the background one.
        var wanted = _cues
            .Select(cue => (
                Cue: cue,
                Files: cue.Files
                    .Select(file => (
                        File: file,
                        Mine: _session.EffectiveFile(_project, file.Path),
                        Stock: _session.StockFile(file.Path),
                        Overridden: _project?.HasOverride(file.Path) == true))
                    .ToList()))
            .ToList();

        var built = await Busy.While("Reading movies and sound…", () =>
            wanted.Select(entry => (
                entry.Cue,
                Files: entry.Files
                    .Select(f => (f.File, Measure: Measure(f.Mine), Changed: Changed(f)))
                    .ToList()))
                .ToList(),
            ex => Status?.Invoke("Could not read the movies: " + ex.Message));

        if (built is null || generation != _generation) return;

        CueList.ItemsSource = built
            .Select(entry => new MediaCueRow
            {
                Label = entry.Cue.Label,
                Note = entry.Cue.Note,
                Files = entry.Files
                    .Select(f => new MediaFileRow
                    {
                        File = f.File,
                        Measure = f.Measure,
                        Changed = f.Changed,
                    })
                    .ToList(),
            })
            .ToList();
    }

    /// <summary>
    /// Which rebuild is the current one.
    ///
    /// Filling these lists reads megabytes and so happens off the drawing thread, which
    /// means two can be in flight at once if someone clicks twice. Whichever finished last
    /// used to win, and that is not always the one that was asked for last: a slow read of
    /// the previous selection could land after a fast read of the new one and put the wrong
    /// thing on screen. Each pass takes a number and drops its results if a newer pass has
    /// started since.
    /// </summary>
    private int _generation;

    /// <summary>
    /// Whether the mod has taken this file over.
    ///
    /// Having an override is not the same as differing from stock — a file copied in and left
    /// alone still reads as the game's — so the bytes decide, and the cheap check comes first.
    /// </summary>
    private static bool Changed((MediaFile File, string? Mine, string? Stock, bool Overridden) f)
    {
        if (!f.Overridden) return false;
        if (f.Mine is null || f.Stock is null) return true;
        if (!File.Exists(f.Mine) || !File.Exists(f.Stock)) return true;

        return new FileInfo(f.Mine).Length != new FileInfo(f.Stock).Length
               || Hashing.Sha256File(f.Mine) != Hashing.Sha256File(f.Stock);
    }

    /// <summary>How long it runs, if it is a sound, and how big it is either way.</summary>
    private static string Measure(string? path)
    {
        if (path is null || !File.Exists(path)) return "missing from the game folder";

        var size = new FileInfo(path).Length;
        var megabytes = $"{size / 1048576.0:0.#} MB";

        if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return megabytes;

        try
        {
            // Only the headers are wanted; a narration clip runs to tens of megabytes. The
            // real size goes with them, or the clip reads as however much was loaded.
            var head = new byte[Math.Min(size, 4096)];
            using (var stream = File.OpenRead(path)) stream.ReadExactly(head);

            if (WaveFile.Describe(head, size) is { } format && format.Duration > TimeSpan.Zero)
                return $"{format.Duration:m\\:ss} · {megabytes}";
        }
        catch (Exception)
        {
            // A sound we cannot parse is still a file someone can replace; its size will do.
        }

        return megabytes;
    }

    /// <summary>The file behind a cue: this mod's if it has one, else the game's.</summary>

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MediaFileRow row) return;
        if (_project is null)
        {
            Ui.Error(Owner, "No project open", "Create or open a mod project first.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"Choose the file to use as {Path.GetFileName(row.Path)}",
            Filter = row.File.Filter,
        };
        if (dialog.ShowDialog(Owner) != true) return;

        try
        {
            // Copied in as it stands. Video is not re-encoded the way pictures are rescaled:
            // there is no shape to match, and a re-encode would cost quality for nothing.
            _session.FileUndo.Record(_project, $"replace {Path.GetFileName(row.Path)}",
                new[] { row.Path },
                () => _project.ImportOverride(row.Path, dialog.FileName),
                refresh: Redraw);

            Status?.Invoke($"{row.Path} now comes from {Path.GetFileName(dialog.FileName)}.");
            OverridesChanged?.Invoke();
            Build();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "replace that file", ex);
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MediaFileRow row) return;
        if (_project is null || !_project.HasOverride(row.Path)) return;

        if (!Ui.ConfirmReset(Owner, row.Path, "The game's own file is used instead.")) return;

        try
        {
            _session.FileUndo.Record(_project, $"reset {Path.GetFileName(row.Path)}",
                new[] { row.Path },
                () => _project.RemoveOverride(row.Path),
                refresh: Redraw);

            Status?.Invoke($"{row.Path} is the game's again.");

            OverridesChanged?.Invoke();
            Build();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "reset that", ex);
        }
    }

    /// <summary>
    /// Replaces a sound with silence of the same length.
    ///
    /// Length matters more here than anywhere else: the act title card is held on screen for
    /// as long as its fanfare plays, so silence cut to the original's length keeps the card
    /// up while removing the sound, and a one-second clip would make it flick past.
    /// </summary>
    private void OnSilence(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MediaFileRow row) return;
        if (_project is null)
        {
            Ui.Error(Owner, "No project open", "Create or open a mod project first.");
            return;
        }

        try
        {
            var stock = _session.StockFile(row.Path);
            var original = File.Exists(stock) ? File.ReadAllBytes(stock) : null;

            _session.FileUndo.Record(_project, $"silence {Path.GetFileName(row.Path)}",
                new[] { row.Path },
                () => _project.WriteOverride(row.Path, original is null
                    ? WaveFile.Silence(TimeSpan.FromSeconds(1))
                    : WaveFile.SilenceLike(original)),
                refresh: Redraw);

            Status?.Invoke($"{row.Path} is silent, and still as long as the game's own.");

            OverridesChanged?.Invoke();
            Build();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "silence that", ex);
        }
    }

    /// <summary>Redraws this pane, for undo: a change taken back that is still on
    /// screen as it was reads as an undo that did not work.</summary>
    private void Redraw()
    {
        Build();
        OverridesChanged?.Invoke();
    }
}
