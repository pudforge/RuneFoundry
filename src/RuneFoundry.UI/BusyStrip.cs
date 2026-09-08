using System.Windows;
using System.Windows.Controls;

namespace RuneFoundry.UI;

/// <summary>
/// "This is loading", said the same way everywhere.
///
/// Some of what the editor opens is genuinely slow — the icon sheet is a 20 MB image that
/// then has 196 pictures cut out of it — and doing that on the thread that draws the window
/// makes the whole program look hung. The work belongs on a background thread, and the
/// moment it starts belongs on screen, or the difference between "loading" and "broken" is
/// left to the person to guess.
///
/// A strip rather than a modal: the rest of the screen stays usable, and the bar sits where
/// the content will appear so it reads as that content arriving.
/// </summary>
public sealed class BusyStrip : Control
{
    static BusyStrip()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(BusyStrip), new FrameworkPropertyMetadata(typeof(BusyStrip)));
    }

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(BusyStrip),
            new PropertyMetadata("Loading…"));

    /// <summary>What is being waited for, in the words the person would use.</summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public static readonly DependencyProperty IsBusyProperty =
        DependencyProperty.Register(nameof(IsBusy), typeof(bool), typeof(BusyStrip),
            new PropertyMetadata(false, OnIsBusyChanged));

    /// <summary>Whether to show at all. Collapsed rather than hidden, so it takes no room.</summary>
    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    private static void OnIsBusyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((BusyStrip)d).Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Runs slow work off the UI thread with the strip showing, then hands the result back
    /// on the UI thread.
    ///
    /// The pattern is the same everywhere it is used, and writing it once means no view can
    /// forget to turn the strip off again — including when the work throws.
    /// </summary>
    public async Task<T?> While<T>(string message, Func<T> work, Action<Exception>? onError = null)
    {
        Message = message;
        IsBusy = true;
        try
        {
            return await Task.Run(work);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return default;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
