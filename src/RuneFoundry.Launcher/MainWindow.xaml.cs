using System.Windows;
using RuneFoundry.Launcher.Views;
using RuneFoundry.UI;

namespace RuneFoundry.Launcher;

/// <summary>
/// The loader: choose a mod, put it on disk, play.
///
/// Deliberately the smaller of the two programs. Someone who only wants to play a mod
/// should not have to walk past a unit table to do it, and the editor is a separate
/// download for the people who are making one.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Session _session = new();
    private LibraryView? _library;

    public MainWindow()
    {
        InitializeComponent();
        Palette.FollowTitleBar(this);
        Loaded += (_, _) => Initialise();
    }

    private void Initialise()
    {
        ApplyTheme(Palette.Parse(_session.Settings.Theme));

        _session.AutoDetect();
        _session.GameChanged += RefreshGameBadge;

        _library = new LibraryView(_session);
        LibraryHost.Content = _library;
        _library.EnsureInitialised();

        RefreshGameBadge();
    }

    // ---- theme ------------------------------------------------------------

    /// <summary>
    /// Repaints the app and ticks the matching menu item.
    ///
    /// The brushes are shared objects, so recolouring them updates every window already
    /// open, including Settings — there is nothing to reload and nothing to restart.
    /// </summary>
    private void ApplyTheme(AppTheme theme)
    {
        Palette.Apply(theme);

        ThemeSystemItem.IsChecked = theme == AppTheme.System;
        ThemeDarkItem.IsChecked = theme == AppTheme.Dark;
        ThemeLightItem.IsChecked = theme == AppTheme.Light;
    }

    private void OnThemeChosen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name) return;

        var theme = Palette.Parse(name);
        ApplyTheme(theme);

        _session.Settings.Theme = Palette.Name(theme);
        _session.Settings.Save();
    }

    private void RefreshGameBadge()
    {
        if (_session.Game is null)
        {
            GameBadge.Text = "No game folder. Choose one in Settings.";
            GameBadge.Visibility = Visibility.Visible;
            return;
        }

        // The folder itself is in Settings; the only thing worth a line in the header is
        // when applying a mod will interrupt with a permission prompt.
        var locked = _session.NeedsElevationToApply(out _);

        GameBadge.Text = locked ? "Windows will ask for permission when you apply a mod" : "";
        GameBadge.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- menu bar ---------------------------------------------------------

    private void OnInstallMod(object sender, RoutedEventArgs e) => _library?.InstallFromDialog();
    private void OnPlay(object sender, RoutedEventArgs e) => _library?.PlaySelected();
    private void OnApply(object sender, RoutedEventArgs e) => _library?.ApplySelected();
    private void OnVerify(object sender, RoutedEventArgs e) => _library?.Verify();

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnOpenVault(object sender, RoutedEventArgs e)
        => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_session.Vault.Root}\"")
            {
                UseShellExecute = true,
            });

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_session) { Owner = this };
        settings.ShowDialog();
        RefreshGameBadge();
    }
}
