using RuneFoundry.UI;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using RuneFoundry.Core;
using RuneFoundry.Core.Formats;

namespace RuneFoundry.Editor;

/// <summary>One frame of the icon sheet: its number, what it is, and its picture.</summary>
public sealed class IconPick
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required BitmapSource? Picture { get; init; }

    public bool Named => Name.Length > 0;

    public string Caption => Named ? $"{Id} · {Name}" : $"Icon {Id}";

    public override string ToString() => Caption;
}

/// <summary>
/// The icon sheet, cut into pictures and named.
///
/// Both the tables and the Icons tab want the same thing — a list of frames with their art
/// and their names — and cutting a 20 MB sheet twice would be silly, so it is loaded once
/// per project here.
/// </summary>
public sealed class IconSheet
{
    private IconSheet(IconAtlas atlas, IconAtlas stock,
                      IReadOnlyList<IconPick> picks, IReadOnlyList<IconPick> stockPicks,
                      IReadOnlyList<IconPick> choices)
    {
        Atlas = atlas;
        Stock = stock;
        Picks = picks;
        StockPicks = stockPicks;
        Choices = choices;
    }

    public IconAtlas Atlas { get; }

    /// <summary>The game's own copy, for telling changed frames from untouched ones.</summary>
    public IconAtlas Stock { get; }

    /// <summary>Each icon as this mod draws it — what the grid and the closed box show.</summary>
    public IReadOnlyList<IconPick> Picks { get; }

    /// <summary>
    /// Each icon as the game draws it, for telling a mod's art from the game's.
    /// </summary>
    public IReadOnlyList<IconPick> StockPicks { get; }

    /// <summary>
    /// What a picker offers: each icon at its own frame, drawn from this mod's sheet.
    ///
    /// Two different things can have happened to an icon, and this keeps them apart.
    /// Pointing icon A at icon B's frame is a change to A, not to B, so the list must go on
    /// showing B where B is — offering "whatever B currently points at" would make the
    /// choices drift as swaps pile up. Painting over B's frame, though, changes what B *is*:
    /// the mod's Ogre is the Ogre now, and a picker showing Blizzard's was offering art the
    /// game no longer has.
    ///
    /// So: the mod's sheet, cut at the game's rectangles.
    /// </summary>
    public IReadOnlyList<IconPick> Choices { get; }

    public IconPick? FindChoice(int id) => Choices.FirstOrDefault(p => p.Id == id);

    public IconPick? Find(int id) => Picks.FirstOrDefault(p => p.Id == id);

    public IconPick? FindStock(int id) => StockPicks.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Which frame's art a frame currently draws. Itself when nothing has been swapped;
    /// otherwise the frame whose original rectangle matches what this one now points at.
    /// </summary>
    public int DrawnBy(int id)
    {
        var now = Atlas.Rect(IconAtlas.Tilesets[0], id);
        if (now is null) return id;
        if (Stock.Rect(IconAtlas.Tilesets[0], id) == now) return id;

        foreach (var pick in Picks)
            if (Stock.Rect(IconAtlas.Tilesets[0], pick.Id) == now) return pick.Id;

        return id;
    }

    public static IconSheet? Load(Session session, ModProject? project)
    {
        if (session.Game is null) return null;

        string Effective(string relativePath) =>
            project is not null && project.HasOverride(relativePath)
                ? project.ResolveContentPath(relativePath)
                : session.StockFile(relativePath) ?? session.Game.ResolveDataPath(relativePath);

        string StockPath(string relativePath) =>
            session.Vault.OriginalFile(relativePath) ?? session.Game.ResolveDataPath(relativePath);

        try
        {
            var atlas = IconAtlas.Load(Effective(IconAtlas.FacePath));
            var stock = IconAtlas.Load(StockPath(IconAtlas.FacePath));
            var labels = Labels(session, project, Effective);

            var imagePath = Effective(IconAtlas.FaceImagePath);
            var stockImagePath = StockPath(IconAtlas.FaceImagePath);

            var sheet = LoadImage(imagePath);

            // A mod can replace the picture as well as the frame list, so the game's own
            // copy is cut separately — unless it is the same file, which it usually is.
            var stockSheet = string.Equals(imagePath, stockImagePath, StringComparison.OrdinalIgnoreCase)
                ? sheet
                : LoadImage(stockImagePath);

            var picks = new List<IconPick>();
            var stockPicks = new List<IconPick>();
            var choices = new List<IconPick>();

            foreach (var id in atlas.Ids)
            {
                labels.TryGetValue(id, out var name);
                picks.Add(new IconPick { Id = id, Name = name ?? "", Picture = Cut(sheet, atlas, id) });
                stockPicks.Add(new IconPick { Id = id, Name = name ?? "", Picture = Cut(stockSheet, stock, id) });

                // The mod's sheet at the game's rectangle: this icon, as this mod draws it.
                choices.Add(new IconPick { Id = id, Name = name ?? "", Picture = Cut(sheet, stock, id) });
            }

            return new IconSheet(atlas, stock, picks, stockPicks, choices);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? Cut(BitmapSource sheet, IconAtlas atlas, int id)
    {
        if (atlas.Rect(IconAtlas.Tilesets[0], id) is not { } rect) return null;
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        if (rect.X + rect.Width > sheet.PixelWidth || rect.Y + rect.Height > sheet.PixelHeight) return null;

        var cut = new CroppedBitmap(sheet, new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height));
        cut.Freeze();
        return cut;
    }

    private static BitmapSource LoadImage(string path)
    {
        // Through a stream, so no handle is left on a file the mod may replace.
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// What each icon is, in this mod's own words: the unit side from the table the game
    /// indexes, the upgrade side from the icon field in upgrades.dat.
    /// </summary>
    private static Dictionary<int, string> Labels(
        Session session, ModProject? project, Func<string, string> effective)
    {
        GameStrings? strings = null;
        UpgradeDataFile? upgrades = null;

        try { strings = GameStrings.Load(effective("Strings/enUS.json")); } catch { }
        try { upgrades = UpgradeDataFile.Load(effective("Rez/upgrades.dat")); } catch { }

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
}
