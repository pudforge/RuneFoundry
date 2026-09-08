using System.Buffers.Binary;

namespace RuneFoundry.Core.Formats;

/// <summary>
/// ZSoft PCX, 8-bit indexed — the form every .pcx in the game uses (manufacturer 10,
/// version 5, RLE encoded, one plane, with a 768-byte VGA palette in the last 769
/// bytes behind a 0x0C marker). Only that shape is supported: the game has no others,
/// and quietly half-decoding an exotic variant would be worse than saying so.
/// </summary>
public sealed class PcxFile
{
    public int Width { get; }
    public int Height { get; }
    public IndexedImage Image { get; }

    /// <summary>The palette embedded in the file's trailer, if it had one.</summary>
    public Palette? EmbeddedPalette { get; }

    private PcxFile(int width, int height, IndexedImage image, Palette? palette)
    {
        Width = width;
        Height = height;
        Image = image;
        EmbeddedPalette = palette;
    }

    public static PcxFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static bool LooksLikePcx(byte[] data)
        => data.Length > 128 && data[0] == 0x0A && data[2] == 1 && data[3] == 8 && data[65] == 1;

    public static PcxFile Parse(byte[] data)
    {
        if (data.Length < 128) throw new InvalidDataException("Not a PCX: file is shorter than its 128-byte header.");
        if (data[0] != 0x0A) throw new InvalidDataException("Not a PCX: wrong magic byte.");
        if (data[2] != 1) throw new InvalidDataException("Unsupported PCX: only RLE-encoded files are handled.");

        int bitsPerPixel = data[3];
        int planes = data[65];
        if (bitsPerPixel != 8 || planes != 1)
            throw new InvalidDataException($"Unsupported PCX: {bitsPerPixel}bpp with {planes} plane(s); only 8-bit single-plane is handled.");

        int xMin = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
        int yMin = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        int xMax = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        int yMax = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10));
        int bytesPerLine = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(66));

        var width = xMax - xMin + 1;
        var height = yMax - yMin + 1;
        if (width <= 0 || height <= 0 || bytesPerLine <= 0)
            throw new InvalidDataException("Not a PCX: header declares a zero or negative image size.");
        if ((long)width * height > 64L * 1024 * 1024)
            throw new InvalidDataException("PCX declares an implausibly large image.");

        // The palette trailer, when present, is the last 769 bytes and starts with 0x0C.
        Palette? palette = null;
        var pixelEnd = data.Length;
        if (data.Length >= 769 && data[^769] == 0x0C)
        {
            palette = Palette.FromBytes(data[^768..]);
            pixelEnd = data.Length - 769;
        }

        var image = new IndexedImage(width, height);
        var p = 128;

        for (var y = 0; y < height; y++)
        {
            var x = 0;
            // Rows are padded to bytesPerLine, which can exceed width; the surplus is discarded.
            while (x < bytesPerLine)
            {
                if (p >= pixelEnd) throw new InvalidDataException($"PCX pixel data ends early, at row {y} of {height}.");

                var b = data[p++];
                int run;
                byte value;

                if ((b & 0xC0) == 0xC0)
                {
                    run = b & 0x3F;
                    if (p >= pixelEnd) throw new InvalidDataException("PCX run-length byte is missing its value.");
                    value = data[p++];
                }
                else
                {
                    run = 1;
                    value = b;
                }

                for (var i = 0; i < run && x < bytesPerLine; i++, x++)
                {
                    if (x < width) image.Set(x, y, value);
                }
            }
        }

        return new PcxFile(width, height, image, palette);
    }

    public byte[] ToBgra32(Palette? fallback = null)
    {
        var palette = EmbeddedPalette ?? fallback ?? Palette.Greyscale();
        // Full-frame PCX art is opaque: index 0 is a real colour here, not a transparency key.
        return Image.ToBgra32(palette, treatIndexZeroAsTransparent: false);
    }

    public string Describe() => $"{Width}x{Height}, 8-bit{(EmbeddedPalette is null ? ", no embedded palette" : ", embedded palette")}";
}
