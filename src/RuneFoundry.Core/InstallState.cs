using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuneFoundry.Core;

/// <summary>One file that a mod currently has applied to the game folder.</summary>
public sealed class AppliedFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    /// <summary>Hash of what the mod wrote. If the file on disk no longer matches, something else changed it.</summary>
    [JsonPropertyName("moddedSha256")] public string ModdedSha256 { get; set; } = "";

    /// <summary>Hash of the stock file we displaced, or null when the mod added a file that did not exist.</summary>
    [JsonPropertyName("originalSha256")] public string? OriginalSha256 { get; set; }

    /// <summary>False when the mod introduced this path — uninstall deletes it rather than restoring.</summary>
    [JsonPropertyName("hadOriginal")] public bool HadOriginal { get; set; }
}

public sealed class InstalledMod
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("addedUtc")] public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Disabled mods stay in the library and keep their package, but write nothing to the game.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>Paths this mod wants to provide. Whether it wins depends on load order.</summary>
    [JsonPropertyName("providedPaths")] public List<string> ProvidedPaths { get; set; } = new();
}

/// <summary>
/// What the loader believes is currently on disk. Load order is the list order:
/// later mods win conflicts, exactly as the list reads top to bottom.
/// </summary>
public sealed class InstallState
{
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")] public int Schema { get; set; } = CurrentSchema;
    [JsonPropertyName("gameRoot")] public string GameRoot { get; set; } = "";
    [JsonPropertyName("mods")] public List<InstalledMod> Mods { get; set; } = new();

    /// <summary>Every path the loader has written, keyed by path, with the mod that owns it.</summary>
    [JsonPropertyName("applied")] public Dictionary<string, AppliedFile> Applied { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which mod each applied path currently comes from.</summary>
    [JsonPropertyName("owners")] public Dictionary<string, string> Owners { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Campaign slots whose objective this loader changed, and the id each held before —
    /// which is the only way to put the executable back, since the change is two bytes in
    /// the middle of a 5 MB file and nothing else records what used to be there.
    /// </summary>
    // Campaign objectives were once patched into Warcraft II.exe on disk and remembered
    // here so they could be put back. They are not: the executable verifies its own image
    // and refuses to start if a byte differs, so the numbers are written into the running
    // game instead and vanish when it closes. Nothing to remember, and nothing to restore.

    /// <summary>
    /// What the executable hashed to when we last wrote it. If it no longer matches, the
    /// game has been patched or repaired underneath us and the remembered ids may describe
    /// a file that no longer exists.
    /// </summary>

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static InstallState Load(string path)
    {
        if (!File.Exists(path)) return new InstallState();
        try
        {
            var state = JsonSerializer.Deserialize<InstallState>(File.ReadAllText(path), Options) ?? new InstallState();
            // Dictionaries deserialize with the default comparer; game paths are case-insensitive on Windows.
            state.Applied = new Dictionary<string, AppliedFile>(state.Applied, StringComparer.OrdinalIgnoreCase);
            state.Owners = new Dictionary<string, string>(state.Owners, StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"RuneFoundry's state file is unreadable ({ex.Message}). " +
                $"Originals are still in the vault; you can repair by hand or delete {path} after restoring.", ex);
        }
    }

    /// <summary>Written via a temp file so a crash mid-save cannot destroy the record of what to restore.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));

        try
        {
            // Replace rather than move onto it: a move leaves a brand new file, owned by
            // whoever wrote it. The elevated helper writes this file too, and a state.json
            // belonging to Administrators is one the app could never save again. Replace
            // keeps the destination's security descriptor.
            if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
            else File.Move(temp, path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Leaving the half-written temp behind would be read as a saved state later.
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }

            throw new IOException(
                $"Could not save RuneFoundry's state to {path}: {ex.Message}\n\n" +
                "This usually means the file was written while running as administrator and "
                + "now belongs to one. Applying a mod repairs it.", ex);
        }
    }

    public InstalledMod? Find(string id) => Mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
