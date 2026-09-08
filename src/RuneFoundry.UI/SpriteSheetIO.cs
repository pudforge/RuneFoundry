using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RuneFoundry.Core.Formats;

namespace RuneFoundry.UI;

/// <summary>What an export wrote, so the screen can say it plainly.</summary>
/// <param name="Folder">Where the files went.</param>
/// <param name="Sheet">The grid of frames.</param>
/// <param name="Mask">The team-colour grid, when the unit has one.</param>
/// <param name="Columns">Frames across: one animation.</param>
/// <param name="Rows">Frames down: one per facing.</param>
public sealed record SpriteExport(string Folder, string Sheet, string? Mask, int Columns, int Rows, int Frames);

/// <summary>What an import changed, so the screen can say what it cost.</summary>
public sealed record SpriteImport(int Frames, int MaskFrames, int Width, int Height, bool Grew);

/// <summary>
/// A unit's frames as a grid you can open in any paint program, and the way back.
///
/// The game's own sheets are packed for the machine: frames trimmed to their contents, sorted
/// by whatever fitted, one unit's walk cycle scattered across a 8076x7644 image among
/// seventeen others. Perfectly good to draw from and hopeless to draw on.
///
/// Exporting lays one unit out as it reads: a row per facing, a column per animation frame,
/// every frame drawn untrimmed at its true position on its own canvas so that what lines up
/// in the file lines up in the game. Importing is the reverse — each cell is trimmed back to
/// its contents, and the frames are packed into the space the old ones leave behind.
///
/// The one thing the artist must keep is the grid: same cell size, same number of cells.
/// Everything inside a cell is theirs.
/// </summary>
public static class SpriteSheetIO
{
    public const string SidecarName = "frames.json";

    /// <summary>The game draws five facings and mirrors them for the other three.</summary>
    public const int Facings = 5;

    /// <summary>Frames whose names end this way carry player colour rather than picture.</summary>
    public const string MaskSuffix = "_team";

    /// <summary>
    /// A prefix as a file name: <c>peon_</c> becomes <c>peon</c>.
    ///
    /// The index spells prefixes with the joining underscore because that is what the frame
    /// names need. A file called <c>peon_.png</c> would just look like a mistake.
    /// </summary>
    public static string Stem(string prefix) => prefix.TrimEnd('_');

    // ---- reading and writing pixels ------------------------------------------------

    /// <summary>A picture as plain bytes, in the only two shapes these sheets come in.</summary>
    private sealed record Surface(byte[] Pixels, int Width, int Height, int BytesPerPixel)
    {
        public int Stride => Width * BytesPerPixel;

        public static Surface Of(BitmapSource image)
        {
            // Masks ship as 8-bit greyscale and sprites as colour with alpha. Converting a
            // mask to colour would quadruple it for nothing and lose its format on the way
            // back out, so each is kept as it arrived.
            var grey = image.Format == PixelFormats.Gray8;
            var source = grey ? image : Convert(image);

            var bpp = grey ? 1 : 4;
            var stride = image.PixelWidth * bpp;
            var pixels = new byte[stride * image.PixelHeight];
            source.CopyPixels(pixels, stride, 0);

            return new Surface(pixels, image.PixelWidth, image.PixelHeight, bpp);
        }

        private static BitmapSource Convert(BitmapSource image) =>
            image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

        public Surface Blank(int width, int height) =>
            new(new byte[width * height * BytesPerPixel], width, height, BytesPerPixel);

        /// <summary>Copies a rectangle from another surface. Anything off the edge is skipped.</summary>
        public void Draw(Surface from, int sx, int sy, int w, int h, int dx, int dy)
        {
            for (var row = 0; row < h; row++)
            {
                var y = sy + row;
                var ty = dy + row;
                if (y < 0 || y >= from.Height || ty < 0 || ty >= Height) continue;

                var take = Math.Min(w, Math.Min(from.Width - sx, Width - dx));
                if (take <= 0 || sx < 0 || dx < 0) continue;

                Buffer.BlockCopy(from.Pixels, y * from.Stride + sx * BytesPerPixel,
                                 Pixels, ty * Stride + dx * BytesPerPixel,
                                 take * BytesPerPixel);
            }
        }

        public BitmapSource ToBitmap()
        {
            var format = BytesPerPixel == 1 ? PixelFormats.Gray8 : PixelFormats.Bgra32;
            var bitmap = BitmapSource.Create(Width, Height, 96, 96, format, null, Pixels, Stride);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// The smallest rectangle holding everything visible, or an empty one for a blank cell.
        ///
        /// "Visible" is alpha for a sprite and any value at all for a mask, which has no alpha
        /// channel — a mask pixel of zero is simply no team colour there.
        /// </summary>
        public IconRect Bounds(int ox, int oy, int w, int h)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;

            for (var y = 0; y < h; y++)
            {
                var row = (oy + y) * Stride;
                for (var x = 0; x < w; x++)
                {
                    var at = row + (ox + x) * BytesPerPixel;
                    var lit = BytesPerPixel == 1 ? Pixels[at] != 0 : Pixels[at + 3] != 0;
                    if (!lit) continue;

                    if (x < x0) x0 = x;
                    if (y < y0) y0 = y;
                    if (x > x1) x1 = x;
                    if (y > y1) y1 = y;
                }
            }

            return x1 < x0 ? new IconRect(0, 0, 0, 0) : new IconRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        }
    }

    private static void Save(Surface surface, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(surface.ToBitmap()));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ---- shape of the grid ---------------------------------------------------------

    /// <summary>
    /// How to lay the frames out: a row per facing where the count allows it.
    ///
    /// Every animated unit the game ships divides by five — the knight's 70 frames are five
    /// facings of fourteen, the grunt's 60 are five of twelve — and consecutive frames belong
    /// to the same facing. Anything that does not divide goes in one row, which is right for
    /// the one-frame and two-frame odds and ends.
    /// </summary>
    public static (int Columns, int Rows) Grid(int frames) =>
        frames > Facings && frames % Facings == 0 ? (frames / Facings, Facings) : (frames, 1);

    // ---- export --------------------------------------------------------------------

    /// <summary>
    /// Writes a unit's frames as a grid, plus its team-colour grid and a note of the shape.
    /// </summary>
    public static SpriteExport Export(
        string folder,
        string spritePath,
        SpriteSource source,
        string sheetFile, string atlasFile,
        string? maskSheetFile, string? maskAtlasFile)
    {
        Directory.CreateDirectory(folder);

        var atlas = SpriteAtlas.Load(atlasFile);
        var frames = atlas.Sequence(source.Prefix);

        if (frames.Count == 0)
            throw new InvalidDataException($"{atlasFile} has no frames named {source.Prefix}0 and up.");

        var canvasW = frames[0].CanvasWidth;
        var canvasH = frames[0].CanvasHeight;

        if (frames.Any(f => f.CanvasWidth != canvasW || f.CanvasHeight != canvasH))
            throw new InvalidDataException(
                "Every frame needs the same canvas for a grid export. "
                + $"{source.Prefix} uses a different one per frame.");

        var (columns, rows) = Grid(frames.Count);

        var sheetPath = Path.Combine(folder, Stem(source.Prefix) + ".png");
        WriteGrid(sheetFile, frames, canvasW, canvasH, columns, sheetPath);

        string? maskPath = null;
        IReadOnlyList<AtlasFrame> maskFrames = Array.Empty<AtlasFrame>();

        if (maskAtlasFile is not null && maskSheetFile is not null && File.Exists(maskAtlasFile))
        {
            maskFrames = SpriteAtlas.Load(maskAtlasFile).Sequence(source.Prefix, MaskSuffix);

            if (maskFrames.Count > 0)
            {
                maskPath = Path.Combine(folder, Stem(source.Prefix) + MaskSuffix + ".png");
                var (maskColumns, _) = Grid(maskFrames.Count);
                WriteGrid(maskSheetFile, maskFrames, canvasW, canvasH, maskColumns, maskPath);
            }
        }

        var sidecar = new JsonObject
        {
            ["sprite"] = spritePath,
            ["atlas"] = source.Atlas,
            ["prefix"] = source.Prefix,
            ["canvas"] = new JsonObject { ["w"] = canvasW, ["h"] = canvasH },
            ["columns"] = columns,
            ["rows"] = rows,
            ["frames"] = frames.Count,
            ["sheet"] = Path.GetFileName(sheetPath),
            ["mask"] = maskPath is null ? null : Path.GetFileName(maskPath),
            ["maskFrames"] = maskFrames.Count,
            ["note"] = "Keep the grid: same cell size, same number of cells, one row per facing. "
                       + "Everything inside a cell is yours. Straight (non-premultiplied) alpha.",
        };

        File.WriteAllText(Path.Combine(folder, SidecarName),
            sidecar.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return new SpriteExport(folder, sheetPath, maskPath, columns, rows, frames.Count);
    }

    /// <summary>Draws every frame untrimmed into its cell, so the grid reads as the animation.</summary>
    private static void WriteGrid(
        string sheetFile, IReadOnlyList<AtlasFrame> frames,
        int canvasW, int canvasH, int columns, string outputPath)
    {
        var sheet = Surface.Of(AtlasImage.Load(sheetFile));
        var rows = (frames.Count + columns - 1) / columns;
        var grid = sheet.Blank(columns * canvasW, rows * canvasH);

        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            var cellX = i % columns * canvasW;
            var cellY = i / columns * canvasH;

            grid.Draw(sheet, f.Rect.X, f.Rect.Y, f.Rect.Width, f.Rect.Height,
                      cellX + f.OffsetX, cellY + f.OffsetY);
        }

        Save(grid, outputPath);
    }

    // ---- import --------------------------------------------------------------------

    /// <summary>
    /// Reads an edited grid back into the sheet, trimming each cell and packing the results
    /// into the space the unit's old frames leave behind.
    ///
    /// Returns the two files to write into the mod: the sheet and its frame table.
    /// </summary>
    /// <summary>
    /// Where one era's art is, allowing for a folder that only holds one copy of it.
    ///
    /// Looked for in order: the era's own folder, the folder given, then any other era's
    /// folder. A sprite drawn once and meant for every season is the normal case, and
    /// asking for four identical copies of it was our idea, not the game's.
    /// </summary>
    public static string? ArtFolder(string root, string era, int eraCount)
    {
        var own = Path.Combine(root, era);
        if (File.Exists(Path.Combine(own, SidecarName))) return own;

        if (File.Exists(Path.Combine(root, SidecarName))) return root;

        if (eraCount <= 1) return null;

        foreach (var directory in Directory.Exists(root)
                     ? Directory.EnumerateDirectories(root)
                     : Array.Empty<string>())
            if (File.Exists(Path.Combine(directory, SidecarName)))
                return directory;

        return null;
    }

    public static (byte[] Sheet, string Atlas, byte[]? Mask, string? MaskAtlas, SpriteImport Result) Import(
        string folder,
        SpriteSource source,
        string sheetFile, string atlasFile,
        string? maskSheetFile, string? maskAtlasFile)
    {
        var sidecarPath = Path.Combine(folder, SidecarName);
        if (!File.Exists(sidecarPath))
            throw new FileNotFoundException(
                $"{SidecarName} is not in that folder. Import the folder an export wrote.");

        var sidecar = JsonNode.Parse(File.ReadAllText(sidecarPath)) as JsonObject
                      ?? throw new InvalidDataException($"{SidecarName} is not readable.");

        // The prefix is the key the atlas files the frames under, not a label. Renaming it
        // to match a new drawing breaks the only link back to the sprite it came from, and
        // "holds hut_, not rock_" does not say that. The sidecar records where it came
        // from, so the message can.
        var prefix = sidecar["prefix"]?.GetValue<string>();
        if (prefix != source.Prefix)
        {
            var from = sidecar["sprite"]?.GetValue<string>();

            throw new InvalidDataException(
                from is null
                    ? $"That folder holds {prefix ?? "something else"}, not {source.Prefix}. "
                      + "Import it onto the sprite it was exported from."
                    : $"That folder was exported from {from}, and this sprite files its "
                      + $"frames under {source.Prefix}. Either select {from} instead, or, if you renamed "
                      + $"\"prefix\" in {SidecarName}, put it back to \"{source.Prefix}\".");
        }

        var atlas = SpriteAtlas.Load(atlasFile);
        var (sheetBytes, placedFrames, width, height, grew) =
            Rebuild(folder, sidecar, atlas, source.Prefix, "", sheetFile);

        byte[]? maskBytes = null;
        string? maskJson = null;
        var maskCount = 0;

        if (maskAtlasFile is not null && maskSheetFile is not null
            && sidecar["mask"]?.GetValue<string>() is not null && File.Exists(maskAtlasFile))
        {
            var maskAtlas = SpriteAtlas.Load(maskAtlasFile);
            var (bytes, count, _, _, _) =
                Rebuild(folder, sidecar, maskAtlas, source.Prefix, MaskSuffix, maskSheetFile);

            maskBytes = bytes;
            maskJson = maskAtlas.ToJson();
            maskCount = count;
        }

        return (sheetBytes, atlas.ToJson(), maskBytes, maskJson,
                new SpriteImport(placedFrames, maskCount, width, height, grew));
    }

    /// <summary>Where one frame's new pixels come from, and where they sit on its canvas.</summary>
    private readonly record struct FrameArt(int X, int Y, int Width, int Height, int OffsetX, int OffsetY);

    private static (byte[] Bytes, int Frames, int Width, int Height, bool Grew) Rebuild(
        string folder, JsonObject sidecar, SpriteAtlas atlas, string prefix, string suffix, string sheetFile)
    {
        var name = suffix.Length == 0
            ? sidecar["sheet"]?.GetValue<string>() ?? Stem(prefix) + ".png"
            : sidecar["mask"]!.GetValue<string>();

        var gridPath = Path.Combine(folder, name);
        if (!File.Exists(gridPath)) throw new FileNotFoundException($"{name} is not in that folder.");

        var existing = atlas.Sequence(prefix, suffix);
        if (existing.Count == 0) throw new InvalidDataException($"The sheet has no {prefix} frames to replace.");

        var canvasW = existing[0].CanvasWidth;
        var canvasH = existing[0].CanvasHeight;
        var (columns, _) = Grid(existing.Count);
        var rows = (existing.Count + columns - 1) / columns;

        var grid = Surface.Of(AtlasImage.Load(gridPath));

        if (grid.Width != columns * canvasW || grid.Height != rows * canvasH)
            throw new InvalidDataException(
                $"{name} is {grid.Width} x {grid.Height}. It has to stay "
                + $"{columns * canvasW} x {rows * canvasH}: {columns} across and {rows} down, "
                + $"each cell {canvasW} x {canvasH}.");

        var art = new List<FrameArt>();

        for (var i = 0; i < existing.Count; i++)
        {
            var cellX = i % columns * canvasW;
            var cellY = i / columns * canvasH;
            var bounds = grid.Bounds(cellX, cellY, canvasW, canvasH);

            // A cell drawn empty is legitimate — some frames genuinely show nothing — and
            // needs a rectangle all the same, since the table has no way to say "no picture".
            if (bounds.Width == 0) bounds = new IconRect(0, 0, 1, 1);

            art.Add(new FrameArt(cellX + bounds.X, cellY + bounds.Y,
                                 bounds.Width, bounds.Height, bounds.X, bounds.Y));
        }

        return Pack(atlas, existing, art, grid, Surface.Of(AtlasImage.Load(sheetFile)), canvasW, canvasH);
    }

    /// <summary>
    /// Puts one unit's frames back the way the game draws them, leaving every other unit in
    /// the sheet as it is.
    ///
    /// Needed because a sheet holds eighteen units. Dropping the file to get one of them back
    /// would throw away the other seventeen, and the frames cannot simply return to their old
    /// rectangles either — those were freed when the unit was imported, and another unit's
    /// import may since have taken them. So the game's own art is packed in afresh.
    /// </summary>
    public static (byte[] Sheet, string Atlas, byte[]? Mask, string? MaskAtlas, SpriteImport Result) Restore(
        SpriteSource source,
        string sheetFile, string atlasFile,
        string stockSheetFile, string stockAtlasFile,
        string? maskSheetFile, string? maskAtlasFile,
        string? stockMaskSheetFile, string? stockMaskAtlasFile)
    {
        var atlas = SpriteAtlas.Load(atlasFile);
        var (bytes, count, width, height, grew) =
            RestoreOne(atlas, SpriteAtlas.Load(stockAtlasFile), source.Prefix, "", sheetFile, stockSheetFile);

        byte[]? maskBytes = null;
        string? maskJson = null;
        var maskCount = 0;

        if (maskAtlasFile is not null && maskSheetFile is not null
            && stockMaskAtlasFile is not null && stockMaskSheetFile is not null
            && File.Exists(maskAtlasFile) && File.Exists(stockMaskAtlasFile))
        {
            var maskAtlas = SpriteAtlas.Load(maskAtlasFile);
            var stockMask = SpriteAtlas.Load(stockMaskAtlasFile);

            if (stockMask.Sequence(source.Prefix, MaskSuffix).Count > 0)
            {
                var (maskSheet, frames, _, _, _) = RestoreOne(
                    maskAtlas, stockMask, source.Prefix, MaskSuffix, maskSheetFile, stockMaskSheetFile);

                maskBytes = maskSheet;
                maskJson = maskAtlas.ToJson();
                maskCount = frames;
            }
        }

        return (bytes, atlas.ToJson(), maskBytes, maskJson,
                new SpriteImport(count, maskCount, width, height, grew));
    }

    private static (byte[] Bytes, int Frames, int Width, int Height, bool Grew) RestoreOne(
        SpriteAtlas atlas, SpriteAtlas stock, string prefix, string suffix,
        string sheetFile, string stockSheetFile)
    {
        var wanted = stock.Sequence(prefix, suffix);
        if (wanted.Count == 0) throw new InvalidDataException($"The game's sheet has no {prefix} frames.");

        var art = wanted
            .Select(f => new FrameArt(f.Rect.X, f.Rect.Y, f.Rect.Width, f.Rect.Height, f.OffsetX, f.OffsetY))
            .ToList();

        return Pack(atlas, wanted, art,
                    Surface.Of(AtlasImage.Load(stockSheetFile)),
                    Surface.Of(AtlasImage.Load(sheetFile)),
                    wanted[0].CanvasWidth, wanted[0].CanvasHeight);
    }

    /// <summary>
    /// Places a unit's frames into the sheet and writes the table to match.
    ///
    /// Everything that is not this unit stays exactly where it is and keeps its space
    /// reserved, so frames shared with another unit's are never trodden on. The unit's own
    /// old pixels are left where they lie: nothing points at them any more, and clearing them
    /// would cost a pass over the whole sheet to no visible end.
    /// </summary>
    private static (byte[] Bytes, int Frames, int Width, int Height, bool Grew) Pack(
        SpriteAtlas atlas, IReadOnlyList<AtlasFrame> frames, IReadOnlyList<FrameArt> art,
        Surface from, Surface sheet, int canvasW, int canvasH)
    {
        var mine = frames.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var occupied = atlas.Frames.Where(f => !mine.Contains(f.Name)).Select(f => f.Rect).ToList();

        var space = new SheetSpace(sheet.Width, sheet.Height, occupied);

        // Identical frames share one rectangle, the way the game's own packing does — a walk
        // cycle that repeats a pose costs the sheet nothing.
        var seen = new Dictionary<string, IconRect>(StringComparer.Ordinal);
        var placed = new List<(IconRect To, int FromX, int FromY)>();

        for (var i = 0; i < frames.Count; i++)
        {
            var a = art[i];
            var key = Key(from, a.X, a.Y, a.Width, a.Height);

            if (!seen.TryGetValue(key, out var at))
            {
                at = space.Place(a.Width, a.Height);
                seen[key] = at;
                placed.Add((at, a.X, a.Y));
            }

            atlas.Write(new AtlasFrame(frames[i].Name, at, a.OffsetX, a.OffsetY, canvasW, canvasH));
        }

        var grew = space.Height > sheet.Height;
        var output = sheet.Blank(sheet.Width, space.Height);
        output.Draw(sheet, 0, 0, sheet.Width, sheet.Height, 0, 0);

        foreach (var (to, fromX, fromY) in placed)
            output.Draw(from, fromX, fromY, to.Width, to.Height, to.X, to.Y);

        atlas.Resize(output.Width, output.Height);

        return (Encode(output), frames.Count, output.Width, output.Height, grew);
    }

    /// <summary>
    /// The frames placed differently in one table than in another — which is what tells us a
    /// unit's art has been replaced, since importing always repacks.
    /// </summary>
    public static IReadOnlyList<string> ChangedFrames(SpriteAtlas mine, SpriteAtlas stock)
    {
        var was = stock.Frames.ToDictionary(f => f.Name, StringComparer.Ordinal);

        return mine.Frames
            .Where(f => !was.TryGetValue(f.Name, out var before)
                        || before.Rect != f.Rect
                        || before.OffsetX != f.OffsetX
                        || before.OffsetY != f.OffsetY)
            .Select(f => f.Name)
            .ToArray();
    }

    /// <summary>A frame's contents boiled down, so two identical ones can share a rectangle.</summary>
    private static string Key(Surface grid, int x, int y, int w, int h)
    {
        var hash = System.Security.Cryptography.SHA256.Create();
        var row = new byte[w * grid.BytesPerPixel];

        for (var r = 0; r < h; r++)
        {
            Buffer.BlockCopy(grid.Pixels, (y + r) * grid.Stride + x * grid.BytesPerPixel,
                             row, 0, row.Length);
            hash.TransformBlock(row, 0, row.Length, null, 0);
        }

        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return $"{w}x{h}:" + System.Convert.ToHexString(hash.Hash!);
    }

    private static byte[] Encode(Surface surface)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(surface.ToBitmap()));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
