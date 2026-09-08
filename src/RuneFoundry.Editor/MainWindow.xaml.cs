using System.IO;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using RuneFoundry.Core;
using RuneFoundry.Editor.Views;
using RuneFoundry.UI;

namespace RuneFoundry.Editor;

/// <summary>
/// The campaign editor: build a mod by changing the game's files.
///
/// It keeps Save &amp; test, which is build, install, apply and launch — the same path the
/// loader takes to play a mod, through the same <see cref="ModApplier"/>. An author should
/// not have to switch programs to see their change in the game.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Session _session = new();
    private EditorView? _editor;

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

        _editor = new EditorView(_session);
        EditorHost.Content = _editor;
        _editor.EnsureInitialised();

        // Ctrl+Z and Ctrl+Y, on the window so they work wherever the focus happens to be —
        // except inside a text box, where the box's own undo is the one people mean.
        InputBindings.Add(new KeyBinding(new Command(() => _editor?.UndoLast()),
            Key.Z, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Command(() => _editor?.RedoLast()),
            Key.Y, ModifierKeys.Control));

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

        var locked = _session.NeedsElevationToApply(out _);

        GameBadge.Text = locked ? "Windows will ask for permission when you test a mod" : "";
        GameBadge.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- menu bar ---------------------------------------------------------

    /// <summary>
    /// Rebuilds the recent list each time the menu opens.
    ///
    /// Built here rather than kept in sync: the list changes whenever a project is opened,
    /// created or saved under a new name, and a menu nobody is looking at is the cheapest
    /// possible thing to rebuild.
    /// </summary>
    private void FillRecentProjects()
    {
        RecentMenuItem.Items.Clear();

        var recent = _session.Settings.RecentProjects;
        var current = _editor?.ProjectPath;

        var listed = 0;
        foreach (var path in recent)
        {
            // The project already open is not somewhere to go.
            if (string.Equals(path, current, StringComparison.OrdinalIgnoreCase)) continue;

            var item = new MenuItem
            {
                // The file name is what distinguishes them; the folder is the tie-breaker,
                // and two mods called MyMod in different folders is the normal case.
                Header = $"_{listed + 1}  {Path.GetFileNameWithoutExtension(path)}",
                InputGestureText = Shorten(Path.GetDirectoryName(path) ?? ""),
                ToolTip = path,
            };

            var target = path;
            item.Click += (_, _) => _editor?.OpenRecent(target);
            RecentMenuItem.Items.Add(item);
            listed++;
        }

        if (listed == 0)
        {
            RecentMenuItem.Items.Add(new MenuItem { Header = "Nothing yet", IsEnabled = false });
            return;
        }

        RecentMenuItem.Items.Add(new Separator());

        var clear = new MenuItem { Header = "_Forget this list" };
        clear.Click += (_, _) =>
        {
            _session.Settings.RecentProjects.Clear();
            _session.Settings.Save();
        };
        RecentMenuItem.Items.Add(clear);
    }

    /// <summary>A folder short enough to sit beside a menu item without pushing it wide.</summary>
    private static string Shorten(string folder)
    {
        const int room = 44;
        if (folder.Length <= room) return folder;

        var tail = folder[^room..];
        var cut = tail.IndexOf(Path.DirectorySeparatorChar);
        return "…" + (cut >= 0 ? tail[cut..] : tail);
    }

    private void OnFileMenuOpened(object sender, RoutedEventArgs e)
    {
        FillRecentProjects();

        var hasProject = _editor?.HasProject == true;
        SaveMenuItem.IsEnabled = hasProject;
        SaveAsMenuItem.IsEnabled = hasProject;
        CloseMenuItem.IsEnabled = hasProject;
        BuildMenuItem.IsEnabled = hasProject;
        OpenFolderMenuItem.IsEnabled = hasProject;
        TestMenuItem.IsEnabled = hasProject && _session.Installer is not null;
    }

    private void OnNewProject(object sender, RoutedEventArgs e) => _editor?.NewProject();
    private void OnOpenProject(object sender, RoutedEventArgs e) => _editor?.OpenProject();
    private void OnSaveProject(object sender, RoutedEventArgs e) => _editor?.SaveProject();
    private void OnSaveProjectAs(object sender, RoutedEventArgs e) => _editor?.SaveProjectAs();
    private void OnCloseProject(object sender, RoutedEventArgs e) => _editor?.CloseProject();
    private void OnBuild(object sender, RoutedEventArgs e) => _editor?.Build();
    private void OnTestInGame(object sender, RoutedEventArgs e) => _editor?.TestInGame();
    private void OnOpenProjectFolder(object sender, RoutedEventArgs e) => _editor?.OpenProjectFolder();

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    // ---- undo -------------------------------------------------------------

    /// <summary>
    /// The menu names the change it will take back, so the choice is informed rather than a
    /// guess about what "Undo" means right now.
    /// </summary>
    private void OnEditMenuOpened(object sender, RoutedEventArgs e)
    {
        var stack = _editor?.Undo;

        UndoMenuItem.Header = stack?.UndoLabel ?? "Undo";
        UndoMenuItem.IsEnabled = stack?.CanUndo == true;

        RedoMenuItem.Header = stack?.RedoLabel ?? "Redo";
        RedoMenuItem.IsEnabled = stack?.CanRedo == true;
    }

    private void OnUndo(object sender, RoutedEventArgs e) => _editor?.UndoLast();
    private void OnRedo(object sender, RoutedEventArgs e) => _editor?.RedoLast();

    /// <summary>
    /// The editor can apply a mod, so it can check one too — and an author who has just
    /// tested a mod is exactly the person who wants to know the game folder is still sound.
    /// </summary>
    private void OnVerify(object sender, RoutedEventArgs e)
    {
        var installer = _session.Installer;
        if (installer is null)
        {
            Ui.Error(this, "No game folder", "Set your Warcraft II Remastered folder in Settings first.");
            return;
        }

        var problems = installer.Verify();
        if (problems.Count == 0)
        {
            Ui.Info(this, "Verify", "Every file the applied mod replaced still matches it.");
            return;
        }

        Ui.Error(this, "Verify", string.Join("\n", problems));
    }

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

/// <summary>
/// The smallest thing a KeyBinding will accept. The app has no command layer and does not
/// need one for two shortcuts; this keeps the menu and the keys calling the same method.
/// </summary>
internal sealed class Command : ICommand
{
    private readonly Action _run;

    public Command(Action run) => _run = run;

    /// <summary>
    /// Required by ICommand and never raised: undo and redo are always offered, and the menu
    /// settles what they can do when it opens rather than pushing changes at the bindings.
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _run();
}
