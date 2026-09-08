using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using RuneFoundry.UI;

namespace RuneFoundry.Editor.Views;

/// <summary>One project on the start page.</summary>
public sealed class RecentProject
{
    public required string Path { get; init; }

    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>Whether the file is still where it was.</summary>
    public bool Exists => File.Exists(Path);

    public bool Missing => !Exists;

    public string Note => "Not there any more. Moved, renamed or deleted.";
}

/// <summary>
/// What the editor shows before a project is chosen.
///
/// The editor used to reopen whatever was last open, which meant it started already editing
/// something — and the fastest way to damage a mod is to change it while believing you are
/// looking at a different one. Nothing loads until it is picked here.
///
/// The list is also where entries are removed, because that is where they are seen. Removing
/// takes the project off this list and does not touch the project itself, and the button says
/// so.
/// </summary>
public partial class StartView : UserControl
{
    public StartView() => InitializeComponent();

    /// <summary>Chosen from the list.</summary>
    public event Action<string>? OpenRequested;

    /// <summary>The two buttons at the top, handled by whoever owns project loading.</summary>
    public event Action? NewRequested;
    public event Action? BrowseRequested;

    /// <summary>Taken off the list, so the settings holder can save.</summary>
    public event Action<string>? ForgetRequested;

    /// <summary>
    /// Fills the list. A project that has gone missing is shown and greyed rather than
    /// hidden: it explains where a mod went, and lets it be removed deliberately.
    /// </summary>
    public void Show(IEnumerable<string> recent)
    {
        var rows = recent.Select(path => new RecentProject { Path = path }).ToList();

        RecentList.ItemsSource = rows;
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNew(object sender, RoutedEventArgs e) => NewRequested?.Invoke();
    private void OnOpen(object sender, RoutedEventArgs e) => BrowseRequested?.Invoke();

    /// <summary>
    /// Opens the project that was clicked.
    ///
    /// Its own handler because a mouse event carries its own argument type and WPF matches
    /// the delegate exactly; the menu item next door still arrives as a plain routed event.
    /// Both end up in the same place.
    /// </summary>
    private void OnRowClicked(object sender, MouseButtonEventArgs e) => OpenRecent(sender);

    private void OnOpenRecent(object sender, RoutedEventArgs e) => OpenRecent(sender);

    /// <summary>
    /// A project whose folder has gone is left alone. The row says so and does not open,
    /// rather than opening onto an error.
    /// </summary>
    private void OpenRecent(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentProject { Exists: true } row)
            OpenRequested?.Invoke(row.Path);
    }

    private void OnForget(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentProject row)
            ForgetRequested?.Invoke(row.Path);
    }
}
