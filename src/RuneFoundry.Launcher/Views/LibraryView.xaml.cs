using System.Windows.Input;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RuneFoundry.Core;
using RuneFoundry.UI;

namespace RuneFoundry.Launcher.Views;

public partial class LibraryView : UserControl
{
    private readonly Session _session;
    private readonly List<ModListItem> _rows = new();

    private bool _busy;

    private readonly ModApplier _applier;

    public LibraryView(Session session)
    {
        InitializeComponent();

        // The loader's own chrome, driven by the shared service rather than by a second
        // copy of what it does.
        _applier = new ModApplier(session, Window.GetWindow(this));
        _applier.Status += SetStatus;
        _applier.Busy += SetBusy;
        _applier.LibraryChanged += RebuildRows;
        _applier.ProgressVisible += visible =>
            Progress.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _applier.Progressed += (done, total, indeterminate) =>
        {
            Progress.IsIndeterminate = indeterminate;
            Progress.Maximum = total;
            Progress.Value = done;
        };
        _session = session;
        _session.GameChanged += OnGameChanged;
    }

    /// <summary>Called by the shell once both views exist.</summary>
    public void EnsureInitialised() => OnGameChanged();

    private Window? Owner => Window.GetWindow(this);

    private void OnGameChanged()
    {
        if (_session.Game is null)
        {
            SetStatus("No game folder set. Open Settings to choose your installation.");
            ApplyButton.IsEnabled = false;
            PlayButton.IsEnabled = false;
            return;
        }

        RebuildRows();
    }

    /// <summary>Re-reads the installer state, for when the editor has just installed something.</summary>
    public void ReloadFromDisk()
    {
        if (_session.Game is null) return;
        _session.UseGame(_session.Game);   // recreates the installer from state.json
    }

    /// <summary>What is actually on disk right now: a mod id, or null for vanilla.</summary>
    private string? ActiveId => _session.Installer?.ActiveMod?.Id;

    /// <summary>What the user has picked, which is not applied until Apply or Play.</summary>
    private ModListItem? Selected => ModList.SelectedItem as ModListItem;

    private void RebuildRows()
    {
        var installer = _session.Installer;
        if (installer is null) return;

        var previous = Selected?.Id ?? (Selected?.IsVanilla == true ? "" : null);

        _rows.Clear();
        // Vanilla sits at the top as a row of its own, so playing unmodded is the same
        // gesture as playing a mod rather than a separate button somewhere else.
        _rows.Add(new ModListItem(null));
        foreach (var mod in installer.State.Mods) _rows.Add(new ModListItem(mod));

        var active = ActiveId;
        foreach (var row in _rows)
            row.IsActive = row.IsVanilla ? active is null : row.Id.Equals(active, StringComparison.OrdinalIgnoreCase);

        ModList.ItemsSource = null;
        ModList.ItemsSource = _rows;

        // Keep the user's selection across a rebuild; otherwise fall back to what is playing.
        ModList.SelectedItem =
            (previous is not null ? _rows.FirstOrDefault(r => MatchesId(r, previous)) : null)
            ?? _rows.FirstOrDefault(r => r.IsActive)
            ?? _rows[0];

        RefreshStatus();
    }

    private static bool MatchesId(ModListItem row, string id)
        => id.Length == 0 ? row.IsVanilla : row.Id.Equals(id, StringComparison.OrdinalIgnoreCase);

    private void RefreshStatus()
    {
        var installer = _session.Installer;
        if (installer is null) return;

        var active = _rows.FirstOrDefault(r => r.IsActive);
        var applied = installer.State.Applied.Count;

        var parts = new List<string> { "Playing: " + (active?.Name ?? "Vanilla") };
        if (applied > 0) parts.Add(applied == 1 ? "1 file replaced" : $"{applied} files replaced");
        parts.Add(installer.State.Mods.Count == 1 ? "1 mod in library" : $"{installer.State.Mods.Count} mods in library");

        SetStatus(string.Join(" · ", parts));

        var selection = Selected;
        var switching = selection is not null && !selection.IsActive;

        ApplyButton.IsEnabled = !_busy && switching;
        PlayButton.IsEnabled = !_busy && selection is not null;
        PlayLabel.Text = switching ? $"Play {Trim(selection!.Name)}" : "Play";
        RemoveButton.IsEnabled = selection is { IsVanilla: false };
    }

    private static string Trim(string name) => name.Length <= 18 ? name : name[..17] + "…";

    private void SetStatus(string text) => StatusText.Text = text;

    private void ShowDetail(ModListItem? row)
    {
        var installer = _session.Installer;
        if (row is null || installer is null)
        {
            DetailEmpty.Visibility = Visibility.Visible;
            DetailBody.Visibility = Visibility.Collapsed;
            return;
        }

        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailBody.Visibility = Visibility.Visible;

        DetailName.Text = row.Name;

        if (row.IsVanilla)
        {
            DetailMeta.Text = row.IsActive ? "Currently playing" : "Not currently playing";
            DetailDescription.Text =
                "Playing this puts every original file back and removes anything a mod added. " +
                "Your mods stay in the library.";
            FilesHeading.Text = "Files";
            FileList.ItemsSource = Array.Empty<FileRow>();
            return;
        }

        var mod = row.Mod!;
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(mod.Version)) meta.Add("Version " + mod.Version);
        if (!string.IsNullOrWhiteSpace(mod.Author)) meta.Add(mod.Author);
        meta.Add("added " + mod.AddedUtc.ToLocalTime().ToString("d MMM yyyy"));
        DetailMeta.Text = string.Join(" · ", meta);

        DetailDescription.Text = string.IsNullOrWhiteSpace(mod.Description) ? "No description." : mod.Description;

        FilesHeading.Text = mod.ProvidedPaths.Count == 1 ? "Files (1)" : $"Files ({mod.ProvidedPaths.Count})";
        FileList.ItemsSource = mod.ProvidedPaths
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var live = installer.State.Owners.TryGetValue(path, out var owner)
                           && owner.Equals(mod.Id, StringComparison.OrdinalIgnoreCase);
                return new FileRow(live ? "●" : "○", path, live ? "" : "not applied");
            })
            .ToList();
    }

    // Entry points for the window's menu bar.
    public void PlaySelected() => OnPlay(this, new RoutedEventArgs());
    public void ApplySelected() => OnApply(this, new RoutedEventArgs());
    public void Verify() => OnVerify(this, new RoutedEventArgs());
    public void InstallFromDialog() => OnAddMod(this, new RoutedEventArgs());

    // ---- commands -------------------------------------------------------

    private void OnModSelected(object sender, SelectionChangedEventArgs e)
    {
        ShowDetail(Selected);
        RefreshStatus();
    }

    private void OnAddMod(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add mod package",
            Filter = "Warcraft II mods (*.w2mod;*.zip)|*.w2mod;*.zip|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(Owner) == true) AddPackages(dialog.FileNames);
    }

    /// <summary>Adds packages to the library and selects the last one added.</summary>
    public bool AddPackages(IEnumerable<string> paths)
    {
        var installer = _session.Installer;
        if (installer is null)
        {
            Ui.Error(Owner, "No game folder", "Set your Warcraft II Remastered folder in Settings first.");
            return false;
        }

        var added = new List<string>();
        var failed = new List<string>();

        foreach (var path in paths)
        {
            try
            {
                added.Add(installer.AddPackage(path).Id);
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (added.Count > 0)
        {
            SaveState(installer);
            RebuildRows();
            ModList.SelectedItem = _rows.FirstOrDefault(r => MatchesId(r, added[^1]));
            SetStatus($"Added {added.Count} mod(s). Press Play to switch to one.");
        }

        if (failed.Count > 0)
            Ui.Error(Owner, failed.Count == 1 ? "Could not add that mod" : "Some mods could not be added",
                string.Join("\n\n", failed));

        return added.Count > 0;
    }

    /// <summary>
    /// Installs a package and selects it, so Save & test can hand it straight to Apply.
    /// Returns the mod id, or null if it could not be installed.
    /// </summary>
    public string? InstallForTesting(string packagePath)
    {
        var id = _applier.Install(packagePath);
        if (id is not null) ModList.SelectedItem = _rows.FirstOrDefault(r => MatchesId(r, id));
        return id;
    }

    private void OnRemoveMod(object sender, RoutedEventArgs e)
    {
        var installer = _session.Installer;
        if (Selected is not { IsVanilla: false } row || installer is null) return;

        if (!Ui.Confirm(Owner, "Remove mod",
                $"Remove \"{row.Name}\" from the library?\n\n" +
                (row.IsActive
                    ? "It is applied, so the original files go back first."
                    : "The original files are already in place.")))
            return;

        if (row.IsActive) installer.SetOnlyEnabled(null);

        installer.RemoveMod(row.Id);
        SaveState(installer);

        // Removing what was playing means the game folder still holds its files.
        if (row.IsActive) _ = ApplySelection(row.Id, launch: false, silent: true);
        else RebuildRows();
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        await ApplySelection(Selected.IsVanilla ? null : Selected.Id, launch: false);
    }

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        await ApplySelection(Selected.IsVanilla ? null : Selected.Id, launch: true);
    }

    /// <summary>
    /// Makes one mod — or vanilla — the thing on disk, then optionally launches.
    /// Public so Save & test can run the same path the Play button does.
    /// </summary>
    public async Task<bool> ApplySelection(string? modId, bool launch, bool silent = false)
    {
        if (_busy) return false;
        return await _applier.Apply(modId, launch, silent);
    }

    /// <summary>
    /// Writes the library back to the vault, reporting rather than throwing if it cannot.
    /// The state file lives in ProgramData and can end up owned by an administrator, in
    /// which case the change is not remembered — and silently forgetting it is worse than
    /// saying so.
    /// </summary>
    private void SaveState(ModInstaller installer)
    {
        try
        {
            installer.Save();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "save the library", ex);
        }
    }


    private void SetBusy(bool busy)
    {
        _busy = busy;
        ModList.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy;
        PlayButton.IsEnabled = !busy;
    }

    private void OnVerify(object sender, RoutedEventArgs e)
    {
        var installer = _session.Installer;
        if (installer is null) return;

        var problems = installer.Verify();
        if (problems.Count == 0)
        {
            Ui.Info(Owner, "Verify",
                installer.State.Applied.Count == 0
                    ? "The game is vanilla and nothing is replaced."
                    : $"All {installer.State.Applied.Count} modded file(s) are exactly as the studio left them.");
            return;
        }

        Ui.Info(Owner, "Verify",
            $"{problems.Count} problem(s) found:\n\n" +
            string.Join("\n", problems.Take(25)) +
            (problems.Count > 25 ? $"\n… and {problems.Count - 25} more" : "") +
            "\n\nPressing Play again will rewrite the affected files.");
    }

    /// <summary>
    /// Starts the game through Battle.net, which is the only way Warcraft II Remastered
    /// will actually stay running. Returns false, having explained why, if it could not.
    /// </summary>
    public async Task<bool> LaunchGame() => await _applier.Launch();


    // ---- drag and drop --------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var packages = paths.Where(p =>
            p.EndsWith(ModPackage.Extension, StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (packages.Length == 0)
        {
            Ui.Error(Owner, "Not a mod package", "Drop a .w2mod file (or the .zip it was renamed from).");
            return;
        }

        AddPackages(packages);
    }

    // ---- the row's own menu ----------------------------------------------

    /// <summary>
    /// Double-clicking a mod plays it, which is what the row is for. The same action is in
    /// the menu with the gesture named beside it, so it is discoverable rather than folklore.
    /// </summary>
    private void OnModDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A double-click on the empty space below the rows is not a double-click on a mod.
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>() is null) return;

        PlaySelected();
    }

    /// <summary>
    /// Settles what the menu can do before it opens, so nothing on it is a dead end. It acts
    /// on the selection, and right-clicking a row selects it first.
    /// </summary>
    private void OnModMenuOpening(object sender, RoutedEventArgs e)
    {
        var row = Selected;
        var installed = _session.Installer is not null;

        ModPlayItem.IsEnabled = installed && !_busy;
        ModApplyItem.IsEnabled = installed && !_busy;
        ModVerifyItem.IsEnabled = installed;
        ModFolderItem.IsEnabled = row is { IsVanilla: false };
        ModRemoveItem.IsEnabled = row is { IsVanilla: false } && !_busy;
    }

    /// <summary>Shows where a mod's package actually lives, for the curious and the stuck.</summary>
    private void OnShowModFolder(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsVanilla: false } row) return;

        var path = _session.Vault.PackagePath(row.Id);
        var argument = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{_session.Vault.Root}\"";

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Could not open Explorer: " + ex.Message);
        }
    }

    /// <summary>
    /// Selects what was right-clicked. Without this the menu acts on whatever was selected
    /// before, which is how you reset the wrong one.
    /// </summary>
    private void OnModRightClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>() is { } item
            && !item.IsSelected)
            item.IsSelected = true;
    }
}
