namespace RuneFoundry.Core.Formats;

/// <summary>
/// An 8-bit palette-indexed bitmap plus a per-pixel transparency mask.
///
/// The mask is kept separate from the index because Warcraft II sprites have two
/// different kinds of "nothing": a run the encoder skipped, and a pixel that genuinely
/// holds palette index 0. Collapsing them loses information the packager may want.
/// </summary>
public sealed class IndexedImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major palette indices, Width * Height bytes.</summary>
    public byte[] Indices { get; }

    /// <summary>Row-major opacity flags, Width * Height entries.</summary>
    public bool[] Opaque { get; }

    public IndexedImage(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        Width = width;
        Height = height;
        Indices = new byte[width * height];
        Opaque = new bool[width * height];
    }

    public void Set(int x, int y, byte index)
    {
        var i = y * Width + x;
        Indices[i] = index;
        Opaque[i] = true;
    }

    /// <summary>
    /// Flattens to premultiply-free BGRA32, the layout WPF's Bgra32 WriteableBitmap wants.
    /// Core stays free of any UI framework by handing back plain bytes.
    /// </summary>
    public byte[] ToBgra32(Palette palette, bool treatIndexZeroAsTransparent = true)
    {
        var pixels = new byte[Width * Height * 4];
        for (var i = 0; i < Indices.Length; i++)
        {
            var index = Indices[i];
            var visible = Opaque[i] && !(treatIndexZeroAsTransparent && index == Palette.TransparentIndex);
            if (!visible) continue;

            var (r, g, b) = palette[index];
            var o = i * 4;
            pixels[o] = b;
            pixels[o + 1] = g;
            pixels[o + 2] = r;
            pixels[o + 3] = 255;
        }
        return pixels;
    }
}
