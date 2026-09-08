namespace RuneFoundry.Core.Formats;

/// <summary>
/// A 256-entry RGB palette. The game ships these as .ppl files — exactly 768 bytes,
/// three bytes per entry — one per tileset (forest, iceland, swamp, xswamp).
/// GRP frames are palette indices, so nothing sprite-shaped can be drawn without one.
/// </summary>
public sealed class Palette
{
    public const int EntryCount = 256;
    public const int FileSize = EntryCount * 3;

    private readonly byte[] _rgb;

    /// <summary>
    /// Index 0 is the transparency key in Warcraft II sprites: it is drawn as a hole,
    /// not as whatever colour happens to sit in slot 0.
    /// </summary>
    public const byte TransparentIndex = 0;

    private Palette(byte[] rgb) => _rgb = rgb;

    public static Palette FromBytes(byte[] data)
    {
        if (data.Length < FileSize)
            throw new InvalidDataException($"Palette needs {FileSize} bytes, got {data.Length}.");

        var rgb = data[..FileSize];

        // The game's .ppl files hold VGA values, which run 0-63, not 0-255. Copied
        // straight into a bitmap every sprite came out at a quarter brightness — dark
        // enough to look like a broken decode rather than a scaling mistake. A palette
        // whose brightest channel is 63 is one of those; anything above that is already
        // 8-bit (a PCX carries its own palette that way) and is left alone.
        if (rgb.Max() <= 63)
            for (var i = 0; i < rgb.Length; i++)
                rgb[i] = (byte)(rgb[i] * 255 / 63);

        return new Palette(rgb);
    }

    public static Palette Load(string path) => FromBytes(File.ReadAllBytes(path));

    public (byte R, byte G, byte B) this[int index]
    {
        get
        {
            var i = (index & 0xFF) * 3;
            return (_rgb[i], _rgb[i + 1], _rgb[i + 2]);
        }
    }

    /// <summary>
    /// A neutral greyscale ramp, used when no tileset palette is to hand. Sprites come
    /// out legible in shape if not in colour, which beats refusing to preview at all.
    /// </summary>
    public static Palette Greyscale()
    {
        var rgb = new byte[FileSize];
        for (var i = 0; i < EntryCount; i++)
        {
            rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = (byte)i;
        }
        return new Palette(rgb);
    }

    /// <summary>
    /// Finds the palette that goes with a game asset. Tileset art lives beside its .ppl
    /// (Art\bgs\Forest\forest.ppl and friends); anything else falls back to the forest
    /// palette, which is what most unit art was authored against.
    /// </summary>
    public static Palette ForGameAsset(GameInstall game, string relativePath)
    {
        var directory = Path.GetDirectoryName(game.ResolveDataPath(relativePath));
        if (directory is not null && Directory.Exists(directory))
        {
            var sibling = Directory.EnumerateFiles(directory, "*.ppl").FirstOrDefault();
            if (sibling is not null && TryLoad(sibling, out var local)) return local!;
        }

        var forest = Path.Combine(game.DataRoot, "Art", "bgs", "Forest", "forest.ppl");
        if (TryLoad(forest, out var fallback)) return fallback!;

        return Greyscale();
    }

    public static bool TryLoad(string path, out Palette? palette)
    {
        palette = null;
        try
        {
            if (!File.Exists(path)) return false;
            palette = Load(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
