using System.Windows.Media.Imaging;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using RuneFoundry.Core;
using RuneFoundry.Core.Formats;
using RuneFoundry.UI;

namespace RuneFoundry.Editor.Views;


/// <summary>
/// One picture in a campaign row: a thumbnail, what it is, and whether the mod changed it.
///
/// An act has two — the card drawn in the mission list and the full-screen title card with
/// its title lettered over it — so they sit side by side rather than as separate rows. They
/// are a pair in the game and replacing one without the other is usually a mistake.
/// </summary>
public sealed class ArtSlot : Observable
{
    public required CampaignPicture Picture0 { get; init; }
    public required string Label { get; init; }
    public required string Shape { get; init; }

    /// <summary>Dark line art needs something to be dark against; parchment does not.</summary>
    public bool WantsLightBacking { get; init; }

    private BitmapSource? _thumbnail;

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    /// <summary>
    /// The campaign picture as the pointer rests on it, where there is one.
    ///
    /// A pair is one thing to replace and two things to look at, so both are shown. Showing
    /// only the faint one meant a mod could change the picture people actually see and the
    /// card would not move.
    /// </summary>
    public CampaignPicture? Hovered { get; init; }

    private BitmapSource? _hoveredThumbnail;

    public BitmapSource? HoveredThumbnail
    {
        get => _hoveredThumbnail;
        set { if (Set(ref _hoveredThumbnail, value)) Raise(nameof(HasPair)); }
    }

    public bool HasPair => _hoveredThumbnail is not null;

    private bool _changed;

    public bool Changed
    {
        get => _changed;
        set { if (Set(ref _changed, value)) Raise(nameof(State)); }
    }

    public string State => _changed ? "replaced by this mod" : "the game's own art";

    private string _note = "";

    /// <summary>Anything else worth knowing, such as a picture two campaigns share.</summary>
    public string Note
    {
        get => _note;
        set { if (Set(ref _note, value)) Raise(nameof(HasNote)); }
    }

    public bool HasNote => _note.Length > 0;
}

/// <summary>One row of the campaign's artwork: the campaign picture, or one act.</summary>
public sealed class CampaignArtRow : Observable
{
    public required string Label { get; init; }
    public required IReadOnlyList<ArtSlot> Slots { get; init; }

    /// <summary>The language-file key for an act's name, or null for the campaign picture.</summary>
    public string? TitleKey { get; init; }

    public bool HasTitle => TitleKey is not null;

    private string _coverage = "";

    /// <summary>Which of this act's missions the mod replaces, and which it leaves alone.</summary>
    public string Coverage
    {
        get => _coverage;
        set { if (Set(ref _coverage, value)) Raise(nameof(HasCoverage)); }
    }

    public bool HasCoverage => _coverage.Length > 0;

    private string _title = "";

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    private bool _titleChanged;

    public bool TitleChanged
    {
        get => _titleChanged;
        set { if (Set(ref _titleChanged, value)) Raise(nameof(CanReset)); }
    }

    /// <summary>
    /// Whether anything about this row differs from the game: either picture, or the name.
    /// Reset puts the whole row back, because an act is its pictures and its title together.
    /// </summary>
    public bool CanReset => _titleChanged || Slots.Any(s => s.Changed);

    /// <summary>Recomputed when a slot changes, since CanReset reads across them.</summary>
    public void RefreshCanReset() => Raise(nameof(CanReset));
}

/// <summary>A mission in the list down the left.</summary>
public sealed class MissionRow : Observable
{
    /// <summary>The mission this row is, or null when the row is a campaign.</summary>
    public CampaignMission? Mission { get; init; }

    /// <summary>The campaign this row is, or the one a mission belongs to.</summary>
    public required Campaign Campaign { get; init; }

    public required string Title { get; init; }

    /// <summary>
    /// Whether this row is the campaign itself rather than one of its missions.
    ///
    /// A campaign is a row because it has settings of its own — its picture, its act maps and
    /// their names — and a list with one selection is the only way the right-hand side can
    /// know what it is showing.
    /// </summary>
    public bool IsCampaign => Mission is null;

    public bool IsMission => Mission is not null;

    private string _note = "";

    /// <summary>What this mod has changed about the mission, or nothing.</summary>
    public string Note
    {
        get => _note;
        set { if (Set(ref _note, value)) Raise(nameof(HasNote)); }
    }

    public bool HasNote => _note.Length > 0;

    private bool _isChanged;

    /// <summary>
    /// Whether this mod changes the mission. Shown as the same dot the file tree uses, so
    /// "my mod touches this" looks the same everywhere rather than being spelled out in
    /// one place and drawn in another.
    /// </summary>
    public bool IsChanged
    {
        get => _isChanged;
        set { if (Set(ref _isChanged, value)) Raise(nameof(Marker)); }
    }

    public string Marker => _isChanged ? "●" : "";

    private bool _canReset;

    /// <summary>Whether this mod has changed anything about the mission.</summary>
    public bool CanReset
    {
        get => _canReset;
        set => Set(ref _canReset, value);
    }

    public override string ToString() => Title;
}

/// <summary>One briefing page: its text, and the recording that reads it out.</summary>
public sealed class PageRow : Observable
{
    public required BriefingPage Page { get; init; }

    public string Label => $"Page {Page.Number}";
    public string SpeechPath => Page.SpeechPath;

    private string _text = "";

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    private bool _speechOverridden;

    public bool SpeechOverridden
    {
        get => _speechOverridden;
        set { if (Set(ref _speechOverridden, value)) Raise(nameof(SpeechState)); }
    }

    public string SpeechState => _speechOverridden ? "replaced by this mod" : "the game's own recording";

    private bool _isPlaying;

    /// <summary>Drives the button: the same one plays, then pauses, then resumes.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set { if (Set(ref _isPlaying, value)) Raise(nameof(PlayLabel)); }
    }

    public string PlayLabel => _isPlaying ? "Stop" : "Play";
}

/// <summary>
/// The campaign lens: the same files the tree holds, arranged as the game presents them.
///
/// A mission is not one file. Its map is a .PUD under Campaign, its name, objectives and
/// briefing are keys in a language file under Strings, and each briefing page has a
/// recording under Speech — three folders and a naming scheme that changes between the
/// base game and the expansion. Replacing a mission through the tree means knowing all of
/// that. Here it is one screen.
/// </summary>
public partial class CampaignView : UserControl
{
    private Session _session = null!;
    private ModProject? _project;

    private readonly List<MissionRow> _rows = new();
    private MissionRow? _selected;
    private List<PageRow> _pages = new();

    /// <summary>The campaign's closing screen, edited on the campaign pane.</summary>
    private List<PageRow> _epiloguePages = new();
    private bool _epilogueDirty;

    private ushort[]? _objectives;
    private ushort[]? _thresholds;
    private uint[]? _allowedUnits;

    /// <summary>The three campaign masks, used to seed the parts of ALOW we do not open.</summary>
    private readonly Dictionary<CampaignTech.Table, uint[]> _tech = new();
    private GameStrings? _strings;
    private string _stringsPath = CampaignText.Default;
    private bool _loading;
    private bool _textDirty;

    private readonly AudioPlayer _audio = new();

    /// <summary>Reports progress to whatever is hosting this.</summary>
    public event Action<string>? Status;

    /// <summary>
    /// Brings this pane to the front, set by whatever hosts it. Undo uses it: a change taken
    /// back on a tab you are not looking at may as well not have happened.
    /// </summary>
    public Action? Reveal { get; set; }

    private UndoStack Undo => _session.Undo;

    /// <summary>Puts a campaign change on the stack, pointed back at its mission.</summary>
    private void Record(string describe, CampaignMission mission, Action apply, Action revert)
    {
        if (Undo.Suspended) return;

        Undo.Push(new Edit(describe, apply, revert, () =>
        {
            Reveal?.Invoke();

            var row = _rows.FirstOrDefault(r => r.Mission is { } m
                                                && m.Campaign.Id == mission.Campaign.Id
                                                && m.Number == mission.Number);
            if (row is not null) MissionList.SelectedItem = row;
        }));
    }

    /// <summary>Raised when a file was added to or dropped from the mod.</summary>
    public event Action? OverridesChanged;

    public CampaignView()
    {
        InitializeComponent();

        // One player for the whole panel, so starting a second page's line stops the first.
        _audio.Changed += RefreshPlayButtons;
    }

    private void RefreshPlayButtons()
    {
        foreach (var page in _pages) page.IsPlaying = _audio.IsPlaying_(SoundPath(page));
    }

    private Window? Owner => Window.GetWindow(this);

    public void Attach(Session session)
    {
        _session = session;
        _stringsPath = string.IsNullOrWhiteSpace(session.Settings.CampaignLocale)
            ? CampaignText.Default
            : session.Settings.CampaignLocale;

        TimingPane.Attach(session);
        TimingPane.Status += message => Status?.Invoke(message);
        TimingPane.OverridesChanged += AfterFileChange;

        MoviesPane.Attach(session);
        MoviesPane.Status += message => Status?.Invoke(message);
        MoviesPane.OverridesChanged += AfterFileChange;
    }

    /// <summary>Called whenever the project or the game folder changes.</summary>
    public void Refresh(ModProject? project)
    {
        _project = project;
        Build();
    }

    // ---- the mission list ------------------------------------------------

    private void Build()
    {
        var keep = _selected?.Mission;
        _rows.Clear();

        if (_session?.Game is null)
        {
            MissionList.ItemsSource = null;
            ShowDetail(null);
            return;
        }

        _loading = true;

        FillLocales();
        LoadObjectives();
        _strings = LoadStrings();

        foreach (var campaign in Campaign.All)
        {
            // The campaign leads its own missions, as a row you can select.
            _rows.Add(new MissionRow { Campaign = campaign, Title = campaign.ToString() });

            foreach (var mission in campaign.Missions(_strings))
            {
                var name = _strings?.Get(mission.NameKey);
                _rows.Add(new MissionRow
                {
                    Mission = mission,
                    Campaign = campaign,
                    Title = string.IsNullOrWhiteSpace(name)
                        ? $"{mission.Number}."
                        : $"{mission.Number}. {name}",
                });
            }
        }

        RefreshNotes();

        MissionList.ItemsSource = new ListCollectionView(_rows);

        _loading = false;

        // A ListBox over a CollectionView follows that view's current item, which starts on
        // the first row — so a mission ends up highlighted on its own, while _loading was
        // still suppressing the selection handler. The result was a list with something
        // picked and a panel showing nothing. Settle it here instead of leaving it to WPF.
        var again = keep is null
            ? null
            : _rows.FirstOrDefault(r => r.Mission is { } m
                                        && m.Campaign.Id == keep.Campaign.Id
                                        && m.Number == keep.Number);

        // The first row is a campaign, and opening on a campaign is a reasonable place to
        // start — but a remembered mission wins.
        var show = again ?? MissionList.SelectedItem as MissionRow ?? _rows.FirstOrDefault();

        MissionList.SelectedItem = show;
        Select(show);
    }

    /// <summary>
    /// Marks the missions this mod has touched. Cheap enough to redo whenever anything
    /// changes, and it is the only way to see at a glance what a half-finished campaign
    /// replacement still needs.
    /// </summary>
    private void RefreshNotes()
    {
        // Read once, not once per mission: this is a 130 KB file and there are 52 of them.
        var stock = _project?.HasOverride(_stringsPath) == true ? StockStrings() : null;

        foreach (var row in _rows)
        {
            if (_project is null) { row.Note = ""; continue; }

            // A campaign row has files of its own: the pages the game shows once the last
            // mission is won. Silencing one is a change the mod owns, and the list said
            // nothing about it because only missions were ever counted here.
            if (row.Mission is not { } mission)
            {
                var closing = row.Campaign.Epilogue(_strings)
                    .Count(page => _project.HasOverride(page.SpeechPath));

                row.IsChanged = closing > 0;
                row.Note = closing switch
                {
                    0 => "",
                    1 => "closing speech replaced",
                    _ => $"{closing} closing speeches replaced",
                };

                row.CanReset = false;
                continue;
            }

            var files = mission.Files().Count(_project.HasOverride);
            var text = stock is not null && TextDiffersFromGame(mission, stock);

            var parts = new List<string>();
            if (files == 1) parts.Add("map replaced");
            else if (files > 1) parts.Add($"{files} files replaced");
            if (text) parts.Add("text changed");
            if (_project.ObjectiveFor(mission.ExeSlot) is { } objective)
                parts.Add(CampaignObjectiveNames.Describe(objective).ToLowerInvariant());


            // A mission the mod does not replace still plays, as the game's own. Saying
            // nothing there reads as "nothing to report" when it means "your players will
            // play Blizzard's mission here".
            // The dot is the whole message in the list: a mission with no mark is the
            // game's own, and saying so on every unchanged row filled the list with the
            // word "unchanged" repeated fifty three times. What changed is on hover, and
            // in the pane beside the list, which has room for it.
            row.IsChanged = parts.Count > 0;
            row.Note = parts.Count > 0 ? string.Join(" · ", parts) : "";
            row.CanReset = parts.Count > 0;
        }
    }

    /// <summary>
    /// Whether this mod's language file says anything different about a mission. Compared
    /// against the game's own copy rather than remembered, so a hand-edited file counts.
    /// </summary>
    private bool TextDiffersFromGame(CampaignMission mission, GameStrings stock)
    {
        if (_strings is null) return false;

        foreach (var key in Keys(mission))
            if (_strings.Get(key) != stock.Get(key))
                return true;

        return false;
    }

    private static IEnumerable<string> Keys(CampaignMission mission)
    {
        yield return mission.NameKey;
        yield return mission.ObjectivesKey;
        yield return mission.SummaryKey;
        foreach (var page in mission.Pages) yield return page.TextKey;
    }

    // ---- strings ---------------------------------------------------------

    private void FillLocales()
    {
        if (_session.Game is null) return;

        var available = CampaignText.Available(_session.Game);
        if (available.Count == 0) return;

        if (!available.Contains(_stringsPath, StringComparer.OrdinalIgnoreCase))
            _stringsPath = available.Contains(CampaignText.Default, StringComparer.OrdinalIgnoreCase)
                ? CampaignText.Default
                : available[0];

        LocaleBox.ItemsSource = available.Select(Path.GetFileNameWithoutExtension).ToList();
        LocaleBox.SelectedItem = Path.GetFileNameWithoutExtension(_stringsPath);
    }

    /// <summary>
    /// The stock objective ids, read from the game executable.
    ///
    /// Read from the vault's copy where there is one: once a mod with objective changes has
    /// been applied, the executable on disk holds those changes, and reading it back would
    /// show the mod's own choice as if it were the campaign's.
    /// </summary>
    private void LoadObjectives()
    {
        _objectives = null;
        if (_session.Game is null) return;

        var vaultCopy = Path.Combine(_session.Vault.Root, "executable", "Warcraft II.exe");
        var path = File.Exists(vaultCopy) ? vaultCopy : _session.Game.ExecutablePath;

        try
        {
            if (!File.Exists(path)) return;

            var exe = File.ReadAllBytes(path);
            if (!CampaignObjectives.TryLocate(exe, out _, out _)) return;

            _objectives = CampaignObjectives.Read(exe);
            _thresholds = CampaignObjectives.ReadThresholds(exe);

            // Only the unit mask is shown; the upgrade and spell masks move with it, and a
            // mission that restricts one restricts all three.
            _tech.Clear();
            foreach (var table in Enum.GetValues<CampaignTech.Table>())
                _tech[table] = CampaignTech.Read(exe, table);

            _allowedUnits = _tech[CampaignTech.Table.Units];
        }
        catch
        {
            // Not being able to read it just means the victory section says so.
        }
    }

    /// <summary>The strings as they stand: this mod's copy if it has one, else the game's.</summary>
    private GameStrings? LoadStrings()
    {
        var path = EffectiveStringsPath();
        if (path is null) return null;

        try
        {
            return GameStrings.Load(path);
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Could not read {_stringsPath}: {ex.Message}");
            return null;
        }
    }

    private GameStrings? StockStrings()
    {
        if (_session.Game is null) return null;

        try
        {
            return GameStrings.Load(_session.StockFile(_stringsPath)
                                    ?? _session.Game.ResolveDataPath(_stringsPath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The language file as it stands: this mod's copy if it has one, else the game's.
    ///
    /// Through the session rather than the game folder. That folder holds another mod's
    /// files while one is applied, so reading it directly opened a new project showing the
    /// applied mod's campaign text as this one's starting point. The vault keeps what was
    /// displaced, which is what makes the answer the game's own.
    ///
    /// Null when the locale has no file, which is how the locale list drops the ones this
    /// build of the game does not ship.
    /// </summary>
    private string? EffectiveStringsPath()
    {
        var path = _session.EffectiveFile(_project, _stringsPath);
        return path is not null && File.Exists(path) ? path : null;
    }

    private void OnLocaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LocaleBox.SelectedItem is not string locale) return;

        SaveText();
        _stringsPath = $"{CampaignText.Folder}/{locale}.json";
        _session.Settings.CampaignLocale = _stringsPath;
        _session.Settings.Save();

        Build();
    }

    // ---- the detail panel ------------------------------------------------

    private void OnMissionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        SaveText();
        _audio.Stop();
        Select(MissionList.SelectedItem as MissionRow);
    }

    /// <summary>
    /// Shows whatever the list has selected: a campaign, a mission, or neither.
    ///
    /// One method rather than one per kind. When a header was a separate thing that could be
    /// clicked, selecting a mission left the campaign pane up behind it — there was no single
    /// place that knew both were mutually exclusive.
    /// </summary>
    private void Select(MissionRow? row)
    {
        if (row is { IsCampaign: true })
        {
            ShowCampaign(row.Campaign);
            return;
        }

        _campaign = null;
        CampaignDetail.Visibility = Visibility.Collapsed;
        ShowDetail(row);
    }

    private void ShowDetail(MissionRow? row)
    {
        _selected = row;

        Detail.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        Placeholder.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        CampaignDetail.Visibility = Visibility.Collapsed;

        if (row is null) return;

        var mission = row.Mission;

        _loading = true;

        MissionTitle.Text = row.Title;
        MissionSubtitle.Text = $"{mission.Campaign} · mission {mission.Number} of {mission.Campaign.MissionCount}";

        MapPath.Text = mission.MapPath;
        NameBox.Text = _strings?.Get(mission.NameKey) ?? "";
        ObjectivesBox.Text = _strings?.Get(mission.ObjectivesKey) ?? "";

        _pages = mission.Pages.Select(page => new PageRow
        {
            Page = page,
            Text = _strings?.Get(page.TextKey) ?? "",
        }).ToList();
        PageList.ItemsSource = _pages;

        RefreshFileStates();
        RefreshObjective();
        ShowTech();

        _loading = false;
        _textDirty = false;
    }

    /// <summary>
    /// Fills the victory dropdown for the selected mission.
    ///
    /// The first entry is always the campaign's own condition, named, so leaving it alone
    /// is a choice rather than an absence of one. The rest are the game's other conditions,
    /// destroy-all first because it is what most people want.
    /// </summary>
    private void RefreshObjective()
    {
        if (_selected is null) return;

        var slot = _selected.Mission.ExeSlot;
        var stock = _objectives is not null && slot < _objectives.Length ? _objectives[slot] : (ushort?)null;

        var choices = new List<CampaignObjective>
        {
            new(-1, stock is null
                ? "Campaign default"
                : $"{CampaignObjectiveNames.Describe(stock.Value)} (default)"),
        };

        // Everything except whatever the slot already is: picking that would be a no-op.
        choices.AddRange(CampaignObjectiveNames.Choices.Where(c => c.Id != stock));

        var wanted = _project?.ObjectiveFor(slot);

        _loading = true;
        ObjectiveBox.ItemsSource = choices;
        ObjectiveBox.SelectedItem = choices.FirstOrDefault(c => c.Id == (wanted ?? -1)) ?? choices[0];
        ObjectiveBox.IsEnabled = _project is not null && stock is not null;
        _loading = false;

        ShowObjectiveNotes();
    }

    /// <summary>
    /// The delivery objective is the one with settable numbers: how many units must reach
    /// the Circle of Power, and whether only a Hero counts. Both are the same word in the
    /// table next to the objective ids.
    /// </summary>
    private void ShowDeliveryFields(CampaignObjective? chosen)
    {
        var isDelivery = chosen?.Id == 0x0200;
        DeliveryPanel.Visibility = isDelivery ? Visibility.Visible : Visibility.Collapsed;
        if (!isDelivery || _selected is null) return;

        var slot = _selected.Mission.ExeSlot;
        var word = _project?.ThresholdFor(slot)
                   ?? (_thresholds is not null && slot < _thresholds.Length ? _thresholds[slot] : 1);

        // The stock word can be 0 for a mission that never used a counter; 1 is the
        // smallest thing that means anything.
        var count = Math.Max(1, CampaignObjectives.CountOf((ushort)word));

        _loading = true;
        DeliveryCount.Text = count.ToString();
        DeliveryHeroes.Text = CampaignObjectives.HeroesOf((ushort)word).ToString();
        _loading = false;
    }

    private void OnDeliveryChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected is null || _project is null) return;
        if (!int.TryParse(DeliveryCount.Text, out var count) || count < 1 || count > 255) return;
        if (!int.TryParse(DeliveryHeroes.Text, out var heroes) || heroes < 0) return;

        // The game grants the Hero bonus only when bit 12 of the threshold is set, and an
        // even number of Heroes leaves it clear — the mission would then need thousands of
        // ordinary deliveries and could never be finished. Rather than let that be typed
        // and discovered in play, an even number is corrected down to the nearest odd one.
        var corrected = heroes;
        if (!CampaignObjectives.HeroCountWorks(corrected)) corrected--;
        if (corrected > count) corrected = CampaignObjectives.HeroCountWorks(count) ? count : count - 1;
        if (corrected < 0) corrected = 0;

        if (corrected != heroes)
        {
            _loading = true;
            DeliveryHeroes.Text = corrected.ToString();
            DeliveryHeroes.CaretIndex = DeliveryHeroes.Text.Length;
            _loading = false;

            Status?.Invoke($"Heroes must be an odd number. Set to {corrected}.");
        }

        heroes = corrected;

        var slot = _selected.Mission.ExeSlot;
        var was = _project.ThresholdFor(slot);

        var word = CampaignObjectives.Threshold(count, heroes);
        if (!_project.SetThreshold(slot, word)) return;

        _project.Save();
        ShowObjectiveNotes();

        var mission = _selected.Mission;
        void Set(int? value)
        {
            using (Undo.Quiet())
            {
                _project.SetThreshold(slot, value);
                _project.Save();
                if (_selected?.Mission == mission)
                {
                    ShowDeliveryFields(ObjectiveBox.SelectedItem as CampaignObjective);
                    ShowObjectiveNotes();
                }
            }
        }

        Record($"{mission} delivery of {count}", mission, () => Set(word), () => Set(was));

        var note = heroes == 0
            ? $"{count} unit(s) must reach the Circle of Power."
            : $"{count} unit(s) must reach the Circle of Power, {heroes} of them Heroes.";

        Status?.Invoke($"{_selected.Mission}: {note}");
    }

    private void ShowObjectiveNotes()
    {
        var chosen = ObjectiveBox.SelectedItem as CampaignObjective;
        var changing = chosen is not null && chosen.Id >= 0;

        ShowDeliveryFields(chosen);

        ObjectiveNeeds.Text = chosen?.Requires is { Length: > 0 } needs
            ? $"Your map must have {needs}."
            : "";

        // Eight of these change the map before play starts. That is not a footnote — it is
        // the part a mission designer will not see coming.
        // The dropdown row already carries the lose clause and the side effect, so what is
        // left to say underneath is only what the map has to provide.
        ObjectiveAlso.Text = "";

        // Said once, plainly, on the only screen where it applies.
        ObjectiveWarning.Text = changing
            ? "Nothing on disk changes. This works in single-player only."
            : "";
    }

    /// <summary>
    /// Says whether the game holds this mission back, and lets the mod lift it.
    ///
    /// The lifting is done in the mission's own map, not in the running game: the map's
    /// ALOW chunk is parsed after the campaign masks are seeded, so it has the last word
    /// (W2R-RE-NOTES §5a). That keeps the whole feature inside a file this mod already
    /// ships, with nothing to write into the game while it runs.
    ///
    /// The checkbox has no stored state — it reads the map. One place to be wrong is
    /// better than two that can disagree, and replacing the map by hand cannot leave a
    /// stale tick behind.
    /// </summary>
    private void ShowTech()
    {
        if (_selected is null) return;

        var slot = _selected.Mission.ExeSlot;
        var stock = _allowedUnits is not null && slot < _allowedUnits.Length ? _allowedUnits[slot] : (uint?)null;

        var map = ReadMap();
        var humans = map is null ? Array.Empty<int>() : CampaignTech.HumanPlayers(map.PlayerOwners).ToArray();
        var open = map is not null && CampaignTech.OpensEverythingFor(PudFile.ReadAllow(map.Bytes), humans);

        TechState.Text = map is null
            ? "This mission's map could not be read."
            : humans.Length == 0
                ? "This map has no player slot to open up."
                : open
                    ? "This mod's copy of the map lifts the restriction."
                    : stock is null || CampaignTech.Restricts(stock.Value)
                        ? "The game holds part of the tech tree back on this mission."
                        : "The game already allows everything on this mission.";

        _loading = true;
        TechFree.IsChecked = open;
        TechFree.IsEnabled = _project is not null && map is not null && humans.Length > 0;
        _loading = false;
    }

    /// <summary>
    /// Writes — or takes back out — the mission map's ALOW chunk.
    ///
    /// Ticking it copies the map into the mod if it is not there already, which is the same
    /// rule the rest of the editor follows: editing a thing is what pulls it in.
    /// </summary>
    private void OnTechFreeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected is null || _project is null) return;

        var free = TechFree.IsChecked == true;
        var relativePath = _selected.Mission.MapPath;

        try
        {
            var map = ReadMap();
            if (map is null) throw new InvalidOperationException("This mission's map could not be read.");

            var humans = CampaignTech.HumanPlayers(map.PlayerOwners);

            byte[] updated;
            if (free)
            {
                var slot = _selected.Mission.ExeSlot;
                uint At(CampaignTech.Table table)
                {
                    var values = _tech.TryGetValue(table, out var read) ? read : null;
                    return values is not null && slot < values.Length ? values[slot] : CampaignTech.Everything;
                }

                updated = PudFile.WriteAllow(map.Bytes, CampaignTech.AllowArrays(
                    At(CampaignTech.Table.Units), At(CampaignTech.Table.Upgrades),
                    At(CampaignTech.Table.Spells), humans));
            }
            else
            {
                updated = PudFile.RemoveAllow(map.Bytes);
            }

            // Taking the restriction back off can leave the map identical to the game's, in
            // which case it was only ever in the mod to carry the chunk — so let it go.
            var stock = _session.StockFile(relativePath);
            if (!free && stock is not null && File.Exists(stock)
                && updated.AsSpan().SequenceEqual(File.ReadAllBytes(stock)))
                _project.RemoveOverride(relativePath);
            else
                _project.WriteOverride(relativePath, updated);

            _project.Save();

            // The map is a file, but this is a checkbox: what it toggles is one chunk, and
            // putting the chunk back is exactly as cheap as taking it out. The map's *bytes*
            // before the toggle are what gets restored, so a hand-replaced map survives.
            var mission = _selected.Mission;
            var before = map.Bytes;
            var after = updated;
            var hadOverride = _project.HasOverride(relativePath);

            void Put(byte[] bytes, bool keep)
            {
                using (Undo.Quiet())
                {
                    var stockPath = _session.StockFile(relativePath);
                    if (!keep && stockPath is not null && File.Exists(stockPath)
                        && bytes.AsSpan().SequenceEqual(File.ReadAllBytes(stockPath)))
                        _project.RemoveOverride(relativePath);
                    else
                        _project.WriteOverride(relativePath, bytes);

                    _project.Save();
                    RefreshNotes();
                    if (_selected?.Mission == mission) { ShowTech(); RefreshFileStates(); }
                    OverridesChanged?.Invoke();
                }
            }

            Record($"{mission} build restrictions {(free ? "lifted" : "restored")}", mission,
                   () => Put(after, free), () => Put(before, hadOverride));

            Status?.Invoke(free
                ? $"{_selected.Mission} can build anything (player {string.Join(", ", humans.Select(h => h + 1))})."
                : $"{_selected.Mission} keeps the campaign's own tech tree.");

            AfterFileChange();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "change the tech tree", ex);
            ShowTech();
        }
    }

    /// <summary>This mod's copy of the selected mission's map, else the game's own.</summary>
    private MapBytes? ReadMap()
    {
        if (_selected is null) return null;

        var relativePath = _selected.Mission.MapPath;
        var path = _project?.HasOverride(relativePath) == true
            ? _project.ResolveContentPath(relativePath)
            : _session.StockFile(relativePath);

        try
        {
            if (path is null || !File.Exists(path)) return null;

            var bytes = File.ReadAllBytes(path);
            return new MapBytes(bytes, PudFile.Parse(bytes).PlayerOwners);
        }
        catch
        {
            return null;
        }
    }

    private sealed record MapBytes(byte[] Bytes, IReadOnlyList<byte> PlayerOwners);

    private void OnObjectiveChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selected is null || _project is null) return;
        if (ObjectiveBox.SelectedItem is not CampaignObjective chosen) return;

        var slot = _selected.Mission.ExeSlot;
        var was = _project.ObjectiveFor(slot);
        var now = chosen.Id < 0 ? (int?)null : chosen.Id;

        if (!_project.SetObjective(slot, now)) return;

        _project.Save();
        RefreshNotes();
        ShowObjectiveNotes();

        var mission = _selected.Mission;
        void Set(int? value)
        {
            using (Undo.Quiet())
            {
                _project.SetObjective(slot, value);
                _project.Save();
                RefreshNotes();
                if (_selected?.Mission == mission) { RefreshObjective(); ShowObjectiveNotes(); }
            }
        }

        Record($"{mission} rule to {chosen.Label.ToLowerInvariant()}", mission,
               () => Set(now), () => Set(was));

        Status?.Invoke(chosen.Id < 0
            ? $"{_selected.Mission} keeps the campaign's own objective."
            : $"{_selected.Mission}: {chosen.Label}.");
    }

    /// <summary>Says which of a mission's files this mod owns, without re-reading anything else.</summary>
    private void RefreshFileStates()
    {
        // The closing pages belong to the campaign, not to a mission, so they are read
        // before the mission check. Silencing one used to leave it saying the game's
        // recording was still playing, because nothing below this line ever ran for them.
        foreach (var page in _epiloguePages)
            page.SpeechOverridden = _project?.HasOverride(page.SpeechPath) == true;

        if (_selected is null) return;

        var mapOverridden = _project?.HasOverride(_selected.Mission.MapPath) == true;
        MapState.Text = mapOverridden ? "replaced by this mod" : "the game's own map";
        MapResetButton.IsEnabled = mapOverridden;

        // Only one of these is ever the right thing to press. Opening the game's own map
        // in an editor would edit the file every mod on this machine reads from, so that
        // is offered as a copy first.
        MapOpenButton.Visibility = mapOverridden ? Visibility.Visible : Visibility.Collapsed;
        MapCopyButton.Visibility = mapOverridden ? Visibility.Collapsed : Visibility.Visible;
        MapCopyButton.IsEnabled = _project is not null;

        foreach (var page in _pages)
            page.SpeechOverridden = _project?.HasOverride(page.SpeechPath) == true;

        StringsState.Text = _project?.HasOverride(_stringsPath) == true
            ? $"Editing this mod's copy of {_stringsPath}."
            : "RuneFoundry saves your text into this project.";
    }

    // ---- text ------------------------------------------------------------

    private void OnTextEdited(object sender, TextChangedEventArgs e)
    {
        if (!_loading) if ((sender as FrameworkElement)?.DataContext is PageRow row && _epiloguePages.Contains(row))
            _epilogueDirty = true;
        else
            _textDirty = true;
    }

    private void OnTextLostFocus(object sender, RoutedEventArgs e)
    {
        // One template serves both lists, so the handler works out which it was editing.
        // A mission is selected or it is not; the closing screen belongs to the campaign.
        if ((sender as FrameworkElement)?.DataContext is PageRow row
            && _epiloguePages.Contains(row))
        {
            SaveEpilogueText();
            return;
        }

        SaveText();
    }

    /// <summary>
    /// Writes the closing screen's text into the mod's language file.
    ///
    /// Its own routine rather than a branch of SaveText, because that one is built around a
    /// selected mission and the closing screen has none. Both end at SaveStrings, which is
    /// where the file is actually written or dropped.
    /// </summary>
    private void SaveEpilogueText()
    {
        if (_loading || !_epilogueDirty) return;
        if (_project is null || _strings is null || _campaign is null || _session.Game is null) return;

        _epilogueDirty = false;

        var changed = false;
        foreach (var page in _epiloguePages)
        {
            if (_strings.Get(page.Page.TextKey) == page.Text) continue;

            _strings.Set(page.Page.TextKey, page.Text);
            changed = true;
        }

        if (!changed) return;

        try
        {
            SaveStrings($"RuneFoundry saved {_campaign}'s closing screen.");

            RefreshFileStates();
            OverridesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "save the text", ex);
        }
    }

    /// <summary>
    /// Writes the mission's text into this mod's language file.
    ///
    /// The file is built from the game's own and then changed, so a mod that alters one
    /// mission name still ships a language file identical to retail everywhere else. If
    /// the edits are undone the copy becomes identical again and is dropped, which keeps
    /// a mod from claiming a 130 KB file it does not actually change.
    /// </summary>
    private void SaveText()
    {
        if (_loading || !_textDirty || _selected is null || _project is null || _strings is null) return;
        if (_session.Game is null) return;

        _textDirty = false;

        var mission = _selected.Mission;

        var undo = new List<(string Key, string? Was, string Now)>();

        var changed = Apply(mission.NameKey, NameBox.Text);
        changed |= Apply(mission.ObjectivesKey, ObjectivesBox.Text);

        foreach (var page in _pages) changed |= Apply(page.Page.TextKey, page.Text);

        if (!changed) return;

        // One entry for the whole edit rather than one per key: what the person did was
        // "changed this mission's text", and undoing half of that would be nonsense.
        if (undo.Count > 0)
        {
            void Put(bool forward)
            {
                using (Undo.Quiet())
                {
                    foreach (var (key, was, now) in undo)
                    {
                        var value = forward ? now : was;
                        if (value is null) _strings!.Remove(key);
                        else _strings!.Set(key, value);
                    }

                    SaveStrings($"Reset {mission} text in {_stringsPath}.");
                    RefreshNotes();
                    if (_selected?.Mission == mission) ShowDetail(_selected);
                }
            }

            Record($"{mission} text", mission, () => Put(true), () => Put(false));
        }

        try
        {
            SaveStrings($"Saved {mission} text into {_stringsPath}.");

            RefreshNotes();
            RefreshFileStates();
            OverridesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "save the text", ex);
        }

        bool Apply(string key, string value)
        {
            var was = _strings!.Get(key);
            if (was == value) return false;

            undo.Add((key, was, value));
            _strings.Set(key, value);
            return true;
        }
    }

    /// <summary>
    /// Writes this mod's language file, or drops it when the edits have cancelled out.
    ///
    /// A mod that ships a 130 KB string table identical to the game's would have it backed
    /// up, reported as changed and restored on uninstall, all for nothing — so an override
    /// that no longer says anything is removed rather than written.
    /// </summary>
    private void SaveStrings(string savedMessage)
    {
        if (_project is null || _strings is null || _session.Game is null) return;

        var target = _project.ResolveContentPath(_stringsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var bytes = _strings.ToBytes();
        var stock = _session.StockFile(_stringsPath) ?? _session.Game.ResolveDataPath(_stringsPath);

        if (File.Exists(stock) && bytes.AsSpan().SequenceEqual(File.ReadAllBytes(stock)))
        {
            if (_project.HasOverride(_stringsPath))
            {
                _project.RemoveOverride(_stringsPath);
                Status?.Invoke("RuneFoundry put the game's own text back.");
            }
            return;
        }

        File.WriteAllBytes(target, bytes);
        Status?.Invoke(savedMessage);
    }

    // ---- files -----------------------------------------------------------

    /// <summary>
    /// Selects what was right-clicked. Without this the menu would act on the previously
    /// selected mission, which is the kind of thing that resets the wrong one.
    /// </summary>
    private void OnMissionRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && !item.IsSelected) item.IsSelected = true;
    }

    /// <summary>
    /// Puts one mission back to stock: its map, its briefing recordings, its text entries
    /// and its scenario rule. Everything else in the mod is left alone.
    /// </summary>
    private void OnResetMission(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MissionRow row) row = _selected!;
        if (row is null || _project is null) return;

        var mission = row.Mission;
        var stock = StockStrings();
        var plan = MissionReset.Prepare(_project, mission, _strings, stock, ReadMapFor(mission));

        if (plan.IsEmpty)
        {
            Status?.Invoke($"{mission} is already as the game shipped it.");
            return;
        }

        if (!Ui.ConfirmReset(Owner, mission.ToString(),
                $"Discards {plan.Describe()}. The rest of the mod is left alone."))
            return;

        try
        {
            if (MissionReset.Apply(_project, mission, plan, _strings, stock))
                SaveStrings($"Reset {mission} text in {_stringsPath}.");
            _project.Save();

            // The panel is showing values that have just stopped being true.
            if (_selected == row) ShowDetail(row);

            RefreshNotes();
            RefreshFileStates();
            OverridesChanged?.Invoke();

            Status?.Invoke($"Reset {mission}. Discarded {plan.Describe()}.");
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "reset that mission", ex);
        }
    }

    /// <summary>The bytes of a mission's map as this mod has it, or null.</summary>
    private byte[]? ReadMapFor(CampaignMission mission)
    {
        if (_project?.HasOverride(mission.MapPath) != true) return null;

        try { return File.ReadAllBytes(_project.ResolveContentPath(mission.MapPath)); }
        catch { return null; }
    }

    private void OnReplaceMap(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        Replace(_selected.Mission.MapPath, "Warcraft II map (*.pud)|*.pud|All files (*.*)|*.*");
    }

    private void OnResetMap(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        Reset(_selected.Mission.MapPath);
    }

    /// <summary>
    /// Opens the map in whatever program handles .PUD files, usually a map editor.
    ///
    /// The mod's copy when there is one, so an edit lands on the file the game will load
    /// rather than on the game's own.
    /// </summary>
    /// <summary>
    /// Takes the game's map into the mod, then opens it.
    ///
    /// The same move the file tree calls "Copy and edit". Without it, opening a map in an
    /// editor edits the game's own file: every mod on the machine reads that, and the
    /// vault's copy is the only way back.
    /// </summary>
    private void OnCopyAndEditMap(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _project is null || _session.Game is null) return;

        var path = _selected.Mission.MapPath;

        try
        {
            _project.SeedFromGame(path, _session.Game, _session.Vault);
        }
        catch (Exception ex)
        {
            Ui.Failed(Window.GetWindow(this), "copy the map into this mod", ex);
            return;
        }

        RefreshFileStates();
        OverridesChanged?.Invoke();
        Status?.Invoke($"{Path.GetFileName(path)} is now this mod's own. Opening it.");

        OnOpenMap(sender, e);
    }

    private void OnOpenMap(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        // This mod's copy only. Handing an editor the game's own file would edit what
        // every mod on the machine reads from.
        if (_project?.HasOverride(_selected.Mission.MapPath) != true)
        {
            Status?.Invoke("This mission still uses the game's map. Copy it into the mod first.");
            return;
        }

        var path = _project.ResolveContentPath(_selected.Mission.MapPath);

        if (!File.Exists(path))
        {
            Status?.Invoke("There is no map file to open.");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Nothing registered for .PUD is the usual reason, and it is worth saying so.
            Status?.Invoke("Could not open the map: " + ex.Message);
        }
    }

    private void OnShowMap(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var path = _project?.HasOverride(_selected.Mission.MapPath) == true
            ? _project.ResolveContentPath(_selected.Mission.MapPath)
            : _session.Game?.ResolveDataPath(_selected.Mission.MapPath);

        if (path is null || !File.Exists(path)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status?.Invoke("Could not open Explorer: " + ex.Message);
        }
    }

    private void OnReplaceSpeech(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PageRow page)
            Replace(page.SpeechPath, "Wave audio (*.wav)|*.wav|All files (*.*)|*.*");
    }

    /// <summary>
    /// Silences a briefing page: the text stays, the voice goes.
    ///
    /// The clip is generated to the length of the one it replaces, because the page is held
    /// on screen while its recording plays — a short clip would flick past before anyone
    /// could read it.
    /// </summary>
    private void OnSilenceSpeech(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PageRow page) return;
        Silence(page.SpeechPath, $"{page.Label} is silent. Its text stays on screen.");
    }

    private void Silence(string relativePath, string message)
    {
        if (_project is null)
        {
            Ui.Error(Owner, "No project open", "Create or open a mod project first.");
            return;
        }

        try
        {
            var stock = _session.StockFile(relativePath);
            var original = stock is not null && File.Exists(stock) ? File.ReadAllBytes(stock) : null;

            _session.FileUndo.Record(_project, $"silence {Path.GetFileName(relativePath)}",
                new[] { relativePath },
                () => _project.WriteOverride(relativePath, original is null
                    ? WaveFile.Silence(TimeSpan.FromSeconds(1))
                    : WaveFile.SilenceLike(original)),
                refresh: AfterFileChange);

            Status?.Invoke(message);
            AfterFileChange();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "silence that", ex);
        }
    }

    private void OnResetSpeech(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PageRow page) Reset(page.SpeechPath);
    }

    private void Replace(string relativePath, string filter)
    {
        if (_project is null)
        {
            Ui.Error(Owner, "No project open", "Create or open a mod project first.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"Choose the file to use as {Path.GetFileName(relativePath)}",
            Filter = filter,
        };
        if (dialog.ShowDialog(Owner) != true) return;

        try
        {
            _session.FileUndo.Record(_project, $"replace {Path.GetFileName(relativePath)}",
                new[] { relativePath },
                () => _project.ImportOverride(relativePath, dialog.FileName),
                refresh: AfterFileChange);

            Status?.Invoke($"{relativePath} now comes from {Path.GetFileName(dialog.FileName)}.");
            AfterFileChange();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "replace that file", ex);
        }
    }

    private void Reset(string relativePath)
    {
        if (_project is null || !_project.HasOverride(relativePath)) return;

        if (!Ui.ConfirmReset(Owner, relativePath, "The game's own file is used instead."))
            return;

        try
        {
            _session.FileUndo.Record(_project, $"reset {Path.GetFileName(relativePath)}",
                new[] { relativePath },
                () => _project.RemoveOverride(relativePath),
                refresh: AfterFileChange);

            Status?.Invoke($"{relativePath} is the game's again.");
            AfterFileChange();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "reset that file", ex);
        }
    }

    private void AfterFileChange()
    {
        RefreshNotes();
        RefreshFileStates();
        OverridesChanged?.Invoke();
    }

    // ---- speech ----------------------------------------------------------

    /// <summary>The file behind a page's speech: this mod's if it has one, else the game's.</summary>
    private string SoundPath(PageRow page)
        => _project?.HasOverride(page.SpeechPath) == true
            ? _project.ResolveContentPath(page.SpeechPath)
            : _session.Game?.ResolveDataPath(page.SpeechPath) ?? "";

    private void OnPlaySpeech(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PageRow page) return;

        var path = SoundPath(page);
        var playing = _audio.IsPlaying_(path);

        var error = _audio.Toggle(path);
        if (error is not null) { Status?.Invoke(error); return; }

        Status?.Invoke(playing
            ? $"Stopped {Path.GetFileName(path)}."
            : $"Playing {Path.GetFileName(path)}.");
    }

    /// <summary>Stops anything playing, and writes out whatever was being typed.</summary>
    public void Leave()
    {
        SaveText();
        _audio.Stop();
    }

    // ---- a campaign's own artwork -----------------------------------------

    /// <summary>
    /// How faint the unhovered campaign picture is.
    ///
    /// Measured from the game's own art, twice. The first measurement compared every pixel
    /// the lit frame covers and came out at 36% to 44%, which drew a picture visibly fainter
    /// than the game's. It was wrong because three of the four shipped pairs are not one
    /// drawing at two opacities: the lit frame covers ground the dim frame never does, by
    /// 354,100 pixels on the human expansion and 441,068 on Tides of Darkness, and averaging
    /// those empty pixels in dragged the figure down.
    ///
    /// Compared only where both frames have something, the dim one carries 40%, 52%, 57% and
    /// 62% of the lit one's alpha. Only the orc expansion pair, the 40%, lines up pixel for
    /// pixel; the rest are separate drawings, so their ratio is a rough guide rather than a
    /// measurement of anything.
    ///
    /// The number here is higher than all of them, and set by eye against the game rather
    /// than by arithmetic. That is the right way round: the measurement describes four
    /// hand-drawn pairs, and what this has to do is dim one picture enough to read as
    /// unselected without looking broken. Judgement beats a contaminated average.
    ///
    /// One number for all four, because the spread between them is smaller than the
    /// difference a person would notice.
    /// </summary>
    private const double UnhoveredAlpha = 0.8;

    private Campaign? _campaign;
    private readonly List<CampaignArtRow> _artRows = new();


    private void ShowCampaign(Campaign campaign)
    {
        _campaign = campaign;
        _selected = null;
        _audio.Stop();

        Detail.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Collapsed;
        CampaignDetail.Visibility = Visibility.Visible;

        CampaignTitle.Text = campaign.ToString();
        ShowCoverage(campaign);
        CampaignSubtitle.Text =
            $"{campaign.MissionCount} missions. The picture below is what the campaign chooser "
            + "shows. Below that, each act has the card it shows in the mission list, the full-"
            + "screen title card the game holds on when the act begins, and its briefing splash.";

        BuildArtRows();
    }

    /// <summary>
    /// Fills the campaign's art rows.
    ///
    /// The two sheets are 27 MB and 28 MB, and this used to read one of them again for every
    /// row — six full decodes to cut six thumbnails, on the thread that draws the window. Now
    /// each sheet is read once, on a background thread, and everything handed back is frozen.
    /// </summary>
    /// <summary>
    /// Fills the campaign's art rows: the campaign picture, then one row per act holding its
    /// mission list card, its title card and its briefing splash.
    ///
    /// The sheets are 27 MB and 28 MB and the loose cards are 8 MB each, so all of it is read
    /// once on a background thread and handed back frozen. Reading a sheet per picture — which
    /// this did at first — meant fourteen decodes to draw six thumbnails.
    /// </summary>
    /// <summary>
    /// Which rebuild is the current one.
    ///
    /// Filling these lists reads megabytes and so happens off the drawing thread, which
    /// means two can be in flight at once if someone clicks twice. Whichever finished last
    /// used to win, and that is not always the one that was asked for last: a slow read of
    /// the previous selection could land after a fast read of the new one and put the wrong
    /// thing on screen. Each pass takes a number and drops its results if a newer pass has
    /// started since.
    /// </summary>
    private int _generation;

    private async void BuildArtRows()
    {
        var generation = ++_generation;

        _artRows.Clear();
        CampaignArtList.ItemsSource = null;

        if (_campaign is null || _session.Game is null) return;

        var campaign = _campaign;

        // Both halves of the campaign picture are read. The pointed-at one is not a row of
        // its own: it is shown beside the picture it belongs to.
        var wanted = CampaignArt.For(campaign.Id)
            .Select(picture => (
                Picture: picture,
                Mine: _session.EffectiveFile(_project, picture.Image),
                Stock: _session.StockFile(picture.Image),
                Atlas: picture.Atlas is null ? null : _session.EffectiveFile(_project, picture.Atlas)))
            .ToList();

        var built = await Busy.While("Reading the campaign's artwork…", () =>
        {
            var sheets = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            var atlases = new Dictionary<string, FrameAtlas>(StringComparer.OrdinalIgnoreCase);

            BitmapSource? Sheet(string? path)
            {
                if (path is null || !File.Exists(path)) return null;
                if (sheets.TryGetValue(path, out var had)) return had;

                var loaded = AtlasImage.Load(path);
                sheets[path] = loaded;
                return loaded;
            }

            var made = new List<(CampaignPicture Picture, IconRect? Rect, BitmapSource? Thumb, bool Changed)>();

            foreach (var (picture, mine, stock, atlasPath) in wanted)
            {
                IconRect? rect = null;

                if (picture.InSheet && atlasPath is not null && File.Exists(atlasPath))
                {
                    if (!atlases.TryGetValue(atlasPath, out var atlas))
                        atlases[atlasPath] = atlas = FrameAtlas.Load(atlasPath);
                    rect = atlas.Rect(picture.Frame!);
                }

                BitmapSource? thumb = null;
                var changed = false;

                if (Sheet(mine) is { } sheet)
                {
                    if (!picture.InSheet)
                    {
                        // A whole file: the picture is the file, and any difference in bytes
                        // is a replacement — cheaper and surer than comparing pixels.
                        rect = new IconRect(0, 0, sheet.PixelWidth, sheet.PixelHeight);
                        thumb = Shrink(sheet);
                        changed = mine is not null && stock is not null && !SameFile(mine, stock);
                    }
                    else if (rect is { } box
                             && box.X + box.Width <= sheet.PixelWidth
                             && box.Y + box.Height <= sheet.PixelHeight)
                    {
                        var area = new Int32Rect(box.X, box.Y, box.Width, box.Height);

                        var cut = new CroppedBitmap(sheet, area);
                        cut.Freeze();
                        thumb = cut;

                        if (Sheet(stock) is { } original)
                            changed = !AtlasImage.SameArea(sheet, original, area);
                    }
                }

                made.Add((picture, rect, thumb, changed));
            }

            return made;
        },
        ex => Status?.Invoke("Could not read the campaign's artwork: " + ex.Message));

        // The person may have moved on while that was running.
        // Two guards, because they catch different things. The campaign check drops a read
        // for a campaign nobody is looking at any more; the generation check drops an older
        // read of the same campaign that happened to finish after a newer one.
        if (built is null || _campaign != campaign || generation != _generation) return;

        ArtSlot Slot(int index)
        {
            var (picture, rect, thumb, changed) = built[index];
            return new ArtSlot
            {
                Picture0 = picture,
                Label = picture.Label,
                Shape = rect is { } r ? $"{r.Width} x {r.Height}" : "not in this build",
                Thumbnail = thumb,
                Changed = changed,
                Hovered = CampaignArt.SketchHovered(campaign.Id) is { } other
                          && CampaignArt.Sketch(campaign.Id)?.Frame == picture.Frame
                    ? other
                    : null,
                WantsLightBacking = picture.Frame?.StartsWith("sketch", StringComparison.Ordinal) == true,
            };
        }

        // The campaign picture first, then the acts, each with its cards.
        var rows = new List<CampaignArtRow>();

        // The pointed-at picture rides along with the one it belongs to, so it is taken out
        // of the run before anything is counted off it.
        var lit = CampaignArt.SketchHovered(campaign.Id);
        var litIndex = lit is null
            ? -1
            : built.FindIndex(b => b.Picture.Frame == lit.Frame);

        var litThumb = litIndex >= 0 ? built[litIndex].Thumb : null;
        var litChanged = litIndex >= 0 && built[litIndex].Changed;

        if (litIndex >= 0) built.RemoveAt(litIndex);

        var at = 0;

        if (built.Count > 0 && !ActNumber(built[0].Picture.Frame).HasValue)
        {
            var slot = Slot(at++);

            slot.HoveredThumbnail = litThumb;

            // Either half being the mod's own makes the pair the mod's own: they are
            // written together and read as one picture.
            if (litChanged) slot.Changed = true;

            rows.Add(new CampaignArtRow { Label = "Campaign picture", Slots = new[] { slot } });
        }

        for (var act = 1; act <= CampaignArt.Acts && at + 1 < built.Count + 1; act++)
        {
            // Mission list card, title card and briefing splash, in the order the game shows them.
            var slots = new List<ArtSlot>();
            for (var i = 0; i < 3 && at < built.Count; i++) slots.Add(Slot(at++));
            if (slots.Count == 0) break;

            var key = CampaignArt.ActTitleKey(campaign.Id, act);

            // Eight of these ship, not sixteen: both campaigns of a race read the same one.
            var race = campaign.Id.EndsWith("orc", StringComparison.Ordinal) ? "orc" : "human";

            foreach (var slot in slots.Where(s => s.Label == "Briefing splash"))
                slot.Note = $"Shown before {CampaignArt.ActRange(campaign.Id, act)}. "
                            + $"Shared with the other {race} campaigns.";

            var inAct = CampaignArt.MissionsInAct(campaign.Id, act);
            var replaced = _rows
                .Where(r => r.Mission is { } m && m.Campaign.Id == campaign.Id && inAct.Contains(m.Number))
                .Count(r => r.Mission is { } m && Replaces(m));

            rows.Add(new CampaignArtRow
            {
                Label = $"Act {act}",
                Coverage = $"{CampaignArt.ActRange(campaign.Id, act)} · "
                           + (replaced == 0 ? "none replaced"
                              : replaced == inAct.Count ? "all replaced"
                              : $"{replaced} of {inAct.Count} replaced"),
                Slots = slots,
                TitleKey = key,
                Title = _strings?.Get(key) ?? "",
                TitleChanged = TitleDiffersFromGame(key),
            });
        }

        _artRows.AddRange(rows);
        CampaignArtList.ItemsSource = _artRows;

        CampaignArtNote.Text =
            "You replace each picture on its own. RuneFoundry scales what you supply to the "
            + "size beside it.";

        MoviesPane.Describe("Movies and sound",
            "What this campaign plays. " + MediaListView.MovieAdvice
            + " The act fanfare is a sound of its own. It holds each act's title card on screen.");

        ShowEpilogue(campaign);
        TimingPane.Refresh(_project, campaign.Id);
        MoviesPane.Refresh(_project, GameMedia.For(campaign.Id));
    }

    /// <summary>A thumbnail-sized copy, so an 8 MB card is not held at full size per row.</summary>
    private static BitmapSource Shrink(BitmapSource image)
    {
        var scale = 240.0 / Math.Max(1, image.PixelWidth);
        if (scale >= 1) return image;

        var small = new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale));
        small.Freeze();
        return small;
    }

    private static bool SameFile(string one, string two)
    {
        try
        {
            return new FileInfo(one).Length == new FileInfo(two).Length
                   && Hashing.Sha256File(one) == Hashing.Sha256File(two);
        }
        catch
        {
            return false;
        }
    }

    private IconRect? RectOf(CampaignPicture picture)
    {
        try
        {
            var path = _session.EffectiveFile(_project, picture.Atlas);
            return path is null ? null : FrameAtlas.Load(path).Rect(picture.Frame);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The mod's copy of a sheet if it has one, else the game's own.</summary>

    private BitmapSource? Cut(CampaignPicture picture)
    {
        try
        {
            var image = _session.EffectiveFile(_project, picture.Image);
            if (image is null || !File.Exists(image)) return null;
            if (RectOf(picture) is not { } rect) return null;

            var sheet = AtlasImage.Load(image);
            if (rect.X + rect.Width > sheet.PixelWidth || rect.Y + rect.Height > sheet.PixelHeight)
                return null;

            var cut = new CroppedBitmap(sheet,
                new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height));
            cut.Freeze();
            return cut;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Whether this frame has actually been drawn over, not just the sheet adopted.</summary>
    private bool DiffersFromStock(CampaignPicture picture)
    {
        try
        {
            if (_project?.HasOverride(picture.Image) != true) return false;
            if (RectOf(picture) is not { } rect) return false;

            var stock = _session.StockFile(picture.Image);
            if (stock is null || !File.Exists(stock)) return false;

            var area = new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height);
            return !AtlasImage.SameArea(
                AtlasImage.Load(_project.ResolveContentPath(picture.Image)),
                AtlasImage.Load(stock), area);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Puts a picture of the author's choosing in one slot.
    ///
    /// Two shapes behind one button. A mission list card is a rectangle of a shared sheet, so
    /// it is drawn into and the sheet keeps its size and format. A title card is a file of its own, so the
    /// file is replaced — scaled to the size the game ships, because everything else on that
    /// screen is laid out against it.
    /// </summary>
    private async void OnReplaceCampaignArt(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArtSlot slot) return;
        if (_project is null || _session.Game is null || _campaign is null) return;

        var shape = SizeOf(slot.Picture0);
        if (shape is null)
        {
            Ui.Error(Owner, "Nothing to replace", "This build of the game has no such picture.");
            return;
        }

        var (width, height) = shape.Value;

        var what = $"{_campaign}'s {slot.Label.ToLowerInvariant()}";
        if (PictureChooser.Ask(Owner, what, width, height) is not { } chosen) return;

        // The campaign chooser draws two pictures: one as the pointer rests on it and a
        // fainter one when it does not. They are separate drawings in the game's art, so
        // replacing one alone leaves the author's art showing in one state and Blizzard's
        // in the other.
        //
        // Either half of the pair stands for both. Keying only on the unhovered one meant
        // replacing the hovered picture wrote a single frame and looked like it had worked.
        var plain = _campaign is null ? null : CampaignArt.Sketch(_campaign.Id);
        var lit = _campaign is null ? null : CampaignArt.SketchHovered(_campaign.Id);

        var isSketch = plain is not null && lit is not null
                       && (plain.Frame == slot.Picture0.Frame || lit.Frame == slot.Picture0.Frame);

        if (!isSketch) { plain = null; lit = null; }

        var composed = await Working.While(Owner,
            $"Importing {Path.GetFileName(chosen)}…",
            () =>
            {
                var picture = AtlasImage.Load(chosen);
                var made = new List<ComposedArt>();

                if (lit is not null && plain is not null)
                {
                    // The picture goes in whole where the pointer rests, and faded where it
                    // does not, whichever of the two the author picked.
                    //
                    // Both frames are drawn into one sheet in one pass. Composing them
                    // separately gives two whole sheets, each holding one of the changes,
                    // and writing both to the one path leaves only the second: the pair
                    // looked replaced on the card and the game still lit up Blizzard's.
                    if (ComposePair(lit, picture, plain, AtlasImage.Fade(picture, UnhoveredAlpha))
                        is { } pair)
                        made.Add(pair);
                }
                else if (ComposeArt(slot.Picture0, picture) is { } only)
                {
                    made.Add(only);
                }

                return made;
            },
            ex => Ui.Failed(Owner, "replace that picture", ex));

        if (composed is null || composed.Count == 0) return;

        try
        {
            CommitArt($"replace {slot.Label.ToLowerInvariant()}", composed);

            Status?.Invoke($"{_campaign}: {slot.Label.ToLowerInvariant()} now uses "
                           + $"{Path.GetFileName(chosen)}, scaled to {width} x {height}"
                           + (isSketch
                               ? ". Both the pointed-at picture and the fainter one beside it were written."
                               : "."));

            OverridesChanged?.Invoke();
            BuildArtRows();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "replace that picture", ex);
        }
    }

    /// <summary>The size a picture has to end up, whether it is a frame or a whole file.</summary>
    private (int Width, int Height)? SizeOf(CampaignPicture picture)
    {
        if (picture.InSheet)
            return RectOf(picture) is { } rect ? (rect.Width, rect.Height) : null;

        try
        {
            var path = _session.EffectiveFile(_project, picture.Image);
            if (path is null || !File.Exists(path)) return null;

            var image = AtlasImage.Load(path);
            return (image.PixelWidth, image.PixelHeight);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes a picture, into a sheet or over a whole file.</summary>
    /// <summary>One finished picture, ready to be written.</summary>
    private readonly record struct ComposedArt(CampaignPicture Picture, byte[] Bytes);

    /// <summary>
    /// Works out the bytes a picture becomes, without writing anything.
    ///
    /// Split from the writing so it can run off the thread that draws the window. This is
    /// the slow half: the campaign sheets are 27 and 28 MB, and decoding one, drawing into
    /// it and encoding it again is seconds of work that used to happen with the window
    /// frozen and nothing on screen to say why.
    /// </summary>
    private ComposedArt? ComposeArt(CampaignPicture picture, BitmapSource image)
    {
        if (_project is null || _session.Game is null) return null;

        var path = _session.EffectiveFile(_project, picture.Image);
        if (path is null || !File.Exists(path)) return null;

        if (!picture.InSheet)
        {
            var original = AtlasImage.Load(path);
            return new ComposedArt(picture,
                AtlasImage.Resized(image, original.PixelWidth, original.PixelHeight, original.Format));
        }

        if (RectOf(picture) is not { } rect) return null;

        var sheet = AtlasImage.Load(path);
        return new ComposedArt(picture, AtlasImage.ReplaceFrames(sheet, image,
            new[] { new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height) }));
    }

    /// <summary>
    /// Draws two pictures into one sheet.
    ///
    /// Both frames live in the same 27 MB sheet, so this reads it once and writes into it
    /// twice, and what comes back is a single file holding both changes.
    /// </summary>
    private ComposedArt? ComposePair(CampaignPicture first, BitmapSource firstArt,
                                     CampaignPicture second, BitmapSource secondArt)
    {
        if (_project is null || _session.Game is null) return null;
        if (!first.InSheet || !second.InSheet) return null;

        // One sheet, or this is not a pair and each half needs its own write.
        if (!string.Equals(first.Image, second.Image, StringComparison.OrdinalIgnoreCase)) return null;

        var path = _session.EffectiveFile(_project, first.Image);
        if (path is null || !File.Exists(path)) return null;

        if (RectOf(first) is not { } one || RectOf(second) is not { } two) return null;

        var sheet = AtlasImage.Load(path);
        var parts = new[]
        {
            (firstArt, new Int32Rect(one.X, one.Y, one.Width, one.Height)),
            (secondArt, new Int32Rect(two.X, two.Y, two.Width, two.Height)),
        };

        return new ComposedArt(first, AtlasImage.ReplaceFrames(sheet, parts));
    }

    /// <summary>
    /// Writes composed pictures into the mod, as one undoable step.
    ///
    /// On the drawing thread deliberately: recording an undo entry raises the stack's
    /// Changed event, and that reaches bindings which cannot be touched from anywhere else.
    /// </summary>
    private void CommitArt(string describe, IReadOnlyList<ComposedArt> art)
    {
        if (_project is null || _session.Game is null || art.Count == 0) return;

        _session.FileUndo.Record(_project, describe,
            art.Select(a => a.Picture.Image).Distinct().ToList(),
            () =>
            {
                foreach (var (picture, bytes) in art)
                {
                    _project.WriteOverride(picture.Image, bytes);

                    // The description is not changed, since the frame stays where it is, but
                    // the mod needs its own copy beside the sheet it now owns.
                    if (picture.Atlas is { } atlas && !_project.HasOverride(atlas))
                        _project.SeedFromGame(atlas, _session.Game, _session.Vault);
                }
            },
            refresh: () => { BuildArtRows(); OverridesChanged?.Invoke(); });
    }

    /// <summary>
    /// Puts a whole act back: both its pictures and its name.
    ///
    /// An act is those things together, so restoring one and not the others would leave a
    /// title card lettered with a name the act no longer has.
    /// </summary>
    private async void OnResetCampaignArt(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CampaignArtRow row) return;
        if (_project is null || _campaign is null) return;

        var parts = new List<string>();
        if (row.Slots.Any(s => s.Changed)) parts.Add(row.Slots.Count > 1 ? "its pictures" : "its picture");
        if (row.TitleChanged) parts.Add("its name");

        if (!Ui.ConfirmReset(Owner, $"{_campaign}'s {row.Label.ToLowerInvariant()}",
                $"The game's own {string.Join(" and ", parts)} come back."))
            return;

        try
        {
            var pictures = new List<CampaignPicture>();

            foreach (var slot in row.Slots)
            {
                pictures.Add(slot.Picture0);

                // The faint copy is made from the lit one, so it goes back with it.
                if (CampaignArt.Sketch(_campaign.Id)?.Frame == slot.Picture0.Frame
                    && CampaignArt.SketchHovered(_campaign.Id) is { } lit)
                    pictures.Add(lit);
            }

            if (!await RestorePictures($"reset {row.Label.ToLowerInvariant()}", pictures)) return;

            if (row.TitleKey is { } key) RestoreTitle(key);

            Status?.Invoke($"{_campaign}: {row.Label.ToLowerInvariant()} is back to the game's own.");
            OverridesChanged?.Invoke();
            BuildArtRows();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "reset that", ex);
        }
    }

    /// <summary>
    /// Restores one picture. A whole file is simply dropped from the mod; a frame has the
    /// game's pixels drawn back into it, so the other frames in the same sheet are untouched.
    /// </summary>
    /// <summary>
    /// Works out what a picture looks like with the game's own frame back in it.
    ///
    /// Null means there is nothing to compose: either the mod does not have the file, or the
    /// picture is a whole file and putting it back is dropping the override rather than
    /// drawing anything. <see cref="RestoreWholeFiles"/> handles that half.
    ///
    /// Split from the writing for the same reason as <see cref="ComposeArt"/>: drawing into a
    /// 28 MB sheet and encoding it again is seconds of work.
    /// </summary>
    /// <summary>
    /// Puts the game's own frames back into one sheet.
    ///
    /// Every picture handed in must live in the same sheet, and they are all written in one
    /// pass. Restoring them one at a time gives a whole sheet per picture, each holding a
    /// single frame's worth of the repair, and writing those to the one path keeps only the
    /// last — which for a campaign picture put half the pair back and left the other half
    /// as the mod's.
    /// </summary>
    private ComposedArt? ComposeRestore(IReadOnlyList<CampaignPicture> pictures)
    {
        if (_project is null || pictures.Count == 0) return null;

        var first = pictures[0];
        if (!_project.HasOverride(first.Image)) return null;

        var stock = _session.StockFile(first.Image);
        if (stock is null || !File.Exists(stock)) return null;

        var original = AtlasImage.Load(stock);
        var parts = new List<(BitmapSource Picture, Int32Rect Frame)>();

        foreach (var picture in pictures)
        {
            if (!picture.InSheet) continue;
            if (RectOf(picture) is not { } rect) continue;

            var area = new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height);
            if (area.X + area.Width > original.PixelWidth
                || area.Y + area.Height > original.PixelHeight)
                continue;

            var cut = new CroppedBitmap(original, area);
            cut.Freeze();

            parts.Add((cut, area));
        }

        if (parts.Count == 0) return null;

        var mine = AtlasImage.Load(_project.ResolveContentPath(first.Image));
        return new ComposedArt(first, AtlasImage.ReplaceFrames(mine, parts));
    }

    /// <summary>Drops the overrides for pictures that are whole files, which needs no drawing.</summary>
    private void RestoreWholeFiles(IEnumerable<CampaignPicture> pictures)
    {
        if (_project is null) return;

        foreach (var picture in pictures)
            if (!picture.InSheet && _project.HasOverride(picture.Image))
                _project.RemoveOverride(picture.Image);
    }

    /// <summary>
    /// Puts pictures back, behind the working overlay when there is drawing to do.
    ///
    /// Whole files are dropped outright and cost nothing, so the overlay only appears when
    /// a sheet has to be redrawn.
    /// </summary>
    private async Task<bool> RestorePictures(string describe, IReadOnlyList<CampaignPicture> pictures)
    {
        if (_project is null) return false;

        var inSheet = pictures.Where(p => p.InSheet && _project.HasOverride(p.Image)).ToList();
        var whole = pictures.Where(p => !p.InSheet && _project.HasOverride(p.Image)).ToList();

        List<ComposedArt> composed = new();

        if (inSheet.Count > 0)
        {
            // Grouped by sheet: two frames of one sheet are one write, not two.
            var made = await Working.While(Owner, "Putting the game's picture back…",
                () => inSheet
                    .GroupBy(p => p.Image, StringComparer.OrdinalIgnoreCase)
                    .Select(sheet => ComposeRestore(sheet.ToList()))
                    .OfType<ComposedArt>()
                    .ToList(),
                ex => Ui.Failed(Owner, "reset that", ex));

            if (made is null) return false;
            composed = made;
        }

        var paths = composed.Select(a => a.Picture.Image)
            .Concat(whole.Select(p => p.Image))
            .Distinct()
            .ToList();

        if (paths.Count == 0) return true;

        _session.FileUndo.Record(_project, describe, paths,
            () =>
            {
                foreach (var (picture, bytes) in composed) _project.WriteOverride(picture.Image, bytes);
                RestoreWholeFiles(whole);
            },
            refresh: () => { BuildArtRows(); OverridesChanged?.Invoke(); });

        return true;
    }

    /// <summary>Whether this mod's language file says something different from the game's.</summary>
    private bool TitleDiffersFromGame(string key)
    {
        if (_project?.HasOverride(_stringsPath) != true || _strings is null) return false;

        var stock = StockStrings();
        return stock is not null && _strings.Get(key) != stock.Get(key);
    }

    /// <summary>
    /// Puts one act's name back to the game's wording, and says whether it had to.
    ///
    /// Only this key — the language file holds every mission's text too, and dropping the
    /// whole override would undo work that has nothing to do with this act.
    /// </summary>
    private bool RestoreTitle(string key)
    {
        if (_strings is null || StockStrings() is not { } stock) return false;

        var wanted = stock.Get(key);
        if (_strings.Get(key) == wanted) return false;

        if (wanted is null) _strings.Remove(key);
        else _strings.Set(key, wanted);

        SaveStrings($"Restored {key} in {_stringsPath}.");
        return true;
    }

    /// <summary>The act a frame belongs to, or null when it is not an act picture.</summary>
    private static int? ActNumber(string? frame)
    {
        if (frame is null) return null;

        var cut = frame.LastIndexOf("_act", StringComparison.Ordinal);
        if (cut < 0) return null;

        return int.TryParse(frame[(cut + 4)..], out var act) ? act : null;
    }

    /// <summary>
    /// Saves an act's name into this mod's language file.
    ///
    /// Committed when the box loses focus rather than on every keystroke, so the undo entry
    /// is the rename rather than each letter of it.
    /// </summary>
    private void OnActTitleCommitted(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CampaignArtRow row) return;
        if (row.TitleKey is not { } key || _project is null || _strings is null || _campaign is null) return;

        var was = _strings.Get(key);
        if (was == row.Title) return;

        var campaign = _campaign;

        void Put(string? value)
        {
            using (Undo.Quiet())
            {
                if (value is null) _strings!.Remove(key);
                else _strings!.Set(key, value);

                SaveStrings($"Renamed {key} in {_stringsPath}.");
                if (_campaign == campaign) { row.Title = value ?? ""; BuildArtRows(); }
            }
        }

        _strings.Set(key, row.Title);
        SaveStrings($"Renamed {key} in {_stringsPath}.");

        // The row has to know straight away, or Reset stays greyed until the campaign is
        // selected again — which looks like the rename did not take.
        row.TitleChanged = TitleDiffersFromGame(key);

        var now = row.Title;
        if (!Undo.Suspended)
            Undo.Push(new Edit($"{campaign} {row.Label.ToLowerInvariant()} name",
                () => Put(now), () => Put(was),
                () => { Reveal?.Invoke(); ShowCampaign(campaign); }));

        Status?.Invoke($"{campaign}: {row.Label.ToLowerInvariant()} is now \"{row.Title}\".");
    }

    /// <summary>
    /// Puts one picture back, leaving the rest of the act alone.
    ///
    /// Separate from resetting the act because the three pictures are edited separately —
    /// someone who redrew one card and kept the game's other should be able to undo the one
    /// without losing the act's name too.
    /// </summary>
    private async void OnResetSlot(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArtSlot slot) return;
        if (_project is null || _campaign is null) return;

        if (!Ui.ConfirmReset(Owner, $"{_campaign}'s {slot.Label.ToLowerInvariant()}",
                "The game's own picture comes back. Nothing else in this act changes."))
            return;

        try
        {
            var pictures = new List<CampaignPicture> { slot.Picture0 };

            // The faint copy is made from the lit one, so it goes back with it.
            if (CampaignArt.Sketch(_campaign.Id)?.Frame == slot.Picture0.Frame
                && CampaignArt.SketchHovered(_campaign.Id) is { } lit)
                pictures.Add(lit);

            if (!await RestorePictures($"reset {slot.Label.ToLowerInvariant()}", pictures)) return;

            Status?.Invoke($"{_campaign}: {slot.Label.ToLowerInvariant()} is back to the game's own.");
            OverridesChanged?.Invoke();
            BuildArtRows();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "reset that", ex);
        }
    }

    /// <summary>
    /// Fills the campaign's closing screen: the pages the game shows after the last mission
    /// is won, before the ending movie.
    ///
    /// The pages come from the language file rather than a fixed count, the way mission
    /// briefings do. Tides of Darkness closes on one page a race; Beyond the Dark Portal
    /// closes on two for the humans and three for the orcs.
    /// </summary>
    private void ShowEpilogue(Campaign campaign)
    {
        _epiloguePages = campaign.Epilogue(_strings)
            .Select(page => new PageRow
            {
                Page = page,
                Text = _strings?.Get(page.TextKey) ?? "",
                SpeechOverridden = _project?.HasOverride(page.SpeechPath) == true,
            })
            .ToList();

        EpilogueList.ItemsSource = _epiloguePages;
        EpiloguePanel.Visibility = _epiloguePages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        EpilogueNote.Text = _epiloguePages.Count == 1
            ? "The page the game shows when this campaign is won, before the ending movie."
            : $"The {_epiloguePages.Count} pages the game shows when this campaign is won, "
              + "before the ending movie.";
    }

    /// <summary>Whether this mod replaces anything about a mission.</summary>
    private bool Replaces(CampaignMission mission) =>
        _project is not null && mission.Files().Any(_project.HasOverride);

    /// <summary>
    /// Says how much of a campaign the mod actually replaces.
    ///
    /// The game plays fourteen missions whatever a mod does. Campaign length is not a number
    /// anywhere a mod can write, so a campaign of eight runs its eight and then carries on
    /// into the game's own ninth. That is worth saying plainly on the screen where someone
    /// would otherwise find it out by playing.
    ///
    /// Only phrased as a hand-off when the replaced missions are an unbroken run from the
    /// first. Replacing missions 1, 2 and 5 has no single point where the game takes over,
    /// so it gets a count instead of a sentence that would be wrong.
    /// </summary>
    private void ShowCoverage(Campaign campaign)
    {
        if (_project is null || _strings is null)
        {
            CampaignCoverage.Text = "";
            return;
        }

        var missions = campaign.Missions(_strings);
        var mine = missions.Where(Replaces).Select(m => m.Number).ToList();

        if (mine.Count == 0)
        {
            CampaignCoverage.Text =
                $"This mod replaces none of {campaign}'s {missions.Count} missions yet.";
            return;
        }

        // An unbroken run from the first mission: only then is there a single point
        // where the mod stops and the game takes over.
        var run = mine[0] == 1 && mine[^1] == mine.Count;

        CampaignCoverage.Text = run && mine.Count < missions.Count
            ? $"Replaces missions 1 to {mine.Count}. Missions {mine.Count + 1} to "
              + $"{missions.Count} are the game's own and play after yours."
            : run
                ? $"Replaces all {missions.Count} missions."
                : $"Replaces {mine.Count} of {missions.Count} missions. The rest are the game's own.";
    }
}
