using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RuneFoundry.Core.Formats;
using RuneFoundry.UI;

namespace RuneFoundry.Editor.Views;

/// <summary>
/// Writing a campaign picture out as a file.
///
/// What comes out is what the game has: a picture that is a file of its own is copied
/// byte for byte, and a picture that lives in a sheet is cut from the full-size sheet.
/// Neither goes anywhere near what is drawn on the card — that is a 240 pixel thumbnail,
/// and exporting it produced something too small to work on, which is the opposite of the
/// point.
/// </summary>
public partial class CampaignView
{
    private async void OnExportCampaignArt(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArtSlot slot) return;

        if (Source(slot.Picture0) is null)
        {
            Status?.Invoke("That picture is not in this build, so there is nothing to export.");
            return;
        }

        // A pair is two files, so it asks for a folder. A single picture asks for a name,
        // which is what someone exporting one thing expects.
        var targets = slot.HasPair && slot.Hovered is not null
            ? AskForFolder(slot)
            : AskForFile(slot);

        if (targets is null || targets.Count == 0) return;

        var written = await Working.While(Owner, "Exporting…",
            () => (int?)targets.Count(pair => Export(pair.Picture, pair.Target)),
            ex => Ui.Failed(Owner, "export that picture", ex));

        if (written is null) return;

        Status?.Invoke(written == 1
            ? $"Wrote {Path.GetFileName(targets[0].Target)}."
            : $"Wrote {written} files to {Path.GetDirectoryName(targets[0].Target)}.");
    }

    /// <summary>Where a picture's bytes are: this mod's copy if it has one, else the game's.</summary>
    private string? Source(CampaignPicture picture) => _session.EffectiveFile(_project, picture.Image);

    private List<(CampaignPicture Picture, string Target)>? AskForFolder(ArtSlot slot)
    {
        var folder = new OpenFolderDialog { Title = $"Export {slot.Label.ToLowerInvariant()} to" };
        if (folder.ShowDialog(Window.GetWindow(this)) != true) return null;

        return new List<(CampaignPicture, string)>
        {
            (slot.Picture0, Path.Combine(folder.FolderName, Name(slot.Picture0))),
            (slot.Hovered!, Path.Combine(folder.FolderName, Name(slot.Hovered!))),
        };
    }

    private List<(CampaignPicture Picture, string Target)>? AskForFile(ArtSlot slot)
    {
        var name = Name(slot.Picture0);
        var extension = Path.GetExtension(name);

        var dialog = new SaveFileDialog
        {
            Title = $"Export {slot.Label.ToLowerInvariant()}",
            Filter = extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? "PNG image (*.png)|*.png"
                : $"Picture (*{extension})|*{extension}|All files (*.*)|*.*",
            FileName = name,
            AddExtension = true,
            DefaultExt = extension,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return null;

        return new List<(CampaignPicture, string)> { (slot.Picture0, dialog.FileName) };
    }

    /// <summary>
    /// Writes one picture at the size the game holds it.
    ///
    /// A whole file is copied rather than decoded and encoded again: whatever the game
    /// ships is exactly what lands, down to the bytes, and nothing is lost to a round trip
    /// through a decoder.
    /// </summary>
    private bool Export(CampaignPicture picture, string target)
    {
        var source = Source(picture);
        if (source is null || !File.Exists(source)) return false;

        if (!picture.InSheet)
        {
            File.Copy(source, target, overwrite: true);
            return true;
        }

        // In a sheet, so it has to be cut — from the sheet itself, at its own size.
        if (RectOf(picture) is not { } rect) return false;

        var sheet = AtlasImage.Load(source);
        if (rect.X + rect.Width > sheet.PixelWidth || rect.Y + rect.Height > sheet.PixelHeight)
            return false;

        var cut = new CroppedBitmap(sheet, new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height));
        cut.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cut));

        using var stream = File.Create(target);
        encoder.Save(stream);
        return true;
    }

    /// <summary>
    /// What to call the file.
    ///
    /// A whole file keeps the game's own name, since that is what was copied and it is the
    /// name to put back. A frame cut out of a sheet has no file name of its own, so it is
    /// named after the campaign and the frame.
    /// </summary>
    private string Name(CampaignPicture picture)
    {
        if (!picture.InSheet) return Path.GetFileName(picture.Image);

        var stem = picture.Frame ?? Path.GetFileNameWithoutExtension(picture.Image);
        foreach (var bad in Path.GetInvalidFileNameChars()) stem = stem.Replace(bad, '-');

        return _campaign is null ? stem + ".png" : $"{_campaign.Id}-{stem}.png";
    }
}
