using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RuneFoundry.Core;

namespace RuneFoundry.UI;

/// <summary>
/// Per-user preferences: where the game is, and which project was last open. Kept out of
/// the backup vault deliberately — the vault is machine state that must survive, this is
/// just where the window last looked.
/// </summary>
public sealed class Settings
{
    [JsonPropertyName("gameRoot")] public string GameRoot { get; set; } = "";
    [JsonPropertyName("lastProject")] public string LastProject { get; set; } = "";

    /// <summary>
    /// Projects opened before, newest first.
    ///
    /// Kept separately from <see cref="LastProject"/>, which is the one reopened at startup.
    /// Paths rather than handles: a project that has been moved or deleted should still be
    /// listed, so the menu can say so rather than quietly forgetting it.
    /// </summary>
    [JsonPropertyName("recentProjects")] public List<string> RecentProjects { get; set; } = new();

    /// <summary>How many to keep. Long enough to cover the ones you switch between.</summary>
    public const int RecentLimit = 10;

    /// <summary>
    /// Puts a project at the top of the list, without letting it appear twice.
    ///
    /// Compared without case because Windows paths are, and the same project reached through
    /// two spellings is one project.
    /// </summary>
    public void RememberRecent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var full = Path.GetFullPath(path);
        RecentProjects.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, full);

        if (RecentProjects.Count > RecentLimit)
            RecentProjects.RemoveRange(RecentLimit, RecentProjects.Count - RecentLimit);
    }

    /// <summary>
    /// How to start the game. Battle.net by default, because Warcraft II Remastered
    /// checks that it was launched by the client and quits if it was not.
    /// </summary>
    [JsonPropertyName("launchMethod")] public LaunchMethod LaunchMethod { get; set; } = LaunchMethod.BattleNet;

    /// <summary>
    /// Ticks per second, for turning AI sleeps into durations. Only affects what is
    /// displayed — the values written to the file are always the raw tick counts.
    /// </summary>
    [JsonPropertyName("ticksPerSecond")] public int TicksPerSecond { get; set; } = 52;

    /// <summary>
    /// Whether saving an ai.bin may lay the whole file out again so scripts can change
    /// size. Off by default: the fixed-layout writer cannot move anything, so it cannot
    /// get one of the file's ~330 internal pointers wrong.
    /// </summary>

    /// <summary>The language file the campaign lens edits, e.g. <c>Strings/enUS.json</c>.</summary>
    [JsonPropertyName("campaignLocale")] public string CampaignLocale { get; set; } = "";

    /// <summary>
    /// The map editor to hand a .pud to — PUDForge or another.
    ///
    /// Asked for the first time it is wanted rather than set up front: most people never open
    /// a map at all, and a settings page full of paths nobody has filled in is its own problem.
    /// </summary>
    [JsonPropertyName("mapEditor")] public string MapEditor { get; set; } = "";

    /// <summary>
    /// The AI script that was open last, by its slot number, so opening ai.bin again lands
    /// where you left off rather than back at the top of 84. -1 is the shared routine,
    /// which is a real entry in the list but not a slot.
    /// </summary>
    [JsonPropertyName("lastAiScript")] public int LastAiScript { get; set; }

    /// <summary>"dark", "light", or "system" to follow the Windows setting.</summary>
    [JsonPropertyName("theme")] public string Theme { get; set; } = "system";

    /// <summary>
    /// Whether campaign rules may be written into the running game: "allow", "skip", or
    /// empty for "ask me".
    ///
    /// A remembered answer to a question about the person's own Battle.net account, so it
    /// belongs to them rather than to any mod — and it starts unset, because a default of
    /// yes would be the silent behaviour this replaced.
    /// </summary>
    [JsonPropertyName("campaignRulesConsent")] public string CampaignRulesConsent { get; set; } = "";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RuneFoundry", "settings.json");

    /// <summary>Where this file lived when the app was called RuneFoundryStudio.</summary>
    private static string FormerFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RuneFoundryStudio", "settings.json");

    public static Settings Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath
                : File.Exists(FormerFilePath) ? FormerFilePath
                : null;

            return path is null
                ? new Settings()
                : JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        }
        catch
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Losing a remembered folder is never worth interrupting the user over.
        }
    }
}
