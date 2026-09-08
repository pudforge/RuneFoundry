using System.Diagnostics;
using System.IO;
using System.Media;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using Microsoft.Win32;
using RuneFoundry.Core;
using RuneFoundry.Core.Formats;
using RuneFoundry.UI;

namespace RuneFoundry.Editor.Views;

public partial class EditorView : UserControl
{
    private readonly Session _session;

    private ModProject? _project;
    private TreeNode? _root;
    private TreeNode? _selected;
    private AssetInfo? _asset;

    /// <summary>The file the preview is showing — the override if there is one, else the stock file.</summary>
    private string? _previewSource;

    /// <summary>Set while code is populating controls, so change handlers ignore their own writes.</summary>
    private bool _loadingUi;

    private bool _textDirty;

    /// <summary>
    /// Pending edits are written a moment after typing stops, and whenever the selection
    /// moves. Before this, switching files threw away unsaved work without a word.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _autoSave;
    private bool _saving;
    private JsonTable? _jsonTable;

    private bool _datEditable;
    private DatTable? _datTable;
    private int _datRecord;

    private bool _aiEditable;

    private AiFile? _aiFile;
    private AiScript? _aiScript;
    private System.Collections.ObjectModel.ObservableCollection<AiBlock>? _aiBlocks;

    private readonly AudioPlayer _audio = new();

    public EditorView(Session session)
    {
        InitializeComponent();
        _session = session;
        _session.GameChanged += OnGameChanged;
        _session.GameFilesChanged += ReloadFromDisk;

        // The button has to turn back into a play button when the clip ends by itself.
        _audio.Changed += RefreshPlayButton;
        PreviewVideo.MediaEnded += (_, _) => { _videoPlaying = false; RefreshPlayButton(); };

        GameArtPane.Attach(session);
        GameArtPane.Status += SetStatus;
        GameArtPane.OverridesChanged += RefreshOverrides;

        CampaignPane.Attach(session);
        CampaignPane.Status += SetStatus;

        // Undoing a campaign change from another tab has to show the change, and only the
        // editor knows how to get to the campaign lens.
        CampaignPane.Reveal = () => { CampaignLens.IsChecked = true; OnLensChanged(this, new RoutedEventArgs()); };
        CampaignPane.OverridesChanged += RefreshOverrides;

        IconsPane.Attach(session);
        IconsPane.Status += SetStatus;
        IconsPane.OverridesChanged += RefreshOverrides;

        SpritesPane.Attach(session);
        SpritesPane.Status += SetStatus;
        SpritesPane.OverridesChanged += RefreshOverrides;
    }

    private Window? Owner => Window.GetWindow(this);

    private bool _initialised;

    /// <summary>
    /// Called by the shell rather than from Loaded: this view starts inside a collapsed
    /// host, and relying on the load event to populate it is a race not worth taking.
    /// </summary>
    public void EnsureInitialised()
    {
        if (_initialised) return;
        _initialised = true;

        // Nothing is opened for you. The editor used to reopen whatever was last open, which
        // meant it started already editing something — and the quickest way to damage a mod
        // is to change it while believing you are looking at another one.
        StartPane.OpenRequested += path => OpenRecent(path);
        StartPane.NewRequested += NewProject;
        StartPane.BrowseRequested += OpenProject;
        StartPane.ForgetRequested += Forget;

        RebuildTree();
        RefreshProjectHeader();
        ShowStartPage();
    }

    private void OnGameChanged()
    {
        if (!_initialised) return;
        RebuildTree();
        RefreshProjectHeader();
    }

    /// <summary>True when a project is open, which is what the File menu greys out on.</summary>
    public bool HasProject => _project is not null;

    // Entry points for the window's menu bar. The handlers below keep their event
    // signatures because the in-view buttons still use them.
    public void NewProject() => OnNewProject(this, new RoutedEventArgs());
    /// <summary>The project that is open, or null. The recent list leaves it out.</summary>
    public string? ProjectPath => _project?.ProjectPath;

    public void OpenProject() => OnOpenProject(this, new RoutedEventArgs());
    public void SaveProject() => OnSaveProject(this, new RoutedEventArgs());
    public void SaveProjectAs() => OnSaveProjectAs(this, new RoutedEventArgs());
    public void CloseProject() => OnCloseProject(this, new RoutedEventArgs());
    public void Build() => OnBuild(this, new RoutedEventArgs());
    public void TestInGame() => OnTestInGame(this, new RoutedEventArgs());
    public void OpenProjectFolder() => OnOpenProjectFolder(this, new RoutedEventArgs());

    // ---- project --------------------------------------------------------

    private void OnNewProject(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "New mod project",
            Filter = "Mod project (*.w2proj)|*.w2proj",
            FileName = "MyMod" + ModProject.Extension,
            AddExtension = true,
            DefaultExt = ModProject.Extension,
        };
        if (dialog.ShowDialog(Owner) != true) return;

        try
        {
            SaveProjectQuietly();
            _project = ModProject.Create(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName));
            if (_session.Game is not null)
            {
                _project.LastGameRoot = _session.Game.Root;
                _project.Save();
            }
            RememberProject();
            ShowProject();
            SetStatus($"Created {Path.GetFileName(dialog.FileName)}. Its files live in {_project.ContentRoot}.");
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "create the project", ex);
        }
    }

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open mod project",
            Filter = "Mod project (*.w2proj)|*.w2proj|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Owner) == true) TryOpenProject(dialog.FileName, quiet: false);
    }

    private void TryOpenProject(string path, bool quiet)
    {
        try
        {
            SaveProjectQuietly();
            _project = ModProject.Load(path);

            // A project remembers the install it was authored against, which saves
            // re-picking the folder when a project moves between machines.
            if (_session.Game is null && !string.IsNullOrWhiteSpace(_project.LastGameRoot))
                _session.TryUseGame(_project.LastGameRoot, out _);

            RememberProject();
            ShowProject();
            SetStatus($"Opened {Path.GetFileName(path)}. {_project.EnumerateOverrides().Count} override(s).");
        }
        catch (Exception ex)
        {
            if (!quiet) Ui.Failed(Owner, "open the project", ex);
            ShowProject();
        }
    }

    /// <summary>
    /// Saves the project file and any edit sitting in the preview pane. Project metadata
    /// is already written as you type, so this exists mostly to flush the editors and to
    /// give the menu the Save entry people expect to find.
    /// </summary>
    private void OnSaveProject(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        if (!SavePendingEdit()) return;

        try
        {
            _project.Save();
            SetStatus($"Saved {Path.GetFileName(_project.ProjectPath)}.");
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "save the project", ex);
        }
    }

    /// <summary>
    /// Writes the project, and a copy of everything in its content folder, to a new
    /// location and continues working there — so it branches a mod rather than moving it.
    /// </summary>
    private void OnSaveProjectAs(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        if (!SavePendingEdit()) return;

        var dialog = new SaveFileDialog
        {
            Title = "Save project as",
            Filter = "Mod project (*.w2proj)|*.w2proj",
            FileName = Path.GetFileName(_project.ProjectPath),
            AddExtension = true,
            DefaultExt = ModProject.Extension,
            InitialDirectory = _project.ProjectDirectory,
        };
        if (dialog.ShowDialog(Owner) != true) return;

        if (string.Equals(Path.GetFullPath(dialog.FileName), _project.ProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            OnSaveProject(sender, e);
            return;
        }

        try
        {
            _project.Save();

            var source = _project;
            var copy = ModProject.Create(dialog.FileName, source.Name);
            copy.Id = source.Id;
            copy.Version = source.Version;
            copy.Author = source.Author;
            copy.Description = source.Description;
            copy.Website = source.Website;
            copy.LastGameRoot = source.LastGameRoot;
            copy.Save();

            foreach (var relativePath in source.EnumerateOverrides())
                copy.ImportOverride(relativePath, source.ResolveContentPath(relativePath));

            _project = copy;
            RememberProject();
            ShowProject();
            SetStatus($"Saved a copy as {Path.GetFileName(copy.ProjectPath)}. You are now editing that one.");
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "save a copy", ex);
        }
    }

    private void OnCloseProject(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        if (!SavePendingEdit()) return;

        SaveProjectQuietly();
        _project = null;
        _selected = null;

        _session.Settings.LastProject = "";
        _session.Settings.Save();

        ShowProject();
        SetStatus("Project closed.");
    }

    private void OnOpenProjectFolder(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_project.ProjectDirectory}\"") { UseShellExecute = true });
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;

        // Handled in code rather than through InputBindings so the shortcuts still work
        // while focus is inside one of the editors.
        switch (e.Key)
        {
            case System.Windows.Input.Key.N when ctrl: OnNewProject(sender, e); break;
            case System.Windows.Input.Key.O when ctrl: OnOpenProject(sender, e); break;
            case System.Windows.Input.Key.S when ctrl: OnSaveProject(sender, e); break;
            case System.Windows.Input.Key.B when ctrl: OnBuild(sender, e); break;
            case System.Windows.Input.Key.F5: OnTestInGame(sender, e); break;
            case System.Windows.Input.Key.Escape when JsonFilterBox.IsKeyboardFocusWithin
                                                      && JsonFilterBox.Text.Length > 0:
                OnJsonFilterClear(sender, e);
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void RememberProject()
    {
        if (_project is not null)
        {
            _session.Settings.LastProject = _project.ProjectPath;
            _session.Settings.RememberRecent(_project.ProjectPath);
        }

        _session.Settings.Save();
    }

    /// <summary>
    /// Opens a project from the recent list.
    ///
    /// A path that no longer resolves is dropped rather than left to fail again — the file
    /// has been moved or deleted, and offering it a second time is offering a dead end.
    /// </summary>
    public void OpenRecent(string path)
    {
        if (!File.Exists(path))
        {
            _session.Settings.RecentProjects.RemoveAll(
                p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _session.Settings.Save();

            Ui.Error(Owner, "That project is not there any more",
                $"{path}\n\nIt has been removed from the recent list.");
            return;
        }

        TryOpenProject(path, quiet: false);
    }

    private void SaveProjectQuietly()
    {
        // Whatever is being typed belongs to the project that is still open, so it has to
        // be written before another one takes its place.
        if (_project is not null) SavePendingEdit(refresh: false);

        try { _project?.Save(); } catch { /* never block on a preference write */ }
    }

    /// <summary>
    /// Points the whole editor at the project that was just opened, created or copied.
    ///
    /// Setting <c>_project</c> is not enough: the tree marks, the overrides list, the unit
    /// and upgrade tables, the AI editor, the campaign pane and the file in the preview all
    /// belong to the project they were loaded from, and without this a new mod opened
    /// showing the previous one's work.
    /// </summary>
    private void ShowProject()
    {
        ShowLaunchTargets();
        ShowClans();

        // The history described the project that was open; it says nothing about this one,
        // and an undo that reached across projects would write into the wrong files.
        Undo.Clear();
        _session.FileUndo.Clear();

        // Opening one puts the tabs up; closing one puts the start page back.
        ShowStartPage();

        _selected = null;
        _datStrings = null;
        _icons = null;
        _sounds = null;
        _datChanged.Clear();
        _datRenamed.Clear();
        _datNameDirty = false;

        RebuildTree();
        RefreshProjectHeader();
        IconsPane.Refresh(_project);
        SpritesPane.Refresh(_project);

        // A lens that is about one file re-opens it from the new project; the rest go back
        // to nothing selected.
        if (LensFile is { } path) OpenLensFile(path);
        else ShowSelected();
    }

    private void RefreshProjectHeader()
    {
        _loadingUi = true;

        // The campaign lens works on the same project, so it follows it around.
        CampaignPane.Refresh(_project);
        GameArtPane.Refresh(_project);

        if (_project is null)
        {
            ProjectTitle.Text = "No project open";
            ProjectSubtitle.Text = _session.Game is null
                ? "Set your game folder in Settings, then create a project."
                : "Create or open a project to start replacing files.";
            MetaPanel.IsEnabled = false;
            SetProjectCommandsEnabled(false);
            NameBox.Text = IdBox.Text = VersionBox.Text = AuthorBox.Text = DescriptionBox.Text = "";
        }
        else
        {
            ProjectTitle.Text = string.IsNullOrWhiteSpace(_project.Name) ? "(unnamed mod)" : _project.Name;
            ProjectSubtitle.Text = _project.ProjectPath;
            MetaPanel.IsEnabled = true;
            SetProjectCommandsEnabled(true);

            NameBox.Text = _project.Name;
            IdBox.Text = _project.Id;
            VersionBox.Text = _project.Version;
            AuthorBox.Text = _project.Author;
            DescriptionBox.Text = _project.Description;
        }

        _loadingUi = false;
        RefreshOverrides();
        RefreshActionButtons();
    }

    /// <summary>Everything that needs an open project, greyed out together.</summary>
    private void SetProjectCommandsEnabled(bool enabled)
    {
        // Testing additionally needs somewhere to install to. The File menu enables its
        // own items when it opens, so only the button is set here.
        TestButton.IsEnabled = enabled && _session.Installer is not null;
    }

    private void OnMetaChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingUi || _project is null) return;

        _project.Name = NameBox.Text;
        _project.Id = IdBox.Text.Trim();
        _project.Version = VersionBox.Text.Trim();
        _project.Author = AuthorBox.Text;
        _project.Description = DescriptionBox.Text;
        _project.Save();

        ProjectTitle.Text = string.IsNullOrWhiteSpace(_project.Name) ? "(unnamed mod)" : _project.Name;
    }

    // ---- tree -----------------------------------------------------------

    /// <summary>
    /// Guards against the TreeView raising SelectedItemChanged(null) when its ItemsSource
    /// is replaced. Without this, rebuilding the tree silently clears the selection and
    /// the preview goes blank instead of refreshing.
    /// </summary>
    private bool _rebuildingTree;

    /// <summary>
    /// Set when the tree's contents have moved on but the tree is not on screen to care.
    ///
    /// Every lens switch used to rebuild all 1,952 nodes even when the lens hides the tree,
    /// which is most of them — the AI, unit, campaign and sprite lenses all fold it away.
    /// The work was real and the result was never looked at.
    /// </summary>
    private bool _treeStale;

    private void RebuildTree()
    {
        // Nothing to see, so nothing to build; ShowTree catches up when it comes back.
        if (TreeCard is { Visibility: not Visibility.Visible })
        {
            _treeStale = true;
            return;
        }

        _treeStale = false;

        using var _perf = RuneFoundry.UI.Perf.Time("RebuildTree");
        if (_session.Game is null)
        {
            GameTree.ItemsSource = null;
            _root = null;
            return;
        }

        var expanded = _root?.SelfAndDescendants()
            .Where(n => n.IsFolder && n.IsExpanded)
            .Select(n => n.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var extras = _project?.EnumerateOverrides() ?? (IReadOnlyList<string>)Array.Empty<string>();

        _rebuildingTree = true;
        try
        {
            _root = TreeNode.Build(_session.Game, extras);
            if (_project is not null) _root.RefreshOverrideMarks(_project, _session.Game);

            if (expanded is not null)
                foreach (var node in _root.SelfAndDescendants().Where(n => n.IsFolder))
                    node.IsExpanded = expanded.Contains(node.RelativePath);

            var filter = FilterBox.Text.Trim();
            if (filter.Length > 0) _root.ApplyFilter(filter);

            GameTree.ItemsSource = _root.VisibleChildren;
        }
        finally
        {
            _rebuildingTree = false;
        }
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_root is null) return;

        _rebuildingTree = true;
        try
        {
            _root.ApplyFilter(FilterBox.Text.Trim());
            GameTree.ItemsSource = null;
            GameTree.ItemsSource = _root.VisibleChildren;
        }
        finally
        {
            _rebuildingTree = false;
        }
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_rebuildingTree) return;

        // The tree raises this when its container is realised, which can be after code
        // has already selected the node and shown it. Loading the same file twice is
        // wasted work; for ai.bin it built the whole lens twice.
        var node = e.NewValue as TreeNode;
        if (ReferenceEquals(node, _selected)) return;

        _selected = node;
        ShowSelected();
    }

    private TreeNode? FindNode(string? relativePath)
        => relativePath is null || _root is null
            ? null
            : _root.SelfAndDescendants().FirstOrDefault(
                n => !n.IsFolder && n.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Re-points the selection at a path in the current tree, opening the way down to it.</summary>
    private void SelectPath(string? relativePath)
    {
        var node = FindNode(relativePath);
        _selected = node;
        if (node is null) return;

        for (var parent = node.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
        node.IsSelected = true;
    }

    private void OnOverrideSelected(object sender, SelectionChangedEventArgs e)
    {
        if (OverrideList.SelectedItem is not OverrideRow row) return;
        SelectPath(row.Path);
        ShowSelected();
    }

    // ---- preview --------------------------------------------------------

    private void OnReload(object sender, RoutedEventArgs e)
    {
        ReloadFromDisk();
        SetStatus("Reloaded from disk.");
    }

    /// <summary>
    /// Re-reads everything from disk, keeping the selection. Used by the Reload button and
    /// whenever the library applies or restores — a restore puts the stock bytes back and
    /// can delete files a mod had added, so the tree itself may be different afterwards.
    /// </summary>
    private void ReloadFromDisk()
    {
        if (!_initialised || _session.Game is null) return;

        var path = _selected?.RelativePath;
        RebuildTree();
        SelectPath(path);
        RefreshOverrides();
        ShowSelected();
    }

    private void ShowSelected()
    {
        using var _perf = RuneFoundry.UI.Perf.Time("ShowSelected");
        // Commit before tearing down: this used to drop unsaved edits silently.
        _autoSave?.Stop();
        AutoSave();

        StopMedia();

        _textDirty = false;
        _asset = null;
        _jsonTable = null;
        _previewSource = null;

        PreviewImage.Source = null;
        JsonRows.ItemsSource = null;
        PreviewVideo.Visibility = Visibility.Collapsed;
        OpenExternallyButton.Visibility = Visibility.Collapsed;
        EditMapButton.Visibility = Visibility.Collapsed;
        AiPanel.Visibility = Visibility.Collapsed;
        DatPanel.Visibility = Visibility.Collapsed;
        _datTable = null;
        _aiFile = null;
        _aiScript = null;
        StopFrames();
        FrameBar.Visibility = Visibility.Collapsed;
        TextScroller.Visibility = Visibility.Collapsed;
        JsonPanel.Visibility = Visibility.Collapsed;
        SaveTextButton.Visibility = Visibility.Collapsed;
        PlaySoundButton.Visibility = Visibility.Collapsed;
        StopSoundButton.Visibility = Visibility.Collapsed;
        ExportPngButton.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Visibility = Visibility.Visible;

        if (_selected is null || _selected.IsFolder || _session.Game is null)
        {
            PreviewPath.Text = _selected?.IsFolder == true ? _selected.RelativePath + "/" : "Nothing selected";
            PreviewInfo.Text = "";
            PreviewPlaceholder.Text = _selected?.IsFolder == true
                ? $"{_selected.DescendantFiles().Count()} files in this folder."
                : "Select a file in the tree.";
            RefreshActionButtons();
            return;
        }

        var relativePath = _selected.RelativePath;
        PreviewPath.Text = relativePath;

        var overridden = _project is not null && _project.HasOverride(relativePath);
        var gamePath = _session.Game.ResolveDataPath(relativePath);

        // A file this mod does not include is shown as the game shipped it, which is not
        // the same as what is in the game folder while another mod is applied.
        _previewSource = overridden
            ? _project!.ResolveContentPath(relativePath)
            : _session.StockFile(relativePath) ?? gamePath;

        if (!File.Exists(_previewSource))
        {
            PreviewInfo.Text = "File is missing from disk.";
            PreviewPlaceholder.Text = "Missing.";
            RefreshActionButtons();
            return;
        }

        var (info, image) = PreviewRenderer.Describe(_previewSource, _session.Game, relativePath);
        _asset = info;

        ShowModBadge(overridden, File.Exists(gamePath));
        var detail = new List<string> { info.TypeName, info.Summary, Ui.FormatBytes(info.SizeBytes) };
        if (info.Warning is not null) detail.Add("⚠ " + info.Warning);
        PreviewInfo.Text = string.Join(" · ", detail.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (info.Kind is AssetKind.Wave or AssetKind.Video)
        {
            PlaySoundButton.Visibility = Visibility.Visible;
            StopSoundButton.Visibility = Visibility.Visible;
            StopSoundButton.IsEnabled = false;
            RefreshPlayButton();
        }
        if (info.Kind == AssetKind.Video) OpenExternallyButton.Visibility = Visibility.Visible;

        // Everything is editable as long as there is a mod to edit into. Editing a stock
        // file is taken as the request to include it: the first keystroke copies it in
        // (see AdoptIntoMod), which is less to explain than a pane of dead controls and a
        // button you have to find first.
        var editable = _project is not null;

        if (info.Kind is AssetKind.UnitData or AssetKind.UpgradeData)
        {
            ShowDat(_previewSource, info.Kind, editable);
        }
        else if (info.Kind == AssetKind.AiScripts)
        {
            ShowAi(_previewSource, editable);
        }
        else if (info.Kind == AssetKind.Json)
        {
            _ = ShowJsonAsync(_previewSource, editable);
        }
        else if (info.Kind == AssetKind.Tbl && info.Strings is not null)
        {
            ShowEditableText(string.Join(Environment.NewLine, info.Strings),
                $"{info.Strings.Count} string(s), one per line", editable);
        }
        else if (info.Kind == AssetKind.Text)
        {
            ShowEditableText(File.ReadAllText(_previewSource), "one file, edited as text", editable);
        }
        else if (info.Kind == AssetKind.Wave)
        {
            PreviewPlaceholder.Text = $"Sound file, {info.Summary}.\nUse ▶ Play below to listen.";
        }
        else if (info.Kind == AssetKind.Video)
        {
            ShowVideo(_previewSource);
        }
        else if (image is not null)
        {
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewImage.Source = image;
            ExportPngButton.Visibility = Visibility.Visible;

            if (info.FrameCount > 1)
            {
                FrameBar.Visibility = Visibility.Visible;
                _loadingUi = true;
                FrameSlider.Maximum = info.FrameCount - 1;
                FrameSlider.Value = 0;
                _loadingUi = false;
                FrameLabel.Text = $"1 / {info.FrameCount}";
            }
        }
        else
        {
            PreviewPlaceholder.Text = "RuneFoundry has no preview for this format.";
        }

        RefreshActionButtons();
    }

    private void ShowEditableText(string text, string hint, bool editable)
    {
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        TextScroller.Visibility = Visibility.Visible;

        // Saving is only offered for a file the mod actually owns; otherwise the content
        // is shown for reference and the way to start editing is Copy original into mod.
        SaveTextButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        SaveTextButton.IsEnabled = false;

        TextEditor.IsReadOnly = !editable;

        _loadingUi = true;
        TextEditor.Text = text;
        _loadingUi = false;
        _textDirty = false;

        PreviewInfo.Text += " · " + (editable ? hint : ReadOnlyHint);
    }

    private const string ReadOnlyHint =
        "read-only. Open or create a mod project to edit";

    /// <summary>
    /// Says whether the file on screen belongs to the mod. Three states worth telling
    /// apart: the mod replaces the game's copy, the mod adds a file the game does not
    /// ship, or this is the game's own file and nothing has touched it.
    /// </summary>
    private void ShowModBadge(bool overridden, bool inGame)
    {
        if (ModBadge is null) return;

        ModBadge.Visibility = Visibility.Visible;
        ModBadgeText.Text = !overridden ? "Game file" : inGame ? "In this mod" : "Added by this mod";

        var brush = (System.Windows.Media.Brush)FindResource(overridden ? "Accent" : "TextDim");
        ModBadgeText.Foreground = brush;
        ModBadge.BorderBrush = brush;
    }

    /// <summary>
    /// Shows a JSON file as an aligned, editable table.
    ///
    /// Parsing happens off the UI thread: the biggest atlas the game ships is 45,552
    /// nodes, and building that on the dispatcher stalled the window. The table itself
    /// only ever hands the list a screenful of rows.
    /// </summary>
    private async Task ShowJsonAsync(string path, bool editable)
    {
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "Reading JSON…";

        JsonTable table;
        try
        {
            var json = await Task.Run(() => File.ReadAllText(path));
            table = await Task.Run(() => JsonTable.Parse(json, readOnly: !editable));
        }
        catch (Exception ex)
        {
            // Malformed JSON still deserves a look, so fall back to raw text.
            try
            {
                ShowEditableText(File.ReadAllText(path),
                    "could not parse as JSON (" + ex.Message + "), shown as text", editable);
            }
            catch
            {
                PreviewPlaceholder.Text = "Could not read this file: " + ex.Message;
            }
            return;
        }

        // The user may have clicked something else while we were parsing.
        if (!string.Equals(_previewSource, path, StringComparison.OrdinalIgnoreCase)) return;

        _jsonTable = table;
        table.Edited += MarkEdited;

        _loadingUi = true;
        JsonFilterBox.Text = "";
        JsonFilterHint.Visibility = Visibility.Visible;
        _loadingUi = false;

        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        JsonPanel.Visibility = Visibility.Visible;
        JsonRows.ItemsSource = table.Rows;

        SaveTextButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        SaveTextButton.IsEnabled = false;

        PreviewInfo.Text += $" · {table.NodeCount} values · " +
            (editable ? "edit in place, then Save changes" : ReadOnlyHint);
    }

    private void RefreshJsonRows()
    {
        if (_jsonTable is null) return;
        JsonRows.ItemsSource = null;
        JsonRows.ItemsSource = _jsonTable.Rows;
    }

    private void OnJsonToggle(object sender, RoutedEventArgs e)
    {
        if (_jsonTable is null || (sender as FrameworkElement)?.DataContext is not JsonTreeNode node) return;
        _jsonTable.Toggle(node);
        RefreshJsonRows();
    }

    /// <summary>
    /// Re-filtering on every keystroke means walking the whole document each time, which
    /// on a 45,000-value atlas is felt. The work is deferred until typing pauses.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _filterDebounce;

    private void OnJsonFilterChanged(object sender, TextChangedEventArgs e)
    {
        var empty = JsonFilterBox.Text.Length == 0;
        JsonFilterHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        JsonFilterClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        if (_loadingUi || _jsonTable is null) return;

        _filterDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _filterDebounce.Tick -= ApplyJsonFilter;
        _filterDebounce.Tick += ApplyJsonFilter;

        // Restarting the timer is what makes it fire once, after the last keystroke.
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void ApplyJsonFilter(object? sender, EventArgs e)
    {
        _filterDebounce?.Stop();
        if (_jsonTable is null) return;

        _jsonTable.Filter = JsonFilterBox.Text.Trim();
        RefreshJsonRows();
    }

    private void OnJsonFilterClear(object sender, RoutedEventArgs e)
    {
        JsonFilterBox.Clear();
        _filterDebounce?.Stop();

        if (_jsonTable is null) return;
        _jsonTable.Filter = "";
        RefreshJsonRows();
        JsonFilterBox.Focus();
    }

    private void OnJsonExpandAll(object sender, RoutedEventArgs e)
    {
        if (_jsonTable is null) return;

        // Opening everything on a large file is a lot of rows even virtualized, so it asks.
        if (_jsonTable.NodeCount > 5000 && !Ui.Confirm(Owner, "Expand everything?",
                $"This file has {_jsonTable.NodeCount} values. Expanding all of them makes scrolling slow.\n\n" +
                "Filtering is faster. Expand anyway?"))
            return;

        _jsonTable.ExpandAll();
        RefreshJsonRows();
    }

    private void OnJsonCollapseAll(object sender, RoutedEventArgs e)
    {
        if (_jsonTable is null) return;
        _jsonTable.CollapseAll();
        RefreshJsonRows();
    }

    /// <summary>
    /// Shows a .dat table: records on the left, one control per field on the right.
    ///
    /// Nothing here knows what a hit point is. The schema says each field's kind, and the
    /// row model turns that into a spinner, checkbox, dropdown or bank of checkboxes — so
    /// a field added to the schema gets an editor without touching this view.
    /// </summary>
    /// <summary>
    /// The language file as it stands: this mod's copy if it has one, else the game's.
    /// Only the names are wanted here, so a missing or unreadable file is not an error —
    /// the built-in names cover it.
    /// </summary>
    private GameStrings? LoadEditorStrings()
    {
        if (_session.Game is null) return null;

        var path = _project is not null && _project.HasOverride(StringsPath)
            ? _project.ResolveContentPath(StringsPath)
            : _session.StockFile(StringsPath) ?? _session.Game.ResolveDataPath(StringsPath);

        try
        {
            return File.Exists(path) ? GameStrings.Load(path) : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- whole waves ------------------------------------------------------

    /// <summary>
    /// The rows of one section, as a range. Sections are contiguous by construction — the
    /// labels are assigned in a single pass down the list — so a start and a count is all
    /// there is to find.
    /// </summary>
    private (int Start, int Count) SectionRange(string name)
    {
        if (_aiBlocks is null) return (-1, 0);

        var start = -1;
        var count = 0;

        for (var i = 0; i < _aiBlocks.Count; i++)
        {
            if (_aiBlocks[i].Section != name)
            {
                if (start >= 0) break;
                continue;
            }
            if (start < 0) start = i;
            count++;
        }
        return (start, count);
    }

    /// <summary>The section a group-header button belongs to.</summary>
    private static string? SectionOf(object sender)
        => (sender as FrameworkElement)?.DataContext
            is System.Windows.Data.CollectionViewGroup group
            ? group.Name as string
            : null;

    /// <summary>
    /// The chevron sits inside the header button, and a Button swallows the click, so the
    /// header's own toggle never sees it. Flipping it here keeps both routes working.
    /// </summary>
    private void OnSectionToggle(object sender, RoutedEventArgs e)
    {
        for (DependencyObject? at = sender as DependencyObject; at is not null;
             at = System.Windows.Media.VisualTreeHelper.GetParent(at))
        {
            if (at is not System.Windows.Controls.Primitives.ToggleButton head) continue;
            head.IsChecked = head.IsChecked != true;
            return;
        }
    }

    private void OnWaveMoveUp(object sender, RoutedEventArgs e) => MoveSection(SectionOf(sender), -1);
    private void OnWaveMoveDown(object sender, RoutedEventArgs e) => MoveSection(SectionOf(sender), 1);

    /// <summary>
    /// Swaps a wave with its neighbour. The wave numbers are not stored anywhere — they
    /// are worked out from the order — so the two simply trade names afterwards.
    /// </summary>
    private void MoveSection(string? name, int direction)
    {
        if (_aiBlocks is null || name is null) return;

        var (start, count) = SectionRange(name);
        if (start < 0) return;

        if (direction < 0)
        {
            if (start == 0) return;
            var (previousStart, _) = SectionRange(_aiBlocks[start - 1].Section);
            if (previousStart < 0) return;
            MoveRange(start, count, previousStart);
        }
        else
        {
            var after = start + count;
            if (after >= _aiBlocks.Count) return;
            var (_, nextCount) = SectionRange(_aiBlocks[after].Section);
            // The next section slides up into this one's place as these rows come out.
            MoveRange(start, count, start + nextCount);
        }

        MarkAiEdited();
        RegroupAiBlocks();
    }

    private void MoveRange(int start, int count, int to)
    {
        if (_aiBlocks is null) return;

        var moving = new List<AiBlock>(count);
        for (var i = 0; i < count; i++) moving.Add(_aiBlocks[start + i]);
        for (var i = 0; i < count; i++) _aiBlocks.RemoveAt(start);
        for (var i = 0; i < count; i++) _aiBlocks.Insert(to + i, moving[i]);
    }

    private void OnWaveDuplicate(object sender, RoutedEventArgs e)
    {
        var name = SectionOf(sender);
        if (_aiBlocks is null || name is null) return;

        var (start, count) = SectionRange(name);
        if (start < 0) return;

        for (var i = 0; i < count; i++)
        {
            var copy = AiBlock.From(_aiBlocks[start + i].ToInstruction());
            copy.BuildListLookup = LookUpBuildItem;
            copy.BuildListChoices = BuildChoices;
            copy.Advise = AdviseAiRow;
            copy.Caution = CautionAiRow;
            copy.PropertyChanged += OnAiBlockEdited;
            _aiBlocks.Insert(start + count + i, copy);
        }

        MarkAiEdited();
        RegroupAiBlocks();
        SetStatus($"Duplicated {name}.");
    }

    private void OnWaveDelete(object sender, RoutedEventArgs e)
    {
        var name = SectionOf(sender);
        if (_aiBlocks is null || name is null) return;

        var (start, count) = SectionRange(name);
        if (start < 0) return;

        if (!Ui.Confirm(Owner, "Delete this wave",
                $"Remove all {count} instructions in {name}?"))
            return;

        for (var i = 0; i < count; i++) _aiBlocks.RemoveAt(start);

        MarkAiEdited();
        RegroupAiBlocks();
        SetStatus($"Deleted {name}.");
    }

    private void OnAiInsertSetup(object sender, RoutedEventArgs e)
    {
        if (_aiBlocks is null) return;

        var at = 0;
        foreach (var instruction in AiTemplates.StandardSetup())
        {
            var block = AiBlock.From(instruction);
            block.BuildListLookup = LookUpBuildItem;
            block.BuildListChoices = BuildChoices;
            block.PropertyChanged += OnAiBlockEdited;
            _aiBlocks.Insert(at++, block);
        }

        MarkAiEdited();
        RegroupAiBlocks();
        SetStatus("Inserted the standard 26-variable setup block.");
    }

    private bool ShowingSource => AiSourceMode.IsChecked == true;

    /// <summary>
    /// Switching views commits whichever one was showing, so the two never drift apart.
    /// Blocks are the source of truth while they are up, and vice versa.
    /// </summary>
    private void OnAiModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || _aiFile is null || _aiScript is null || AiBlockList is null) return;

        if (ShowingSource)
        {
            // Leaving blocks: fold them into the script, then render the text.
            if (!CommitAiBlocks()) { _loadingUi = true; AiBlocksMode.IsChecked = true; _loadingUi = false; return; }

            _loadingUi = true;
            AiEditor.Text = AiFile.ToText(_aiScript);
            _loadingUi = false;
        }
        else
        {
            // Leaving source: parse it, then rebuild the blocks from what it produced.
            if (!CommitAiText()) { _loadingUi = true; AiSourceMode.IsChecked = true; _loadingUi = false; return; }
            ShowAiScript(_aiScript);
        }

        AiBlockList.Visibility = ShowingSource ? Visibility.Collapsed : Visibility.Visible;
        AiEditor.Visibility = ShowingSource ? Visibility.Visible : Visibility.Collapsed;
        AiBlockButtons.Visibility = ShowingSource ? Visibility.Collapsed : Visibility.Visible;

        AiHint.Text = ShowingSource
            ? "var / goto / sleep / wait, one per line. ; starts a comment."
            : "Each row is one instruction. Add builds on the end; select a row to move or delete it.";
    }

    private int SelectedBlockIndex => AiBlockList.SelectedIndex;

    /// <summary>
    /// The row a row-button belongs to. Taken from the button's DataContext rather than
    /// the list selection, so clicking an action on a row you have not selected still
    /// acts on that row.
    /// </summary>
    private int IndexOf(object sender)
        => _aiBlocks is null || (sender as FrameworkElement)?.DataContext is not AiBlock block
            ? -1
            : _aiBlocks.IndexOf(block);

    private void OnRowMoveUp(object sender, RoutedEventArgs e)
    {
        var at = IndexOf(sender);
        if (Protected(at, "moved")) return;

        // Moving the row above the setup block would push a setup row down out of it.
        if (at > 0 && Protected(at - 1, "moved")) return;

        MoveRow(at, -1);
    }

    private void OnRowMoveDown(object sender, RoutedEventArgs e)
    {
        var at = IndexOf(sender);
        if (Protected(at, "moved")) return;

        MoveRow(at, 1);
    }

    /// <summary>
    /// Whether a row belongs to the setup block, which is not to be moved or removed.
    ///
    /// The block is 26 variable assignments that 78 of the game's own 84 scripts open with,
    /// and a script that does not set its variables inherits whatever the script before it
    /// left in them. Reordering it is worse than deleting it: the rows still look right and
    /// the values arrive in the wrong order.
    ///
    /// Rows below it are yours. The editor says no here rather than greying the buttons out,
    /// because a button that is there and does nothing on some rows reads as broken.
    /// </summary>
    private bool Protected(int at, string what)
    {
        if (_aiBlocks is null || at < 0 || at >= _aiBlocks.Count) return false;
        if (_aiBlocks[at].Section != "SETUP") return false;

        SetStatus($"The setup block cannot be {what}. Rows below it are yours to arrange.");
        return true;
    }

    private void MoveRow(int from, int delta)
    {
        if (_aiBlocks is null || from < 0) return;

        var to = from + delta;
        if (to < 0 || to >= _aiBlocks.Count) return;

        _aiBlocks.Move(from, to);
        AiBlockList.SelectedIndex = to;
        MarkAiEdited();
        RegroupAiBlocks();
    }

    private void OnRowDuplicate(object sender, RoutedEventArgs e)
    {
        var at = IndexOf(sender);
        if (_aiBlocks is null || at < 0) return;

        var copy = AiBlock.From(_aiBlocks[at].ToInstruction());
        copy.BuildListLookup = LookUpBuildItem;
        copy.BuildListChoices = BuildChoices;
        copy.PropertyChanged += OnAiBlockEdited;

        _aiBlocks.Insert(at + 1, copy);
        AiBlockList.SelectedIndex = at + 1;
        MarkAiEdited();
        RegroupAiBlocks();
    }

    private void OnRowInsertAfter(object sender, RoutedEventArgs e)
    {
        var at = IndexOf(sender);
        if (_aiBlocks is null || at < 0) return;

        var block = AiBlock.NewVar();
        block.BuildListLookup = LookUpBuildItem;
        block.BuildListChoices = BuildChoices;
        block.PropertyChanged += OnAiBlockEdited;

        _aiBlocks.Insert(at + 1, block);
        AiBlockList.SelectedIndex = at + 1;
        MarkAiEdited();
        RegroupAiBlocks();
    }

    private void OnRowDelete(object sender, RoutedEventArgs e)
    {
        var at = IndexOf(sender);
        if (_aiBlocks is null || at < 0) return;
        if (Protected(at, "removed")) return;

        _aiBlocks.RemoveAt(at);
        AiBlockList.SelectedIndex = Math.Min(at, _aiBlocks.Count - 1);
        MarkAiEdited();
        RegroupAiBlocks();
    }

    /// <summary>
    /// How many bytes this script has left before it stops fitting.
    ///
    /// Measured to the trailer rather than to the end of the slot. Thirty of the game's
    /// scripts keep a build list or a rate table right behind their code, addressed by
    /// absolute offset, and every one of them ends exactly where that data starts — so the
    /// slot looks roomy and there is not one byte to spare. Counting to the end of the slot
    /// offered that space, and the save then refused the edit.
    /// </summary>
    private int AiRoomLeft =>
        _aiScript is null || _aiBlocks is null
            ? 0
            : _aiScript.CodeCapacity - AiScript.HeaderLength - _aiBlocks.Sum(b => b.Length);

    /// <summary>
    /// Adds an attack wave, asked for in words rather than assembled by hand.
    ///
    /// The wave goes after the last one rather than after the cursor. A script runs top to
    /// bottom and loops, so waves are a sequence and a new one belongs at the end of it;
    /// dropping it wherever the selection happened to be put it inside another wave as
    /// often as not.
    /// </summary>
    /// <summary>
    /// Greys the wave button when the slot has no room for one.
    ///
    /// Offering an action that cannot succeed and then explaining why is worse than not
    /// offering it: the tooltip says how much room is left either way.
    /// </summary>
    private void ShowAiRoom()
    {
        if (AiAddWaveButton is null) return;

        var left = AiRoomLeft;
        var needed = AiTemplates.AttackWave(AiTemplates.AttackDomain.Land).Sum(i => i.Length);

        if (AiBudgetDetail is not null && _aiScript is not null)
        {
            var room = _aiScript.CodeCapacity - AiScript.HeaderLength;
            var used = room - left;

            AiBudgetDetail.Text =
                $"{used} of {room} bytes used, {left} free. "
                + (_aiScript.Trailer.Length > 0
                    ? "The rest of this slot holds data other scripts point at, so the code stops here. "
                    : "")
                + "This script can only use its own room; clearing another does not help it.";
        }


        AiAddWaveButton.IsEnabled = left >= needed;
        AiAddWaveButton.ToolTip = left >= needed
            ? $"Add an attack wave. {left} bytes left in this slot."
            : $"No room for a wave: {left} bytes left and one needs {needed}.";
    }

    private void OnAiAddWave(object sender, RoutedEventArgs e)
    {
        if (_aiBlocks is null || _aiScript is null) return;

        // Unit counts are standing targets, not orders: a script that already asks for
        // Footmen keeps getting them, so a later wave only has to gather and attack. The
        // wizard opens with nothing to build once something has already been asked for.
        var alreadyBuilds = _aiBlocks
            .Skip(_aiBlocks.Count(b => b.Section == "SETUP"))
            .Any(b => b.Opcode == (byte)AiOpcode.Var && AiText.IsUnitCountVariable(b.Target));

        if (WaveWizard.Ask(Owner, AiRoomLeft, buildsAlready: alreadyBuilds) is not { } wave) return;

        // After the last wave, and before the goto that loops back, which is the only thing
        // that should stay at the bottom.
        var at = _aiBlocks.Count;
        while (at > 0 && _aiBlocks[at - 1].Opcode == (byte)AiOpcode.Goto) at--;

        var first = at;

        foreach (var instruction in wave)
        {
            var block = AiBlock.From(instruction);
            block.BuildListLookup = LookUpBuildItem;
            block.BuildListChoices = BuildChoices;
            block.PropertyChanged += OnAiBlockEdited;
            _aiBlocks.Insert(at++, block);
        }

        AiBlockList.SelectedIndex = first;
        MarkAiEdited();
        RegroupAiBlocks();

        SetStatus($"Added an attack wave. {AiRoomLeft} bytes left in this slot.");
    }

    /// <summary>
    /// Empties a slot and puts the setup block back, ready for a script of your own.
    ///
    /// This is the way to write an AI script here. Every slot sits at a position the header
    /// and the maps point at, so a script can only ever use its own room and no slot can
    /// borrow from another. Taking a roomy slot and clearing it is what gives you room, and
    /// the slot keeps its number, so whatever referred to it still does.
    ///
    /// The setup block stays because a script that never sets its variables inherits
    /// whatever the last one left, and 78 of the game's own 84 scripts open with it. It is
    /// 26 instructions of the 26 the slot may hold plenty more of, and it is easier to
    /// delete rows than to remember which ones the convention wanted.
    /// </summary>
    private void OnAiUseSlot(object sender, RoutedEventArgs e)
    {
        if (_aiBlocks is null || _aiScript is null) return;

        var room = _aiScript.CodeCapacity - AiScript.HeaderLength;
        var setup = AiTemplates.StandardSetup();
        var setupBytes = setup.Sum(i => i.Length);

        if (!Ui.Confirm(Owner, "Clear and use this script",
                $"Empty \"{_aiScript.Name}\" and start one of your own?\n\n"
                + $"It has {room} bytes. The setup block takes {setupBytes}. "
                + $"{room - setupBytes} bytes are left to write in.\n\n"
                + "This script keeps its number. Anything that called the game's AI now "
                + "calls yours. What was here is discarded."))
            return;

        _aiBlocks.Clear();

        foreach (var instruction in setup)
        {
            var block = AiBlock.From(instruction);
            block.BuildListLookup = LookUpBuildItem;
            block.BuildListChoices = BuildChoices;
            block.PropertyChanged += OnAiBlockEdited;
            _aiBlocks.Add(block);
        }

        MarkAiEdited();
        RegroupAiBlocks();
        ShowAiRoom();

        SetStatus($"{_aiScript.Name} is yours. {setupBytes} of {room} bytes used.");
    }


    /// <summary>
    /// Puts one script back to the game's own version, leaving the rest of the file alone.
    ///
    /// Resetting the whole of ai.bin throws away work on all eighty-four. This restores the
    /// instructions of the selected script only, from the copy the vault holds, and leaves
    /// every other script as the mod has it.
    /// </summary>
    private void OnAiRevertScript(object sender, RoutedEventArgs e)
    {
        if (_aiBlocks is null || _aiScript is null || _project is null) return;

        AiScript stock;
        try
        {
            stock = AiFile.Load(_session.RequireStock(AiScriptPath)).Scripts[_aiScript.Index];
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "read the game's own copy of this script", ex);
            return;
        }

        if (stock.OriginalBytes.AsSpan().SequenceEqual(_aiScript.OriginalBytes)
            && !_aiScript.IsModified)
        {
            SetStatus($"{_aiScript.Name} already matches the game's own copy.");
            return;
        }

        // Slots that share bytes are one script wearing two numbers, so putting one back
        // puts the other back with it. Saying so beats a surprise.
        var shared = _aiScript.SharedWith.Count > 0
            ? "\n\nThis script shares its bytes with "
              + string.Join(", ", _aiScript.SharedWith.Select(AiTables.AiName))
              + ", which go back with it."
            : "";

        if (!Ui.Confirm(Owner, "Undo changes to this script",
                $"Put \"{_aiScript.Name}\" back to the game's own version?\n\n"
                + $"Every other script in the file keeps your changes.{shared}"))
            return;

        _aiBlocks.Clear();

        foreach (var instruction in stock.Instructions)
        {
            var block = AiBlock.From(instruction);
            block.BuildListLookup = LookUpBuildItem;
            block.BuildListChoices = BuildChoices;
            block.Advise = AdviseAiRow;
            block.Caution = CautionAiRow;
            block.PropertyChanged += OnAiBlockEdited;
            _aiBlocks.Add(block);
        }

        MarkAiEdited();
        RegroupAiBlocks();
        ShowAiRoom();

        SetStatus($"{_aiScript.Name} is back to the game's own version. "
                  + "Save to write it into the mod.");
    }

    /// <summary>
    /// Copies the script to the clipboard, in the form the source view shows.
    ///
    /// A script had no way out of the editor: it could be built a row at a time and that
    /// was all. As text it can be kept beside the mod, posted, or pasted into a roomier
    /// slot — and what is copied is what is on screen, not what was last saved.
    /// </summary>
    private void OnAiCopyScript(object sender, RoutedEventArgs e)
    {
        if (_aiScript is null) return;
        if (!CommitAiScript()) return;

        try
        {
            Clipboard.SetText(AiFile.ToText(_aiScript));

            SetStatus($"Copied {_aiScript.Name} to the clipboard: "
                      + $"{_aiScript.Instructions.Count} instructions, {_aiScript.CodeLength - AiScript.HeaderLength} bytes.");
        }
        catch (Exception ex)
        {
            // Another program can hold the clipboard open, and the failure is theirs.
            Ui.Failed(Owner, "copy that script", ex);
        }
    }

    /// <summary>
    /// Replaces the script with one pasted in as text.
    ///
    /// The window checks what is pasted as it is typed: that it assembles, and that it fits
    /// this slot. Both are the checks the save makes, brought forward to where they can
    /// still be acted on.
    /// </summary>
    private void OnAiPasteScript(object sender, RoutedEventArgs e)
    {
        if (_aiBlocks is null || _aiScript is null) return;
        if (!CommitAiScript()) return;

        // A slot two numbers share is one script, and replacing it replaces both.
        if (_aiScript.SharedWith.Count > 0
            && !Ui.Confirm(Owner, "Replace this script",
                $"\"{_aiScript.Name}\" shares its bytes with "
                + string.Join(", ", _aiScript.SharedWith.Select(AiTables.AiName))
                + ", which are the same script wearing another number.\n\n"
                + "Replacing it replaces them too. Carry on?"))
            return;

        var pasted = ScriptText.Ask(Owner, _aiScript.Name,
            _aiScript.CodeCapacity - AiScript.HeaderLength, AiFile.ToText(_aiScript));

        if (pasted is null) return;

        _aiBlocks.Clear();
        foreach (var instruction in pasted) _aiBlocks.Add(NewAiBlock(instruction));

        MarkAiEdited();
        RegroupAiBlocks();
        ShowAiRoom();

        SetStatus($"{_aiScript.Name} replaced: {pasted.Count} instructions, "
                  + $"{AiRoomLeft} bytes left. Save to write it into the mod.");
    }

    /// <summary>
    /// Lays ai.bin out again, so a script can grow past the room it was given.
    ///
    /// Every script sits where the header and the maps point at it, so ordinarily nothing
    /// moves and nothing can grow. This is the way out of that: the file is packed again
    /// from the scripts themselves and every pointer is rewritten. The result is read back
    /// and compared instruction by instruction before it is offered.
    ///
    /// Behind a button and a warning rather than done quietly at save time. Every script
    /// moves, so a mod that runs any AI is a thing to play through again.
    /// </summary>
    private void OnAiMakeRoom(object sender, RoutedEventArgs e)
    {
        if (_aiFile is null || _project is null || _selected is null) return;
        if (!CommitAiScript()) return;

        var tight = _aiFile.Scripts.Count(s => !s.IsEmpty
                                               && s.CodeCapacity - s.CodeLength < AiRoomWarning);

        if (!Ui.Confirm(Owner, "Make room in ai.bin",
                "Lay this file out again so its scripts can grow?\n\n"
                + $"{tight} of the 84 scripts have almost no room left. A rebuild packs them "
                + "again and rewrites every pointer, which gives each one space to grow.\n\n"
                + "Every script moves. Play through any mission that runs an AI before you "
                + "publish. Slots that share bytes are given a copy each, so a change to one "
                + "stops changing the other."))
            return;

        try
        {
            var bytes = RebuildAiFile();

            // Read back before writing: what the rebuild says it wrote has to be what the
            // file actually holds.
            var after = AiFile.Parse(bytes);
            var mismatch = Compare(_aiFile, after);

            if (mismatch is not null)
            {
                Ui.Error(Owner, "The rebuild does not match", mismatch);
                _aiRebuilt = false;
                return;
            }

            var target = _project.ResolveContentPath(_selected.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            var path = _selected.RelativePath;
            Write(path, "AI scripts", () => File.WriteAllBytes(target, bytes));

            ReloadAiFrom(bytes);
            ShowAiRoom();
            AfterOverrideChanged(path, $"Made room in {path}. Every script moved.");
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "make room in that file", ex);
        }
    }

    /// <summary>
    /// Asks whether to lay the file out again, when a save has just failed for want of room.
    ///
    /// Asked rather than done. The rebuild moves every script, and a mod that runs an AI
    /// wants a play-through afterwards; doing that silently inside a save would spend the
    /// author's confidence without telling them.
    /// </summary>
    private bool OfferToMakeRoom(string why) =>
        Ui.Confirm(Owner, "This script has outgrown its slot",
            why + "\n\nRuneFoundry can lay the file out again so scripts can grow. "
            + "Every script moves, so play through any mission that runs an AI before you "
            + "publish. The game's own copy is kept, so Reset still puts it all back.\n\n"
            + "Make room and save?");

    /// <summary>How little room counts as "almost none", for the warning's count.</summary>
    private const int AiRoomWarning = 6;

    /// <summary>
    /// Checks a rebuilt file against the one it came from, script by script and instruction
    /// by instruction. Returns what differs, or null when nothing does.
    /// </summary>
    private static string? Compare(AiFile before, AiFile after)
    {
        if (before.Scripts.Count != after.Scripts.Count)
            return $"{before.Scripts.Count} scripts went in and {after.Scripts.Count} came out.";

        for (var i = 0; i < before.Scripts.Count; i++)
        {
            var was = before.Scripts[i];
            var now = after.Scripts[i];

            if (was.Instructions.Count != now.Instructions.Count)
                return $"{was.Name} had {was.Instructions.Count} instructions and now has "
                       + $"{now.Instructions.Count}.";

            for (var j = 0; j < was.Instructions.Count; j++)
            {
                if (was.Instructions[j].Opcode == now.Instructions[j].Opcode
                    && was.Instructions[j].Operands.AsSpan()
                        .SequenceEqual(now.Instructions[j].Operands))
                    continue;

                // A goto is the one thing that legitimately changes: it points at a byte,
                // and the bytes have moved.
                if (was.Instructions[j].Opcode == (byte)AiOpcode.Goto
                    && now.Instructions[j].Opcode == (byte)AiOpcode.Goto)
                    continue;

                return $"{was.Name} instruction {j + 1} changed.";
            }
        }

        return null;
    }

    /// <summary>A row for one instruction, wired to everything a row needs to know.</summary>
    private AiBlock NewAiBlock(AiInstruction instruction)
    {
        var block = AiBlock.From(instruction);

        block.BuildListLookup = LookUpBuildItem;
        block.BuildListChoices = BuildChoices;
        block.Advise = AdviseAiRow;
        block.Caution = CautionAiRow;
        block.PropertyChanged += OnAiBlockEdited;

        return block;
    }

    private void OnAiAddBlock(object sender, RoutedEventArgs e)
    {
        if (_aiBlocks is null) return;

        var block = AiBlock.NewVar();
        block.BuildListLookup = LookUpBuildItem;
        block.BuildListChoices = BuildChoices;
        block.PropertyChanged += OnAiBlockEdited;

        var at = SelectedBlockIndex >= 0 ? SelectedBlockIndex + 1 : _aiBlocks.Count;
        _aiBlocks.Insert(at, block);
        AiBlockList.SelectedIndex = at;
        MarkAiEdited();
        RegroupAiBlocks();
    }

    /// <summary>
    /// Marks the project dirty and redraws the script list.
    ///
    /// The redraw is what keeps the byte budget honest. A script can only use its own room,
    /// so the number that matters while editing is how much of it is left, and a figure that
    /// only updated when the list was rebuilt would be telling you about the script as it
    /// was when you opened it.
    /// </summary>
    private void MarkAiEdited()
    {
        MarkEdited();
        AiScriptList.Items.Refresh();
        ShowAiRoom();
    }

    /// <summary>Folds the block rows back into the script. False if a row cannot be written.</summary>
    private bool CommitAiBlocks()
    {
        if (_aiFile is null || _aiScript is null || _aiBlocks is null) return true;

        var instructions = new List<AiInstruction>(_aiBlocks.Count);
        for (var i = 0; i < _aiBlocks.Count; i++)
        {
            try
            {
                instructions.Add(_aiBlocks[i].ToInstruction());
            }
            catch (Exception ex)
            {
                Ui.Error(Owner, "That block cannot be saved", $"Row {i + 1}: {ex.Message}");
                AiBlockList.SelectedIndex = i;
                return false;
            }
        }

        _aiScript.Instructions.Clear();
        _aiScript.Instructions.AddRange(instructions);
        _aiScript.IsModified = true;
        RefreshAiList();
        return true;
    }

    private void OnAiScriptSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || AiScriptList.SelectedItem is not AiRow row) return;

        // Keep whatever was typed before moving on; losing it silently would be worse.
        if (!CommitAiScript()) return;

        RememberAiScript(row.Script.Index);
        ShowAiScript(row.Script);
    }

    /// <summary>Notes which script is open, so the next visit starts there.</summary>
    private void RememberAiScript(int index)
    {
        if (_session.Settings.LastAiScript == index) return;

        _session.Settings.LastAiScript = index;
        _session.Settings.Save();
    }

    private void OnAiTextEdited(object sender, TextChangedEventArgs e)
    {
        if (_loadingUi) return;
        MarkEdited();
    }

    /// <summary>
    /// Commits whichever view is showing into the selected script. Returns false, having
    /// said why, if the edit cannot be written — so a broken script never reaches the file.
    /// </summary>
    private bool CommitAiScript()
    {
        if (_aiFile is null || _aiScript is null || !_textDirty) return true;
        return ShowingSource ? CommitAiText() : CommitAiBlocks();
    }

    private bool CommitAiText()
    {
        if (_aiFile is null || _aiScript is null) return true;

        try
        {
            var instructions = AiFile.FromText(AiEditor.Text);
            _aiScript.Instructions.Clear();
            _aiScript.Instructions.AddRange(instructions);
            _aiScript.IsModified = true;
            RefreshAiList();
            return true;
        }
        catch (Exception ex)
        {
            Ui.Error(Owner, "That AI script does not compile", ex.Message);
            return false;
        }
    }

    private void RefreshAiList()
    {
        if (_aiFile is null) return;

        var selected = AiScriptList.SelectedIndex;
        _loadingUi = true;
        AiScriptList.ItemsSource = BuildAiRows();
        AiScriptList.SelectedIndex = selected;
        _loadingUi = false;
    }

    private void OnTextEdited(object sender, TextChangedEventArgs e)
    {
        if (_loadingUi) return;
        MarkEdited();
    }

    /// <summary>Records an edit and restarts the auto-save countdown.</summary>
    private void MarkEdited()
    {
        _textDirty = true;
        SaveTextButton.IsEnabled = _project is not null;
        AdoptIntoMod();
        ScheduleAutoSave();
    }

    /// <summary>
    /// Copies the game's file into the mod the moment someone edits it.
    ///
    /// The save that follows writes the whole file from what is on screen, so the copy is
    /// not needed for the content. It is here so the file shows as the mod's straight
    /// away — in the tree, in the overrides list and in the badge above the preview —
    /// rather than only once the first save lands.
    ///
    /// Deliberately does not re-show the file: reloading the preview mid-keystroke would
    /// throw away what is being typed.
    /// </summary>
    private void AdoptIntoMod()
    {
        // A value set while a file is being loaded into the editors is not an edit.
        if (_loadingUi || _project is null || _session.Game is null || _selected is null) return;

        var relativePath = _selected.RelativePath;
        if (_project.HasOverride(relativePath)) return;

        try
        {
            _project.SeedFromGame(relativePath, _session.Game, _session.Vault);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not add {relativePath} to the mod: {ex.Message}");
            return;
        }

        _previewSource = _project.ResolveContentPath(relativePath);
        _root?.RefreshOverrideMarks(_project, _session.Game);
        RefreshOverrides();
        ShowModBadge(true, true);
        RefreshActionButtons();
        SetStatus($"Added {relativePath} to the mod.");
    }

    private void ScheduleAutoSave()
    {
        if (_project is null) return;

        _autoSave ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900),
        };
        _autoSave.Tick -= OnAutoSaveTick;
        _autoSave.Tick += OnAutoSaveTick;

        _autoSave.Stop();
        _autoSave.Start();
    }

    private void OnAutoSaveTick(object? sender, EventArgs e)
    {
        _autoSave?.Stop();
        AutoSave();
    }

    /// <summary>
    /// Writes a pending edit without the full tree refresh a manual save does. Editing is
    /// only possible on a file the mod already owns, so no override appears or disappears
    /// here and the tree cannot go stale.
    /// </summary>
    private void AutoSave()
    {
        if (_saving || !_textDirty || _project is null) return;

        _saving = true;
        try
        {
            SavePendingEdit(refresh: false);
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>
    /// Steps through a sprite's frames. Ten a second is roughly the rate the game animates
    /// at, and it wraps rather than stopping at the end: a walk cycle only reads as one
    /// when it repeats.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _frameTimer;

    private bool FramesPlaying => _frameTimer?.IsEnabled == true;

    private void OnFramePlay(object sender, RoutedEventArgs e)
    {
        if (FramesPlaying)
        {
            StopFrames();
            return;
        }

        if (_asset?.RenderFrame is null || _asset.FrameCount < 2) return;

        _frameTimer ??= new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };

        _frameTimer.Tick -= OnFrameTick;
        _frameTimer.Tick += OnFrameTick;
        _frameTimer.Start();
        RefreshFramePlayButton();
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        if (_asset?.RenderFrame is null || _asset.FrameCount < 2)
        {
            StopFrames();
            return;
        }

        var next = (int)Math.Round(FrameSlider.Value) + 1;
        FrameSlider.Value = next > FrameSlider.Maximum ? 0 : next;
    }

    private void StopFrames()
    {
        _frameTimer?.Stop();
        RefreshFramePlayButton();
    }

    private void RefreshFramePlayButton()
    {
        if (FramePlayButton is null) return;

        var playing = FramesPlaying;
        FramePlayLabel.Text = playing ? "Stop" : "Play";
        FramePlayIcon.Data = (System.Windows.Media.Geometry)FindResource(
            playing ? "IconSquare" : "IconPlay");
    }

    /// <summary>
    /// Sprites are indexed images the game draws from a palette; a PNG of a frame is the
    /// only form other tools can open. What is on screen is what gets written, palette and
    /// transparency included.
    /// </summary>
    private void OnExportPng(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is not System.Windows.Media.Imaging.BitmapSource frame) return;

        var stem = Path.GetFileNameWithoutExtension(_previewSource) ?? "frame";
        var number = _asset is { FrameCount: > 1 } ? $"_{(int)Math.Round(FrameSlider.Value):000}" : "";

        var dialog = new SaveFileDialog
        {
            Title = "Export PNG",
            Filter = "PNG image (*.png)|*.png",
            FileName = stem + number + ".png",
            DefaultExt = ".png",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            WritePng(frame, dialog.FileName);
            SetStatus("Exported " + Path.GetFileName(dialog.FileName) + ".");
        }
        catch (Exception ex)
        {
            SetStatus("Could not write the PNG: " + ex.Message);
        }
    }

    private void OnExportAllFrames(object sender, RoutedEventArgs e)
    {
        if (_asset?.RenderFrame is null || _asset.FrameCount < 1) return;

        var stem = Path.GetFileNameWithoutExtension(_previewSource) ?? "frame";

        var dialog = new OpenFolderDialog
        {
            Title = $"Export {_asset.FrameCount} frames of {stem}",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        StopFrames();

        var written = 0;
        try
        {
            for (var i = 0; i < _asset.FrameCount; i++)
            {
                var image = PreviewRenderer.Render(_asset, i);
                if (image is null) continue;

                WritePng(image, Path.Combine(dialog.FolderName, $"{stem}_{i:000}.png"));
                written++;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Stopped after {written} frame(s): {ex.Message}");
            return;
        }

        SetStatus($"Exported {written} frame(s) to {dialog.FolderName}.");
    }

    private static void WritePng(System.Windows.Media.Imaging.BitmapSource image, string path)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));

        using var file = File.Create(path);
        encoder.Save(file);
    }

    private void OnFrameChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingUi || _asset?.RenderFrame is null) return;

        var frame = (int)Math.Round(e.NewValue);
        PreviewImage.Source = PreviewRenderer.Render(_asset, frame);
        FrameLabel.Text = $"{frame + 1} / {_asset.FrameCount}";
    }

    private void RefreshActionButtons()
    {
        var haveFile = _selected is { IsFolder: false } && _session.Game is not null;
        var haveProject = _project is not null;
        var overridden = haveFile && haveProject && _project!.HasOverride(_selected!.RelativePath);
        var inGame = haveFile && File.Exists(_session.Game!.ResolveDataPath(_selected!.RelativePath));

        SeedButton.IsEnabled = haveFile && haveProject && inGame && !overridden;
        ReplaceButton.IsEnabled = haveFile && haveProject;
        RemoveOverrideButton.IsEnabled = overridden;
        RemoveOverrideButton.Content = ResetLabel(overridden, inGame);

        ShowMapEditorButton();
        ShowInExplorerButton.IsEnabled = _previewSource is not null;
    }

    // ---- context menu ----------------------------------------------------

    /// <summary>
    /// A TreeView does not select on right-click, so without this the menu would act on
    /// whatever was selected before rather than the row under the pointer.
    /// </summary>
    private void OnTreeRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not TreeViewItem)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);

        if (source is TreeViewItem item) item.IsSelected = true;
    }

    /// <summary>
    /// What can be done to whatever is selected. Shared by the buttons and both context
    /// menus so they cannot drift into disagreeing about it.
    /// </summary>
    private (bool HaveFile, bool HaveProject, bool Overridden, bool InGame) FileState()
    {
        var haveFile = _selected is { IsFolder: false } && _session.Game is not null;
        var haveProject = _project is not null;
        var overridden = haveFile && haveProject && _project!.HasOverride(_selected!.RelativePath);
        var inGame = haveFile && File.Exists(_session.Game!.ResolveDataPath(_selected!.RelativePath));
        return (haveFile, haveProject, overridden, inGame);
    }

    /// <summary>
    /// "Reset" only means something when the mod replaced a file that already existed;
    /// for a file the mod added, it is a removal.
    /// </summary>
    private static string ResetLabel(bool overridden, bool inGame)
        => overridden && !inGame ? "Remove from mod" : "Reset to game default";

    /// <summary>
    /// A ListBox does not select on right-click either, so without this the menu would act
    /// on whatever was selected before rather than the row under the pointer.
    /// </summary>
    private void OnOverrideRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not ListBoxItem)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);

        if (source is ListBoxItem item) item.IsSelected = true;
    }

    private void OnOverrideMenuOpening(object sender, RoutedEventArgs e)
    {
        // Selecting a row already put the tree and the preview on that file, so the
        // handlers behind these items are the same ones the buttons use.
        var (haveFile, haveProject, overridden, inGame) = FileState();

        OvrReplace.IsEnabled = haveFile && haveProject;
        OvrReset.IsEnabled = overridden;
        OvrReload.IsEnabled = haveFile;
        OvrExplorer.IsEnabled = _previewSource is not null;
        OvrCopyPath.IsEnabled = haveFile;

        OvrReset.Header = ResetLabel(overridden, inGame);
    }

    private void OnFileMenuOpening(object sender, RoutedEventArgs e)
    {
        var (haveFile, haveProject, overridden, inGame) = FileState();

        CtxSeed.IsEnabled = haveFile && haveProject && inGame && !overridden;
        CtxReplace.IsEnabled = haveFile && haveProject;
        CtxReset.IsEnabled = overridden;
        CtxReload.IsEnabled = haveFile;
        CtxExplorer.IsEnabled = _previewSource is not null;
        CtxOpen.IsEnabled = _previewSource is not null;
        CtxCopyPath.IsEnabled = haveFile;

        CtxReset.Header = ResetLabel(overridden, inGame);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            Clipboard.SetText(_selected.RelativePath);
            SetStatus($"Copied {_selected.RelativePath}");
        }
        catch (Exception ex)
        {
            SetStatus("Could not copy to the clipboard: " + ex.Message);
        }
    }

    // ---- file actions ---------------------------------------------------

    private void OnSeedOriginal(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _project is null || _session.Game is null) return;

        try
        {
            _project.SeedFromGame(_selected.RelativePath, _session.Game, _session.Vault);
            AfterOverrideChanged(_selected.RelativePath,
                $"Copied {_selected.RelativePath} into the mod. Edit it in {_project.ContentRoot}.");
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "copy that file", ex);
        }
    }

    private void OnReplaceFile(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _project is null) return;

        var extension = Path.GetExtension(_selected.Name);
        var dialog = new OpenFileDialog
        {
            Title = $"Choose the file to use as {_selected.Name}",
            Filter = extension.Length > 1
                ? $"Same type (*{extension})|*{extension}|All files (*.*)|*.*"
                : "All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Owner) != true) return;

        Import(dialog.FileName);
    }

    private void Import(string sourceFile)
    {
        if (_selected is null || _project is null) return;

        var relativePath = _selected.RelativePath;
        try
        {
            WarnOnFormatMismatch(sourceFile, relativePath);

            _session.FileUndo.Record(_project, $"replace {Path.GetFileName(relativePath)}",
                new[] { relativePath },
                () => _project.ImportOverride(relativePath, sourceFile),
                refresh: () => AfterOverrideChanged(relativePath, $"{relativePath} put back."));

            AfterOverrideChanged(relativePath,
                $"{relativePath} will be replaced by {Path.GetFileName(sourceFile)}.");
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "replace that file", ex);
        }
    }

    /// <summary>
    /// The game reads these files by format, not by extension, so a PNG dropped in where a
    /// GRP belongs loads as garbage. Warn, but let the author proceed — they may know
    /// something we do not.
    /// </summary>
    private void WarnOnFormatMismatch(string sourceFile, string relativePath)
    {
        if (_session.Game is null) return;

        var gamePath = _session.Game.ResolveDataPath(relativePath);
        if (!File.Exists(gamePath)) return;

        try
        {
            var incoming = AssetInspector.Inspect(sourceFile);
            var existing = AssetInspector.Inspect(gamePath, _session.Game, relativePath);
            if (incoming.Kind == existing.Kind || incoming.Kind == AssetKind.Binary) return;

            Ui.Info(Owner, "Format looks different",
                "RuneFoundry added the file. Convert it to the original format if the game "
                + "misbehaves.");
        }
        catch
        {
            // A failed sniff is not a reason to block the import.
        }
    }

    private void OnSaveText(object sender, RoutedEventArgs e) => SavePendingEdit();

    // ---- lenses ----------------------------------------------------------

    private bool ShowingCampaign => CampaignLens?.IsChecked == true;
    private bool ShowingIcons => IconsLens?.IsChecked == true;
    private bool ShowingSprites => SpritesLens?.IsChecked == true;
    private bool ShowingAi => AiLens?.IsChecked == true;
    private bool ShowingDetails => DetailsLens?.IsChecked == true;
    private bool ShowingUnits => UnitsLens?.IsChecked == true;
    private bool ShowingUpgrades => UpgradesLens?.IsChecked == true;

    /// <summary>The lenses that are the file pane pointed at one file, with the tree folded away.</summary>
    private bool ShowingOneFile => ShowingAi || ShowingUnits || ShowingUpgrades;

    private string? LensFile =>
        ShowingAi ? AiScriptPath :
        ShowingUnits ? UnitDataPath :
        ShowingUpgrades ? UpgradeDataPath : null;

    /// <summary>
    /// Switches between the four ways of looking at the same mod.
    ///
    /// Files is the folder tree. Campaign and Mod details are their own panes. AI scripts
    /// is the file pane with the tree folded away and ai.bin already open — the script list
    /// inside the AI editor is a better sidebar for that job than a tree holding one file,
    /// and the file is still reachable the ordinary way from Files.
    /// </summary>
    private void OnLensChanged(object sender, RoutedEventArgs e)
    {
        using var _perf = RuneFoundry.UI.Perf.Time("OnLensChanged");
        // Fires while the XAML is still being built, before the panes exist.
        if (FilesPane is null || CampaignPane is null || DetailsPane is null) return;

        // Leaving any lens means whatever was being typed is written down first.
        SavePendingEdit(refresh: false);
        if (!ShowingCampaign) CampaignPane.Leave();

        CampaignPane.Visibility = ShowingCampaign ? Visibility.Visible : Visibility.Collapsed;
        IconsPane.Visibility = ShowingIcons ? Visibility.Visible : Visibility.Collapsed;
        SpritesPane.Visibility = ShowingSprites ? Visibility.Visible : Visibility.Collapsed;
        DetailsPane.Visibility = ShowingDetails ? Visibility.Visible : Visibility.Collapsed;
        FilesPane.Visibility = ShowingCampaign || ShowingIcons || ShowingSprites || ShowingDetails
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (ShowingCampaign)
        {
            StopMedia();
            CampaignPane.Refresh(_project);
            return;
        }

        if (ShowingIcons)
        {
            StopMedia();
            IconsPane.Refresh(_project);
            return;
        }

        if (ShowingSprites)
        {
            StopMedia();
            SpritesPane.Refresh(_project);
            return;
        }

        // Any of the other lenses may have added or dropped files while it was up. The tree
        // is told first so it knows whether it is worth rebuilding at all.
        RebuildTree();
        ShowTree(!ShowingOneFile);
        RefreshOverrides();

        if (ShowingDetails) return;

        if (LensFile is not { } path) return;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        OpenLensFile(path);
        if (RuneFoundry.UI.Perf.On)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(() => RuneFoundry.UI.Perf.Note(
                    $"{"lens to first frame",-28} {watch.Elapsed.TotalMilliseconds,8:F1} ms")));
    }

    /// <summary>Folds the game tree away, giving its width to whatever is being edited.</summary>
    private void ShowTree(bool show)
    {
        if (TreeCard is null) return;

        var wasHidden = TreeCard.Visibility != Visibility.Visible;

        TreeCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TreeColumn.Width = show ? new GridLength(310) : new GridLength(0);
        TreeColumn.MinWidth = show ? 230 : 0;
        TreeGap.Width = show ? new GridLength(10) : new GridLength(0);

        // The mod's own files belong to the Files tab, alongside the game's. A lens is about
        // one file, so the column folds away with the tree rather than taking width from it.
        OverrideCard.Visibility = TreeCard.Visibility;
        OverrideColumn.Width = show ? new GridLength(330) : new GridLength(0);
        OverrideColumn.MinWidth = show ? 240 : 0;
        OverrideGap.Width = show ? new GridLength(10) : new GridLength(0);

        // Coming back into view after something changed underneath it.
        if (show && wasHidden && _treeStale) RebuildTree();
    }

    /// <summary>
    /// Points the file pane at the one file a lens is about. Each of these lenses is the
    /// ordinary preview with the tree folded away: the editor inside it — the script list,
    /// the unit table — is a better sidebar than a tree holding a single file, and the file
    /// is still reachable the usual way from Files.
    /// </summary>
    private void OpenLensFile(string relativePath)
    {
        if (_session.Game is null)
        {
            SetStatus("Set your game folder in Settings first.");
            return;
        }

        SavePendingEdit(refresh: false);

        _rebuildingTree = true;
        try { SelectPath(relativePath); }
        finally { _rebuildingTree = false; }

        ShowSelected();
    }

    private const string AiScriptPath = "Rez/ai.bin";

    /// <summary>The unit table the game reads. unitdato.dat and _unitdata.dat are older
    /// copies it does not load, so they stay in Files rather than getting a tab.</summary>
    private const string UnitDataPath = "Rez/unitdata.dat";

    private const string UpgradeDataPath = "Rez/upgrades.dat";

    /// <summary>Set by <see cref="EncodeAiFile"/> when the last save laid the file out again.</summary>
    private bool _aiRebuilt;


    /// <summary>
    /// The bytes to write for an ai.bin.
    ///
    /// Written in place, always. Every script sits at a position the header and the maps
    /// point at, so writing in place cannot get one of the file's several hundred pointers
    /// wrong: nothing moves, so nothing can move to the wrong place.
    ///
    /// The cost is that a script cannot grow past its own room, and the way to write a
    /// script of your own is to take a roomy slot and clear it. The list shows every slot's
    /// budget so one can be chosen on purpose.
    ///
    /// Laying the file out again would lift that limit and is implemented, in AiLinker, with
    /// its result read back and checked instruction by instruction. It is not offered. Every
    /// script would move, so every mission that runs any AI is a thing to retest, and the
    /// confidence that buys is smaller than the confidence of never moving anything.
    /// </summary>
    private byte[] EncodeAiFile()
    {
        _aiRebuilt = false;
        return _aiFile!.ToBytes();
    }

    private byte[] RebuildAiFile()
    {
        // Slots that share one script are given their own copy while everything is moving
        // anyway. It costs a few hundred bytes and it is the only way "AI 37" stops
        // silently meaning "AI 37 and AI 76".
        var bytes = AiLinker.Rebuild(_aiFile!, separateAliasedSlots: true, out var report);
        _aiRebuilt = true;
        SetStatus("Rebuilt ai.bin. " + report.Summary + ".");
        return bytes;
    }

    /// <summary>Re-reads the file just written, so the offsets on screen are the real ones.</summary>
    private void ReloadAiFrom(byte[] bytes)
    {
        var slot = AiScriptList.SelectedIndex;

        try
        {
            _aiFile = AiFile.Parse(bytes);
        }
        catch
        {
            // Rebuild verified it before writing; if it still will not read, leave the
            // stale view alone rather than blanking the editor mid-edit.
            return;
        }

        _loadingUi = true;
        AiScriptList.ItemsSource = BuildAiRows();
        AiScriptList.SelectedIndex = slot >= 0 && slot < AiScriptList.Items.Count ? slot : 0;
        _loadingUi = false;

        if (AiScriptList.SelectedItem is AiRow row) ShowAiScript(row.Script);
    }

    /// <summary>
    /// Writes whatever the text or JSON editor is holding into the project.
    /// Returns false only when there was something to save and saving it failed.
    /// </summary>
    private bool SavePendingEdit(bool refresh = true)
    {
        SaveDatName();
        SaveDatText();

        if (_selected is null || _project is null || _asset is null || !_textDirty) return true;

        var relativePath = _selected.RelativePath;
        try
        {
            var target = _project.ResolveContentPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Every kind but one is recorded: a .dat change is already on the stack as the
            // field it changed, which is a better thing to take back than a whole file.
            if (_asset.Kind is AssetKind.UnitData or AssetKind.UpgradeData && _datTable is not null)
            {
                File.WriteAllBytes(target, _datTable.ToBytes());
                MirrorUnitStats(relativePath);
                MirrorStatsIntoMaps(relativePath);
                _datRestored.Clear();
            }
            else if (_asset.Kind == AssetKind.AiScripts && _aiFile is not null)
            {
                if (!CommitAiScript()) return false;

                byte[] bytes;
                try
                {
                    bytes = EncodeAiFile();
                }
                catch (InvalidOperationException tooBig)
                {
                    // The one failure a person can act on: the script outgrew its slot.
                    // Offering the way out here beats making them find it, and it is asked
                    // rather than done, because every script moves.
                    if (!OfferToMakeRoom(tooBig.Message)) return false;

                    bytes = RebuildAiFile();
                    if (Compare(_aiFile, AiFile.Parse(bytes)) is { } wrong)
                    {
                        Ui.Error(Owner, "The rebuild does not match", wrong);
                        return false;
                    }
                }

                Write(relativePath, "AI scripts", () => File.WriteAllBytes(target, bytes));

                // A rebuild moves every script, so the offsets on screen are now wrong.
                if (_aiRebuilt) ReloadAiFrom(bytes);
            }
            else if (_asset.Kind == AssetKind.Json && _jsonTable is not null)
            {
                var json = _jsonTable.ToJsonString();
                Write(relativePath, Path.GetFileName(relativePath),
                    () => File.WriteAllText(target, json, new UTF8Encoding(false)));
            }
            else if (_asset.Kind == AssetKind.Tbl)
            {
                var lines = TextEditor.Text.Replace("\r\n", "\n").Split('\n');
                Write(relativePath, Path.GetFileName(relativePath), () => new TblFile(lines).Save(target));
            }
            else
            {
                var text = TextEditor.Text;
                Write(relativePath, Path.GetFileName(relativePath),
                    () => File.WriteAllText(target, text, new UTF8Encoding(false)));
            }

            _textDirty = false;
            SaveTextButton.IsEnabled = false;

            if (refresh) AfterOverrideChanged(relativePath, $"Saved {relativePath} into the mod.");
            else SetStatus($"Saved {relativePath} at {DateTime.Now:HH:mm:ss}.");
            return true;
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "save that", ex);
            return false;
        }
    }

    /// <summary>
    /// Writes a file the editor owns, and puts the write on the undo stack.
    ///
    /// Undo restores the file as it stood before this save, which for the first save of a
    /// session is the copy taken from the game — so a script edited, saved and thought
    /// better of goes all the way back. The screen is re-read from the file afterwards,
    /// because the model in memory is now the wrong one.
    /// </summary>
    private void Write(string relativePath, string what, Action write)
    {
        if (_project is null) return;

        _session.FileUndo.Record(_project, $"edit {what}", new[] { relativePath }, write,
            refresh: () =>
            {
                _textDirty = false;
                AfterOverrideChanged(relativePath, $"{relativePath} put back.");
            });
    }

    /// <summary>
    /// Tides of Darkness has its own copy of the unit table.
    ///
    /// The game loads <c>rez\unitdato.dat</c> and <c>rez\unitdata.dat</c> at startup and
    /// keeps both, swapping the live one for the shadow copy (the exchange routine at
    /// <c>0x004C4A70</c>) as it moves between the original campaign and the expansion. A
    /// change written to one of them therefore shows up in only half the game, which is
    /// what "my edit did not take" usually means.
    ///
    /// So every change made to one table is written to the other as well, matched by field
    /// rather than by offset — the two files have different layouts, because the expansion
    /// table carries the swamp tileset's frames.
    /// </summary>
    /// <summary>
    /// Fields whose value has to reach the other table and the maps even though it now
    /// matches the game's, because it was just put back. Cleared by the save that uses it.
    /// </summary>
    private readonly List<(string Key, int Record, int Component)> _datRestored = new();

    /// <summary>What the mirrors should write: everything this mod changed, plus anything just reset.</summary>
    private List<(string Key, int Record, int Component, uint Mine, uint Theirs)> MirrorList(DatTable stock)
    {
        var changes = _datTable!.Diff(stock).ToList();

        foreach (var (key, record, component) in _datRestored)
        {
            if (changes.Any(c => c.Key == key && c.Record == record && c.Component == component)) continue;
            if (!_datTable.Has(key)) continue;

            changes.Add((key, record, component,
                _datTable.GetRaw(key, record, component),
                stock.Has(key) ? stock.GetRaw(key, record, component) : 0));
        }

        return changes;
    }

    private void MirrorUnitStats(string relativePath)
    {
        if (_datTable is null || _project is null || _session.Game is null) return;

        var twin = UnitTableTwin(relativePath);
        if (twin is null) return;

        var stockPath = _session.StockFile(relativePath);
        if (!File.Exists(stockPath)) return;

        try
        {
            var changes = MirrorList(UnitDataFile.Load(stockPath));
            if (changes.Count == 0) return;

            if (!_project.HasOverride(twin))
            {
                if (!File.Exists(_session.Game.ResolveDataPath(twin))) return;
                _project.SeedFromGame(twin, _session.Game, _session.Vault);
            }

            var twinPath = _project.ResolveContentPath(twin);
            var other = UnitDataFile.Load(twinPath);

            var applied = 0;
            foreach (var (key, record, component, mine, _) in changes)
            {
                // The original table is a field short here and there; skip what it lacks
                // rather than writing at an offset that means something else.
                if (!other.Has(key) || record >= other.RecordCount) continue;

                other.SetRaw(key, record, mine, component);
                applied++;
            }

            if (applied == 0) return;

            other.Save(twinPath);
            _root?.RefreshOverrideMarks(_project, _session.Game);
            RefreshOverrides();
        }
        catch (Exception ex)
        {
            SetStatus($"Saved, but could not update {twin}: {ex.Message}");
        }
    }

    /// <summary>
    /// Carries unit-stat changes into the maps this mod ships.
    ///
    /// A PUD keeps its own copy of the whole unit table in its UDTA chunk, and for that map
    /// the game reads it instead of rez\unitdata.dat. A campaign mission therefore ignores
    /// a stat change made only to the .dat — the mission plays with whatever the map was
    /// saved with. Every map in the mod gets the same edits, so a change means the same
    /// thing wherever it is played.
    /// </summary>
    private void MirrorStatsIntoMaps(string relativePath)
    {
        if (_datTable is null || _project is null || _session.Game is null) return;
        if (!relativePath.Equals(UnitDataPath, StringComparison.OrdinalIgnoreCase)
            && !relativePath.Equals("Rez/unitdato.dat", StringComparison.OrdinalIgnoreCase)) return;

        var stockPath = _session.StockFile(relativePath);
        if (!File.Exists(stockPath)) return;

        List<(string Key, int Record, int Component, uint Mine, uint Theirs)> changes;
        try
        {
            changes = MirrorList(UnitDataFile.Load(stockPath));
        }
        catch
        {
            return;
        }
        if (changes.Count == 0) return;

        var updated = new List<string>();

        foreach (var mapPath in _project.EnumerateOverrides()
                     .Where(p => p.EndsWith(".pud", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            try
            {
                var file = _project.ResolveContentPath(mapPath);
                var bytes = File.ReadAllBytes(file);

                var table = PudFile.ReadUnitTable(bytes);
                if (table is null) continue;

                var applied = 0;
                foreach (var (key, record, component, mine, _) in changes)
                {
                    if (!table.Has(key) || record >= table.RecordCount) continue;
                    if (table.GetRaw(key, record, component) == mine) continue;

                    table.SetRaw(key, record, mine, component);
                    applied++;
                }

                if (applied == 0) continue;

                File.WriteAllBytes(file, PudFile.WriteUnitTable(bytes, table));
                updated.Add(Path.GetFileName(mapPath));
            }
            catch (Exception ex)
            {
                SetStatus($"Saved, but could not update {mapPath}: {ex.Message}");
            }
        }

        if (updated.Count > 0)
            SetStatus($"Unit stats written into {updated.Count} map(s): {string.Join(", ", updated)}.");
    }

    /// <summary>The other unit table, or null for a file that has no twin.</summary>
    private static string? UnitTableTwin(string relativePath) =>
        relativePath.Equals(UnitDataPath, StringComparison.OrdinalIgnoreCase) ? "Rez/unitdato.dat"
        : relativePath.Equals("Rez/unitdato.dat", StringComparison.OrdinalIgnoreCase) ? UnitDataPath
        : null;

    private void OnRemoveOverride(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _project is null) return;

        var relativePath = _selected.RelativePath;
        var inGame = _session.Game is not null
                     && File.Exists(_session.Game.ResolveDataPath(relativePath));

        if (!Ui.ConfirmReset(Owner, relativePath,
                inGame
                    ? "The game's own file is used instead."
                    : "The game does not ship this file, so nothing replaces it."))
            return;

        try
        {
            _session.FileUndo.Record(_project, $"reset {Path.GetFileName(relativePath)}",
                new[] { relativePath },
                () => _project.RemoveOverride(relativePath),
                refresh: () => AfterOverrideChanged(relativePath, $"{relativePath} put back."));

            AfterOverrideChanged(relativePath, $"Removed {relativePath} from the mod.");
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "reset that file", ex);
        }
    }

    /// <summary>
    /// Everything that changes an override funnels through here: refresh the tree, put the
    /// selection back on the same path, and re-read the preview from disk. Taking the path
    /// as an argument rather than trusting _selected is what makes this survive a rebuild.
    /// </summary>
    private void AfterOverrideChanged(string relativePath, string status)
    {
        if (_project is null || _session.Game is null || _root is null) return;

        var stillExists = _project.HasOverride(relativePath)
                          || File.Exists(_session.Game.ResolveDataPath(relativePath));
        var known = FindNode(relativePath) is not null;

        // A brand-new file needs a node; a removed added-file needs its node gone.
        if (!known || !stillExists) RebuildTree();
        else _root.RefreshOverrideMarks(_project, _session.Game);

        RefreshOverrides();
        SelectPath(relativePath);
        ShowSelected();
        SetStatus(status);
    }

    private void OnShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (_previewSource is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_previewSource}\"") { UseShellExecute = true });
    }

    // ---- sound ----------------------------------------------------------

    /// <summary>
    /// Points the player at a video. WebM playback leans on whatever codecs Windows has —
    /// the VP9/WebM extensions are not present on every machine — so a failure is caught
    /// and turned into an offer to open the file in whatever app the system does use,
    /// rather than a blank rectangle.
    /// </summary>
    private void ShowVideo(string path)
    {
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        PreviewVideo.Visibility = Visibility.Visible;

        try
        {
            PreviewVideo.Source = new Uri(path);
            PreviewVideo.Position = TimeSpan.Zero;
            PreviewVideo.Play();
            _videoPlaying = true;
            StopSoundButton.IsEnabled = true;
            RefreshPlayButton();
        }
        catch (Exception ex)
        {
            ShowVideoFailure(ex.Message);
        }
    }

    private void ShowVideoFailure(string reason)
    {
        PreviewVideo.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text =
            "Windows cannot decode this video here (" + reason + ").\n" +
            "Use “Open in player” to watch it outside the studio.";
        StopSoundButton.IsEnabled = false;
    }

    private void OnVideoOpened(object sender, RoutedEventArgs e)
    {
        // Only now is the duration known, so the summary is completed here.
        if (PreviewVideo.NaturalDuration.HasTimeSpan)
        {
            var duration = PreviewVideo.NaturalDuration.TimeSpan;
            PreviewInfo.Text += $" · {PreviewVideo.NaturalVideoWidth}x{PreviewVideo.NaturalVideoHeight}" +
                                $", {duration.TotalSeconds:0.#}s";
        }
    }

    private void OnVideoEnded(object sender, RoutedEventArgs e)
    {
        PreviewVideo.Stop();
        StopSoundButton.IsEnabled = false;
    }

    private void OnVideoFailed(object sender, ExceptionRoutedEventArgs e)
        => ShowVideoFailure(e.ErrorException?.Message ?? "no decoder");

    /// <summary>
    /// Play, pause, resume — the same button for all three, because that is what a play
    /// button does everywhere else.
    /// </summary>
    private void OnPlayMedia(object sender, RoutedEventArgs e)
    {
        if (_previewSource is null) return;

        if (_asset?.Kind == AssetKind.Video)
        {
            if (_videoPlaying)
            {
                PreviewVideo.Stop();
                _videoPlaying = false;
                SetStatus($"Stopped {Path.GetFileName(_previewSource)}.");
            }
            else
            {
                PreviewVideo.Play();
                _videoPlaying = true;
                SetStatus($"Playing {Path.GetFileName(_previewSource)}.");
            }

            StopSoundButton.IsEnabled = true;
            RefreshPlayButton();
            return;
        }

        var playing = _audio.IsPlaying_(_previewSource);
        var error = _audio.Toggle(_previewSource);

        if (error is not null) { SetStatus("Could not play that sound: " + error); return; }

        StopSoundButton.IsEnabled = true;
        SetStatus(playing
            ? $"Stopped {Path.GetFileName(_previewSource)}."
            : $"Playing {Path.GetFileName(_previewSource)}.");
    }

    private bool _videoPlaying;

    /// <summary>Keeps the button showing what pressing it will do next.</summary>
    private void RefreshPlayButton()
    {
        if (PlayIcon is null || PlayLabel is null) return;

        var playing = _asset?.Kind == AssetKind.Video
            ? _videoPlaying
            : _previewSource is not null && _audio.IsPlaying_(_previewSource);

        PlayIcon.Data = (System.Windows.Media.Geometry)FindResource(playing ? "IconSquare" : "IconPlay");
        PlayLabel.Text = playing ? "Stop" : "Play";
    }

    private void OnStopMedia(object sender, RoutedEventArgs e)
    {
        StopMedia();
        SetStatus("Stopped.");
    }

    private void OnOpenExternally(object sender, RoutedEventArgs e)
    {
        if (_previewSource is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_previewSource) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Could not open that file: " + ex.Message);
        }
    }

    /// <summary>Stops whatever is playing, sound or video.</summary>
    private void StopMedia()
    {
        StopFrames();
        _unitAudio.Stop();
        StopSound();
        _videoPlaying = false;

        try
        {
            PreviewVideo.Stop();
        }
        catch
        {
            // Stopping a player that never opened anything is not worth reporting.
        }

        if (StopSoundButton is not null) StopSoundButton.IsEnabled = false;
    }

    private void StopSound() => _audio.Stop();

    // ---- overrides list -------------------------------------------------

    private void RefreshOverrides()
    {
        using var _perf = RuneFoundry.UI.Perf.Time("RefreshOverrides");
        if (_project is null)
        {
            OverrideList.ItemsSource = null;
            OverrideHeading.Text = "In this mod";
            OverrideSummary.Text = "";
            return;
        }

        var overrides = _project.EnumerateOverrides();

        // Against the vault's originals, not the game folder: applying this mod put its own
        // files there, and comparing them with themselves calls every override unchanged.
        var applied = _session.Installer?.State.Applied.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unchanged = _session.Game is not null
            ? _project.FindUnchangedOverrides(_session.Game, _session.Vault, applied)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        long totalSize = 0;
        var rows = new List<OverrideRow>();

        foreach (var path in overrides)
        {
            var size = new FileInfo(_project.ResolveContentPath(path)).Length;
            totalSize += size;

            // "New" means the game ships nothing here — which, once applied, is no longer
            // visible from the game folder, so the vault decides it.
            var isNew = _session.Game is not null
                        && !_session.Vault.HasOriginal(path)
                        && (applied?.Contains(path) == true
                            || !File.Exists(_session.Game.ResolveDataPath(path)));
            var note = isNew ? $"new file · {Ui.FormatBytes(size)}"
                : unchanged.Contains(path) ? $"identical to stock · {Ui.FormatBytes(size)}"
                : Ui.FormatBytes(size);

            rows.Add(new OverrideRow(path, note));
        }

        OverrideList.ItemsSource = rows;
        OverrideHeading.Text = $"In this mod ({rows.Count})";

        var summary = $"{Ui.FormatBytes(totalSize)} of content";
        if (unchanged.Count > 0)
            summary += $" · {unchanged.Count} identical to the stock file and will change nothing in game";
        OverrideSummary.Text = summary;
    }

    // ---- build and test -------------------------------------------------

    private bool ReadyToBuild()
    {
        if (_project is null) return false;

        // The editors hold their changes in the control until Save, so building without
        // this would quietly package the previous content and the edit would not be in
        // the mod at all.
        if (!SavePendingEdit()) return false;

        if (_project.EnumerateOverrides().Count == 0)
        {
            Ui.Error(Owner, "Nothing to package",
                "This mod does not replace any files yet.\n\n" +
                "Pick a game file in the tree. Then use “Copy and edit”, " +
                "or “Replace with file…” to swap in your own.");
            return false;
        }

        var problems = _project.BuildManifest(_session.Game).ValidateMetadata();
        if (problems.Count > 0)
        {
            Ui.Error(Owner, "Fix these first", string.Join("\n", problems.Select(p => "• " + p)));
            return false;
        }
        return true;
    }

    private async void OnBuild(object sender, RoutedEventArgs e)
    {
        if (_project is null || !ReadyToBuild()) return;

        if (_session.Game is not null)
        {
            // Against the vault's originals, the way the overrides list does it. Applying
            // this mod put its own files in the game folder, so comparing an override with
            // what is there compares it with itself and calls every one of them unchanged.
            var applied = _session.Installer?.State.Applied.Keys
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var unchanged = _project.FindUnchangedOverrides(_session.Game, _session.Vault, applied);
            if (unchanged.Count > 0 && !Ui.Confirm(Owner, "Some files are unchanged",
                    $"{unchanged.Count} of your override(s) are byte-identical to the stock game file, " +
                    "so they will not change anything:\n\n" +
                    string.Join("\n", unchanged.Take(10)) +
                    (unchanged.Count > 10 ? $"\n… and {unchanged.Count - 10} more" : "") +
                    "\n\nBuild anyway?"))
                return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Build mod package",
            Filter = "Warcraft II mod (*.w2mod)|*.w2mod",
            FileName = _project.Id + ModPackage.Extension,
            AddExtension = true,
            DefaultExt = ModPackage.Extension,
            InitialDirectory = _project.ProjectDirectory,
        };
        if (dialog.ShowDialog(Owner) != true) return;

        try
        {
            _project.Save();
            var manifest = _project.Build(dialog.FileName, _session.Game);
            var size = new FileInfo(dialog.FileName).Length;

            SetStatus($"Built {Path.GetFileName(dialog.FileName)}. {manifest.Files.Count} file(s), {Ui.FormatBytes(size)}.");
            Ui.Info(Owner, "Built",
                $"{Path.GetFileName(dialog.FileName)}\n\n{manifest.Files.Count} file(s), {Ui.FormatBytes(size)}.");
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "build the mod", ex);
        }
    }

    /// <summary>
    /// Build, install, apply and launch, in one go. This is the loop a mod author runs
    /// dozens of times, so it deliberately asks nothing along the way.
    /// </summary>
    /// <summary>Fills the launch list once, and leaves it on the game's front end.</summary>
    private void ShowLaunchTargets()
    {
        if (LaunchTargetBox is null || LaunchTargetBox.ItemsSource is not null) return;

        LaunchTargetBox.ItemsSource = LaunchTargets.All();
        LaunchTargetBox.SelectedIndex = 0;
    }

    private async void OnTestInGame(object sender, RoutedEventArgs e)
    {
        if (_project is null || _session.Installer is null) return;

        // Chosen here rather than read at launch time, so a change made while the build
        // runs cannot send the game somewhere the status line did not say.
        var target = LaunchTargetBox?.SelectedItem as LaunchTarget;
        GameLauncher.Scenario = target?.Scenario;

        // A mod that replaces nothing has nothing to package, but the button is also how
        // the game gets started and how a mission is opened straight away. Refusing to
        // launch because there is nothing to install answers a question nobody asked.
        if (!SavePendingEdit()) return;

        if (_project.EnumerateOverrides().Count == 0)
        {
            SetStatus(target?.Scenario is null
                ? "Nothing to install. Starting the game as it is."
                : $"Nothing to install. Starting the game at {target.Scenario}.");

            var plain = new ModApplier(_session, Owner);
            plain.Status += SetStatus;
            await plain.Launch();
            return;
        }

        if (!ReadyToBuild()) return;

        try
        {
            _project.Save();

            // Staged under the vault rather than the project folder, so repeated test
            // runs do not litter the author's working directory.
            var staging = Path.Combine(_session.Vault.Root, "staging");
            Directory.CreateDirectory(staging);
            var packagePath = Path.Combine(staging, _project.Id + ModPackage.Extension);

            // Hashing and zipping every replaced file. A mod carrying a sprite sheet is a
            // hundred megabytes of it, which is seconds with the window dead otherwise.
            var manifest = await Working.While(Owner, $"Building {_project.Name}…",
                () => _project.Build(packagePath, _session.Game),
                ex => Ui.Failed(Owner, "build the mod", ex));

            if (manifest is null) return;

            SetStatus($"Built {manifest.Files.Count} file(s), installing…");

            // The editor installs and applies through the same service the loader uses, so
            // testing a mod does exactly what playing one does.
            var applier = new ModApplier(_session, Owner);
            applier.Status += SetStatus;

            var modId = applier.Install(packagePath);
            if (modId is null)
            {
                SetStatus("Could not install the mod. See the error above.");
                return;
            }

            // Same path the Play button takes: make this mod the one on disk, then launch.
            SetStatus("Applying to the game…");
            if (!await applier.Apply(modId, launch: true))
            {
                SetStatus("Apply failed. The game folder is unchanged.");
                return;
            }

            SetStatus(target?.Scenario is null
                ? $"Playing {_project.Name}. {manifest.Files.Count} file(s) replaced."
                : $"Playing {_project.Name} from {target.Scenario}. "
                  + $"{manifest.Files.Count} file(s) replaced.");
        }
        catch (Exception ex)
        {
            // Includes the type: a bare message is rarely enough to tell what actually failed.
            Ui.Failed(Owner, "test the mod", ex);
        }
    }

    // ---- drag and drop --------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Dropping a file replaces whatever is selected in the tree — the fastest path from
    /// "I exported a PNG" to "the mod contains it".
    /// </summary>
    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        if (paths[0].EndsWith(ModProject.Extension, StringComparison.OrdinalIgnoreCase))
        {
            TryOpenProject(paths[0], quiet: false);
            return;
        }

        if (_project is null)
        {
            Ui.Error(Owner, "No project open", "Create or open a mod project before adding files.");
            return;
        }
        if (_selected is null || _selected.IsFolder)
        {
            Ui.Error(Owner, "Nothing selected",
                "Select the game file you want to replace in the tree first, then drop your file.");
            return;
        }
        if (paths.Length > 1)
        {
            Ui.Error(Owner, "One file at a time",
                "A drop replaces the selected game file, so only one file can be dropped at once.");
            return;
        }

        Import(paths[0]);
    }

    private void SetStatus(string text) => StatusText.Text = text;


    // ---- undo -------------------------------------------------------------

    /// <summary>The session's history; every screen pushes to the same one.</summary>
    public UndoStack Undo => _session.Undo;

    /// <summary>
    /// Puts a .dat field change on the stack.
    ///
    /// The write has already happened — DatRow raises this after the fact — so the entry
    /// only has to know how to put the number back, and how to say what it did.
    /// </summary>
    private void RecordDatEdit(DatFieldEdit change, int record)
    {
        if (Undo.Suspended || _datTable is null) return;

        var table = _datTable;
        var path = _selected?.RelativePath;
        var what = RecordTitle(record);

        void Write(long value)
        {
            using (Undo.Quiet())
            {
                table.Set(change.FieldKey, change.Record, value, change.Component);

                // The row on screen is showing the old number until it is told.
                if (_selected?.RelativePath == path && _datRecord == record) ShowDatRecord(record);
                MarkEdited();
                MarkDatRecordChanged(record);
            }
        }

        Undo.Push(new Edit(
            $"{what} {change.FieldLabel.ToLowerInvariant()} {change.Was} to {change.Now}",
            () => Write(change.Now),
            () => Write(change.Was),
            () => Reveal(path, record)));
    }

    /// <summary>The record's name if it has one, else its number — for the menu text.</summary>
    private string RecordTitle(int record)
    {
        var rows = DatRecordList.ItemsSource as IEnumerable<DatRecordRow>;
        var row = rows?.FirstOrDefault(r => r.Index == record);
        return string.IsNullOrWhiteSpace(row?.Title) ? $"record {record}" : row!.Title;
    }

    /// <summary>
    /// Takes back the last change and shows where it happened.
    ///
    /// Switching to the file the change belongs to matters more than it sounds: an undo you
    /// cannot see is indistinguishable from one that did not work.
    /// </summary>
    public void UndoLast()
    {
        if (Undo.Undo() is { } edit) GoTo(edit);
    }

    public void RedoLast()
    {
        if (Undo.Redo() is { } edit) GoTo(edit);
    }

    private void GoTo(Edit edit)
    {
        edit.Show?.Invoke();
        SetStatus(edit.Describe);
    }

    /// <summary>Brings a .dat record back into view, wherever the editor happens to be.</summary>
    private void Reveal(string? path, int record)
    {
        if (path is not null && _selected?.RelativePath != path) SelectPath(path);

        if (_datTable is not null && record >= 0 && record < _datTable.RecordCount)
            SelectDatRecord(record);
    }

    /// <summary>Puts the cursor on a record, so an undone change is the thing you are looking at.</summary>
    private void SelectDatRecord(int record)
    {
        if (DatRecordList.ItemsSource is not IEnumerable<DatRecordRow> rows) return;

        var row = rows.FirstOrDefault(r => r.Index == record);
        if (row is not null) DatRecordList.SelectedItem = row;
    }

    /// <summary>
    /// Puts a language-file change on the stack.
    ///
    /// Several keys can belong to one edit — a rename writes both the modern key and the
    /// classic stat_txt one — and they go back together, because taking half a rename back
    /// would leave the unit called two different things.
    /// </summary>
    private void RecordStringEdit(string describe, int record, List<(string Key, string? Was, string Now)> changes)
    {
        if (Undo.Suspended || changes.Count == 0 || _project is null) return;

        var path = _selected?.RelativePath;

        void Put(bool forward)
        {
            using (Undo.Quiet())
            {
                try
                {
                    var target = _project.ResolveContentPath(StringsPath);
                    if (!File.Exists(target)) return;

                    var strings = GameStrings.Load(target);
                    foreach (var (key, was, now) in changes)
                    {
                        var value = forward ? now : was;
                        if (value is null) strings.Remove(key);
                        else strings.Set(key, value);
                    }

                    strings.Save(target);
                    _datStrings = strings;

                    // The mark follows the text: a rename taken back is not a rename, and
                    // leaving the dot behind says this mod changed something it no longer
                    // changes.
                    MarkDatRenamed(record, forward);

                    Reveal(path, record);
                    ShowRecordText(record);

                    // The list shows names too, so it has to follow.
                    var selected = DatRecordList.SelectedIndex;
                    _loadingUi = true;
                    BuildDatRecordList();
                    DatRecordList.SelectedIndex = selected;
                    _loadingUi = false;
                }
                catch (Exception ex)
                {
                    SetStatus("Could not put the text back: " + ex.Message);
                }
            }
        }

        Undo.Push(new Edit(describe, () => Put(true), () => Put(false), () => Reveal(path, record)));
    }

    // ---- the start page ---------------------------------------------------

    /// <summary>Whether there is a project to show tabs for.</summary>
    private bool HasStartPage => _project is null;

    /// <summary>
    /// Puts the start page up, or takes it down.
    ///
    /// The tab strip goes with it: eight tabs over an empty editor is eight ways to look at
    /// nothing, and it invites clicking before choosing.
    /// </summary>
    private void ShowStartPage()
    {
        var starting = HasStartPage;

        if (starting) StartPane.Show(_session.Settings.RecentProjects);

        StartPane.Visibility = starting ? Visibility.Visible : Visibility.Collapsed;
        TabStrip.Visibility = starting ? Visibility.Collapsed : Visibility.Visible;

        if (starting)
        {
            // Everything else is about a project, and there is not one.
            FilesPane.Visibility = Visibility.Collapsed;
            DetailsPane.Visibility = Visibility.Collapsed;
            CampaignPane.Visibility = Visibility.Collapsed;
            IconsPane.Visibility = Visibility.Collapsed;
            SpritesPane.Visibility = Visibility.Collapsed;
        }
        else
        {
            OnLensChanged(this, new RoutedEventArgs());
        }
    }

    /// <summary>Takes a project off the recent list. The project itself is not touched.</summary>
    private void Forget(string path)
    {
        _session.Settings.RecentProjects.RemoveAll(
            p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

        if (string.Equals(_session.Settings.LastProject, path, StringComparison.OrdinalIgnoreCase))
            _session.Settings.LastProject = "";

        _session.Settings.Save();
        StartPane.Show(_session.Settings.RecentProjects);

        SetStatus($"{Path.GetFileNameWithoutExtension(path)} removed from the recent list.");
    }

    // ---- editing a map in somebody else's editor -------------------------

    /// <summary>The map last handed to an outside editor, so it can be re-read on demand.</summary>
    private string? _externalEdit;

    /// <summary>Whether this file is a map the mod owns, and so is safe to let an editor write to.</summary>
    private bool CanEditMap =>
        _selected is { IsFolder: false } file
        && file.RelativePath.EndsWith(".pud", StringComparison.OrdinalIgnoreCase)
        && _project?.HasOverride(file.RelativePath) == true;

    /// <summary>
    /// Offers the map editor for a map the mod has its own copy of.
    ///
    /// Only for a copy the mod owns. Handing an outside editor a stock file would either fail
    /// on a read-only path under Program Files or, worse, succeed and edit the real game
    /// install — so the button follows the same line the pane already draws, and appears only
    /// after Copy and edit.
    /// </summary>
    private void ShowMapEditorButton() =>
        EditMapButton.Visibility = CanEditMap ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The configured map editor, asking for one the first time and remembering it.
    ///
    /// Asked rather than the button being greyed out with no explanation: "nothing happens
    /// and I cannot see why" is worse than one file dialog.
    /// </summary>
    private string? MapEditorPath(bool choose = false)
    {
        var configured = _session.Settings.MapEditor;

        if (!choose && configured.Length > 0 && File.Exists(configured)) return configured;

        var dialog = new OpenFileDialog
        {
            Title = "Which map editor should open .pud files?",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (configured.Length > 0 && File.Exists(configured)) dialog.FileName = configured;

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return null;

        _session.Settings.MapEditor = dialog.FileName;
        _session.Settings.Save();
        return dialog.FileName;
    }

    private void OnChooseMapEditor(object sender, RoutedEventArgs e)
    {
        if (MapEditorPath(choose: true) is { } chosen)
            SetStatus($"Maps will open in {Path.GetFileNameWithoutExtension(chosen)}.");
    }

    private void OnEditMap(object sender, RoutedEventArgs e)
    {
        if (!CanEditMap || _selected is null || _project is null) return;

        var file = _project.ResolveContentPath(_selected.RelativePath);

        // Whatever Windows already opens a .pud with. Asking which program to use, before
        // it is known that no program is registered, made a one-click job into a hunt
        // through Program Files. A chosen editor still wins, for anyone who set one.
        var configured = _session.Settings.MapEditor;
        var editor = configured.Length > 0 && File.Exists(configured) ? configured : null;

        try
        {
            Process.Start(editor is null
                ? new ProcessStartInfo(file) { UseShellExecute = true }
                : new ProcessStartInfo(editor, file) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            // Nothing is registered for .pud, so now is the moment to ask.
            SetStatus("Nothing on this machine opens .pud files. " + ex.Message);

            if (MapEditorPath(choose: true) is not { } chosen) return;

            try
            {
                Process.Start(new ProcessStartInfo(chosen, file) { UseShellExecute = false });
                editor = chosen;
            }
            catch (Exception second)
            {
                SetStatus("Could not open the map editor: " + second.Message);
                return;
            }
        }

        // No watcher. The editor writes into the mod's own copy, so there is nothing to
        // import — only a re-read once it has been saved, and asking is more honest than
        // guessing when a save has finished.
        _externalEdit = _selected.RelativePath;
        ShowExternalEdit(editor is null
            ? "your map editor"
            : Path.GetFileNameWithoutExtension(editor));
    }

    private void ShowExternalEdit(string editor)
    {
        if (_externalEdit is null)
        {
            ExternalEditStrip.Visibility = Visibility.Collapsed;
            return;
        }

        ExternalEditNote.Text =
            $"{Path.GetFileName(_externalEdit)} is open in {editor}. It is being edited in "
            + "place, so save there and reload here.";

        ExternalEditStrip.Visibility = Visibility.Visible;
    }

    private void OnReloadExternalEdit(object sender, RoutedEventArgs e)
    {
        var path = _externalEdit;

        _externalEdit = null;
        ExternalEditStrip.Visibility = Visibility.Collapsed;

        ReloadFromDisk();
        if (path is not null) SelectPath(path);

        SetStatus(path is null ? "Reloaded from disk." : $"{Path.GetFileName(path)} re-read from disk.");
    }
}
