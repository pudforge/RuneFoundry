using System.Buffers.Binary;

namespace RuneFoundry.Core.Formats;

public sealed class GrpFrame
{
    public int Index { get; init; }
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Offset of the frame payload, with the high flag bit already masked off.</summary>
    public uint DataOffset { get; init; }

    /// <summary>
    /// The top bit of the offset field, which the game sets to mark a frame as stored
    /// flat rather than run-length encoded. Only Art\human.grp and Art\orc.grp use it,
    /// on four frames between them — but reading the offset without masking it lands
    /// two gigabytes past the end of the file.
    /// </summary>
    public bool UncompressedFlag { get; init; }

    /// <summary>True when the frame's payload turned out to be flat pixels rather than RLE runs.</summary>
    public bool IsUncompressed { get; internal set; }

    /// <summary>Set when the frame could not be decoded either way; the frame is still listed.</summary>
    public string? Error { get; internal set; }
}

/// <summary>
/// Warcraft II sprite sheet.
///
/// Header is frameCount/maxWidth/maxHeight as uint16, then one 8-byte record per frame
/// (x, y, w, h as bytes, then a uint32 offset into the file). Frames with identical
/// artwork share an offset, so the same payload can back several entries.
///
/// Payloads come in two flavours. Most are the classic run-length encoding: a uint16
/// row-offset table followed by per-row commands. A handful (Art\human.grp, Art\orc.grp,
/// Art\hframe.grp among them) store flat width*height pixels instead. Nothing in the
/// header distinguishes the two, so each frame is decoded by trying RLE and falling
/// back to raw when the runs do not add up — verified against all 272 GRPs in the game.
/// </summary>
public sealed class GrpFile
{
    public int MaxWidth { get; }
    public int MaxHeight { get; }
    public IReadOnlyList<GrpFrame> Frames { get; }

    private readonly byte[] _data;

    private GrpFile(byte[] data, int maxWidth, int maxHeight, IReadOnlyList<GrpFrame> frames)
    {
        _data = data;
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        Frames = frames;
    }

    public static GrpFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static GrpFile Parse(byte[] data)
    {
        if (data.Length < 6) throw new InvalidDataException("Not a GRP: file is shorter than its header.");

        int frameCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0));
        int maxWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        int maxHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));

        if (frameCount == 0) throw new InvalidDataException("Not a GRP: it declares zero frames.");

        var headerSize = 6 + frameCount * 8;
        if (headerSize > data.Length)
            throw new InvalidDataException($"Not a GRP: {frameCount} frames need a {headerSize}-byte header but the file is {data.Length} bytes.");

        var frames = new List<GrpFrame>(frameCount);
        for (var i = 0; i < frameCount; i++)
        {
            var o = 6 + i * 8;
            var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 4));
            frames.Add(new GrpFrame
            {
                Index = i,
                OffsetX = data[o],
                OffsetY = data[o + 1],
                Width = data[o + 2],
                Height = data[o + 3],
                DataOffset = rawOffset & 0x7FFFFFFF,
                UncompressedFlag = (rawOffset & 0x80000000) != 0,
            });
        }

        return new GrpFile(data, maxWidth, maxHeight, frames);
    }

    /// <summary>Cheap validity probe for "is this actually a GRP", used to pick a preview renderer.</summary>
    public static bool LooksLikeGrp(byte[] data)
    {
        try
        {
            var grp = Parse(data);
            return grp.Frames.All(f => f.DataOffset < data.Length) && grp.MaxWidth > 0 && grp.MaxHeight > 0;
        }
        catch
        {
            return false;
        }
    }

    public IndexedImage DecodeFrame(int frameIndex)
    {
        var frame = Frames[frameIndex];
        var image = new IndexedImage(Math.Max(frame.Width, 1), Math.Max(frame.Height, 1));

        if (frame.Width == 0 || frame.Height == 0)
        {
            frame.Error = "Frame has zero size.";
            return image;
        }

        // The flag bit says which layout to expect, but it is only ever set in two files,
        // and 116 unflagged frames in the game are flat as well. So the flag picks the
        // order to try and the decoders themselves decide, which handles both honestly.
        Func<GrpFrame, IndexedImage, bool> first = frame.UncompressedFlag ? TryDecodeRaw : TryDecodeRle;
        Func<GrpFrame, IndexedImage, bool> second = frame.UncompressedFlag ? TryDecodeRle : TryDecodeRaw;

        if (first(frame, image))
        {
            frame.IsUncompressed = frame.UncompressedFlag;
            return image;
        }

        // Reset anything the failed attempt wrote before trying the other layout.
        Array.Clear(image.Indices);
        Array.Clear(image.Opaque);

        if (second(frame, image))
        {
            frame.IsUncompressed = !frame.UncompressedFlag;
            return image;
        }

        frame.Error = "Frame payload is neither valid run-length data nor flat pixels.";
        return image;
    }

    private bool TryDecodeRle(GrpFrame frame, IndexedImage image)
    {
        var start = (long)frame.DataOffset;
        if (start + frame.Height * 2L > _data.Length) return false;

        for (var row = 0; row < frame.Height; row++)
        {
            var rowStart = start + BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan((int)(start + row * 2)));
            var p = rowStart;
            var x = 0;

            while (x < frame.Width)
            {
                if (p >= _data.Length) return false;
                var command = _data[p++];

                if ((command & 0x80) != 0)
                {
                    // Transparent run: advance without writing, leaving the mask clear.
                    x += command & 0x7F;
                }
                else if ((command & 0x40) != 0)
                {
                    if (p >= _data.Length) return false;
                    var run = command & 0x3F;
                    if (run == 0) return false;
                    var value = _data[p++];
                    // Advance by the full run even past the edge, so an over-long row
                    // is caught by the bounds check below instead of being clamped away.
                    for (var i = 0; i < run; i++, x++)
                    {
                        if (x < frame.Width) image.Set(x, row, value);
                    }
                }
                else
                {
                    var run = command;
                    if (run == 0) return false;
                    if (p + run > _data.Length) return false;
                    for (var i = 0; i < run; i++, p++)
                    {
                        if (x < frame.Width) image.Set(x, row, _data[p]);
                        x++;
                    }
                }

                if (x > frame.Width) return false;
            }

            if (x != frame.Width) return false;
        }
        return true;
    }

    private bool TryDecodeRaw(GrpFrame frame, IndexedImage image)
    {
        var size = (long)frame.Width * frame.Height;
        if (frame.DataOffset + size > _data.Length) return false;

        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                image.Set(x, y, _data[frame.DataOffset + y * frame.Width + x]);
            }
        }
        return true;
    }

    public string Describe()
    {
        var shared = Frames.Select(f => f.DataOffset).Distinct().Count();
        var note = shared < Frames.Count ? $", {Frames.Count - shared} sharing artwork" : "";
        return $"{Frames.Count} frames, up to {MaxWidth}x{MaxHeight}{note}";
    }
}
