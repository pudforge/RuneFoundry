using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RuneFoundry.Core;
using RuneFoundry.Core.Formats;
using RuneFoundry.UI;

namespace RuneFoundry.Editor.Views;

/// <summary>One sprite in the index, as the list shows it.</summary>
public sealed class SpriteRow
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required int FrameCount { get; init; }
    /// <summary>Whether this mod changes anything about this sprite at all.</summary>
    public required bool Changed { get; init; }

    /// <summary>
    /// Whether this mod redraws the unit, as opposed to pointing it at other art.
    ///
    /// Kept apart from <see cref="Changed"/> because putting the two back is different work:
    /// an index entry is restored by rewriting one line of JSON, and redrawn art has to be
    /// packed back into a sheet it shares with seventeen other units.
    /// </summary>
    public required bool ArtChanged { get; init; }

    /// <summary>What the sprite draws with now.</summary>
    public required SpriteSource? Source { get; init; }

    /// <summary>What it draws with in the game's own copy — what "this sprite's art" means.</summary>
    public required SpriteSource? StockSource { get; init; }

    public string Frames => FrameCount == 1 ? "1 frame" : $"{FrameCount} frames";

    public override string ToString() => Name;
}

/// <summary>
/// The sprite index, and a way to make one sprite draw another's art.
///
/// Which sprite a unit uses is compiled into the game — the path table lives at
/// <c>0x008C3978</c> and nothing in any data file points into it — so a unit cannot be sent
/// to a different sprite. What <c>Art/hd/sprites.json</c> decides is where each sprite's art
/// comes from, and that is a file a mod can replace: give <c>art/unit/human/knight.grp</c>
/// the Ogre's atlas and prefix and every Knight is drawn as an Ogre.
///
/// Frame counts are the catch. A Knight has 70 frames and a Peon 65, so a Knight drawn with
/// Peon art runs out part-way through its animation. Swaps are therefore offered between
/// sprites of the same length unless that is turned off deliberately.
/// </summary>
public partial class SpritesView : UserControl
{
    private Session _session = null!;

    private ModProject? _project;
    private SpriteIndex? _index;
    private SpriteIndex? _stock;
    private readonly List<SpriteRow> _rows = new();
    private readonly Dictionary<string, int> _frameCounts = new();
    private readonly Dictionary<string, BitmapSource> _atlases = new();
    private bool _loading;

    public SpritesView() => InitializeComponent();

    public void Attach(Session session) => _session = session;

    /// <summary>Reports progress to whatever is hosting this.</summary>
    public event Action<string>? Status;

    /// <summary>Raised when a file was added to or dropped from the mod.</summary>
    public event Action? OverridesChanged;

    private Window? Owner => Window.GetWindow(this);

    /// <summary>
    /// The season the frame counts are read from. Any will do: a sprite that exists in two
    /// seasons has the same number of frames in both.
    /// </summary>
    private const string CountEra = "forest";

    /// <summary>
    /// The season the previews are cut from.
    ///
    /// Not a constant any more. A sprite is drawn separately per season and is missing from
    /// some, so showing one season only made an import that had worked in two look like one
    /// that had worked in none.
    /// </summary>
    private string _previewEra = CountEra;

    public void Refresh(ModProject? project)
    {
        _project = project;
        _atlases.Clear();
        Build();
    }

    /// <summary>
    /// The index and the frame counts come off four atlases; reading them on the thread that
    /// draws the window is what made switching to this tab feel like a stall.
    /// </summary>
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

    private async void Build()
    {
        var generation = ++_generation;

        if (_session?.Game is null)
        {
            Summary.Text = "Set your game folder in Settings first.";
            return;
        }

        var stockPath = _session.RequireStock(SpriteIndex.Path);
        var indexPath = _session.RequireFile(_project, SpriteIndex.Path);

        Summary.Text = "";

        var loaded = await Busy.While("Reading the sprite index…",
            () => (Stock: SpriteIndex.Load(stockPath), Index: SpriteIndex.Load(indexPath)),
            ex => Summary.Text = "Could not read the sprite index: " + ex.Message);

        if (loaded.Index is null || generation != _generation) return;

        _stock = loaded.Stock;
        _index = loaded.Index;

        CountFrames();
        BuildRows();

        Summary.Text = $"{_rows.Count} sprites in {Path.GetFileName(SpriteIndex.Path)}. "
                       + "A sprite draws from an atlas; this is which one.";
    }

    /// <summary>
    /// How many frames each sprite has, read once from the era's atlases. It decides which
    /// swaps are safe, so it is worth the four small JSON reads.
    /// </summary>
    private void CountFrames()
    {
        _frameCounts.Clear();
        if (_index is null || _session.Game is null) return;

        // Counted the way the preview, the export and the import count, which is not the
        // same as counting names that look alike: the runestone's five extra drawings are
        // called rock_0_f0 and upwards, and grouping on the text before the last underscore
        // filed them apart from rock_0 and reported one frame where there are six.
        foreach (var group in new[] { "common_era", "human_common", "human_era", "orc_common", "orc_era" })
        {
            if (_index.Atlas(CountEra, group) is not { } files) continue;

            SpriteAtlas atlas;
            try
            {
                atlas = SpriteAtlas.Load(_session.RequireFile(_project, files.Json));
            }
            catch
            {
                // An atlas we cannot read costs the counts for its sprites, nothing else.
                continue;
            }

            foreach (var path in _index.Paths)
            {
                if (_index.Source(path) is not { } source || source.Atlas != group) continue;

                var count = atlas.Sequence(source.Prefix).Count;
                if (count > 0) _frameCounts[group + "/" + source.Prefix] = count;
            }
        }
    }

    private int FrameCount(SpriteSource? source) =>
        source is { } s ? _frameCounts.GetValueOrDefault(s.Atlas + "/" + s.Prefix) : 0;

    /// <summary>
    /// The frame names this mod has moved, per atlas group.
    ///
    /// Importing art always repacks, so a frame sitting somewhere other than where the game
    /// put it is the mark of a unit having been redrawn. Read from the tables rather than the
    /// pictures: comparing two fifty-megabyte sheets for every row in the list is not a thing
    /// a list can do.
    /// </summary>
    private Dictionary<string, HashSet<string>> RedrawnFrames()
    {
        var byGroup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (_index is null || _project is null || _session.Game is null) return byGroup;

        foreach (var group in _index.Eras
                     .SelectMany(era => new[] { "common_era", "human_common", "human_era", "orc_common", "orc_era" }
                         .Select(g => (Era: era, Group: g)))
                     .ToList())
        {
            if (_index.Atlas(group.Era, group.Group) is not { } files) continue;
            if (!_project.HasOverride(files.Json)) continue;
            if (byGroup.ContainsKey(group.Group)) continue;

            try
            {
                var mine = SpriteAtlas.Load(_project.ResolveContentPath(files.Json));
                var stock = SpriteAtlas.Load(_session.RequireStock(files.Json));
                byGroup[group.Group] = SpriteSheetIO.ChangedFrames(mine, stock).ToHashSet(StringComparer.Ordinal);
            }
            catch
            {
                // An atlas we cannot read costs the redrawn marks for its sprites, nothing else.
            }
        }

        return byGroup;
    }

    private void BuildRows()
    {
        if (_index is null) return;

        var changed = _stock is null ? new HashSet<string>() : _index.ChangedFrom(_stock).ToHashSet();
        var redrawn = RedrawnFrames();
        var filter = FilterBox.Text.Trim();

        _rows.Clear();
        foreach (var path in _index.Paths)
        {
            var name = SpriteIndex.Describe(path);
            if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            var source = _index.Source(path);

            var art = source is { } s
                      && redrawn.TryGetValue(s.Atlas, out var moved)
                      && moved.Any(f => f.StartsWith(s.Prefix, StringComparison.Ordinal));

            _rows.Add(new SpriteRow
            {
                Path = path,
                Name = name,
                FrameCount = FrameCount(source),
                Changed = changed.Contains(path) || art,
                ArtChanged = art,
                Source = source,
                StockSource = _stock?.Source(path) ?? source,
            });
        }

        _loading = true;
        var keep = (SpriteList.SelectedItem as SpriteRow)?.Path;
        SpriteList.ItemsSource = null;
        SpriteList.ItemsSource = _rows;
        SpriteList.SelectedItem = _rows.FirstOrDefault(r => r.Path == keep);
        _loading = false;

        ShowChosen(SpriteList.SelectedItem as SpriteRow);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_index is not null) BuildRows();
    }

    private void OnSpriteSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ShowChosen(SpriteList.SelectedItem as SpriteRow);
    }

    private void OnAnyFramesToggled(object sender, RoutedEventArgs e) =>
        ShowChosen(SpriteList.SelectedItem as SpriteRow);

    /// <summary>
    /// Shows the sprite again after its sheet has been rewritten.
    ///
    /// The decoded sheets are kept because they are 20 to 55 MB each, so an import leaves
    /// a picture in hand that no longer matches the file. Forgetting it is what makes the
    /// new art appear; without this an import that worked looked like one that had not.
    /// </summary>
    private void ShowArtAgain()
    {
        _atlases.Clear();
        BuildRows();
        ShowChosen(SpriteList.SelectedItem as SpriteRow);
        OverridesChanged?.Invoke();
    }

    private void ShowChosen(SpriteRow? row)
    {
        _loading = true;

        ChosenTitle.Text = row?.Name ?? "Nothing selected";
        ResetButton.IsEnabled = row is { Changed: true } && _project is not null;
        SourceBox.IsEnabled = row is not null && _project is not null;

        // Exporting only reads, so it needs a sprite but not a project to put the result in.
        ExportButton.IsEnabled = row?.Source is not null;
        ImportButton.IsEnabled = row?.Source is not null && _project is not null;

        if (row is null)
        {
            SourceBox.ItemsSource = null;
            ChosenNote.Text = "";
            FrameList.ItemsSource = null;
            PreviewNote.Visibility = Visibility.Visible;
            _loading = false;
            return;
        }

        // Same-length sprites only, unless that guard is turned off: a sprite drawn with
        // fewer frames than it has has nothing to show for the rest of its animation.
        var choices = _rows.Count == _index?.Paths.Count ? _rows : AllRows();
        SourceBox.ItemsSource = AnyFrames.IsChecked == true
            ? choices
            // The sprite itself is always offered: picking it is how you put it back.
            : choices.Where(r => r.FrameCount == row.FrameCount || r.Path == row.Path).ToList();

        SourceBox.SelectedItem = ((IEnumerable<SpriteRow>)SourceBox.ItemsSource)
            .FirstOrDefault(r => r.StockSource == row.Source);

        // The seasons matter most on a sprite the mod has already changed: that is when
        // someone is asking why a season still looks the way it did. Saying only "this
        // mod draws it with other art" dropped exactly the part they needed.
        var seasons = row.Source is { } src
            ? SeasonNote(row.Path, AtlasesWithArt(AtlasesFor(src), src).Count)
            : ".";

        ChosenNote.Text = _project is null
            ? $"{row.Path}. Open a mod project to change it."
            : row.Changed
                ? $"{row.Path}: {row.Frames}, changed by this mod{seasons}"
                : $"{row.Path}: {row.Frames}, from {row.Source?.Atlas} "
                  + $"as {row.Source?.Prefix}*{seasons}";

        ShowSeasons(row);
        ShowFrames(row);
        _loading = false;
    }

    /// <summary>
    /// Offers the seasons this sprite is drawn in, and no others.
    ///
    /// The rock is in forest and swamp only. Listing winter beside them would invite a
    /// look at art that is not there and read as something missing.
    /// </summary>
    private void ShowSeasons(SpriteRow row)
    {
        if (_index is null || row.Source is not { } source)
        {
            EraBox.ItemsSource = null;
            EraBox.IsEnabled = false;
            return;
        }

        var seasons = AtlasesWithArt(AtlasesFor(source), source).Select(s => s.Era).ToList();
        if (seasons.Count == 0) seasons = _index.Eras.ToList();

        EraBox.ItemsSource = seasons;
        EraBox.IsEnabled = seasons.Count > 1;

        if (!seasons.Contains(_previewEra)) _previewEra = seasons[0];
        EraBox.SelectedItem = _previewEra;
    }

    private void OnPreviewEraChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EraBox.SelectedItem is not string era || era == _previewEra) return;

        _previewEra = era;
        ShowFrames(SpriteList.SelectedItem as SpriteRow ?? default!);
    }

    /// <summary>
    /// How many seasons draw this sprite, and which sprites draw the rest.
    ///
    /// The second half matters more than the first: art imported into the rock leaves a
    /// winter map unchanged, and nothing about the rock says why.
    /// </summary>
    private string SeasonNote(string path, int seasons)
    {
        var all = _index?.Eras.Count ?? 0;
        if (seasons == 0 || all == 0 || seasons == all) return ".";

        var family = _index?.Family(path) ?? Array.Empty<string>();

        return $", in {seasons} of the {all} seasons."
               + (family.Count == 0
                   ? ""
                   : " The rest are drawn by "
                     + string.Join(" and ", family.Select(SpriteIndex.Describe))
                     + ", which take their own art.");
    }

    /// <summary>Every sprite, ignoring the filter, so the picker is not narrowed by it too.</summary>
    private List<SpriteRow> AllRows()
    {
        if (_index is null) return new List<SpriteRow>();

        var changed = _stock is null ? new HashSet<string>() : _index.ChangedFrom(_stock).ToHashSet();
        return _index.Paths.Select(path =>
        {
            var source = _index.Source(path);
            return new SpriteRow
            {
                Path = path,
                Name = SpriteIndex.Describe(path),
                FrameCount = FrameCount(source),
                Changed = changed.Contains(path),

                // These rows only fill the "draw with" picker, which offers art to take
                // rather than art to put back, so whether one has been redrawn is not asked.
                ArtChanged = false,
                Source = source,
                StockSource = _stock?.Source(path) ?? source,
            };
        }).ToList();
    }

    /// <summary>
    /// The sprite's frames, cut out of its atlas. One atlas is decoded at a time and kept:
    /// they are 20-55 MB pictures, so holding all of them would cost hundreds of megabytes.
    /// </summary>
    private void ShowFrames(SpriteRow row)
    {
        FrameList.ItemsSource = null;

        if (row.Source is not { } source || _index is null || _session.Game is null)
        {
            PreviewNote.Text = "This sprite has no atlas.";
            PreviewNote.Visibility = Visibility.Visible;
            return;
        }

        if (_index.Atlas(_previewEra, source.Atlas) is not { } files)
        {
            PreviewNote.Text = $"No {_previewEra} atlas for {source.Atlas}.";
            PreviewNote.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            // Keyed by the file, not the group. A group names a different sheet in every
            // season, so keying on the group alone handed the winter picture to a forest
            // sprite and cut its frames from whatever happened to be there.
            var key = files.Image;

            if (!_atlases.TryGetValue(key, out var sheet))
            {
                Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                sheet = LoadImage(_session.RequireFile(_project, files.Image));

                // These are 20 to 55 MB each and there are twenty of them across the
                // seasons, so only the last few are worth keeping.
                if (_atlases.Count >= 3) _atlases.Clear();
                _atlases[key] = sheet;
            }

            // The same list export and import work from. Counting here separately is how
            // the runestone came to show one of its six pictures: this loop looked for
            // rock_1 and stopped, while the other five are named rock_0_f0 and upwards.
            var pictures = new List<BitmapSource>();
            var atlas = SpriteAtlas.Load(_session.RequireFile(_project, files.Json));

            foreach (var frame in atlas.Sequence(source.Prefix))
            {
                var cut = Cut(sheet, frame.Rect);
                if (cut is not null) pictures.Add(cut);
                if (pictures.Count >= 120) break;      // enough to see what it is
            }

            FrameList.ItemsSource = pictures;
            PreviewNote.Visibility = pictures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (pictures.Count == 0) PreviewNote.Text = "That atlas has no frames with this prefix.";
        }
        catch (Exception ex)
        {
            PreviewNote.Text = "Could not read the atlas: " + ex.Message;
            PreviewNote.Visibility = Visibility.Visible;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private static BitmapSource? Cut(BitmapSource sheet, IconRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0
            || rect.X + rect.Width > sheet.PixelWidth
            || rect.Y + rect.Height > sheet.PixelHeight)
            return null;

        var cut = new CroppedBitmap(sheet, new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height));
        cut.Freeze();
        return cut;
    }

    private static BitmapSource LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }



    private void OnSourceChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _project is null || _index is null) return;
        if (SpriteList.SelectedItem is not SpriteRow target) return;
        if (SourceBox.SelectedItem is not SpriteRow source) return;

        // Nothing to do only when it already draws that sprite's own art. Choosing the
        // sprite itself after a swap is meaningful: it puts the art back.
        if (source.StockSource == target.Source) return;

        var stock = SpriteIndex.Load(_session.RequireStock(SpriteIndex.Path));
        Apply(index => index.Remap(target.Path, source.Path, stock),
              source.Path == target.Path
                  ? $"{target.Name} is back to its own art."
                  : $"{target.Name} is now drawn with {source.Name}'s art.",
              source.Path == target.Path
                  ? $"{target.Name} back to its own art"
                  : $"{target.Name} drawn with {source.Name}'s art");
    }

    /// <summary>
    /// Puts a sprite back: the art it points at, the art it is drawn with, or both.
    ///
    /// Two quite different repairs behind one button, because from the outside they are one
    /// thing. Pointing a sprite at another's art is a line in the index. Redrawing it rewrote
    /// a sheet shared with seventeen other units, and getting that back means putting this
    /// unit's frames in again without disturbing anybody else's.
    /// </summary>
    private async void OnResetSprite(object sender, RoutedEventArgs e)
    {
        if (_project is null || SpriteList.SelectedItem is not SpriteRow target) return;

        if (target.ArtChanged && target.Source is { } source && !await ResetArt(target, source)) return;

        if (_index is not null && _stock is not null
            && !JsonEqual(_index.Source(target.Path), _stock.Source(target.Path)))
        {
            Apply(index => index.RestoreFrom(SpriteIndex.Load(_session.RequireStock(SpriteIndex.Path)), target.Path),
                  $"{target.Name} is back to the game's art.",
                  $"reset {target.Name}'s art");
            return;
        }

        BuildRows();
        OverridesChanged?.Invoke();
    }

    private static bool JsonEqual(SpriteSource? a, SpriteSource? b) => Equals(a, b);

    /// <summary>
    /// Packs the game's own frames for this unit back into the mod's sheet.
    ///
    /// When this unit is the only one the sheet has had redrawn, the overrides are simply
    /// dropped instead — which gives back the game's exact bytes rather than a repacked
    /// approximation of them, and shrinks the mod by fifty megabytes a sheet.
    /// </summary>
    private async Task<bool> ResetArt(SpriteRow row, SpriteSource source)
    {
        if (_project is null || _session.Game is null) return false;

        var sets = AtlasesFor(source);
        if (sets.Count == 0) return false;

        var jobs = sets
            .Select(set => (
                set.Json, set.Image, set.MaskJson, set.MaskImage,
                Sheet: _session.RequireFile(_project, set.Image),
                Atlas: _session.RequireFile(_project, set.Json),
                MaskSheet: set.MaskImage is null ? null : _session.RequireFile(_project, set.MaskImage),
                MaskAtlas: set.MaskJson is null ? null : _session.RequireFile(_project, set.MaskJson),
                StockSheet: _session.RequireStock(set.Image),
                StockAtlas: _session.RequireStock(set.Json),
                StockMaskSheet: set.MaskImage is null ? null : _session.RequireStock(set.MaskImage),
                StockMaskAtlas: set.MaskJson is null ? null : _session.RequireStock(set.MaskJson),
                Overridden: _project.HasOverride(set.Json)))
            .Where(job => job.Overridden)
            .ToList();

        if (jobs.Count == 0) return true;

        var done = await Working.While(Owner, $"Putting {row.Name}'s art back…", () =>
            jobs.Select(job =>
            {
                // If nothing else in this sheet has been redrawn, the whole override goes.
                var moved = SpriteSheetIO.ChangedFrames(
                    SpriteAtlas.Load(job.Atlas), SpriteAtlas.Load(job.StockAtlas));

                if (moved.All(f => f.StartsWith(source.Prefix, StringComparison.Ordinal)))
                    return (job, Drop: true, Sheet: Array.Empty<byte>(), Atlas: "",
                            Mask: (byte[]?)null, MaskAtlas: (string?)null);

                var (sheet, atlas, mask, maskAtlas, _) = SpriteSheetIO.Restore(
                    source, job.Sheet, job.Atlas, job.StockSheet, job.StockAtlas,
                    job.MaskSheet, job.MaskAtlas, job.StockMaskSheet, job.StockMaskAtlas);

                return (job, Drop: false, Sheet: sheet, Atlas: atlas, Mask: mask, MaskAtlas: maskAtlas);
            }).ToList(),
            ex => Ui.Failed(Owner, "reset that art", ex));

        if (done is null) return false;

        try
        {
            var touched = done
                .SelectMany(d => new[] { d.job.Image, d.job.Json, d.job.MaskImage, d.job.MaskJson })
                .OfType<string>()
                .ToList();

            _session.FileUndo.Record(_project, $"reset {row.Name}'s art", touched, () =>
            {
                foreach (var (job, drop, sheet, atlas, mask, maskAtlas) in done)
                {
                    if (drop)
                    {
                        _project.RemoveOverride(job.Image);
                        _project.RemoveOverride(job.Json);

                        if (job.MaskImage is not null) _project.RemoveOverride(job.MaskImage);
                        if (job.MaskJson is not null) _project.RemoveOverride(job.MaskJson);
                        continue;
                    }

                    _project.WriteOverride(job.Image, sheet);
                    _project.WriteOverride(job.Json, System.Text.Encoding.UTF8.GetBytes(atlas));

                    if (mask is not null && maskAtlas is not null
                        && job.MaskImage is not null && job.MaskJson is not null)
                    {
                        _project.WriteOverride(job.MaskImage, mask);
                        _project.WriteOverride(job.MaskJson, System.Text.Encoding.UTF8.GetBytes(maskAtlas));
                    }
                }
            },
            refresh: ShowArtAgain);

            Status?.Invoke($"{row.Name} is drawn with the game's own art again.");
            return true;
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "reset that art", ex);
            return false;
        }
    }

    /// <summary>Re-reads the sprite table and redraws the list from it.</summary>
    private void ReloadIndex()
    {
        if (_project is null) return;

        _index = SpriteIndex.Load(_project.ResolveContentPath(SpriteIndex.Path));
        BuildRows();
    }

    private void Apply(Func<SpriteIndex, bool> edit, string done, string describe)
    {
        if (_project is null || _session.Game is null) return;

        try
        {
            var changed = false;

            // The table is saved rather than written as an override, but undo restores by
            // path either way, and a table the mod did not own before is put back by
            // dropping it.
            _session.FileUndo.Record(_project, describe, new[] { SpriteIndex.Path },
                () =>
                {
                    if (!_project.HasOverride(SpriteIndex.Path))
                        _project.SeedFromGame(SpriteIndex.Path, _session.Game, _session.Vault);

                    var file = _project.ResolveContentPath(SpriteIndex.Path);
                    var table = SpriteIndex.Load(file);

                    changed = edit(table);
                    if (changed) table.Save(file);
                },
                refresh: () => { ReloadIndex(); OverridesChanged?.Invoke(); });

            if (!changed)
            {
                Status?.Invoke("Nothing to change there.");
                return;
            }

            ReloadIndex();
            OverridesChanged?.Invoke();
            Status?.Invoke(done);
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "change that sprite", ex);
        }
    }

    // ---- the row's own menu ----------------------------------------------

    /// <summary>
    /// Double-clicking a sprite opens the list of art to take. The dropdown below is easy to
    /// miss, and swapping is the only thing this list is for.
    /// </summary>
    private void OnSpriteDoubleClick(object sender, RoutedEventArgs e)
    {
        if (SourceBox.IsEnabled) SourceBox.IsDropDownOpen = true;
    }

    private void OnSpriteMenuOpening(object sender, RoutedEventArgs e)
    {
        SpriteSwapItem.IsEnabled = SourceBox.IsEnabled;
        SpriteResetItem.IsEnabled = ResetButton.IsEnabled;
        SpriteExportItem.IsEnabled = ExportButton.IsEnabled;
        SpriteImportItem.IsEnabled = ImportButton.IsEnabled;
    }

    /// <summary>
    /// Selects what was right-clicked. Without this the menu acts on whatever was selected
    /// before, which is how you reset the wrong one.
    /// </summary>
    private void OnSpriteRightClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>() is { } item
            && !item.IsSelected)
            item.IsSelected = true;
    }

    // ---- taking the art out and putting it back --------------------------

    /// <summary>The four files one atlas group is made of, for one tileset.</summary>
    private sealed record AtlasFiles(string Era, string Json, string Image, string? MaskJson, string? MaskImage);

    /// <summary>
    /// The distinct atlases a sprite draws from.
    ///
    /// Usually one: a unit lives in <c>human_common</c> or <c>orc_common</c>, which every
    /// tileset shares. Buildings are the exception — their group resolves to a different file
    /// per tileset, because a barracks in the snow is drawn differently — so those come back
    /// as four, and each is exported and imported on its own.
    /// </summary>
    /// <summary>
    /// Narrows the atlases to the ones that actually hold this sprite.
    ///
    /// A group name is shared by every season, but the art is not: the rock is drawn in
    /// forest and swamp and does not exist in winter or xswamp. Handing an atlas art it
    /// has no frames for fails, and it is not a mistake worth reporting: the sprite simply
    /// is not in that season.
    /// </summary>
    private IReadOnlyList<AtlasFiles> AtlasesWithArt(IReadOnlyList<AtlasFiles> sets, SpriteSource source)
    {
        var found = new List<AtlasFiles>();

        foreach (var set in sets)
        {
            try
            {
                var atlas = SpriteAtlas.Load(_session.RequireFile(_project, set.Json));
                if (atlas.Sequence(source.Prefix, "").Count > 0) found.Add(set);
            }
            catch (Exception)
            {
                // An atlas we cannot read is one we cannot rule out, so it stays in and
                // fails later with a message about itself.
                found.Add(set);
            }
        }

        return found;
    }

    private IReadOnlyList<AtlasFiles> AtlasesFor(SpriteSource source)
    {
        if (_index is null) return Array.Empty<AtlasFiles>();

        var found = new List<AtlasFiles>();

        foreach (var era in _index.Eras)
        {
            if (_index.Atlas(era, source.Atlas) is not { } files) continue;
            if (found.Any(f => f.Json == files.Json)) continue;

            var mask = _index.MaskAtlas(era, source.Atlas);
            found.Add(new AtlasFiles(era, files.Json, files.Image, mask?.Json, mask?.Image));
        }

        return found;
    }

    /// <summary>The file to read a sprite sheet from: this mod's if it has one, else the game's.</summary>

    private static string? ChooseFolder(Window? owner, string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }

    private async void OnExportSprite(object sender, RoutedEventArgs e)
    {
        if (SpriteList.SelectedItem is not SpriteRow row || row.Source is not { } source) return;
        if (_session.Game is null) return;

        var sets = AtlasesWithArt(AtlasesFor(source), source);
        if (sets.Count == 0)
        {
            Ui.Error(Owner, "Nothing to export", $"{row.Name} has no art in this build.");
            return;
        }

        if (ChooseFolder(Owner, $"Where should {row.Name}'s art go?") is not { } folder) return;

        // Resolved here: the project and the session are not the background thread's to touch.
        var jobs = sets
            .Select(set => (
                set.Era,
                Folder: sets.Count == 1 ? folder : Path.Combine(folder, set.Era),
                Sheet: _session.RequireFile(_project, set.Image),
                Atlas: _session.RequireFile(_project, set.Json),
                MaskSheet: set.MaskImage is null ? null : _session.RequireFile(_project, set.MaskImage),
                MaskAtlas: set.MaskJson is null ? null : _session.RequireFile(_project, set.MaskJson)))
            .ToList();

        var written = await Working.While(Owner, $"Writing {row.Name}'s frames…", () =>
            jobs.Select(job => SpriteSheetIO.Export(
                    job.Folder, row.Path, source,
                    job.Sheet, job.Atlas, job.MaskSheet, job.MaskAtlas))
                .ToList(),
            ex => Ui.Failed(Owner, "export that art", ex));

        if (written is null || written.Count == 0) return;

        var first = written[0];
        Status?.Invoke(
            $"{row.Name}: {first.Frames} frames as {first.Columns} across by {first.Rows} down"
            + $" in {folder}. Keep the grid and paint inside the cells."
            + (written.Count > 1
                ? $" This sprite has separate art per season, so there is a folder for each"
                  + $" ({string.Join(", ", sets.Select(s => s.Era))}). Edit one and import"
                  + " the parent folder: the others take the same drawing unless you edit"
                  + " them too."
                : ""));
    }

    private async void OnImportSprite(object sender, RoutedEventArgs e)
    {
        if (SpriteList.SelectedItem is not SpriteRow row || row.Source is not { } source) return;
        if (_session.Game is null) return;

        if (_project is null)
        {
            Ui.Error(Owner, "No project open", "Create or open a mod project first.");
            return;
        }

        var all = AtlasesFor(source);
        var sets = AtlasesWithArt(all, source);

        if (sets.Count == 0)
        {
            // Naming the prefix and the files checked, because "no art" on its own leaves
            // nothing to act on: the answer is either the wrong sprite or a build that
            // does not draw this one.
            Ui.Error(Owner, "Nothing to import",
                $"No atlas in this build has frames named {source.Prefix}, so there is "
                + "nothing here to replace.\n\n"
                + $"{row.Path} says it draws from \"{source.Atlas}\", and that group was "
                + $"looked for in {(all.Count == 0 ? "no atlas at all" : string.Join(", ", all.Select(s => s.Era)))}.\n\n"
                + "Check that the sprite selected on the left is the one the folder was "
                + "exported from.");
            return;
        }

        if (ChooseFolder(Owner, $"Where is {row.Name}'s edited art?") is not { } folder) return;

        // One drawing is usually meant for every season. Rather than make someone copy the
        // same folder four times, an era with nothing of its own borrows from wherever the
        // art actually is: the folder itself, or another era's.
        var missing = sets.Select(s => s.Era)
                          .Where(era => SpriteSheetIO.ArtFolder(folder, era, sets.Count) is null)
                          .ToList();

        if (missing.Count == sets.Count)
        {
            Ui.Error(Owner, "Nothing to import",
                $"There is no {SpriteSheetIO.SidecarName} in that folder, or in a folder "
                + $"named after an era inside it ({string.Join(", ", sets.Select(s => s.Era))}).");
            return;
        }

        var jobs = sets
            .Select(set => (
                set.Era, set.Json, set.Image, set.MaskJson, set.MaskImage,
                Folder: SpriteSheetIO.ArtFolder(folder, set.Era, sets.Count)!,
                Sheet: _session.RequireFile(_project, set.Image),
                Atlas: _session.RequireFile(_project, set.Json),
                MaskSheet: set.MaskImage is null ? null : _session.RequireFile(_project, set.MaskImage),
                MaskAtlas: set.MaskJson is null ? null : _session.RequireFile(_project, set.MaskJson)))
            .ToList();

        // The sheets run to fifty megabytes and are repacked frame by frame, so this is the
        // one thing in here that genuinely must not happen where the window is drawn.
        var done = await Working.While(Owner, $"Repacking {row.Name}…", () =>
            jobs.Select(job =>
            {
                var (sheet, atlas, mask, maskAtlas, result) = SpriteSheetIO.Import(
                    job.Folder, source, job.Sheet, job.Atlas, job.MaskSheet, job.MaskAtlas);

                return (job, sheet, atlas, mask, maskAtlas, result);
            }).ToList(),
            ex => Ui.Failed(Owner, "import that art", ex));

        if (done is null || done.Count == 0) return;

        try
        {
            // Every file the import touches goes into one entry. Undoing half a repack would
            // leave a sheet whose frame table describes a different sheet.
            var touched = done
                .SelectMany(d => new[] { d.job.Image, d.job.Json, d.job.MaskImage, d.job.MaskJson })
                .OfType<string>()
                .ToList();

            _session.FileUndo.Record(_project, $"import {row.Name}'s art", touched, () =>
            {
                foreach (var (job, sheet, atlas, mask, maskAtlas, _) in done)
                {
                    _project.WriteOverride(job.Image, sheet);
                    _project.WriteOverride(job.Json, System.Text.Encoding.UTF8.GetBytes(atlas));

                    if (mask is not null && maskAtlas is not null
                        && job.MaskImage is not null && job.MaskJson is not null)
                    {
                        _project.WriteOverride(job.MaskImage, mask);
                        _project.WriteOverride(job.MaskJson, System.Text.Encoding.UTF8.GetBytes(maskAtlas));
                    }
                }
            },
            refresh: ShowArtAgain);

            var result = done[0].result;
            ShowArtAgain();

            var borrowed = jobs
                .Where(j => !string.Equals(j.Folder, Path.Combine(folder, j.Era),
                                           StringComparison.OrdinalIgnoreCase))
                .Select(j => j.Era)
                .ToList();

            Status?.Invoke(
                $"{row.Name}: {result.Frames} frames redrawn"
                + (result.MaskFrames > 0 ? $", {result.MaskFrames} team-colour frames with them" : "")
                + (jobs.Count > 1 ? $", in {jobs.Count} seasons" : "")
                + $". Sheet is now {result.Width} x {result.Height}"
                + (result.Grew ? ". It grew to fit." : ".")
                + (borrowed.Count > 0
                    ? $" {string.Join(", ", borrowed)} had no art of their own and took the"
                      + " same drawing."
                    : ""));
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "import that art", ex);
        }
    }
}
