using System.IO;
using System.Windows;
using System.Windows.Controls;
using RuneFoundry.Core.Formats;
using RuneFoundry.UI;

namespace RuneFoundry.Editor.Views;

/// <summary>One side's name, as the game shows it beside a player colour.</summary>
public sealed class ClanRow : Observable
{
    public required string Key { get; init; }

    /// <summary>The colour this side plays as, which is what the key is named after.</summary>
    public required string Colour { get; init; }

    private string _name = "";

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>What the game ships, so a change can be seen and put back.</summary>
    public string Stock { get; set; } = "";

    public bool IsChanged => Name.Trim() != Stock;
}

/// <summary>
/// The names of the sides: the human nations, the orc clans, and the expansion clans.
///
/// Twenty three strings in the language file, keyed by campaign and player colour rather
/// than by anything a person would search for, and the first thing a total conversion
/// wants to change. Buried in a 1,614 entry file they were effectively unreachable.
/// </summary>
public partial class EditorView
{
    /// <summary>The three groups, in the order the campaigns come.</summary>
    private static readonly (string Prefix, string Label)[] ClanGroups =
    {
        ("clan_human", "Human nations"),
        ("clan_orc", "Orc clans"),
        ("clan_expansion", "Expansion clans"),
    };

    private readonly List<ClanRow> _clans = new();
    private bool _clansDirty;

    /// <summary>
    /// Fills the three lists from the mod's language file, or the game's where the mod has
    /// none yet.
    /// </summary>
    private void ShowClans()
    {
        if (HumanClanList is null) return;

        _clans.Clear();

        GameStrings? mine = null;
        GameStrings? stock = null;

        try
        {
            mine = GameStrings.Load(_session.RequireFile(_project, StringsPath));
            stock = GameStrings.Load(_session.RequireStock(StringsPath));
        }
        catch (Exception)
        {
            // No language file to read is not worth a dialog here; the lists stay empty
            // and the rest of the page still works.
        }

        foreach (var (prefix, _) in ClanGroups)
        {
            var rows = new List<ClanRow>();

            foreach (var key in (mine?.Keys ?? Array.Empty<string>())
                         .Where(k => k.StartsWith(prefix + "_", StringComparison.Ordinal))
                         .OrderBy(k => k, StringComparer.Ordinal))
            {
                var row = new ClanRow
                {
                    Key = key,
                    Colour = Colour(key[(prefix.Length + 1)..]),
                    Name = mine?.Get(key) ?? "",
                    Stock = stock?.Get(key) ?? mine?.Get(key) ?? "",
                };

                row.PropertyChanged += (_, _) => _clansDirty = true;
                rows.Add(row);
                _clans.Add(row);
            }

            var list = prefix switch
            {
                "clan_human" => HumanClanList,
                "clan_orc" => OrcClanList,
                _ => ExpansionClanList,
            };

            list.ItemsSource = null;
            list.ItemsSource = rows;
        }
    }

    /// <summary>The colour as a person says it, from the end of the key.</summary>
    private static string Colour(string tail) =>
        tail.Length == 0 ? tail : char.ToUpperInvariant(tail[0]) + tail[1..];

    /// <summary>
    /// Writes the changed names into the mod's language file.
    ///
    /// On leaving the box rather than on every keystroke: the file is rewritten whole, and
    /// doing that per character would be silly.
    /// </summary>
    private void SaveClans()
    {
        if (!_clansDirty || _loadingUi) return;
        _clansDirty = false;

        if (_project is null || _session.Game is null) return;

        try
        {
            var target = _project.ResolveContentPath(StringsPath);
            if (!File.Exists(target))
            {
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
            var changes = new List<(string Key, string? Was, string Now)>();

            foreach (var row in _clans)
            {
                var name = row.Name.Trim();

                // An empty name would leave the game with nothing to draw, so it is left
                // as it was rather than written away.
                if (name.Length == 0 || strings.Get(row.Key) == name) continue;

                changes.Add((row.Key, strings.Get(row.Key), name));
                strings.Set(row.Key, name);
            }

            if (changes.Count == 0) return;

            strings.Save(target);
            _datStrings = strings;

            SetStatus(changes.Count == 1
                ? "Renamed one side."
                : $"Renamed {changes.Count} sides.");

            RefreshOverrides();

            // One entry for the whole commit rather than one per name: what the person did
            // was rename the sides, and half of that put back is not a state they were ever in.
            void Put(bool forward)
            {
                using (Undo.Quiet())
                {
                    var file = GameStrings.Load(target);

                    foreach (var (key, was, now) in changes)
                    {
                        var value = forward ? now : was;
                        if (value is not null) file.Set(key, value);
                    }

                    file.Save(target);
                    _datStrings = file;

                    ShowClans();
                    RefreshOverrides();
                }
            }

            Undo.Push(new Edit(
                changes.Count == 1 ? $"rename {changes[0].Now}" : $"rename {changes.Count} sides",
                () => Put(true),
                () => Put(false)));
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "rename that", ex);
        }
    }

    private void OnClanCommitted(object sender, RoutedEventArgs e) => SaveClans();

    private void OnClanReset(object sender, RoutedEventArgs e)
    {
        _loadingUi = true;
        foreach (var row in _clans) row.Name = row.Stock;
        _loadingUi = false;

        _clansDirty = true;
        SaveClans();
        ShowClans();
    }
}
