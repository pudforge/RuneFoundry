using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RuneFoundry.UI;

/// <summary>
/// Putting your own picture into one frame of a sheet.
///
/// The game does not read the file you import. It reads the sheet, at fixed rectangles a
/// JSON alongside it describes — so the requirement is on the sheet, not on your image: the
/// sheet has to keep its exact pixel size and its 8-bit RGBA format, and each frame has to
/// stay exactly where and how big it was. A portrait frame is 207x171; the sheet itself is
/// 3971x3287.
///
/// That is why an imported image of any size is accepted and scaled into the frame rather
/// than refused. Refusing would push the resizing onto the person, in an external editor,
/// to hit a number the editor knows already — and getting it wrong there is the only way
/// this goes wrong at all.
///
/// The one thing worth warning about is shape: an image whose proportions differ from the
/// frame's is stretched, and stretching is visible. <see cref="Fits"/> is how a caller can
/// say so beforehand.
/// </summary>
public static class AtlasImage
{
    /// <summary>Reads a sheet without keeping a handle on the file it came from.</summary>
    public static BitmapSource Load(string path)
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

    /// <summary>
    /// Whether an image has the frame's proportions, within a pixel's rounding. False means
    /// it will be stretched, which is worth saying out loud before it happens.
    /// </summary>
    /// <summary>
    /// How big a picture is, without decoding it.
    ///
    /// Wanted so that "this is a different shape, is that alright?" can be asked before any
    /// of the slow work starts. Decoding a 3840x2160 replacement to answer a yes-or-no
    /// question is what made the question feel like a freeze.
    /// </summary>
    public static (int Width, int Height) MeasureFile(string path)
    {
        using var stream = File.OpenRead(path);

        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);

        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    public static bool Fits(BitmapSource image, int width, int height)
    {
        if (image.PixelWidth <= 0 || image.PixelHeight <= 0 || width <= 0 || height <= 0) return false;

        var wanted = (double)width / height;
        var actual = (double)image.PixelWidth / image.PixelHeight;
        return Math.Abs(wanted - actual) < 0.01;
    }

    /// <summary>
    /// Draws an image into one or more rectangles of a sheet and returns the new sheet as
    /// PNG bytes.
    ///
    /// Pixels are copied rather than rendered through a DrawingVisual, because a visual
    /// brings device-independent units and DPI into it — and a sheet whose DPI is not 96
    /// would then be written back a different size, which is exactly the failure this has to
    /// avoid. The sheet's dimensions here are the ones it arrived with, always.
    /// </summary>
    public static byte[] ReplaceFrames(BitmapSource sheet, BitmapSource replacement, IEnumerable<Int32Rect> frames)
        => Write(sheet, frames, (frame, format) => Pixels(Convert(Scale(replacement, frame.Width, frame.Height), format), frame, format));

    /// <summary>
    /// Writes several pictures into one sheet, in a single pass.
    ///
    /// Not the same as calling the one-picture version twice. Each call reads the sheet and
    /// hands back a whole new one, so writing both results to the same file leaves only
    /// whichever landed last — which is how a campaign picture pair ended up half replaced.
    /// </summary>
    public static byte[] ReplaceFrames(BitmapSource sheet,
                                       IReadOnlyList<(BitmapSource Picture, Int32Rect Frame)> parts)
    {
        var byFrame = parts.ToDictionary(p => p.Frame, p => p.Picture);

        return Write(sheet, parts.Select(p => p.Frame),
            (frame, format) => Pixels(Convert(Scale(byFrame[frame], frame.Width, frame.Height), format),
                                      frame, format));
    }

    /// <summary>
    /// Paints frames a single shade, which is how a team-colour mask is cleared: the mask is
    /// greyscale and black means "no team colour here" — about 91% of a stock mask frame is
    /// already black.
    /// </summary>
    public static byte[] FillFrames(BitmapSource sheet, byte shade, IEnumerable<Int32Rect> frames)
        => Write(sheet, frames, (frame, format) =>
        {
            var bytes = format == PixelFormats.Gray8 ? 1 : 4;
            var pixels = new byte[frame.Width * bytes * frame.Height];

            if (bytes == 1) Array.Fill(pixels, shade);
            else
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = shade;
                    pixels[i + 3] = 255;
                }

            return pixels;
        });

    /// <summary>
    /// The common part: copy pixels into frames and encode, in the sheet's own format.
    ///
    /// Format matters. The team-colour mask ships as 8-bit greyscale — 529 KB against the
    /// face sheet's 20 MB — and converting it to RGBA on the way through would multiply its
    /// size and change what the game is handed.
    /// </summary>
    private static byte[] Write(BitmapSource sheet, IEnumerable<Int32Rect> frames,
                                Func<Int32Rect, PixelFormat, byte[]> make)
    {
        var format = sheet.Format == PixelFormats.Gray8 ? PixelFormats.Gray8 : PixelFormats.Bgra32;
        var canvas = new WriteableBitmap(Convert(sheet, format));

        foreach (var frame in frames)
        {
            if (frame.Width <= 0 || frame.Height <= 0) continue;
            if (frame.X < 0 || frame.Y < 0
                || frame.X + frame.Width > canvas.PixelWidth
                || frame.Y + frame.Height > canvas.PixelHeight)
                continue;

            var stride = frame.Width * (format == PixelFormats.Gray8 ? 1 : 4);
            canvas.WritePixels(frame, make(frame, format), stride, 0);
        }

        canvas.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(canvas));

        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static byte[] Pixels(BitmapSource image, Int32Rect frame, PixelFormat format)
    {
        var stride = frame.Width * (format == PixelFormats.Gray8 ? 1 : 4);
        var pixels = new byte[stride * frame.Height];
        image.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>
    /// The same picture, fainter.
    ///
    /// The campaign chooser draws each campaign twice: lit when the pointer is on it, and
    /// dimmed when it is not. Measuring the game's own art, the dimmed one carries about 40%
    /// of the lit one's alpha — 38.8 against 108.3 for the human campaign, 44.7 against 124.6
    /// for the expansion. So an author supplies the lit picture and the faint one is made
    /// from it, rather than asking for the same drawing twice.
    /// </summary>
    public static BitmapSource Fade(BitmapSource image, double alpha)
    {
        var source = Convert(image, PixelFormats.Bgra32);

        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        // Bgra32 here is straight alpha, so only the alpha byte moves; scaling the colour
        // too would darken what is already drawn dark and lose the artwork.
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = (byte)Math.Clamp(pixels[i] * alpha, 0, 255);

        var faded = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        faded.Freeze();
        return faded;
    }

    /// <summary>
    /// A whole picture at a given size and pixel format, as PNG bytes.
    ///
    /// For the files the game reads directly rather than as part of a sheet. Format is kept
    /// because the act backdrops ship as RGB with no alpha, and handing back RGBA would grow
    /// an 8 MB file for nothing.
    /// </summary>
    public static byte[] Resized(BitmapSource image, int width, int height, PixelFormat format)
    {
        var scaled = Convert(Scale(image, width, height), format);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(scaled));

        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static BitmapSource Scale(BitmapSource image, int width, int height)
    {
        if (image.PixelWidth == width && image.PixelHeight == height) return image;

        var scaled = new TransformedBitmap(image,
            new ScaleTransform((double)width / image.PixelWidth, (double)height / image.PixelHeight));
        scaled.Freeze();

        // TransformedBitmap rounds; a pixel either way would misalign the copy below.
        if (scaled.PixelWidth == width && scaled.PixelHeight == height) return scaled;

        var exact = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawImage(image, new Rect(0, 0, width, height));
        exact.Render(visual);
        exact.Freeze();
        return exact;
    }

    /// <summary>
    /// Whether two sheets are identical over one frame.
    ///
    /// A mod that owns a sheet owns all of it, so "has an override" does not mean "changed
    /// this picture". Comparing the rectangle is what distinguishes the frame someone drew
    /// over from the ninety they did not.
    /// </summary>
    public static bool SameArea(BitmapSource a, BitmapSource b, Int32Rect area)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;
        if (area.X + area.Width > a.PixelWidth || area.Y + area.Height > a.PixelHeight) return false;

        var stride = area.Width * 4;
        var one = new byte[stride * area.Height];
        var two = new byte[stride * area.Height];

        Convert(a, PixelFormats.Bgra32).CopyPixels(area, one, stride, 0);
        Convert(b, PixelFormats.Bgra32).CopyPixels(area, two, stride, 0);

        return one.AsSpan().SequenceEqual(two);
    }

    private static BitmapSource Convert(BitmapSource image, PixelFormat format)
    {
        if (image.Format == format) return image;

        var converted = new FormatConvertedBitmap(image, format, null, 0);
        converted.Freeze();
        return converted;
    }
}
