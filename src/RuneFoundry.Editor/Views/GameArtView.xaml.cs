using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using RuneFoundry.Core;
using RuneFoundry.Core.Formats;
using RuneFoundry.UI;

namespace RuneFoundry.Editor.Views;

/// <summary>One of the game's own pictures, as this panel shows it.</summary>
public sealed class GameArtRow : Observable
{
    public required CampaignPicture Picture { get; init; }
    public required string Label { get; init; }
    public required string Shape { get; init; }

    public BitmapSource? Thumbnail { get; init; }

    private bool _changed;

    public bool Changed
    {
        get => _changed;
        set { if (Set(ref _changed, value)) Raise(nameof(State)); }
    }

    public string State => _changed ? "replaced by this mod" : "the game's own";
}

/// <summary>
/// The pictures that belong to the game rather than to a campaign.
///
/// On Mod details, not in the Campaign tab: the campaign screens are for editing one
/// campaign, and a menu backdrop or a defeat screen is not part of any of them. Someone
/// looking for "what does my mod change about the game as a whole" comes here.
/// </summary>
public partial class GameArtView : UserControl
{
    private Session _session = null!;
    private ModProject? _project;
    private readonly List<GameArtRow> _rows = new();

    public GameArtView() => InitializeComponent();

    public void Attach(Session session)
    {
        _session = session;

        MoviesPane.Attach(session);
        MoviesPane.Status += message => Status?.Invoke(message);
        MoviesPane.OverridesChanged += () => OverridesChanged?.Invoke();
        MusicPane.Attach(session);
        MusicPane.Status += message => Status?.Invoke(message);
        MusicPane.OverridesChanged += () => OverridesChanged?.Invoke();
        MusicPane.Describe("Music",
            "Every track ships twice. Replace both recordings. "
            + "Five tracks ship only once and always play.");

        MoviesPane.Describe("The game's own movies",
            "These movies play outside any campaign. A replacement shows everywhere. "
            + MediaListView.MovieAdvice);
    }

    /// <summary>Reports progress to whatever is hosting this.</summary>
    public event Action<string>? Status;

    /// <summary>Raised when a file was added to or dropped from the mod.</summary>
    public event Action? OverridesChanged;

    private Window? Owner => Window.GetWindow(this);

    public void Refresh(ModProject? project)
    {
        _project = project;
        Build();
        MoviesPane.Refresh(project, GameMedia.Global);
        MusicPane.Refresh(project, GameMusic.All);
    }

    /// <summary>
    /// Reads every picture once, off the thread that draws the window.
    ///
    /// Nine files at up to 14 MB each; decoding them where the window is drawn is the
    /// difference between a tab that opens and one that appears to hang.
    /// </summary>
    private async void Build()
    {
        var generation = ++_generation;

        _rows.Clear();
        ArtList.ItemsSource = null;

        if (_session?.Game is null) return;

        var wanted = GameArt.All
            .Select(picture => (
                Picture: picture,
                Mine: _session.EffectiveFile(_project, picture.Image),
                Stock: _session.StockFile(picture.Image)))
            .ToList();

        var built = await Busy.While("Reading the game's pictures…", () =>
        {
            var made = new List<(CampaignPicture Picture, int W, int H, BitmapSource? Thumb, bool Changed)>();

            foreach (var (picture, mine, stock) in wanted)
            {
                if (mine is null || !File.Exists(mine)) continue;

                var image = AtlasImage.Load(mine);

                // A whole file, so any difference in bytes is a replacement — surer and far
                // cheaper than comparing millions of pixels.
                var changed = stock is not null && File.Exists(stock)
                              && Hashing.Sha256File(mine) != Hashing.Sha256File(stock);

                made.Add((picture, image.PixelWidth, image.PixelHeight, Shrink(image), changed));
            }

            return made;
        },
        ex => Status?.Invoke("Could not read the game's pictures: " + ex.Message));

        if (built is null || generation != _generation) return;

        foreach (var (picture, w, h, thumb, changed) in built)
        {
            _rows.Add(new GameArtRow
            {
                Picture = picture,
                Label = picture.Label,
                Shape = $"{w} x {h}",
                Thumbnail = thumb,
                Changed = changed,
            });
        }

        ArtList.ItemsSource = _rows;
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

    /// <summary>A thumbnail-sized copy, so a 14 MB backdrop is not held at full size per row.</summary>
    private static BitmapSource Shrink(BitmapSource image)
    {
        var scale = 300.0 / Math.Max(1, image.PixelWidth);
        if (scale >= 1) return image;

        var small = new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale));
        small.Freeze();
        return small;
    }


    private async void OnReplace(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameArtRow row) return;
        if (_project is null || _session.Game is null) return;

        var current = _session.EffectiveFile(_project, row.Picture.Image);
        if (current is null || !File.Exists(current)) return;

        int width, height;

        try
        {
            (width, height) = AtlasImage.MeasureFile(current);
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "read that picture", ex);
            return;
        }

        if (PictureChooser.Ask(Owner, row.Label.ToLowerInvariant(), width, height) is not { } chosen)
            return;

        // Decoding and re-encoding several megabytes is what stopped the window.
        var bytes = await Working.While(Owner, $"Importing {Path.GetFileName(chosen)}…",
            () =>
            {
                var picture = AtlasImage.Load(chosen);
                var original = AtlasImage.Load(current);

                return AtlasImage.Resized(picture, original.PixelWidth, original.PixelHeight,
                                          original.Format);
            },
            ex => Ui.Failed(Owner, "replace that picture", ex));

        if (bytes is null) return;

        try
        {
            _session.FileUndo.Record(_project, $"replace {row.Label.ToLowerInvariant()}",
                new[] { row.Picture.Image },
                () => _project.WriteOverride(row.Picture.Image, bytes),
                refresh: Redraw);

            Status?.Invoke($"{row.Label} now uses {Path.GetFileName(chosen)}, "
                           + $"scaled to {width} x {height}.");

            OverridesChanged?.Invoke();
            Build();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "replace that picture", ex);
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameArtRow row) return;
        if (_project is null || !_project.HasOverride(row.Picture.Image)) return;

        if (!Ui.ConfirmReset(Owner, row.Label.ToLowerInvariant(), "The game's own picture comes back."))
            return;

        try
        {
            _session.FileUndo.Record(_project, $"reset {row.Label.ToLowerInvariant()}",
                new[] { row.Picture.Image },
                () => _project.RemoveOverride(row.Picture.Image),
                refresh: Redraw);

            Status?.Invoke($"{row.Label} is back to the game's own.");

            OverridesChanged?.Invoke();
            Build();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "reset that", ex);
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
