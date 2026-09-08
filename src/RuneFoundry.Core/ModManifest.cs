using System.Text.Json;
using System.Text.Json.Serialization;

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

    [JsonPropertyName("files")] public List<ModFileEntry> Files { get; set; } = new();

    /// <summary>
    /// Campaign slots this mod wants won by destroying every enemy. Applying it edits two
    /// bytes of the game executable per slot; the loader records what was there before.
    /// </summary>
    [JsonPropertyName("objectives")] public Dictionary<int, int> Objectives { get; set; } = new();

    /// <summary>Threshold words for counter objectives, by campaign slot.</summary>
    [JsonPropertyName("thresholds")] public Dictionary<int, int> Thresholds { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ModManifest FromJson(string json)
        => JsonSerializer.Deserialize<ModManifest>(json, JsonOptions)
           ?? throw new InvalidDataException("mod.json is empty or malformed.");

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
            problems.Add($"Package uses manifest schema {Schema}; this build understands up to {CurrentSchema}. Update the tools.");
        if (!PathSafety.IsValidModId(Id))
            problems.Add($"Mod id '{Id}' is invalid. Use 2-64 characters of lowercase letters, digits, dot, dash or underscore.");
        if (string.IsNullOrWhiteSpace(Name))
            problems.Add("Mod name is required.");
        if (Files.Count == 0)
            problems.Add("Mod contains no files.");

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
