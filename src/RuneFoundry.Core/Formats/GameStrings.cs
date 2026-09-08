using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace RuneFoundry.Core.Formats;

/// <summary>
/// `Data/Strings/&lt;locale&gt;.json` — the remaster's string table.
///
/// This supersedes the `Rez/*.tbl` files for the remaster. It is a flat object of about
/// 1600 key/value string pairs, and it is better keyed than the TBLs ever were: units and
/// upgrades are addressed by the same id the .dat tables use, rather than by an offset into
/// a string list. `TblFile` stays the right reader for a classic or Battle.net edition
/// install, where these files do not exist.
///
/// Key families that matter for editing:
///
///   unit_&lt;id&gt;                    name, by unit table id
///   unit_&lt;id&gt;_build              the build-button label
///   unit_&lt;id&gt;_tooltip            plus _tooltip_advice, _tooltip_flavor, _requirement
///   upgrade_&lt;id&gt;                 name, by upgrade table id
///   spell_&lt;group&gt;                name, by the upgrade table's `group` field
///   human_&lt;n&gt;_name               campaign mission title; also _summary, _objectives
///   human_&lt;n&gt;_&lt;page&gt;             briefing pages; orc_, xhuman_, xorc_ likewise
///   tips_&lt;n&gt;                     loading tips
///   stat_txt_&lt;n&gt;                 the old TBL contents, carried over
///
/// Values are stored with the file's own key order preserved, so a save produces a diff
/// against retail that a human can read.
/// </summary>
public sealed class GameStrings
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Locales the remaster ships, as the file names carry them.</summary>
    public static readonly string[] KnownLocales =
        { "enUS", "deDE", "esES", "esMX", "frFR", "jaJP", "koKR", "ptBR", "ruRU", "zhCN", "zhTW" };

    /// <summary>
    /// How the file that was read was written, so it can be written back the same way.
    ///
    /// The shipped files do not agree, and neither is wrong. enUS.json is the only one with
    /// escapes: 239 of them, apostrophes and ampersands and its one ©. Every other locale
    /// has none at all and keeps its umlauts, Cyrillic and kanji raw. Picking one style for
    /// both would leave a mod's copy differing from retail on hundreds of lines it never
    /// touched, burying the change that was actually made — so the file that came in decides
    /// the shape of the file that goes out.
    /// </summary>
    private bool _relaxed;
    private bool _crlf = true;

    /// <summary>
    /// What the ten non-English files were written with: everything raw.
    ///
    /// No stock encoder quite gets there. Both this one and UnsafeRelaxedJsonEscaping
    /// escape the non-breaking space — frFR alone has 322 of them — because it is Unicode
    /// category Zs, which .NET forbids whatever the allowed ranges say. AllowCharacters
    /// cannot override that, so the last few are unescaped afterwards by
    /// <see cref="Unescape"/>.
    /// </summary>
    private static readonly JavaScriptEncoder RelaxedEncoder =
        JavaScriptEncoder.Create(new TextEncoderSettings(UnicodeRanges.All));

    /// <summary>
    /// Puts back the characters the encoder escaped and the file keeps raw.
    ///
    /// Everything above U+0020 goes back to its character, except the two that cannot:
    /// the quote and the backslash take their short forms, which is what those files
    /// use. A real control character stays a \u escape.
    /// </summary>
    private static string Unescape(string json) =>
        System.Text.RegularExpressions.Regex.Replace(
            json,
            @"\\u([0-9a-fA-F]{4})",
            match =>
            {
                var value = Convert.ToInt32(match.Groups[1].Value, 16);
                return value switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    < 0x20 => match.Value,
                    _ => ((char)value).ToString(),
                };
            });

    /// <summary>Keys in file order.</summary>
    public IReadOnlyList<string> Keys => _order;

    public int Count => _order.Count;

    public static GameStrings Load(string path) => Parse(File.ReadAllText(path));

    public static bool LooksLikeGameStrings(string text)
    {
        try
        {
            Parse(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static GameStrings Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Not a strings file: the root is not an object.");

        var result = new GameStrings
        {
            // An escape anywhere means the file was written with the strict encoder; its
            // absence, across files with thousands of characters that a strict encoder
            // would have escaped, means the relaxed one.
            _relaxed = !text.Contains("\\u", StringComparison.Ordinal),
            _crlf = !text.Contains('\n') || text.Contains("\r\n"),
        };

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Not a strings file: '{property.Name}' is not a string.");
            result.Set(property.Name, property.Value.GetString() ?? "");
        }
        return result;
    }

    /// <summary>The string under a key, or null. Missing keys are ordinary — not every id has text.</summary>
    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public bool Has(string key) => _values.ContainsKey(key);

    /// <summary>
    /// Sets a key, appending it at the end if it is new so existing order survives.
    ///
    /// Line endings are cut down to the newline alone. A text box on Windows ends a line
    /// with a carriage return and a newline, the game draws the carriage return as a
    /// missing glyph, and pressing Enter in an objective put a question mark on screen.
    /// Not one of the 1,614 strings the game ships contains a carriage return.
    /// </summary>
    public void Set(string key, string value)
    {
        if (!_values.ContainsKey(key)) _order.Add(key);
        _values[key] = value.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    /// <summary>Removes a key. Returns false if it was not there.</summary>
    public bool Remove(string key)
    {
        if (!_values.Remove(key)) return false;
        _order.Remove(key);
        return true;
    }

    public string? this[string key]
    {
        get => Get(key);
        set => Set(key, value ?? "");
    }

    // ---- id-keyed lookups ----------------------------------------------------------

    /// <summary>
    /// Unit name for a unit table id. Null where the game names nothing: the five empty
    /// slots (34, 36, 37, 48, 54), the two start locations, and the five corpse rows.
    /// </summary>
    public string? UnitName(int unit) => Get($"unit_{unit}");

    /// <summary>Build-button label, e.g. "Train Footman".</summary>
    public string? UnitBuildLabel(int unit) => Get($"unit_{unit}_build");

    public string? UnitTooltip(int unit) => Get($"unit_{unit}_tooltip");
    public string? UnitTooltipAdvice(int unit) => Get($"unit_{unit}_tooltip_advice");
    public string? UnitTooltipFlavor(int unit) => Get($"unit_{unit}_tooltip_flavor");
    public string? UnitRequirement(int unit) => Get($"unit_{unit}_requirement");

    /// <summary>
    /// Upgrade name for an upgrade table id, keyed identically to <see cref="UpgradeDataFile"/>.
    ///
    /// Four spells have no key — 34 Holy Vision, 38 Fireball, 43 Eye of Kilrogg and
    /// 46 Death Coil. Those are exactly the rows the upgrade table gives no gold cost, so
    /// they are innate rather than researched and never get a "Learn ..." button.
    /// <see cref="SpellName"/> still names them.
    /// </summary>
    public string? UpgradeName(int upgrade) => Get($"upgrade_{upgrade}");

    public string? UpgradeTooltip(int upgrade) => Get($"upgrade_{upgrade}_tooltip");
    public string? UpgradeRequirement(int upgrade) => Get($"upgrade_{upgrade}_requirement");

    /// <summary>
    /// Spell name, keyed by the upgrade table's `group` field rather than by upgrade id.
    /// Pass <see cref="UpgradeDataFile.Group"/> for a spell row. The eighteen spell groups
    /// are 0-19 with 2 and 12 unused.
    /// </summary>
    public string? SpellName(int group) => Get($"spell_{group}");

    public string? SpellTooltip(int group) => Get($"spell_{group}_tooltip");

    /// <summary>
    /// The best name for an upgrade row: its research label, falling back to the spell name
    /// for the four innate spells that have no research button.
    /// </summary>
    public string? UpgradeOrSpellName(int upgrade, int group) =>
        UpgradeName(upgrade) ?? (UpgradeDataFile.IsSpell(upgrade) ? SpellName(group) : null);

    // ---- campaign ------------------------------------------------------------------

    /// <summary>Campaign key prefixes, in the order the game presents them.</summary>
    public static readonly string[] CampaignPrefixes = { "human", "orc", "xhuman", "xorc" };

    /// <summary>Mission title, e.g. human_1_name = "Hillsbrad".</summary>
    public string? MissionName(string campaign, int mission) => Get($"{campaign}_{mission}_name");

    public string? MissionSummary(string campaign, int mission) => Get($"{campaign}_{mission}_summary");
    public string? MissionObjectives(string campaign, int mission) => Get($"{campaign}_{mission}_objectives");

    /// <summary>One page of a mission briefing. Pages are 1-based and run out without warning.</summary>
    public string? MissionBriefing(string campaign, int mission, int page) => Get($"{campaign}_{mission}_{page}");

    /// <summary>Every briefing page for a mission, in order, stopping at the first gap.</summary>
    public IEnumerable<string> MissionBriefingPages(string campaign, int mission)
    {
        for (var page = 1; ; page++)
        {
            var text = MissionBriefing(campaign, mission, page);
            if (text is null) yield break;
            yield return text;
        }
    }

    /// <summary>Keys sharing a prefix, in file order. For grouping an editor's tree.</summary>
    public IEnumerable<string> KeysWithPrefix(string prefix) =>
        _order.Where(k => k.StartsWith(prefix, StringComparison.Ordinal));

    // ---- output --------------------------------------------------------------------

    public string ToJson()
    {
        // Written key by key rather than serialized from a Dictionary: this order is the
        // file's order, and a Dictionary only happens to keep insertion order until
        // something is removed from it.
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = true,
                   Encoder = _relaxed ? RelaxedEncoder : JavaScriptEncoder.Default,
               }))
        {
            writer.WriteStartObject();
            foreach (var key in _order) writer.WriteString(key, _values[key]);
            writer.WriteEndObject();
        }

        var json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        if (_relaxed) json = Unescape(json);

        // The writer already uses this platform's line ending, which is not necessarily
        // the file's. Normalise, then match what came in. Values carry their own line
        // breaks as an escape, so nothing real is at risk here.
        json = json.Replace("\r\n", "\n");
        return _crlf ? json.Replace("\n", "\r\n") : json;
    }

    /// <summary>The bytes to write: UTF-8, and no byte order mark, as the game ships them.</summary>
    public byte[] ToBytes() =>
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(ToJson());

    /// <summary>Writes via a temp file, matching the other formats.</summary>
    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ToBytes());
        File.Move(temp, path, overwrite: true);
    }

    public string Describe() => $"{Count} strings";
}
