using RuneFoundry.Core.Formats;

namespace RuneFoundry.Core;

/// <summary>
/// Putting one campaign mission back the way the game shipped it, and leaving the rest of
/// the mod alone.
///
/// A mission is made of more than one kind of thing — whole files, entries inside a shared
/// string table, and a number in the executable's objective table — so "reset it" has to
/// mean all three or it is a trap: the map goes back and the briefing text that referred to
/// it does not.
///
/// Reset means *drop this mission's overrides*, not *copy stock bytes into the mod*. A mod
/// carrying a file identical to the game's is a file that will be reported as changed, get
/// backed up, and be restored on uninstall, all to no purpose.
/// </summary>
public static class MissionReset
{
    /// <summary>
    /// What resetting a mission would actually discard. Worked out before anything is
    /// touched so it can be shown to the person about to lose it.
    /// </summary>
    public sealed record Plan(
        IReadOnlyList<string> Files,
        IReadOnlyList<string> TextKeys,
        bool ScenarioRule,
        bool BuildRestrictions)
    {
        public bool IsEmpty => Files.Count == 0 && TextKeys.Count == 0 && !ScenarioRule;

        /// <summary>A plain list of what goes, for a confirmation someone can act on.</summary>
        public string Describe()
        {
            var parts = new List<string>();

            var maps = Files.Count(f => f.EndsWith(".pud", StringComparison.OrdinalIgnoreCase));
            var speech = Files.Count - maps;

            if (maps > 0) parts.Add(BuildRestrictions ? "the map, including its build restrictions" : "the map");
            if (speech == 1) parts.Add("1 briefing recording");
            else if (speech > 1) parts.Add($"{speech} briefing recordings");

            if (TextKeys.Count == 1) parts.Add("1 text entry");
            else if (TextKeys.Count > 1) parts.Add($"{TextKeys.Count} text entries");

            if (ScenarioRule) parts.Add("the scenario rule");

            return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Works out what this mod has changed about a mission.
    ///
    /// Text is compared against the game's own copy rather than remembered, so a string
    /// hand-edited outside the editor still counts as this mission's.
    /// </summary>
    public static Plan Prepare(
        ModProject project, CampaignMission mission, GameStrings? mod, GameStrings? stock, byte[]? map = null)
    {
        var files = mission.Files().Where(project.HasOverride).ToList();

        var keys = new List<string>();
        if (mod is not null && stock is not null)
        {
            foreach (var key in mission.TextKeys())
                if (mod.Get(key) != stock.Get(key))
                    keys.Add(key);
        }

        var slot = mission.ExeSlot;
        var rule = project.ObjectiveFor(slot) is not null || project.ThresholdFor(slot) is not null;

        // Only worth mentioning separately because it is not obvious that it lives in the map.
        var restrictions = map is not null && PudFile.HasAllow(map);

        return new Plan(files, keys, rule, restrictions);
    }

    /// <summary>
    /// Carries the plan out. Returns true when the string table was changed and needs
    /// saving — which the caller does, because only it knows where the mod keeps it.
    /// </summary>
    public static bool Apply(
        ModProject project, CampaignMission mission, Plan plan, GameStrings? mod, GameStrings? stock)
    {
        foreach (var file in plan.Files) project.RemoveOverride(file);

        if (plan.ScenarioRule)
        {
            project.SetObjective(mission.ExeSlot, null);
            project.SetThreshold(mission.ExeSlot, null);
        }

        if (mod is null || stock is null || plan.TextKeys.Count == 0) return false;

        foreach (var key in plan.TextKeys)
        {
            // A key the game does not have is one this mod invented, so it goes rather than
            // being set to an empty string the game would then show.
            if (stock.Get(key) is { } value) mod.Set(key, value);
            else mod.Remove(key);
        }

        return true;
    }
}
