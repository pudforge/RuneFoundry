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

/// <summary>
/// The unit and upgrade record editor: the list of records, the fields of the selected one,
/// and the name, description, icon and sounds that go with it.
///
/// A partial of <see cref="EditorView"/> rather than a view of its own — for now. The state
/// it works on (<c>_datTable</c>, <c>_datStrings</c>, <c>_datRecord</c>) and the controls it
/// drives are declared in the main part, and pulling them apart is a second, larger change:
/// two copies of "which record is selected" would be a worse bug than one long file.
///
/// Splitting the file first is what makes that change reviewable. Everything the record
/// editor touches is now in one place, so the shared state it depends on can be counted.
/// </summary>
public partial class EditorView
{
    private void ShowDat(string path, AssetKind kind, bool editable)
    {
        try
        {
            _datTable = kind == AssetKind.UnitData
                ? UnitDataFile.Load(path)
                : UpgradeDataFile.Load(path);
        }
        catch (Exception ex)
        {
            PreviewPlaceholder.Text = "Could not read this table: " + ex.Message;
            return;
        }

        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        DatPanel.Visibility = Visibility.Visible;

        SaveTextButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        SaveTextButton.IsEnabled = false;

        _datEditable = editable;
        PreviewInfo.Text += " · " + (editable
            ? "pick a record, then edit its fields"
            : ReadOnlyHint);

        _datStrings = LoadEditorStrings();
        FindChangedDatRecords(kind);

        _loadingUi = true;
        DatFilterBox.Text = "";
        DatFilterHint.Text = DatIsUnits ? "Filter units…" : "Filter upgrades…";
        DatFilterHint.Visibility = Visibility.Visible;
        BuildDatRecordList();
        DatRecordList.SelectedIndex = 0;
        _loadingUi = false;

        ShowDatRecord(0);
    }

    private bool DatIsUnits => _datTable is UnitDataFile;

    private void BuildDatRecordList()
    {
        if (_datTable is null) return;

        var filter = DatFilterBox.Text.Trim();
        var rows = new List<DatRecordRow>();

        for (var i = 0; i < _datTable.RecordCount; i++)
        {
            var name = DatRecordName(i);
            if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            rows.Add(new DatRecordRow(i, name, "", DatRecordEdited(i)));
        }

        DatRecordList.ItemsSource = rows;
    }

    /// <summary>
    /// What this record is called, in the words the game itself uses. The language file is
    /// the mod's copy if it has one, so renaming a unit renames it here too; a slot the
    /// strings leave blank falls back to the built-in list.
    /// </summary>
    private string DatRecordName(int record)
    {
        var named = DatIsUnits ? _datStrings?.UnitName(record) : _datStrings?.UpgradeName(record);
        if (!string.IsNullOrWhiteSpace(named))
            return named.Replace("\r", " ").Replace("\n", " ").Trim();

        return DatIsUnits ? DatNames.UnitName(record) : DatNames.UpgradeName(record);
    }

    private GameStrings? _datStrings;

    /// <summary>Records whose numbers differ from the game's own table.</summary>
    private readonly HashSet<int> _datChanged = new();

    /// <summary>
    /// Records this mod has only renamed.
    ///
    /// Kept apart from the numbers, because they are different edits with different ways
    /// back, and saying "changed from the game's table" about a unit whose table is
    /// untouched is not true.
    /// </summary>
    private readonly HashSet<int> _datRenamed = new();

    /// <summary>Whether this mod has changed anything at all about a record.</summary>
    private bool DatRecordEdited(int record) =>
        _datChanged.Contains(record) || _datRenamed.Contains(record);

    /// <summary>
    /// Works out which records this mod has changed, by comparing against the game's copy
    /// of the same table.
    ///
    /// The vault's copy is preferred over the one in the game folder: while a mod is
    /// applied, the game folder holds the mod's version, and comparing a file with itself
    /// would report that nothing had been touched.
    /// </summary>
    private void FindChangedDatRecords(AssetKind kind)
    {
        _datChanged.Clear();
        _datRenamed.Clear();
        if (_datTable is null || _selected is null) return;

        // Two separate questions, and a record can answer yes to either one. Renaming a
        // unit writes to the language file and leaves the table alone, so the numbers check
        // below gives up before it starts — and the rename check used to sit behind it,
        // which meant a unit that had only been renamed carried no mark at all.
        FindChangedDatNumbers(kind);
        FindRenamedDatRecords();
    }

    /// <summary>Records whose numbers this mod has changed.</summary>
    private void FindChangedDatNumbers(AssetKind kind)
    {
        if (_datTable is null || _selected is null) return;

        var relativePath = _selected.RelativePath;

        // A dot means "this mod changed this record". A file the mod does not include has
        // changed nothing, whatever is in the game folder: while a mod is applied the game
        // folder holds its files, so comparing those against stock would keep marking
        // records the mod no longer has any part in.
        if (_project is null || !_project.HasOverride(relativePath)) return;
        var stockPath = _session.Vault.OriginalFile(relativePath)
                        ?? (_session.Game is null ? null : _session.Game.ResolveDataPath(relativePath));

        if (stockPath is null || !File.Exists(stockPath)) return;

        try
        {
            DatTable stock = kind == AssetKind.UnitData
                ? UnitDataFile.Load(stockPath)
                : UpgradeDataFile.Load(stockPath);

            foreach (var (_, record, _, _, _) in _datTable.Diff(stock)) _datChanged.Add(record);
        }
        catch
        {
            // A table we cannot read is one we cannot compare; the list simply shows no marks.
        }
    }

    /// <summary>
    /// Also marks records the mod has only renamed.
    ///
    /// A unit's numbers live in the .dat table, but its name and description live in the
    /// language file. Comparing the table alone meant a renamed unit carried no mark, and
    /// the one edit a person is most likely to make first looked like no edit at all.
    /// </summary>
    private void FindRenamedDatRecords()
    {
        if (_datTable is null || _datStrings is null) return;
        if (_project is null || !_project.HasOverride(StringsPath)) return;

        var stockPath = _session.Vault.OriginalFile(StringsPath)
                        ?? (_session.Game is null
                            ? null
                            : _session.Game.ResolveDataPath(StringsPath));

        if (stockPath is null || !File.Exists(stockPath)) return;

        try
        {
            var stock = GameStrings.Load(stockPath);

            for (var record = 0; record < _datTable.RecordCount; record++)
            {
                if (Strings(_datStrings, record) != Strings(stock, record))
                    _datRenamed.Add(record);
            }
        }
        catch
        {
            // Same as above: unreadable means uncomparable, not changed.
        }
    }

    /// <summary>Every piece of text the editor lets you change for one record.</summary>
    private string Strings(GameStrings strings, int record) => DatIsUnits
        ? string.Join("\u0000",
            strings.UnitName(record), strings.UnitBuildLabel(record),
            strings.UnitTooltip(record), strings.UnitTooltipAdvice(record),
            strings.UnitTooltipFlavor(record), strings.UnitRequirement(record))
        : string.Join("\u0000",
            strings.UpgradeName(record), strings.UpgradeTooltip(record),
            strings.UpgradeRequirement(record));

    private void OnDatFilterChanged(object sender, TextChangedEventArgs e)
    {
        DatFilterHint.Visibility = DatFilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_loadingUi || _datTable is null) return;

        BuildDatRecordList();
    }

    private void OnDatRecordSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || DatRecordList.SelectedItem is not DatRecordRow row) return;
        ShowDatRecord(row.Index);
    }

    /// <summary>
    /// Renaming a unit or an upgrade. The name lives in the language file rather than in
    /// the table, so this writes Strings/enUS.json — which means the mod takes that file
    /// over, the same way editing any other stock file does.
    ///
    /// Written when the box loses focus rather than on every keystroke: a language file is
    /// rewritten whole, and doing that per character would be silly.
    /// </summary>
    private void OnDatNameEdited(object sender, TextChangedEventArgs e)
    {
        if (_loadingUi) return;
        _datNameDirty = true;
    }

    private bool _datNameDirty;

    private void OnDatNameCommitted(object sender, RoutedEventArgs e) => SaveDatName();

    private void SaveDatName()
    {
        if (!_datNameDirty || _loadingUi) return;
        _datNameDirty = false;

        if (_project is null || _session.Game is null || _datTable is null) return;

        var name = DatRecordTitle.Text.Trim();
        if (name.Length == 0)
        {
            SetStatus("A name cannot be empty.");
            _loadingUi = true;
            DatRecordTitle.Text = DatRecordName(_datRecord);
            _loadingUi = false;
            return;
        }

        var key = (DatIsUnits ? "unit_" : "upgrade_") + _datRecord;

        try
        {
            var target = _project.ResolveContentPath(StringsPath);
            if (!File.Exists(target))
            {
                // Nothing to build a language file out of but the game's own.
                var source = _session.Game.ResolveDataPath(StringsPath);
                if (!File.Exists(source))
                {
                    SetStatus($"The game has no {StringsPath} to edit.");
                    return;
                }
                _project.SeedFromGame(StringsPath, _session.Game, _session.Vault);
                _root?.RefreshOverrideMarks(_project, _session.Game);
                RefreshOverrides();
            }

            var strings = GameStrings.Load(target);

            // Two keys hold the same name. unit_<id> is the remaster's own, and
            // stat_txt_<n> is the classic string table carried into the language file —
            // which is the one the unit panel reads, so renaming only the first changed
            // nothing anyone could see. The stat_txt entries are numbered from 1 where the
            // table is numbered from 0, hence the extra one.
            var mirrored = DatIsUnits
                ? $"stat_txt_{DatNames.FirstUnitString + _datRecord + 1}"
                : $"stat_txt_{DatNames.FirstUpgradeString + _datRecord + 1}";

            var wrote = false;
            var undo = new List<(string Key, string? Was, string Now)>();

            // Read before anything is written: afterwards the record answers to its new
            // name, and the entry read "Grunt of the Watch renamed to Grunt of the Watch".
            var before = DatRecordName(_datRecord);

            foreach (var each in new[] { key, mirrored })
            {
                // Only keys the file already has: inventing one would put a string in front
                // of the game that it has no place for.
                if (each == mirrored && !strings.Has(each)) continue;
                if (strings.Get(each) == name) continue;

                undo.Add((each, strings.Get(each), name));
                strings.Set(each, name);
                wrote = true;
            }

            if (wrote)
            {
                strings.Save(target);
                _datStrings = strings;
                _datRenamed.Add(_datRecord);

                // Both keys move together — they are one rename — so they undo together too.
                RecordStringEdit($"{before} renamed to \"{name}\"", _datRecord, undo);
            }

        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "save the name", ex);
            return;
        }

        // The list shows the same name, so it has to follow.
        var selected = DatRecordList.SelectedIndex;
        _loadingUi = true;
        BuildDatRecordList();
        DatRecordList.SelectedIndex = selected;
        _loadingUi = false;

        SetStatus($"Renamed {key} to \"{name}\" in {StringsPath}.");
    }

    private const string StringsPath = "Strings/enUS.json";

    private void OnDatRecordMenuOpening(object sender, RoutedEventArgs e)
    {
        var record = (DatRecordList.SelectedItem as DatRecordRow)?.Index ?? -1;
        DatResetRecord.IsEnabled = record >= 0 && DatRecordEdited(record) && _datEditable;
        DatResetRecord.Header = record >= 0
            ? $"Reset {DatRecordName(record)} to game default"
            : "Reset to game default";
    }

    /// <summary>
    /// Puts one record back the way the game shipped it — every field, and the name with
    /// them, since the name is part of what this pane edits. The rest of the table, and
    /// every other record, is left alone.
    /// </summary>
    private void OnResetDatRecord(object sender, RoutedEventArgs e)
    {
        if (_datTable is null || _project is null || _session.Game is null || _selected is null) return;
        if (DatRecordList.SelectedItem is not DatRecordRow row) return;

        var relativePath = _selected.RelativePath;
        var stockPath = _session.StockFile(relativePath);
        if (!File.Exists(stockPath))
        {
            SetStatus("The game's copy of this table is not available to compare against.");
            return;
        }

        if (!Ui.ConfirmReset(Owner, row.Title, "Every change to this record goes back to the game's values."))
            return;

        try
        {
            DatTable stock = _asset?.Kind == AssetKind.UpgradeData
                ? UpgradeDataFile.Load(stockPath)
                : UnitDataFile.Load(stockPath);

            var restored = 0;
            foreach (var field in _datTable.Fields)
            {
                if (!stock.Has(field.Key)) continue;

                for (var component = 0; component < field.PerRecord; component++)
                {
                    var was = stock.GetRaw(field.Key, row.Index, component);
                    if (_datTable.GetRaw(field.Key, row.Index, component) == was) continue;

                    _datTable.SetRaw(field.Key, row.Index, was, component);
                    _datRestored.Add((field.Key, row.Index, component));
                    restored++;
                }
            }

            ResetRecordName(row.Index);

            _datChanged.Remove(row.Index);
            _datRenamed.Remove(row.Index);
            _textDirty = true;
            if (!SavePendingEdit(refresh: false)) return;

            _loadingUi = true;
            BuildDatRecordList();
            DatRecordList.SelectedIndex = DatRecordList.Items
                .Cast<DatRecordRow>().ToList().FindIndex(r => r.Index == row.Index);
            _loadingUi = false;

            ShowDatRecord(row.Index);
            SetStatus(restored == 0
                ? $"{DatRecordName(row.Index)} already matches the game."
                : $"Reset {DatRecordName(row.Index)} to the game's values ({restored} field(s)).");
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "reset that record", ex);
        }
    }

    private IconSheet? _icons;

    /// <summary>
    /// The picture this record draws, and the picker for changing it.
    ///
    /// The two tables differ in what "change" means. An upgrade names its icon in
    /// <c>upgrades.dat</c>, so picking one writes that number. A unit's icon is fixed in the
    /// game's code, so picking one instead points that icon's frame at the chosen art —
    /// the same swap the Icons tab makes, and it changes the icon everywhere it appears.
    /// </summary>
    private void ShowRecordIcon(int record)
    {
        _icons ??= IconSheet.Load(_session, _project);

        var icon = DatIsUnits
            ? IconNames.ForUnit(record)
            : _datTable is not null && _datTable.Has("icon")
                ? (int)_datTable.Get("icon", record)
                : null;

        _loadingUi = true;
        // The mod's own art, so an icon you have redrawn is the one offered here.
        DatIconBox.ItemsSource = _icons?.Choices;
        DatIconBox.SelectedItem = icon is null || _icons is null
            ? null
            : _icons.FindChoice(DatIsUnits ? _icons.DrawnBy(icon.Value) : icon.Value);
        DatIconBox.IsEnabled = _icons is not null && _datEditable && icon is not null;
        DatIconBox.ToolTip = icon is null
            ? "This record has no icon."
            : DatIsUnits
                ? $"This unit draws icon {icon}."
                : $"Icon {icon}. An upgrade names its own icon, so this changes which one it uses.";
        _loadingUi = false;
    }

    private void OnDatIconChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || _project is null || _session.Game is null || _icons is null) return;
        if (DatIconBox.SelectedItem is not IconPick pick) return;

        if (DatIsUnits)
        {
            if (IconNames.ForUnit(_datRecord) is not { } icon || _icons.DrawnBy(icon) == pick.Id) return;

            SwapIconArt(icon, pick.Id);
            return;
        }

        if (_datTable is null || !_datTable.Has("icon")) return;

        // An upgrade names its icon in upgrades.dat, but not everything that draws it reads
        // that field — the command card gets its icon from the game's own button table. So
        // both are done: the field is pointed at the chosen icon, and the icon the upgrade
        // started with is given the chosen art, which covers the places the field does not.
        var stockIcon = StockUpgradeIcon(_datRecord);

        if ((int)_datTable.Get("icon", _datRecord) != pick.Id)
        {
            _datTable.Set("icon", _datRecord, pick.Id);
            MarkDatRecordChanged(_datRecord);
            _textDirty = true;
            if (!SavePendingEdit(refresh: false)) return;
        }

        if (stockIcon is { } original && original != pick.Id && _icons?.DrawnBy(original) != pick.Id)
        {
            SwapIconArt(original, pick.Id);
            SetStatus($"{DatRecordName(_datRecord)} now uses icon {pick.Id}, "
                      + $"and icon {original} draws its art.");
            return;
        }

        SetStatus($"{DatRecordName(_datRecord)} now uses icon {pick.Id}.");
    }

    /// <summary>The icon an upgrade started with, from the game's own copy of the table.</summary>
    private int? StockUpgradeIcon(int record)
    {
        if (_session.Game is null) return null;

        var path = _session.StockFile("Rez/upgrades.dat");
        if (!File.Exists(path)) return null;

        try { return (int)UpgradeDataFile.Load(path).Get("icon", record); }
        catch { return null; }
    }

    /// <summary>
    /// Points one icon's frame at another's art, in this mod's copy of the sheet. The mask
    /// sheet goes with it: it carries the team-colour cut-out for the same frames, and a
    /// face without its mask is coloured as the icon it replaced.
    /// </summary>
    private void SwapIconArt(int icon, int from)
    {
        if (_project is null || _session.Game is null) return;

        try
        {
            foreach (var path in new[] { IconAtlas.FacePath, IconAtlas.MaskPath })
            {
                if (_project.HasOverride(path)) continue;
                if (!File.Exists(_session.Game.ResolveDataPath(path))) continue;

                _project.SeedFromGame(path, _session.Game, _session.Vault);
            }

            var stockFace = IconAtlas.Load(_session.StockFile(IconAtlas.FacePath)!);

            var facePath = _project.ResolveContentPath(IconAtlas.FacePath);
            var face = IconAtlas.Load(facePath);
            if (face.Remap(icon, from, stockFace) == 0)
            {
                SetStatus("That icon is not in the sheet.");
                return;
            }
            face.Save(facePath);

            var maskPath = _project.ResolveContentPath(IconAtlas.MaskPath);
            if (File.Exists(maskPath))
            {
                var stockMask = IconAtlas.Load(_session.StockFile(IconAtlas.MaskPath)!,
                                               IconAtlas.TeamMaskSuffix);

                var mask = IconAtlas.Load(maskPath, IconAtlas.TeamMaskSuffix);
                if (mask.Remap(icon, from, stockMask) > 0) mask.Save(maskPath);
            }

            _icons = IconSheet.Load(_session, _project);
            ShowRecordIcon(_datRecord);
            _root?.RefreshOverrideMarks(_project, _session.Game);
            RefreshOverrides();
            IconsPane.Refresh(_project);

            SetStatus($"Icon {icon} now draws icon {from}'s art.");
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "replace that icon", ex);
        }
    }

    // ---- the sounds that go with a unit -----------------------------------

    private SoundLibrary? _sounds;
    private readonly AudioPlayer _unitAudio = new();
    private List<UnitSoundRow> _soundRows = new();

    /// <summary>
    /// The sounds this unit plays.
    ///
    /// Which sound a unit uses is decided by the game — two tables of pickers indexed by
    /// unit type, neither of them data (W2R-RE-NOTES §5i) — so nothing here can send a unit
    /// to a different sound. What a mod changes is the file behind it, which is an ordinary
    /// replacement like any other.
    /// </summary>
    private void ShowRecordSounds(int record)
    {
        if (!DatIsUnits || _session.Game is null)
        {
            DatSoundPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _sounds ??= SoundLibrary.Scan(_session.Game);

        // Buildings have no per-type sounds to look up: the picker tables are 58 entries and
        // buildings start at 58, and tracing it found constants compiled into each event's
        // routine rather than a table (see BuildingSounds). So they get the building set,
        // labelled for what it is rather than pretending it belongs to this building.
        var building = BuildingSounds.For(record);

        _soundRows = building is not null
            ? new List<UnitSoundRow>
                {
                    new()
                    {
                        Kind = UnitSoundKind.Acknowledge,
                        FileName = building.FileName,
                        What = "While it works",
                        Path = _sounds.Find(building.FileName),
                    },
                }
                .Where(row => row.Path is not null)
                .ToList()
            : UnitSounds.For(record)
                .Select(sound => new UnitSoundRow
                {
                    Kind = sound.Kind,
                    FileName = sound.FileName,
                    Path = _sounds.Find(sound.FileName),
                })
                .Where(row => row.Path is not null)
                .ToList();

        foreach (var row in _soundRows) RefreshSoundRow(row);

        DatSoundList.ItemsSource = _soundRows;
        DatSoundPanel.Visibility = _soundRows.Count > 0 || UnitSounds.Known(record)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (BuildingSounds.IsBuilding(record))
        {
            DatSoundNote.Text = building is null
                ? "No sound for this unit."
                : "The noise this building makes while it works. "
                  + "Replace the file to change how this building sounds.";
            return;
        }

        // Several units share a voice, and saying whose it is explains why replacing one
        // file changes what a Paladin and a Lothar both say.
        var voice = UnitSounds.VoiceOf(record);

        DatSoundNote.Text = _soundRows.Count == 0
            ? "No sound for this unit."
            : $"The {voice?.Name ?? "unit"} voice. Every unit with this voice says the same lines.";
    }

    private void RefreshSoundRow(UnitSoundRow row)
    {
        if (row.Path is null) return;

        row.Overridden = _project?.HasOverride(row.Path) == true;
        row.IsPlaying = SoundFile(row) is { } file && _unitAudio.IsPlaying_(file);
    }

    /// <summary>This mod's copy of a sound if it has one, else the game's.</summary>
    private string? SoundFile(UnitSoundRow row)
    {
        if (row.Path is null || _session.Game is null) return null;

        return _project is not null && _project.HasOverride(row.Path)
            ? _project.ResolveContentPath(row.Path)
            : _session.Game.ResolveDataPath(row.Path);
    }

    private void OnPlayUnitSound(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UnitSoundRow row) return;
        if (SoundFile(row) is not { } file) return;

        var error = _unitAudio.Toggle(file);
        if (error is not null) SetStatus(error);

        foreach (var each in _soundRows) RefreshSoundRow(each);
    }

    /// <summary>
    /// Uses one of the game's own sounds for this slot.
    ///
    /// The unit plays a fixed file name, so the swap is a copy: the chosen sound's bytes go
    /// into the mod under the name the unit asks for. Copied from the game's own file rather
    /// than from whatever is in the game folder now, which may be another mod's.
    /// </summary>
    /// <summary>
    /// Makes one of a unit's lines silent, without the author having to produce an empty
    /// .wav themselves. The clip matches the original's format and length, so a slot that
    /// is timed against its audio still behaves.
    /// </summary>
    private void OnSilenceUnitSound(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UnitSoundRow row) return;
        if (row.Path is null || _project is null || _session.Game is null) return;

        try
        {
            AdoptIntoMod();

            var stock = _session.StockFile(row.Path);
            var original = stock is not null && File.Exists(stock) ? File.ReadAllBytes(stock) : null;

            _session.FileUndo.Record(_project, $"silence {row.FileName}", new[] { row.Path },
                () => _project.WriteOverride(row.Path, original is null
                    ? WaveFile.Silence(TimeSpan.FromSeconds(1))
                    : WaveFile.SilenceLike(original)),
                refresh: RefreshSounds);

            AfterSoundChanged(row, $"{row.FileName} is silent.");
        }
        catch (Exception ex)
        {
            Ui.Error(Window.GetWindow(this), "Could not silence that sound", ex.Message);
        }
    }

    private void OnSwapUnitSound(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UnitSoundRow row) return;
        if (row.Path is null || _project is null || _session.Game is null || _sounds is null) return;

        var picker = new SoundPickerWindow(_session.Game, _sounds, row.FileName) { Owner = Owner };
        if (picker.ShowDialog() != true || picker.Chosen is not { } chosen) return;

        var source = _session.StockFile(chosen);
        if (!File.Exists(source))
        {
            SetStatus($"{chosen} is missing from the game folder.");
            return;
        }

        try
        {
            _unitAudio.Stop();
            _session.FileUndo.Record(_project, $"{row.FileName} plays {Path.GetFileName(chosen)}",
                new[] { row.Path },
                () => _project.ImportOverride(row.Path, source),
                refresh: RefreshSounds);

            AfterSoundChanged(row, $"{row.FileName} now plays {Path.GetFileName(chosen)}.");
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "swap that sound", ex);
        }
    }

    private void OnReplaceUnitSound(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UnitSoundRow row) return;
        if (row.Path is null || _project is null) return;

        var dialog = new OpenFileDialog
        {
            Title = $"Choose the sound to use as {row.FileName}",
            Filter = "Wave audio (*.wav)|*.wav|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Owner) != true) return;

        try
        {
            _unitAudio.Stop();
            _session.FileUndo.Record(_project, $"replace {row.FileName}", new[] { row.Path },
                () => _project.ImportOverride(row.Path, dialog.FileName),
                refresh: RefreshSounds);

            AfterSoundChanged(row, $"{row.FileName} replaced.");
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "replace that sound", ex);
        }
    }

    private void OnResetUnitSound(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UnitSoundRow row) return;
        if (row.Path is null || _project is null) return;

        if (!Ui.ConfirmReset(Owner, row.FileName, "The game's own recording is used instead.")) return;

        try
        {
            _unitAudio.Stop();
            _session.FileUndo.Record(_project, $"reset {row.FileName}", new[] { row.Path },
                () => _project.RemoveOverride(row.Path),
                refresh: RefreshSounds);

            AfterSoundChanged(row, $"{row.FileName} is back to the game's sound.");
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "reset that sound", ex);
        }
    }

    private void AfterSoundChanged(UnitSoundRow row, string status)
    {
        RefreshSounds();
        SetStatus(status);
    }

    /// <summary>
    /// Redraws what a sound change touched.
    ///
    /// Separate from the status line so undo can call it: taking a change back should put
    /// the screen right without also writing "silent" under a sound that no longer is.
    /// </summary>
    private void RefreshSounds()
    {
        if (_project is not null && _session.Game is not null)
            _root?.RefreshOverrideMarks(_project, _session.Game);

        RefreshOverrides();
        foreach (var each in _soundRows) RefreshSoundRow(each);
    }

    // ---- the words that go with a record ---------------------------------

    private bool _datTextDirty;

    /// <summary>
    /// The line the game shows about this record.
    ///
    /// A unit has two strings: <c>_tooltip</c>, which is flavour ("they smell of toil"), and
    /// <c>_tooltip_advice</c>, which is the one that says what the unit is for. Only the
    /// second is worth editing, so only it is here. An upgrade has one string and it is
    /// already the useful one.
    ///
    /// The box appears only where the language file has that key: inventing one would put a
    /// string in front of the game that it has no place for.
    /// </summary>
    private void ShowRecordText(int record)
    {
        _datStrings ??= LoadEditorStrings();

        var key = TextKey(record);
        var has = _datStrings?.Has(key) == true;

        _loadingUi = true;

        DatTextPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        DatTextBox.Text = has ? _datStrings!.Get(key) ?? "" : "";
        DatTextBox.IsReadOnly = _project is null;
        DatTextBox.ToolTip = $"{key} in Strings/enUS.json.";

        _loadingUi = false;
        _datTextDirty = false;
    }

    /// <summary>The string worth editing: a unit's advice line, an upgrade's tooltip.</summary>
    private string TextKey(int record) =>
        DatIsUnits ? $"unit_{record}_tooltip_advice" : $"upgrade_{record}_tooltip";

    private void OnDatTextEdited(object sender, TextChangedEventArgs e)
    {
        if (!_loadingUi) _datTextDirty = true;
    }

    private void OnDatTextCommitted(object sender, RoutedEventArgs e) => SaveDatText();

    private void SaveDatText()
    {
        if (!_datTextDirty || _loadingUi) return;
        _datTextDirty = false;

        if (_project is null || _session.Game is null || _datTable is null) return;

        try
        {
            var target = _project.ResolveContentPath(StringsPath);
            if (!File.Exists(target))
            {
                if (!File.Exists(_session.Game.ResolveDataPath(StringsPath))) return;
                _project.SeedFromGame(StringsPath, _session.Game, _session.Vault);
                _root?.RefreshOverrideMarks(_project, _session.Game);
                RefreshOverrides();
            }

            var strings = GameStrings.Load(target);
            var key = TextKey(_datRecord);

            if (DatTextPanel.Visibility != Visibility.Visible) return;
            if (!strings.Has(key) || strings.Get(key) == DatTextBox.Text) return;

            var was = strings.Get(key);
            strings.Set(key, DatTextBox.Text);

            strings.Save(target);
            _datStrings = strings;
            SetStatus($"Saved the text for {DatRecordName(_datRecord)}.");

            RecordStringEdit($"{DatRecordName(_datRecord)} description", _datRecord,
                             new List<(string, string?, string)> { (key, was, DatTextBox.Text) });
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "save the text", ex);
        }
    }

    /// <summary>Puts a record's name back to the game's, in both keys that carry it.</summary>
    private void ResetRecordName(int record)
    {
        if (_project is null || _session.Game is null) return;
        if (!_project.HasOverride(StringsPath)) return;

        var stockPath = _session.StockFile(StringsPath);
        if (!File.Exists(stockPath)) return;

        try
        {
            var target = _project.ResolveContentPath(StringsPath);
            var strings = GameStrings.Load(target);
            var stock = GameStrings.Load(stockPath);

            var keys = DatIsUnits
                ? new[] { $"unit_{record}", $"stat_txt_{DatNames.FirstUnitString + record + 1}" }
                : new[] { $"upgrade_{record}", $"stat_txt_{DatNames.FirstUpgradeString + record + 1}" };

            var wrote = false;
            foreach (var key in keys)
            {
                if (stock.Get(key) is not { } was || strings.Get(key) == was) continue;

                strings.Set(key, was);
                wrote = true;
            }

            if (!wrote) return;

            strings.Save(target);
            _datStrings = strings;
        }
        catch (Exception ex)
        {
            SetStatus($"Reset the numbers, but could not restore the name: {ex.Message}");
        }
    }

    /// <summary>
    /// Says whether this mod renames a record, after the name has been written or put back.
    ///
    /// Taking it back is not simply "no longer renamed": the record may have been renamed
    /// before this session, so what the mark should say is whatever the language file now
    /// says, not what this one edit did.
    /// </summary>
    private void MarkDatRenamed(int record, bool renamed)
    {
        if (renamed) { _datRenamed.Add(record); return; }

        if (_datTable is null || _datStrings is null) { _datRenamed.Remove(record); return; }

        var stockPath = _session.Vault.OriginalFile(StringsPath)
                        ?? (_session.Game is null ? null : _session.Game.ResolveDataPath(StringsPath));

        if (stockPath is null || !File.Exists(stockPath)) { _datRenamed.Remove(record); return; }

        try
        {
            var stock = GameStrings.Load(stockPath);

            if (Strings(_datStrings, record) == Strings(stock, record)) _datRenamed.Remove(record);
            else _datRenamed.Add(record);
        }
        catch
        {
            _datRenamed.Remove(record);
        }
    }

    /// <summary>Marks a record as changed as soon as one of its fields is.</summary>
    private void MarkDatRecordChanged(int record)
    {
        if (!_datChanged.Add(record)) return;

        DatRecordNote.Text = "changed from the game's table";

        var selected = DatRecordList.SelectedIndex;
        _loadingUi = true;
        BuildDatRecordList();
        DatRecordList.SelectedIndex = selected;
        _loadingUi = false;
    }

    private void ShowDatRecord(int record)
    {
        SaveDatName();
        SaveDatText();
        _unitAudio.Stop();

        if (_datTable is null || record < 0 || record >= _datTable.RecordCount) return;

        _datRecord = record;

        _loadingUi = true;
        DatRecordTitle.Text = DatRecordName(record);
        _loadingUi = false;

        ShowRecordIcon(record);
        ShowRecordText(record);
        ShowRecordSounds(record);

        DatRecordNote.Text = _datTable is UnitDataFile units && units.IsEmpty(record)
            ? "unused slot, free for a mod to define"
            : _datChanged.Contains(record) && _datRenamed.Contains(record)
                ? "renamed, and changed from the game's table"
            : _datChanged.Contains(record) ? "changed from the game's table"
            : _datRenamed.Contains(record) ? "renamed by this mod"
            : "";

        var rows = _datTable.Fields
            .Select(field => new DatRow(_datTable, field, record))
            .ToList();

        foreach (var row in rows)
        {
            row.IsReadOnly = !_datEditable;
            row.Edited += MarkEdited;
            row.Edited += () => MarkDatRecordChanged(record);
            row.Committed += change => RecordDatEdit(change, record);
        }
        DatFieldList.ItemsSource = rows;
    }

    /// <summary>
    /// Shows ai.bin as its 84 named scripts. Edits accumulate in the parsed file and are
    /// only written when Save is pressed, so switching between scripts is free.
    /// </summary>
    private void ShowAi(string path, bool editable)
    {
        using var _perf = RuneFoundry.UI.Perf.Time("ShowAi");
        try
        {
            _aiFile = AiFile.Load(path);
        }
        catch (Exception ex)
        {
            PreviewPlaceholder.Text = "Could not read the AI scripts: " + ex.Message;
            return;
        }

        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        AiPanel.Visibility = Visibility.Visible;

        AiEditor.IsReadOnly = !editable;

        // The block editor is a grid of live controls, so making the text box read-only
        // did nothing for it and a stock file could be edited through the dropdowns. The
        // list stays enabled so it can still be scrolled and read; only the inputs lock.
        _aiEditable = editable;
        AiBlockButtons.IsEnabled = editable;
        // The group headers are outside the row templates, so they read this rather than
        // a property of a block.
        AiBlockList.Tag = editable;

        SaveTextButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        SaveTextButton.IsEnabled = false;

        AiHint.Text = editable
            ? "var / goto / sleep / wait, one per line. ; starts a comment."
            : ReadOnlyHint;

        var rows = BuildAiRows();

        // Back to whichever script was open last. Matched by slot rather than by position,
        // so it still finds the right one if the list ever shifts.
        var remembered = rows.FindIndex(r => r.Script.Index == _session.Settings.LastAiScript);
        if (remembered < 0) remembered = 0;

        _loadingUi = true;
        AiScriptList.ItemsSource = rows;
        AiScriptList.SelectedIndex = remembered;
        _loadingUi = false;

        _loadingUi = true;
        AiBlocksMode.IsChecked = true;
        _loadingUi = false;

        AiScriptList.ScrollIntoView(rows[remembered]);
        ShowAiScript(rows[remembered].Script);
    }

    /// <summary>
    /// The 84 slots, plus the routine the stubs jump into. That routine is not a slot, but
    /// five scripts do nothing except enter it — leaving it out means those five cannot be
    /// edited at all.
    /// </summary>
    private List<AiRow> BuildAiRows()
    {
        using var _perf = RuneFoundry.UI.Perf.Time("BuildAiRows");
        if (_aiFile is null) return new List<AiRow>();

        // Which scripts this mod already carries, so they can be marked. Read once from
        // the file on disk rather than per row: it is the same answer 84 times.
        var mine = MineInThisMod();

        var rows = _aiFile.Scripts.Select(s => ToAiRow(s, mine.Contains(s.Index))).ToList();

        if (_aiFile.SharedRoutine is { } routine)
        {
            var users = _aiFile.Scripts.Count(s => s.JumpsAwayTo == routine.Offset);
            rows.Add(new AiRow(routine, "Shared routine",
                $"@{routine.Offset} · used by {users} scripts · {routine.Instructions.Count} instructions"));
        }
        return rows;
    }

    /// <summary>
    /// The scripts this mod already carries a different version of.
    ///
    /// Compared against the game's own copy rather than against the file being edited, so
    /// it survives closing the project. Without it a mod with three scripts of its own
    /// looks exactly like a mod with none, in a list of eighty-four.
    /// </summary>
    private HashSet<int> MineInThisMod()
    {
        var mine = new HashSet<int>();

        if (_project?.HasOverride(AiScriptPath) != true) return mine;

        try
        {
            var stock = AiFile.Load(_session.RequireStock(AiScriptPath));

            for (var i = 0; i < AiFile.ScriptCount && i < (_aiFile?.Scripts.Count ?? 0); i++)
            {
                var a = stock.Scripts[i].OriginalBytes;
                var b = _aiFile!.Scripts[i].OriginalBytes;

                if (!a.AsSpan().SequenceEqual(b)) mine.Add(i);
            }
        }
        catch (Exception)
        {
            // A stock file we cannot read costs the marks, nothing else.
        }

        return mine;
    }

    private static AiRow ToAiRow(AiScript script, bool mine)
    {
        var note = script.IsStub
            ? $"${script.Index:X2} · jumps to shared code at {script.JumpsAwayTo}"
            : $"${script.Index:X2} · {script.Instructions.Count} instructions";
        if (script.SharedWith.Count > 0)
            note += " · shared with " + string.Join(", ", script.SharedWith.Select(AiTables.AiName));
        if (script.IsModified) note = "edited · " + note;
        else if (mine) note = "yours · " + note;

        return new AiRow(script, script.Name, note) { IsMine = mine };
    }

    private void ShowAiScript(AiScript script)
    {
        using var _perf = RuneFoundry.UI.Perf.Time("ShowAiScript");
        if (script.Index < 0 && _aiScript?.Index >= 0)
            SetStatus("This routine is shared. Editing it changes every script that jumps into it.");

        _aiScript = script;
        _buildChoices = null;

        _loadingUi = true;

        _aiBlocks = new System.Collections.ObjectModel.ObservableCollection<AiBlock>(
            script.Instructions.Select(AiBlock.From));
        foreach (var block in _aiBlocks)
        {
            block.BuildListLookup = LookUpBuildItem;
            block.BuildListChoices = BuildChoices;
            block.Advise = AdviseAiRow;
            block.Caution = CautionAiRow;
            block.PropertyChanged += OnAiBlockEdited;
        }
        RegroupAiBlocks();
        ShowAiRoom();

        AiEditor.Text = AiFile.ToText(script);

        _loadingUi = false;
    }

    /// <summary>
    /// Checks one row against the script it sits in. Null when nothing is wrong.
    /// </summary>
    private string? AdviseAiRow(AiInstruction instruction)
        => _aiScript is null ? null : AiAdvice.About(_aiScript, instruction);

    /// <summary>The same for what depends on the map rather than the script.</summary>
    private string? CautionAiRow(AiInstruction instruction)
        => _aiScript is null ? null : AiAdvice.Caution(_aiScript, instruction);

    /// <summary>Says what a build limit means for the current script.</summary>
    private string? LookUpBuildItem(int limit) => _aiScript?.DescribeBuildLimit(limit);

    private AiChoice[]? _buildChoices;

    /// <summary>
    /// The current script's build list as pickable entries, cached because every row with
    /// a build cursor asks for it. Empty when the script has no readable list.
    /// </summary>
    private AiChoice[] BuildChoices()
    {
        if (_buildChoices is not null) return _buildChoices;
        if (_aiScript is null || !_aiScript.HasBuildList) return _buildChoices = Array.Empty<AiChoice>();

        // The byte is a limit, so each choice is a stopping point: how far down the list
        // the computer may work. Offering positions instead made the list read as though
        // one entry were being queued, which is not what the game does.
        var list = _aiScript.BuildList;
        var choices = new List<AiChoice> { new(0, "nothing yet") };
        var seen = new Dictionary<string, int>();

        for (var i = 0; i < list.Length && i < AiFile.BuildListMax; i++)
        {
            var name = AiTables.ItemName(list[i]);

            // A list can ask for four Scout Towers. Numbering the repeats keeps the
            // stopping points apart without putting a bare position on every row.
            var times = seen[name] = seen.GetValueOrDefault(name) + 1;
            var label = times == 1 ? name : $"{name} ({Ordinal(times)})";

            choices.Add(new AiChoice((byte)(i + 1),
                i == list.Length - 1 ? $"everything, down to {label}" : $"down to {label}"));
        }

        return _buildChoices = choices.ToArray();
    }

    private static string Ordinal(int n) => n switch
    {
        2 => "2nd",
        3 => "3rd",
        _ => $"{n}th",
    };

    private void OnAiBlockEdited(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loadingUi) return;
        MarkEdited();

        // Changing what a row does can move the boundary between waves.
        if (e.PropertyName is nameof(AiBlock.Kind) or nameof(AiBlock.TargetChoice) or nameof(AiBlock.Value))
            RegroupAiBlocks();
    }

    /// <summary>
    /// Labels each row with the part of the script it belongs to, then regroups the list.
    ///
    /// These labels are not in the file — ai.bin is bytecode with no comments. They are
    /// inferred from a structure the scripts do consistently show: an opening block of
    /// variable assignments, then repeated build-wait-attack cycles. The boundary used is
    /// the attack launch, which 322 of the game's 480 waves reach immediately after a wait.
    /// </summary>
    private void RegroupAiBlocks()
    {
        using var _perf = RuneFoundry.UI.Perf.Time("RegroupAiBlocks");
        if (_aiBlocks is null) return;

        // Which row each jump is aiming at, found before the offsets move.
        //
        // A goto holds an absolute byte. Choosing "WAVE 2" writes down where WAVE 2 begins
        // at that moment, and inserting a row above it moves WAVE 2 without moving the
        // number, so the jump quietly starts pointing into the middle of the wave before.
        // Following the row instead of the byte is what makes the name mean something.
        var aimedAt = new Dictionary<AiBlock, AiBlock>();

        foreach (var block in _aiBlocks)
        {
            if (block.Opcode != (byte)AiOpcode.Goto) continue;

            var target = _aiBlocks.FirstOrDefault(b => b.Offset == (int)block.Value);
            if (target is not null) aimedAt[block] = target;
        }

        // Offsets run from just past the script's two header words.
        var offset = (_aiScript?.Offset ?? 0) + 4;

        var inSetup = true;
        var wave = 1;

        // The opening block sets each of its variables once. Taking "any leading run of
        // setup variables" instead let the block run on into the first wave, and worse,
        // deleting the row that happened to end the run handed those wave rows to the
        // block, where they are protected from editing and hidden inside it. A second
        // write to a variable already set is the boundary the scripts themselves draw.
        var alreadySet = new HashSet<byte>();
        var seenAnyRow = false;

        // A script with no launch at all cannot be divided by them; six shipped scripts
        // are like that, and so is one an author has stripped the launches out of.
        var launchesSomewhere = _aiBlocks.Any(b => b.IsAttackLaunch);

        foreach (var block in _aiBlocks)
        {
            block.Offset = offset;
            offset += block.Length;

            if (inSetup && block.Opcode == (byte)AiOpcode.Var
                        && AiTemplates.SetupVariables.Contains(block.Target)
                        && alreadySet.Add(block.Target))
            {
                block.Section = "SETUP";
                continue;
            }

            inSetup = false;

            if (block.Opcode == (byte)AiOpcode.Goto)
            {
                block.Section = "LOOP";
                continue;
            }

            // The launch ends a wave, so the next row begins the next one.
            //
            // Sleeps look like a tidier boundary and are not: they occur inside a cycle as
            // well as before one, and dividing on them gives 738 blocks for the file's 480
            // attack cycles. A fifth of the cycles have no sleep at all. The launch is the
            // only row that marks exactly one cycle each.
            //
            // Its weakness is scripts that never launch, where everything would be one
            // block. Those fall back to sleeps, which at least divides them.
            var boundary = launchesSomewhere
                ? block.IsAttackLaunch
                : block.Opcode == (byte)AiOpcode.Sleep && seenAnyRow;

            if (!launchesSomewhere && boundary) wave++;

            block.Section = $"WAVE {wave}";
            seenAnyRow = true;

            if (launchesSomewhere && boundary) wave++;
        }

        // Now that every row knows where it sits, each jump is pointed at its row again.
        // A jump that aimed outside this script, at the shared routine for instance, has no
        // row here and is left exactly as it was.
        foreach (var (jump, target) in aimedAt)
            if (jump.Value != (uint)target.Offset)
                jump.Value = (uint)target.Offset;

        // Where the script says it repeats from, which is not always where we start a wave.
        foreach (var block in _aiBlocks) block.IsLoopTarget = false;
        foreach (var target in aimedAt.Values) target.IsLoopTarget = true;

        PublishJumpTargets();

        // Rebuilding the view on every keystroke re-realises the whole list; refreshing
        // the existing one only re-evaluates the grouping.
        if (AiBlockList.ItemsSource is System.Windows.Data.ListCollectionView existing
            && ReferenceEquals(existing.SourceCollection, _aiBlocks))
        {
            existing.Refresh();
            return;
        }

        var view = new System.Windows.Data.ListCollectionView(_aiBlocks);
        // W2M_ABLATE=groups drops the section boxes, to measure what grouping costs. WPF
        // gives up a lot of its virtualisation once a list is grouped, so this is the first
        // thing to test when the AI tab feels slow.
        if (!RuneFoundry.UI.Perf.Skip("groups"))
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(AiBlock.Section)));

        var selected = AiBlockList.SelectedIndex;
        AiBlockList.ItemsSource = view;
        AiBlockList.SelectedIndex = selected;
    }

    /// <summary>
    /// Gives every row the list of places a goto can aim: the first instruction of each
    /// section. The closing loop is left out — a goto onto itself is a hang, not a jump.
    /// </summary>
    private void PublishJumpTargets()
    {
        if (_aiBlocks is null) return;

        var targets = new List<AiJumpTarget>();
        var previous = "";

        foreach (var block in _aiBlocks)
        {
            var section = block.Section;
            if (section != previous && section != "LOOP")
                targets.Add(new AiJumpTarget(block.Offset, $"{section} · byte {block.Offset}"));
            previous = section;
        }

        var published = targets.ToArray();
        foreach (var block in _aiBlocks) block.JumpTargets = published;
    }
}
