using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using RuneFoundry.Core;
using RuneFoundry.Core.Formats;
using RuneFoundry.UI;

namespace RuneFoundry.Editor.Views;

/// <summary>One frame of the icon sheet, as the grid shows it.</summary>
public sealed class IconRow : Observable
{
    public required int Id { get; init; }
    public required BitmapSource? Picture { get; init; }

    private bool _changed;

    public bool Changed
    {
        get => _changed;
        set { if (Set(ref _changed, value)) { Raise(nameof(Caption)); Raise(nameof(Tip)); } }
    }

    public required string Name { get; init; }

    public bool Named => Name.Length > 0;

    public string Caption => (Named ? $"{Id} · {Name}" : $"Icon {Id}") + (Changed ? " ●" : "");

    public required string Tip { get; init; }

    public override string ToString() => Caption;
}

/// <summary>
/// The icon sheet, and a way to make one icon draw another's art.
///
/// The game decides which icon a unit uses in its own code, so nothing here can point a
/// unit at a different icon. What it can do is change what a frame reads from: give the
/// Peasant's frame the Ogre's rectangle and every place the Peasant's icon appears shows an
/// Ogre. That is the swap, and it is why this is a sheet of pictures rather than a list of
/// units.
/// </summary>
public partial class IconsView : UserControl
{
    private Session _session = null!;

    private ModProject? _project;
    private IconAtlas? _face;
    private Dictionary<int, string> _labels = new();
    private IconAtlas? _stockFace;
    private BitmapSource? _sheet;
    private BitmapSource? _stockSheet;
    private readonly List<IconRow> _rows = new();
    private readonly List<IconRow> _stockRows = new();
    private bool _loading;

    public IconsView() => InitializeComponent();

    public void Attach(Session session) => _session = session;

    /// <summary>Reports progress to whatever is hosting this.</summary>
    public event Action<string>? Status;

    /// <summary>Raised when a file was added to or dropped from the mod.</summary>
    public event Action? OverridesChanged;

    public void Refresh(ModProject? project)
    {
        _project = project;
        Build();
    }

    private Window? Owner => Window.GetWindow(this);

    /// <summary>
    /// Loading a 20 MB sheet and cutting 196 pictures out of it is not free, so it happens
    /// off the thread that draws the window and the strip says it is happening. Everything
    /// it produces is frozen, which is what makes it safe to build over there.
    ///
    /// Done once and kept until the project changes.
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

        var facePath = _session.RequireFile(_project, IconAtlas.FacePath);
        var stockFacePath = _session.RequireStock(IconAtlas.FacePath);
        var image = _session.RequireFile(_project, IconAtlas.FaceImagePath);
        var stockImage = _session.RequireStock(IconAtlas.FaceImagePath);

        Summary.Text = "";

        // Everything here — the sheet, the two atlases, the names, and all 196 cut pictures —
        // is built on the background thread and frozen, so the UI thread only ever receives
        // finished objects. Leaving the cutting behind was worth 300 ms of stall on its own.
        var loaded = await Busy.While("Reading the icon sheet…", () =>
        {
            var stockFace = IconAtlas.Load(stockFacePath);
            var face = IconAtlas.Load(facePath);
            var sheet = LoadSheet(image);
            var stockSheet = string.Equals(image, stockImage, StringComparison.OrdinalIgnoreCase)
                ? sheet
                : LoadSheet(stockImage);
            var labels = BuildLabels();
            var (rows, stockRows) = CutRows(face, stockFace, sheet, stockSheet, labels);
            return (stockFace, face, sheet, stockSheet, labels, rows, stockRows);
        },
        ex => Summary.Text = "Could not read the icon sheet: " + ex.Message);

        // Default when the load threw; the message is already on screen.
        if (loaded.face is null || generation != _generation) return;

        _stockFace = loaded.stockFace;
        _face = loaded.face;
        _sheet = loaded.sheet;
        _stockSheet = loaded.stockSheet;
        _labels = loaded.labels;

        _rows.Clear();
        _rows.AddRange(loaded.rows);
        _stockRows.Clear();
        _stockRows.AddRange(loaded.stockRows);

        ShowRows();

        Summary.Text = $"{_rows.Count} icons in {Path.GetFileName(IconAtlas.FaceImagePath)}, "
                       + $"{_labels.Count} of them named by the unit and upgrade tables.";
    }

    /// <summary>
    /// Cuts every icon out of the sheet. Pure work over frozen bitmaps, so it runs on the
    /// background thread and hands back plain lists.
    /// </summary>
    private (List<IconRow> Rows, List<IconRow> StockRows) CutRows(
        IconAtlas face, IconAtlas? stockFace, BitmapSource sheet, BitmapSource? stockSheet,
        Dictionary<int, string> labels)
    {
        var rows = new List<IconRow>();
        var stockRows = new List<IconRow>();

        var changed = stockFace is null
            ? new HashSet<int>()
            : face.ChangedFrom(stockFace).ToHashSet();

        foreach (var id in face.Ids)
        {
            labels.TryGetValue(id, out var name);

            rows.Add(new IconRow
            {
                Id = id,
                Picture = Cut(sheet, face, id),
                Changed = changed.Contains(id),
                Name = name ?? "",
                Tip = Tip(id, name),
            });

            // The picker's entry for an icon: this mod's sheet, cut at the game's rectangle.
            //
            // The rectangle has to be the game's, or the choices drift as swaps pile up —
            // pointing A at B's frame changes A, not B, so B must go on being offered where
            // B is. The picture has to be the mod's, because painting over B's frame does
            // change what B is, and offering Blizzard's art for it was offering something
            // the mod no longer draws.
            stockRows.Add(new IconRow
            {
                Id = id,
                Picture = Cut(sheet, stockFace ?? face, id),
                Changed = changed.Contains(id),
                Name = name ?? "",
                Tip = Tip(id, name),
            });
        }

        return (rows, stockRows);
    }

    /// <summary>Puts the finished rows on screen. The only part that needs the UI thread.</summary>
    private void ShowRows()
    {
        IconList.ItemsSource = null;
        IconList.ItemsSource = _rows;

        // The picker is cut at the game's rectangles, so an icon already swapped still
        // offers every original to swap to, drawn as this mod draws it. Without this it had
        // no items at all: selecting one silently did nothing, because there was nothing to
        // select.
        var chosen = SourceBox.SelectedItem;
        SourceBox.ItemsSource = null;
        SourceBox.ItemsSource = _stockRows;
        SourceBox.SelectedItem = chosen;
    }

    private Dictionary<int, string> BuildLabels()
    {
        GameStrings? strings = null;
        UpgradeDataFile? upgrades = null;

        try { strings = GameStrings.Load(_session.RequireFile(_project, "Strings/enUS.json")); } catch { }
        try { upgrades = UpgradeDataFile.Load(_session.RequireFile(_project, "Rez/upgrades.dat")); } catch { }

        string UnitName(int unit)
        {
            var named = strings?.UnitName(unit);
            return string.IsNullOrWhiteSpace(named) ? DatNames.UnitName(unit) : named;
        }

        string UpgradeName(int upgrade)
        {
            var named = strings?.UpgradeName(upgrade);
            return string.IsNullOrWhiteSpace(named) ? DatNames.UpgradeName(upgrade) : named;
        }

        return upgrades is null
            ? IconNames.Label(UnitName, UpgradeName)
            : IconNames.Label(UnitName, UpgradeName,
                upgrade => (int)upgrades.Get("icon", upgrade), DatSchema.UpgradeCount);
    }

    private string Tip(int id, string? name)
    {
        var users = IconNames.UnitsUsing(id).ToList();
        return users.Count > 1
            ? $"Icon {id} is drawn by {users.Count} units, so a swap changes all of them."
            : name is null ? $"Icon {id}. Nothing in the unit or upgrade tables uses it."
            : $"Icon {id}.";
    }

    private BitmapSource? CutStock(int id)
    {
        if (_stockSheet is null || _stockFace?.Rect(IconAtlas.Tilesets[0], id) is not { } rect) return null;
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        if (rect.X + rect.Width > _stockSheet.PixelWidth || rect.Y + rect.Height > _stockSheet.PixelHeight) return null;

        var cut = new CroppedBitmap(_stockSheet, new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height));
        cut.Freeze();
        return cut;
    }

    /// <summary>The picture for one frame, taken out of the sheet as the atlas describes it.</summary>
    private static BitmapSource? Cut(BitmapSource? sheet, IconAtlas? atlas, int id)
    {
        var _sheet = sheet;
        if (_sheet is null || atlas?.Rect(IconAtlas.Tilesets[0], id) is not { } rect) return null;
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        if (rect.X + rect.Width > _sheet.PixelWidth || rect.Y + rect.Height > _sheet.PixelHeight) return null;

        var cut = new CroppedBitmap(_sheet, new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height));
        cut.Freeze();
        return cut;
    }

    private static BitmapSource LoadSheet(string path)
    {
        // Through a stream, so nothing keeps a handle on a file the mod may replace.
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>This mod's copy of a file if it has one, else the game's.</summary>


    private void OnIconSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ShowChosen(IconList.SelectedItem as IconRow);
    }

    private void ShowChosen(IconRow? row)
    {
        _loading = true;

        ChosenTitle.Text = row is null ? "Nothing selected" : row.Name;
        ChosenPicture.Source = row?.Picture;
        SourceBox.SelectedItem = row is null
            ? null
            : _stockRows.FirstOrDefault(r => r.Id == DrawnBy(row.Id));
        SourceBox.IsEnabled = row is not null && _project is not null;
        ResetButton.IsEnabled = row is { Changed: true } && _project is not null;

        ChosenNote.Text = row is null ? ""
            : _project is null ? $"Icon {row.Id}. Open a mod project to change it."
            : row.Changed ? $"Icon {row.Id}. This mod draws it with other art."
            : $"Icon {row.Id}.";

        _loading = false;
    }

    private void OnSourceChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _project is null || _session.Game is null) return;
        if (IconList.SelectedItem is not IconRow target) return;
        if (SourceBox.SelectedItem is not IconRow source) return;

        // Choosing the icon's own number after a swap is how you put it back, so it is only
        // a no-op when the frame already draws that icon's own art.
        var stockFace = IconAtlas.Load(_session.RequireStock(IconAtlas.FacePath));
        if (stockFace.Rect(IconAtlas.Tilesets[0], source.Id)
            == _face?.Rect(IconAtlas.Tilesets[0], target.Id)) return;

        Apply((atlas, stock) => atlas.Remap(target.Id, source.Id, stock),
              source.Id == target.Id
                  ? $"Icon {target.Id} ({target.Name}) is back to its own art."
                  : $"Icon {target.Id} ({target.Name}) now draws {source.Name}.",
              source.Id == target.Id
                  ? $"icon {target.Id} back to its own art"
                  : $"icon {target.Id} draws {source.Name}");
    }

    /// <summary>
    /// Saves the selected icons as PNG files.
    ///
    /// One icon asks where to put it; several ask for a folder and are named by number and
    /// unit, so a set comes out in an order that means something. The picture saved is the
    /// one on screen, which is the mod's art where the mod has changed it.
    /// </summary>
    private void OnExportIcon(object sender, RoutedEventArgs e)
    {
        var chosen = IconList.SelectedItems.OfType<IconRow>()
                             .Where(r => r.Picture is not null)
                             .ToList();

        if (chosen.Count == 0)
        {
            Status?.Invoke("Select an icon first.");
            return;
        }

        try
        {
            if (chosen.Count == 1)
            {
                var only = chosen[0];
                var dialog = new SaveFileDialog
                {
                    Title = "Export icon",
                    Filter = "PNG image (*.png)|*.png",
                    FileName = FileNameFor(only),
                    AddExtension = true,
                    DefaultExt = ".png",
                };
                if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

                Write(only, dialog.FileName);
                Status?.Invoke($"Exported icon {only.Id} to {Path.GetFileName(dialog.FileName)}.");
                return;
            }

            var folder = new OpenFolderDialog { Title = $"Export {chosen.Count} icons to" };
            if (folder.ShowDialog(Window.GetWindow(this)) != true) return;

            foreach (var row in chosen)
                Write(row, Path.Combine(folder.FolderName, FileNameFor(row)));

            Status?.Invoke($"Exported {chosen.Count} icons to {folder.FolderName}.");
        }
        catch (Exception ex)
        {
            Ui.Failed(Window.GetWindow(this), "export the icons", ex);
        }
    }

    /// <summary>A name that sorts by icon number and still says what the icon is.</summary>
    private static string FileNameFor(IconRow row)
    {
        var name = row.Name;
        foreach (var bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, ' ');

        name = name.Trim();
        return name.Length == 0 ? $"icon-{row.Id:000}.png" : $"icon-{row.Id:000} {name}.png";
    }

    private static void Write(IconRow row, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(row.Picture!));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void OnResetIcon(object sender, RoutedEventArgs e)
    {
        if (_project is null || IconList.SelectedItem is not IconRow target) return;

        Apply((atlas, stock) => atlas.RestoreFrom(stock, target.Id),
              $"Icon {target.Id} ({target.Name}) is back to the game's art.",
              $"reset icon {target.Id}");
    }

    /// <summary>
    /// Runs an edit against the mod's copy of the sheet, taking the file into the mod first
    /// if it is not there yet.
    ///
    /// The face and the mask are two sheets with the same frame names, and each is edited
    /// against its own stock copy — a mask rewritten with rectangles from the face sheet
    /// would take its team colour from the wrong part of a different picture.
    /// </summary>
    private void Apply(Func<IconAtlas, IconAtlas, int> edit, string done, string describe)
    {
        if (_project is null || _session.Game is null) return;

        try
        {
            var wrote = false;

            // Both tables go in one entry, and the entry is recorded even though the write
            // below is a Save rather than an override: undo restores files by path, and a
            // table the mod did not own before is put back by dropping it.
            _session.FileUndo.Record(_project, describe,
                new[] { IconAtlas.FacePath, IconAtlas.MaskPath },
                () => wrote = Edit(edit),
                refresh: () => { Reload(); OverridesChanged?.Invoke(); });

            if (!wrote)
            {
                Status?.Invoke("Nothing to change there.");
                return;
            }

            Reload();
            OverridesChanged?.Invoke();
            Status?.Invoke(done);
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "replace that icon", ex);
        }
    }

    /// <summary>
    /// Runs the edit against both tables, and says whether anything came of it.
    ///
    /// The mask names every frame "&lt;tileset&gt;_&lt;id&gt;_team"; without saying so its frames do
    /// not resolve at all, and a swap changes the face while the team colour stays painted
    /// in the old portrait's shape.
    /// </summary>
    private bool Edit(Func<IconAtlas, IconAtlas, int> edit)
    {
        var wrote = false;

        foreach (var (path, suffix) in new[]
                 {
                     (IconAtlas.FacePath, ""),
                     (IconAtlas.MaskPath, IconAtlas.TeamMaskSuffix),
                 })
        {
            if (!File.Exists(_session.Game!.ResolveDataPath(path))) continue;
            if (!_project!.HasOverride(path))
                _project.SeedFromGame(path, _session.Game, _session.Vault);

            var mine = _project.ResolveContentPath(path);
            var atlas = IconAtlas.Load(mine, suffix);
            if (edit(atlas, IconAtlas.Load(_session.RequireStock(path), suffix)) == 0) continue;

            atlas.Save(mine);
            wrote = true;
        }

        return wrote;
    }

    /// <summary>
    /// Re-cuts the icons from the mod's own sheet.
    ///
    /// Re-cutting 196 icons is the same work the load does, and it is fast enough not to be
    /// worth a second trip off the thread.
    /// </summary>
    private void Reload()
    {
        if (_project is null) return;

        _face = IconAtlas.Load(_project.ResolveContentPath(IconAtlas.FacePath));
        if (_sheet is null) return;

        var (rows, stockRows) = CutRows(_face, _stockFace, _sheet, _stockSheet, _labels);

        _rows.Clear();
        _rows.AddRange(rows);
        _stockRows.Clear();
        _stockRows.AddRange(stockRows);
        ShowRows();
    }

    /// <summary>
    /// Which icon's art a frame currently draws: itself, unless it was swapped, in which
    /// case the icon whose own rectangle matches what this one now points at.
    /// </summary>
    private int DrawnBy(int id)
    {
        if (_face is null || _stockFace is null) return id;

        var now = _face.Rect(IconAtlas.Tilesets[0], id);
        if (now is null || _stockFace.Rect(IconAtlas.Tilesets[0], id) == now) return id;

        foreach (var row in _rows)
            if (_stockFace.Rect(IconAtlas.Tilesets[0], row.Id) == now) return row.Id;

        return id;
    }

    /// <summary>Puts the grid on the icon a unit draws, when that is known.</summary>
    public bool ShowUnit(int unit)
    {
        if (IconNames.ForUnit(unit) is not { } icon) return false;


        var row = _rows.FirstOrDefault(r => r.Id == icon);
        if (row is null) return false;

        IconList.SelectedItem = row;
        IconList.ScrollIntoView(row);
        return true;
    }

    // ---- the tile's own menu ---------------------------------------------

    /// <summary>
    /// Double-clicking a tile opens the list of art to take, which is the only thing anyone
    /// comes to this grid to do. Without it the dropdown below is easy to miss entirely.
    /// </summary>
    private void OnIconDoubleClick(object sender, RoutedEventArgs e)
    {
        if (SourceBox.IsEnabled) SourceBox.IsDropDownOpen = true;
    }

    private void OnIconMenuOpening(object sender, RoutedEventArgs e)
    {
        IconSwapItem.IsEnabled = SourceBox.IsEnabled;
        IconResetItem.IsEnabled = ResetButton.IsEnabled;
    }

    /// <summary>
    /// Selects what was right-clicked. Without this the menu acts on whatever was selected
    /// before, which is how you reset the wrong one.
    /// </summary>
    private void OnIconRightClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>() is { } item
            && !item.IsSelected)
            item.IsSelected = true;
    }

    // ---- your own picture -------------------------------------------------

    /// <summary>
    /// Draws an image of the person's choosing into this icon's frames.
    ///
    /// What the game actually reads is the sheet — 3971x3287, 8-bit RGBA — at the fixed
    /// rectangles the JSON beside it describes. So the size that matters is the frame's, not
    /// the file's, and any image is scaled into it rather than refused: the alternative is
    /// asking someone to hit 207x171 by hand in another program, which is the only way this
    /// goes wrong.
    ///
    /// The sheet is written back at exactly the dimensions it arrived with. Nothing else
    /// would load.
    /// </summary>
    private async void OnImportIcon(object sender, RoutedEventArgs e)
    {
        if (_project is null || _session.Game is null) return;
        if (IconList.SelectedItem is not IconRow target)
        {
            Status?.Invoke("Pick an icon first.");
            return;
        }

        if (_face?.Rect(IconAtlas.Tilesets[0], target.Id) is not { } shape)
        {
            Ui.Error(Owner, "Nothing to replace", "This icon has no frame in the sheet.");
            return;
        }

        var what = $"icon {target.Id} ({target.Name})";
        if (PictureChooser.Ask(Owner, what, shape.Width, shape.Height) is not { } chosen) return;

        try
        {

            // The picture, its mask and both frame tables go in one entry. Undoing the
            // picture without the mask would paint team colour in the old outline.
            var composed = await Working.While(Owner, $"Importing icon {target.Id}…",
                () =>
                {
                    var picture = AtlasImage.Load(chosen);
                    return ComposeIcon(target.Id, picture);
                },
                ex => Ui.Failed(Owner, "replace that picture", ex));

            if (composed is null) return;

            var cleared = composed.Value.Mask is not null;

            _session.FileUndo.Record(_project, $"replace icon {target.Id}",
                new[]
                {
                    IconAtlas.FaceImagePath, IconAtlas.FacePath,
                    IconAtlas.MaskImagePath, IconAtlas.MaskPath,
                },
                () =>
                {
                    WriteIcon(composed.Value);

                },
                refresh: () => { Build(); OverridesChanged?.Invoke(); });

            Status?.Invoke($"Icon {target.Id} ({target.Name}) now uses "
                           + $"{Path.GetFileName(chosen)}, scaled to {shape.Width} x {shape.Height}."
                           + (cleared ? " Its team-colour mask was cleared." : ""));

            OverridesChanged?.Invoke();
            Build();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "replace that picture", ex);
        }
    }

    /// <summary>
    /// Writes the picture into every tileset's copy of the frame, on the mod's own sheet.
    ///
    /// All four tilesets are done together for the same reason a swap does: the game picks
    /// the sheet by the map's terrain, and a portrait replaced only in Forest would revert to
    /// the game's on a Winter map.
    /// </summary>
    private byte[] ComposeInto(string atlasPath, string imagePath, string suffix, int id,
                               System.Windows.Media.Imaging.BitmapSource picture)
    {
        var atlas = IconAtlas.Load(_session.RequireFile(_project, atlasPath), suffix);

        var frames = FramesOf(atlas, id);
        if (frames.Count == 0) throw new InvalidOperationException("That icon has no frames in this sheet.");

        var sheet = AtlasImage.Load(_session.RequireFile(_project, imagePath));
        return AtlasImage.ReplaceFrames(sheet, picture, frames);
    }

    /// <summary>
    /// Puts a composed sheet into the mod.
    ///
    /// Split from the drawing above so the drawing can happen off the thread that paints the
    /// window while this cannot: recording an undo entry raises the stack's Changed event,
    /// and that reaches bindings no other thread may touch.
    /// </summary>
    private void WriteInto(string atlasPath, string imagePath, byte[] sheet)
    {
        _project!.WriteOverride(imagePath, sheet);

        // The JSON is not changed — the frames stay exactly where they were — but the mod
        // needs its own copy beside the image it now owns.
        if (!_project.HasOverride(atlasPath))
            _project.SeedFromGame(atlasPath, _session.Game!, _session.Vault);
    }

    /// <summary>Every tileset's rectangle for one icon, as the image code wants them.</summary>
    private static List<System.Windows.Int32Rect> FramesOf(IconAtlas atlas, int id) =>
        IconAtlas.Tilesets
            .Select(tileset => atlas.Rect(tileset, id))
            .Where(rect => rect is not null)
            .Select(rect => new System.Windows.Int32Rect(
                rect!.Value.X, rect.Value.Y, rect.Value.Width, rect.Value.Height))
            .ToList();

    /// <summary>
    /// Replaces the team-colour mask itself, for anyone who wants their portrait tinted.
    ///
    /// The mask is greyscale: black is untouched, lighter is more of the player's colour. An
    /// imported picture is converted, so a black-and-white image works the way it looks.
    /// </summary>
    /// <summary>An icon's finished sheets, ready to be written.</summary>
    private readonly record struct ComposedIcon(byte[] Face, byte[]? Mask);

    /// <summary>
    /// Draws a picture into the portrait sheet and clears its team-colour frame, without
    /// writing anything.
    ///
    /// The sheets are 19.8 MB each. Decoding both, drawing into them and encoding them again
    /// is the work that used to happen with the window frozen and nothing to say why.
    ///
    /// The mask marks which pixels take the player's colour, in the shape of the portrait
    /// that used to be there. Left alone it would paint colour over the new picture in the
    /// old one's outline, so it is cleared to black, which is "no team colour" and already
    /// about 91% of a stock mask frame.
    /// </summary>
    private ComposedIcon? ComposeIcon(int id, BitmapSource picture)
    {
        if (_project is null || _session.Game is null) return null;

        var atlas = IconAtlas.Load(_session.RequireFile(_project, IconAtlas.FacePath), "");
        var frames = FramesOf(atlas, id);
        if (frames.Count == 0) throw new InvalidOperationException("That icon has no frames in this sheet.");

        var sheet = AtlasImage.Load(_session.RequireFile(_project, IconAtlas.FaceImagePath));
        var face = AtlasImage.ReplaceFrames(sheet, picture, frames);

        byte[]? mask = null;

        var maskAtlas = IconAtlas.Load(_session.RequireFile(_project, IconAtlas.MaskPath), IconAtlas.TeamMaskSuffix);
        var maskFrames = FramesOf(maskAtlas, id);

        if (maskFrames.Count > 0)
            mask = AtlasImage.FillFrames(AtlasImage.Load(_session.RequireFile(_project, IconAtlas.MaskImagePath)), 0, maskFrames);

        return new ComposedIcon(face, mask);
    }

    /// <summary>
    /// Writes composed sheets into the mod, with the frame tables they need beside them.
    ///
    /// The tables are not changed, since the frames stay where they are, but the mod needs
    /// its own copy of each beside the sheet it now owns.
    /// </summary>
    private void WriteIcon(ComposedIcon composed)
    {
        if (_project is null || _session.Game is null) return;

        _project.WriteOverride(IconAtlas.FaceImagePath, composed.Face);

        if (!_project.HasOverride(IconAtlas.FacePath))
            _project.SeedFromGame(IconAtlas.FacePath, _session.Game, _session.Vault);

        if (composed.Mask is null) return;

        _project.WriteOverride(IconAtlas.MaskImagePath, composed.Mask);

        if (!_project.HasOverride(IconAtlas.MaskPath))
            _project.SeedFromGame(IconAtlas.MaskPath, _session.Game, _session.Vault);
    }

    private async void OnImportMask(object sender, RoutedEventArgs e)
    {
        if (_project is null || _session.Game is null) return;
        if (IconList.SelectedItem is not IconRow target)
        {
            Status?.Invoke("Pick an icon first.");
            return;
        }

        // The mask frame is the same size as the portrait it marks, so the shape question
        // has the same answer. It was not being asked here at all.
        if (_face?.Rect(IconAtlas.Tilesets[0], target.Id) is not { } shape)
        {
            Ui.Error(Owner, "Nothing to replace", "This icon has no frame in the sheet.");
            return;
        }

        var what = $"icon {target.Id} ({target.Name})'s team-colour mask";
        if (PictureChooser.Ask(Owner, what, shape.Width, shape.Height) is not { } chosen) return;

        try
        {
            // The mask sheet is 19.8 MB, the same as the portrait sheet, and this ran on the
            // thread that draws the window.
            var sheet = await Working.While(Owner, $"Importing {Path.GetFileName(chosen)}…",
                () => ComposeInto(IconAtlas.MaskPath, IconAtlas.MaskImagePath,
                                  IconAtlas.TeamMaskSuffix, target.Id, AtlasImage.Load(chosen)),
                ex => Ui.Failed(Owner, "replace that mask", ex));

            if (sheet is null) return;

            _session.FileUndo.Record(_project, $"replace icon {target.Id}'s team-colour mask",
                new[] { IconAtlas.MaskImagePath, IconAtlas.MaskPath },
                () => WriteInto(IconAtlas.MaskPath, IconAtlas.MaskImagePath, sheet),
                refresh: () => { Build(); OverridesChanged?.Invoke(); });

            Status?.Invoke($"Icon {target.Id} ({target.Name}) has a new team-colour mask. "
                           + "Black is untinted; lighter greys take more of the player's colour.");

            OverridesChanged?.Invoke();
            Build();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "replace that mask", ex);
        }
    }
}
