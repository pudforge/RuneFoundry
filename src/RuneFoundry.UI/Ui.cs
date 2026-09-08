using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace RuneFoundry.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public static class Ui
{
    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public static bool IsElevated() => RuneFoundry.Core.GameLauncher.IsElevated();

    public static void Error(Window? owner, string title, string message)
        => MessageBox.Show(owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public static void Info(Window? owner, string title, string message)
        => MessageBox.Show(owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(Window? owner, string title, string message)
        => MessageBox.Show(owner!, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    /// <summary>
    /// The one way the whole program says an action did not work.
    ///
    /// The title names the action in the words the button used, so the message answers the
    /// thing that was just pressed. Fifty-two titles had grown for about six real failures:
    /// seven ways to say a reset went wrong, six to say a picture would not go in, seven to
    /// say something would not save. Which one appeared depended on which screen you were
    /// on rather than on what happened.
    ///
    /// The body is the exception's own message. Seventeen call sites prefixed it with the
    /// exception's class name, which tells the reader nothing they can act on and puts
    /// "InvalidDataException" in front of a sentence that already reads as English. The
    /// class name is only used when there is no message at all.
    /// </summary>
    /// <param name="action">
    /// What failed, as a verb phrase completing "Could not". Match the button: "replace
    /// the loading screen", not "handle the image import".
    /// </param>
    public static void Failed(Window? owner, string action, Exception ex)
        => Failed(owner, action, Explain(ex));

    public static void Failed(Window? owner, string action, string reason)
        => Error(owner, $"Could not {action}", reason);

    private static string Explain(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;

    /// <summary>
    /// The one way the whole program asks "really put this back?".
    ///
    /// Every reset throws away something the author made, and they were being asked in
    /// three different wordings and not asked at all in three other places — so which
    /// button was safe depended on which screen you were on. One helper is what keeps that
    /// from drifting again.
    /// </summary>
    /// <param name="what">The thing being reset, named as the person would name it.</param>
    /// <param name="consequence">What takes its place, in a sentence.</param>
    public static bool ConfirmReset(Window? owner, string what, string consequence)
        => Confirm(owner, "Reset to game default",
            $"Put {what} back the way the game shipped it?\n\n{consequence}\n\n" +
            "Your version is discarded. This cannot be undone.");
}

/// <summary>Walking up the visual tree, which WPF makes surprisingly wordy.</summary>
public static class VisualTree
{
    /// <summary>
    /// The nearest ancestor of a type, or null.
    ///
    /// The use is always the same: a click handler needs to know which row was clicked, and
    /// the thing that raised it is some TextBlock four levels inside the row's template.
    /// </summary>
    public static T? FindAncestor<T>(this DependencyObject? from) where T : DependencyObject
    {
        while (from is not null)
        {
            if (from is T match) return match;
            from = from is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(from)
                : LogicalTreeHelper.GetParent(from);
        }
        return null;
    }
}

/// <summary>Inverts a bool for binding, since WPF has no built-in for it.</summary>
public sealed class InverseBooleanConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is bool flag && !flag;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is bool flag && !flag;
}

/// <summary>
/// True for every group except the opening setup block, which starts collapsed: it is 26
/// near-identical rows of initialisation before anything a reader cares about.
/// </summary>
public sealed class SectionStartsOpenConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value as string != "SETUP";

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

/// <summary>
/// Visible only for a wave group. Setup and the closing loop are not things you reorder
/// or drop — moving the opening variable block would change what every wave inherits.
/// </summary>
public sealed class WaveSectionConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter,
        System.Globalization.CultureInfo culture)
        => value is string name && name.StartsWith("WAVE", StringComparison.Ordinal)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter,
        System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
