using System.Windows;
using Microsoft.Win32;

namespace RuneFoundry.UI;

/// <summary>
/// Asking for a picture to put into the game, and checking it will fit.
///
/// Three screens replace pictures: campaign artwork, unit icons, and the pictures the whole
/// game shows. Each had written this for itself, so the same action asked in four different
/// voices. One said "Choose the file to use as loadscreen.png", one said "Picture for icon
/// 42", one said only "Loading screen", and the shape question came back under three
/// different error titles depending on which screen you were on.
///
/// What differs between those screens is what happens to the picture afterwards, which stays
/// where it is. What does not differ is being asked for one.
/// </summary>
public static class PictureChooser
{
    /// <summary>
    /// What the file dialog offers. Anything WPF can decode is fair, since the picture is
    /// re-encoded into the game's own format on the way in.
    /// </summary>
    public const string Filter =
        "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*";

    /// <summary>
    /// Asks for a picture for <paramref name="what"/>, and checks its shape against the
    /// space it has to fill.
    ///
    /// Null means no picture: the dialog was cancelled, the file could not be read, or the
    /// shape was different and the answer was no. Every one of those means the caller does
    /// nothing, so they are one answer rather than three.
    /// </summary>
    /// <param name="what">The thing being replaced, in the words the screen uses for it.</param>
    /// <param name="width">The width the picture ends up.</param>
    /// <param name="height">The height the picture ends up.</param>
    public static string? Ask(Window? owner, string what, int width, int height)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Choose a picture for {what}",
            Filter = Filter,
        };

        if (dialog.ShowDialog(owner) != true) return null;

        int chosenWidth, chosenHeight;

        try
        {
            // Read from the header. Decoding a 3840x2160 replacement to answer a yes-or-no
            // question is what made the question feel like the program had stopped.
            (chosenWidth, chosenHeight) = AtlasImage.MeasureFile(dialog.FileName);
        }
        catch (Exception ex)
        {
            Ui.Failed(owner, "read that picture", ex);
            return null;
        }

        // The only thing worth stopping for. A different size alone is not: the picture is
        // scaled either way, and it is only a different shape that shows.
        if (chosenWidth == width && chosenHeight == height) return dialog.FileName;

        return Ui.Confirm(owner, "Different shape",
            $"That picture is {chosenWidth} x {chosenHeight}, and this one is {width} x {height}.\n\n"
            + "RuneFoundry stretches it to fit. The stretch shows. Crop it to the same "
            + "proportions first if you would rather it did not.")
            ? dialog.FileName
            : null;
    }
}
