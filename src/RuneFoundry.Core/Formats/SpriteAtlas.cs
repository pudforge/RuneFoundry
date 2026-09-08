using System.Text.Json.Nodes;

namespace RuneFoundry.Core.Formats;

/// <summary>
/// One frame of a TexturePacker sheet, with everything needed to put it back.
///
/// The rectangle in the sheet is only half of it. Frames are trimmed — the transparent
/// border is packed away — so drawing one correctly also needs where it sat on its own
/// canvas (<paramref name="OffsetX"/>, <paramref name="OffsetY"/>) and how big that canvas
/// was (<paramref name="CanvasWidth"/>, <paramref name="CanvasHeight"/>). Lose those and
/// every frame of a walk cycle lands in a slightly different place.
/// </summary>
public sealed record AtlasFrame(
    string Name,
    IconRect Rect,
    int OffsetX,
    int OffsetY,
    int CanvasWidth,
    int CanvasHeight)
{
    /// <summary>Whether the sheet holds less than the whole canvas.</summary>
    public bool Trimmed => Rect.Width != CanvasWidth || Rect.Height != CanvasHeight
                           || OffsetX != 0 || OffsetY != 0;
}

/// <summary>
/// A unit sheet's frame table, read and written.
///
/// <see cref="FrameAtlas"/> reads a sheet to find a rectangle to draw into and never changes
/// the description. That is right for replacing one picture inside a fixed layout, and wrong
/// for sprites: an edited unit's frames are a different shape from the ones they replace, so
/// the layout has to be rewritten with them.
///
/// Which turns out to be allowed. The engine takes the texture path from
/// <c>Art/hd/sprites.json</c>, not from the atlas — every atlas's own <c>meta.image</c> is a
/// dead absolute path from the developer's machine — and it reads each frame's rectangle,
/// trim offset and canvas from this table. It also already ships untrimmed frames
/// (<c>battlshp_0_water</c>), so the trim fields are honoured rather than assumed. Nothing
/// about the packing is baked into the executable, so a mod may lay a sheet out as it likes.
/// </summary>
public sealed class SpriteAtlas
{
    private readonly JsonObject _document;
    private readonly JsonObject _frames;

    private SpriteAtlas(JsonObject document, JsonObject frames)
    {
        _document = document;
        _frames = frames;
    }

    public static SpriteAtlas Load(string path) => Parse(File.ReadAllText(path));

    public static SpriteAtlas Parse(string json)
    {
        var document = JsonNode.Parse(json) as JsonObject
                       ?? throw new InvalidDataException("Not an atlas: the file is not a JSON object.");

        if (document["frames"] is not JsonObject frames)
            throw new InvalidDataException("Not an atlas: it has no \"frames\".");

        return new SpriteAtlas(document, frames);
    }

    public int Width => _document["meta"]?["size"]?["w"]?.GetValue<int>() ?? 0;
    public int Height => _document["meta"]?["size"]?["h"]?.GetValue<int>() ?? 0;

    /// <summary>Every frame in the sheet, in the order the file lists them.</summary>
    public IEnumerable<AtlasFrame> Frames =>
        _frames.Select(pair => Read(pair.Key)).OfType<AtlasFrame>();

    public AtlasFrame? Frame(string name) => Read(name);

    private AtlasFrame? Read(string name)
    {
        if (_frames[name] is not JsonObject entry) return null;
        if (entry["frame"] is not JsonObject box) return null;

        var source = entry["spriteSourceSize"] as JsonObject;
        var canvas = entry["sourceSize"] as JsonObject;

        var w = box["w"]!.GetValue<int>();
        var h = box["h"]!.GetValue<int>();

        return new AtlasFrame(
            name,
            new IconRect(box["x"]!.GetValue<int>(), box["y"]!.GetValue<int>(), w, h),
            source?["x"]?.GetValue<int>() ?? 0,
            source?["y"]?.GetValue<int>() ?? 0,
            canvas?["w"]?.GetValue<int>() ?? w,
            canvas?["h"]?.GetValue<int>() ?? h);
    }

    /// <summary>
    /// A unit's frames in play order: <c>peon_0</c>, <c>peon_1</c>, … stopping at the first
    /// gap.
    ///
    /// The prefix is the one <c>Art/hd/sprites.json</c> gives, trailing underscore and all —
    /// <c>peon_</c>, not <c>peon</c>. That underscore is what keeps the Peasant's frames apart
    /// from <c>peong_</c> and <c>peonl_</c>, so it is the file's to supply and not ours to add.
    ///
    /// Counted rather than name-matched, because the order is the animation.
    /// </summary>
    public IReadOnlyList<AtlasFrame> Sequence(string prefix, string suffix = "")
    {
        var frames = new List<AtlasFrame>();

        for (var i = 0; ; i++)
        {
            var frame = Read($"{prefix}{i}{suffix}");
            if (frame is null) break;

            frames.Add(frame);
            frames.AddRange(Variants($"{prefix}{i}", suffix));
        }

        return frames;
    }

    /// <summary>
    /// The extra pictures a numbered frame carries: <c>rock_0_f0</c> to <c>rock_0_f4</c>
    /// beside <c>rock_0</c>.
    ///
    /// Counting alone stops at the first gap, so it found <c>rock_0</c>, looked for
    /// <c>rock_1</c>, and left the five drawings of the runestone that follow it out of the
    /// editor entirely. Ninety frames across the game's atlases are named this way, and
    /// another four hundred carry a second suffix on top.
    ///
    /// Sorted by name so the order is the same every time. It has to be: the order is what
    /// the export grid and the import read back are laid out by.
    /// </summary>
    private IEnumerable<AtlasFrame> Variants(string stem, string suffix)
    {
        var start = stem + "_";

        return _frames
            .Select(pair => pair.Key)
            .Where(name => name.StartsWith(start, StringComparison.Ordinal)
                           && name.EndsWith(suffix, StringComparison.Ordinal)
                           && name != stem + suffix)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(Read)
            .OfType<AtlasFrame>();
    }

    /// <summary>Writes a frame's rectangle and trim back, adding the frame if it is new.</summary>
    public void Write(AtlasFrame frame)
    {
        _frames[frame.Name] = new JsonObject
        {
            ["frame"] = new JsonObject
            {
                ["x"] = frame.Rect.X,
                ["y"] = frame.Rect.Y,
                ["w"] = frame.Rect.Width,
                ["h"] = frame.Rect.Height,
            },
            // Never emitted true: no frame the game ships is rotated, so the path is
            // untested, and writing upright costs nothing but a little sheet area.
            ["rotated"] = false,
            ["trimmed"] = frame.Trimmed,
            ["spriteSourceSize"] = new JsonObject
            {
                ["x"] = frame.OffsetX,
                ["y"] = frame.OffsetY,
                ["w"] = frame.Rect.Width,
                ["h"] = frame.Rect.Height,
            },
            ["sourceSize"] = new JsonObject
            {
                ["w"] = frame.CanvasWidth,
                ["h"] = frame.CanvasHeight,
            },
        };
    }

    public void Resize(int width, int height)
    {
        if (_document["meta"] is not JsonObject meta) return;
        meta["size"] = new JsonObject { ["w"] = width, ["h"] = height };
    }

    public string ToJson() =>
        _document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}

/// <summary>
/// Somewhere to put a frame on a sheet that is already mostly full.
///
/// Repacking a whole sheet would be simpler to write and far worse to use: the game's own
/// packing is dense — 703 frames over a sheet they cover 117% of, because 87 rectangles are
/// shared between identical frames — and a fresh pack by anything less clever would grow it.
/// So everything not being edited stays exactly where it is, and only the edited unit's
/// frames are placed, into the space its old ones leave behind.
///
/// Space is tracked on a coarse grid rather than per pixel. At 8 pixels a cell a 8076x7644
/// sheet is under a million cells, which is quick to scan, and the few wasted pixels at each
/// frame's edge cost less than 3% of a typical sprite's area.
/// </summary>
public sealed class SheetSpace
{
    /// <summary>
    /// Cell size in pixels. Fine enough not to waste space, coarse enough to scan.
    ///
    /// Four rather than eight because the game's own packing leaves gaps of about two pixels,
    /// and a coarser grid rounds those away — which turns the space a unit's old frames free
    /// up into space nothing can be put back into, and grows the sheet for no reason.
    /// </summary>
    public const int Cell = 4;

    /// <summary>Kept clear around every frame so that filtering cannot bleed one into another.</summary>
    public const int Padding = 2;

    private readonly int _columns;
    private bool[] _taken;
    private int _rows;

    public SheetSpace(int width, int height, IEnumerable<IconRect> occupied)
    {
        Width = width;
        _columns = (width + Cell - 1) / Cell;
        _rows = (height + Cell - 1) / Cell;
        _taken = new bool[_columns * _rows];

        foreach (var rect in occupied) Take(rect);
    }

    public int Width { get; }
    public int Height => _rows * Cell;

    /// <summary>
    /// Marks a rectangle as spoken for, exactly as it stands.
    ///
    /// No padding is added here. Separation is the new frame's business — <see cref="Place"/>
    /// asks for it — and charging it at both ends would keep frames twice as far apart as
    /// they need to be.
    /// </summary>
    private void Take(IconRect rect)
    {
        var (c0, r0, c1, r1) = Span(rect);

        for (var r = r0; r < r1; r++)
            for (var c = c0; c < c1; c++)
                _taken[r * _columns + c] = true;
    }

    private (int, int, int, int) Span(IconRect rect)
    {
        var c0 = Math.Max(0, rect.X / Cell);
        var r0 = Math.Max(0, rect.Y / Cell);
        var c1 = Math.Min(_columns, (rect.X + rect.Width + Cell - 1) / Cell);
        var r1 = Math.Min(_rows, (rect.Y + rect.Height + Cell - 1) / Cell);
        return (c0, r0, c1, r1);
    }

    /// <summary>
    /// Room for a frame of this size, taken as it is handed out. The sheet grows downwards
    /// rather than failing, since a sprite the artist has drawn has to go somewhere.
    /// </summary>
    public IconRect Place(int width, int height)
    {
        var needColumns = (width + 2 * Padding + Cell - 1) / Cell;
        var needRows = (height + 2 * Padding + Cell - 1) / Cell;

        if (needColumns > _columns)
            throw new InvalidOperationException(
                $"A frame {width} wide does not fit a sheet {Width} wide.");

        while (true)
        {
            if (Find(needColumns, needRows) is { } at)
            {
                var rect = new IconRect(at.C * Cell + Padding, at.R * Cell + Padding, width, height);
                Take(rect);
                return rect;
            }

            Grow(needRows);
        }
    }

    private (int C, int R)? Find(int needColumns, int needRows)
    {
        // How far down each column stays clear, rebuilt row by row: the standard way to find
        // a clear block without rescanning it for every candidate position.
        var clear = new int[_columns];
        (int C, int R)? best = null;

        for (var r = _rows - 1; r >= 0; r--)
        {
            for (var c = 0; c < _columns; c++)
                clear[c] = _taken[r * _columns + c] ? 0 : clear[c] + 1;

            var run = 0;
            for (var c = 0; c < _columns; c++)
            {
                run = clear[c] >= needRows ? run + 1 : 0;

                // Kept rather than returned: rows are walked upwards, so the last match
                // found is the highest one. Filling from the top is what keeps the sheet
                // from growing when there are holes further up to use.
                if (run >= needColumns) best = (c - needColumns + 1, r);
            }
        }

        return best;
    }

    private void Grow(int extraRows)
    {
        var rows = _rows + Math.Max(extraRows, 16);
        var taken = new bool[_columns * rows];
        _taken.CopyTo(taken, 0);

        _taken = taken;
        _rows = rows;
    }
}
