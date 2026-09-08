using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using RuneFoundry.Core;
using RuneFoundry.Core.Formats;

namespace RuneFoundry.UI;

public partial class SettingsWindow : Window
{
    private readonly Session _session;

    public SettingsWindow(Session session)
    {
        InitializeComponent();
        RuneFoundry.UI.Palette.FollowTitleBar(this);
        _session = session;
        Refresh();
    }

    private void Refresh()
    {
        if (_session.Game is null)
        {
            PathBox.Text = "(not set)";
            PathStatus.Text = "RuneFoundry found no installation. Browse to the folder that holds x86\\Warcraft II.exe.";
        }
        else
        {
            PathBox.Text = _session.Game.Root;

            var files = 0;
            try { files = _session.Game.EnumerateDataFiles().Count(); } catch { /* counted best effort */ }

            PathStatus.Text = files > 0
                ? $"RuneFoundry found {files} moddable files."
                : "Folder looks valid but no data files were found.";
        }

        var battleNet = GameLauncher.FindBattleNet();
        BattleNetStatus.Text = battleNet is null
            ? "Battle.net was not found on this machine."
            : GameLauncher.IsBattleNetRunning
                ? "Battle.net is running and will be asked to launch the game."
                : "Battle.net will be started first, then asked to launch the game.";

        _loading = true;
        LaunchViaBattleNet.IsChecked = _session.Settings.LaunchMethod == LaunchMethod.BattleNet;
        LaunchDirect.IsChecked = _session.Settings.LaunchMethod == LaunchMethod.Direct;
        _loading = false;

        _loading = true;
        TicksBox.Text = _session.Settings.TicksPerSecond.ToString();
        _loading = false;
        ShowTickExample();

        VaultBox.Text = _session.Vault.Root;
        VaultStatus.Text = $"{Ui.FormatBytes(_session.Vault.OriginalsSizeBytes())} of original files backed up.";

        var needsElevation = _session.NeedsElevationToApply(out var blocker);

        if (!Ui.IsElevated())
        {
            Elevation.Text = needsElevation
                ? "A mod needs administrator rights: " + blocker + " Windows asks at that moment."
                : "RuneFoundry writes everything without administrator rights.";
        }
        else if (GameLauncher.CanStartAsDesktopUser(out var why))
        {
            Elevation.Text =
                "RuneFoundry runs as administrator. It starts the game without those rights.";
        }
        else
        {
            // Worth spelling out: this is the difference between the game signing in and not.
            Elevation.Text =
                $"RuneFoundry runs as administrator ({why}). Play refuses to start the game. " +
                "Start it from the Start Menu instead.";
        }
    }

    private bool _loading;

    private void OnTicksChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!int.TryParse(TicksBox.Text, out var ticks) || ticks < 1 || ticks > 1000) return;

        _session.Settings.TicksPerSecond = ticks;
        _session.Settings.Save();
        AiText.TicksPerSecond = ticks;
        ShowTickExample();
    }

    private void ShowTickExample()
        => TicksExample.Text = $"sleep 27000 reads as {AiText.DescribeTicks(27000)}";

    private void OnLaunchMethodChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _session.Settings.LaunchMethod =
            LaunchDirect.IsChecked == true ? LaunchMethod.Direct : LaunchMethod.BattleNet;
        _session.Settings.Save();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the Warcraft II Remastered folder",
            InitialDirectory = _session.Game?.Root ?? @"C:\Program Files (x86)",
        };
        if (dialog.ShowDialog(this) != true) return;

        if (!_session.TryUseGame(dialog.FolderName, out var error))
        {
            Ui.Error(this, "Not a game folder", error);
            return;
        }
        Refresh();
    }

    private void OnDetect(object sender, RoutedEventArgs e)
    {
        var found = GameInstall.Detect();
        if (found is null)
        {
            Ui.Info(this, "Not found",
                "Warcraft II Remastered was not found in the registry or the usual folders. Use Browse.");
            return;
        }

        _session.UseGame(found);
        Refresh();
    }

    private void OnOpenVault(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_session.Vault.Root}\"") { UseShellExecute = true });

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
