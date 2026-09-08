using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RuneFoundry.Core.Formats;

namespace RuneFoundry.UI;

/// <summary>
/// Turns the framework-agnostic pixel buffers Core produces into WPF bitmaps.
/// Kept out of Core so the parsers stay testable without a UI stack.
/// </summary>
public static class PreviewRenderer
{
    /// <summary>
    /// Renders one frame of an asset. Returns null when the asset has no visual form,
    /// or when the file turned out not to be decodable after all.
    /// </summary>
    public static BitmapSource? Render(AssetInfo asset, int frameIndex = 0)
    {
        if (asset.RenderFrame is null) return null;

        try
        {
            var (width, height, pixels) = asset.RenderFrame(frameIndex);
            if (width <= 0 || height <= 0) return null;

            var bitmap = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();   // frozen so it can cross threads and cache cheaply
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Loads a PNG or BMP straight through WPF's own decoders. Used for the formats the
    /// remaster added, which need no help from us.
    ///
    /// Reads the bytes first rather than handing WPF a URI: WPF keeps a process-wide
    /// image cache keyed by URI, so replacing a file and re-previewing the same path
    /// would hand back the previous picture. Going through a stream has no such cache,
    /// and leaves no file handle open on a file the user is still editing.
    /// </summary>
    public static BitmapSource? LoadStandardImage(string path)
    {
        try
        {
            return LoadStandardImage(File.ReadAllBytes(path));
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource? LoadStandardImage(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            // No IgnoreImageCache here: WPF's image cache is keyed by URI, so a stream is
            // never cached to begin with — and setting the flag without a URI makes
            // EndInit throw on the null key.
            bitmap.CacheOption = BitmapCacheOption.OnLoad;          // decode now, release the stream
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Everything the preview pane needs for one file, resolved in one go.</summary>
    public static (AssetInfo Info, BitmapSource? Image) Describe(string path, RuneFoundry.Core.GameInstall? game, string? relativePath)
    {
        var info = AssetInspector.Inspect(path, game, relativePath);
        var image = info.Kind is AssetKind.Png or AssetKind.Bmp
            ? LoadStandardImage(path)
            : Render(info);
        return (info, image);
    }
}
