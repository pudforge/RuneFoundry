using System.Text.Json;
using System.Text.Json.Serialization;

using RuneFoundry.Core.Scenarios;

namespace RuneFoundry.Core;

public sealed class ModFileEntry
{
    /// <summary>Path relative to the game's x86\Data folder, forward-slashed.</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>
    /// True when this file does not exist in a stock install — it is new content the
    /// mod adds rather than a replacement. Uninstall deletes these instead of restoring.
    /// </summary>
    [JsonPropertyName("isNew")] public bool IsNew { get; set; }

    /// <summary>SHA-256 of the stock file this replaces, when known. Used to warn about version drift.</summary>
    [JsonPropertyName("baseSha256")] public string? BaseSha256 { get; set; }
}

public sealed class ModManifest
{
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")] public int Schema { get; set; } = CurrentSchema;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("website")] public string Website { get; set; } = "";
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Free-form note about which game build this was authored against.</summary>
    [JsonPropertyName("builtAgainst")] public string BuiltAgainst { get; set; } = "";

    /// <summary>
    /// The RuneFoundry release that packaged this mod, "0.6.2-alpha". Empty on mods made
    /// before the field existed (0.6.2 and earlier), which are read as the oldest format.
    /// A mod from a newer release than this one is refused; see <see cref="AppVersion"/>.
    /// </summary>
    [JsonPropertyName("builtWith")] public string BuiltWith { get; set; } = "";

    [JsonPropertyName("files")] public List<ModFileEntry> Files { get; set; } = new();

    /// <summary>
    /// Campaign slots this mod wants won by destroying every enemy. Applying it edits two
    /// bytes of the game executable per slot; the loader records what was there before.
    /// </summary>
    [JsonPropertyName("objectives")] public Dictionary<int, int> Objectives { get; set; } = new();

    /// <summary>Threshold words for counter objectives, by campaign slot.</summary>
    [JsonPropertyName("thresholds")] public Dictionary<int, int> Thresholds { get; set; } = new();

    /// <summary>
    /// Rules the author wrote, per mission slot. Carried in the mod so the Launcher can
    /// watch for them without the project that built it.
    /// </summary>
    [JsonPropertyName("scenarios")]
    public Dictionary<int, ScenarioRules> Scenarios { get; set; } = new();

    /// <summary>
    /// The rule shape this mod was written against. A mod from a newer build is refused
    /// rather than half read: a condition this version does not know would silently never
    /// be met, which looks like a mission that cannot be finished.
    /// </summary>
    [JsonPropertyName("scenarioVersion")]
    public int ScenarioVersion { get; set; } = ScenarioRules.Version;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Reads a manifest from any RuneFoundry release, upgraded to what this build expects.
    /// Old mods stay playable: every change to the format has to be handled in
    /// <see cref="Upgrade"/>, never by refusing what an earlier release wrote.
    /// </summary>
    public static ModManifest FromJson(string json)
    {
        var manifest = JsonSerializer.Deserialize<ModManifest>(json, JsonOptions)
                       ?? throw new InvalidDataException("mod.json is empty or malformed.");
        manifest.Upgrade();
        return manifest;
    }

    /// <summary>Whether this mod was packaged by a release older than the one reading it.</summary>
    [JsonIgnore]
    public bool IsFromOlderRelease => AppVersion.Compare(BuiltWith, AppVersion.Current) < 0;

    /// <summary>
    /// Brings a manifest written by an older release up to the current shape, in place.
    /// Each step is keyed on what the old release got wrong or left out, so a step is
    /// harmless on a manifest that never needed it.
    /// </summary>
    private void Upgrade()
    {
        // Missing collections in hand-written or very old manifests read as null.
        Files ??= new();
        Objectives ??= new();
        Thresholds ??= new();
        Scenarios ??= new();

        // Before 0.6.3 the editor took a file's stock hash from the game folder, which holds
        // the mod's own copy once "Save and test" has applied it. Such a hash equals the
        // payload's and describes nothing stock; left in, it makes the loader warn that every
        // file "differs from the copy this mod was built against". Dropping it only loses a
        // drift warning that could never have been right.
        foreach (var file in Files)
        {
            if (file.BaseSha256 is not null &&
                string.Equals(file.BaseSha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                file.BaseSha256 = null;
        }
    }

    public long TotalSize => Files.Sum(f => f.Size);

    /// <summary>
    /// Validates everything a loader needs to trust before it starts writing files.
    /// Returns every problem at once so the user fixes them in one pass.
    /// </summary>
    public IReadOnlyList<string> Validate() => Check(includePayload: true);

    /// <summary>
    /// Validates only what the author controls — identity and file paths — skipping the
    /// hash and size fields. The packager needs this before a build, because those fields
    /// are filled in *by* the build: checking them earlier fails every time.
    /// </summary>
    public IReadOnlyList<string> ValidateMetadata() => Check(includePayload: false);

    private IReadOnlyList<string> Check(bool includePayload)
    {
        var problems = new List<string>();

        if (Schema > CurrentSchema)
            problems.Add($"Package uses manifest schema {Schema}; this build understands up to {CurrentSchema}. Update RuneFoundry.");
        // A newer release may have added fields this one would silently skip, so the mod
        // would be only partly applied. Saying so beats a mission that quietly misbehaves.
        if (AppVersion.IsNewerThanCurrent(BuiltWith))
            problems.Add($"This mod was made with RuneFoundry {BuiltWith}, and this is RuneFoundry {AppVersion.Current}. Update RuneFoundry to play it.");
        if (!PathSafety.IsValidModId(Id))
            problems.Add($"Mod id '{Id}' is invalid. Use 2-64 characters of lowercase letters, digits, dot, dash or underscore.");
        if (string.IsNullOrWhiteSpace(Name))
            problems.Add("Mod name is required.");
        // A mod is not only files: campaign rules live in this manifest, so one that
        // replaces nothing and sets a victory condition still has something to install.
        if (Files.Count == 0 && Scenarios.Count == 0
            && Objectives.Count == 0 && Thresholds.Count == 0)
            problems.Add("Mod contains no files and no campaign rules.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (!PathSafety.IsSafeRelativePath(file.Path, out var pathError))
            {
                problems.Add($"Unsafe path '{file.Path}': {pathError}");
                continue;
            }
            var normalized = PathSafety.Normalize(file.Path);
            if (!seen.Add(normalized))
                problems.Add($"Duplicate entry for '{normalized}'.");

            if (!includePayload) continue;

            if (file.Sha256.Length != 64)
                problems.Add($"Entry '{normalized}' has a malformed SHA-256.");
            if (file.Size < 0)
                problems.Add($"Entry '{normalized}' has a negative size.");
        }
        return problems;
    }
}
