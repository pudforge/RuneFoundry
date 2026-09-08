using RuneFoundry.Core;

namespace RuneFoundry.UI;

/// <summary>
/// One row in the mod list. The studio loads a single mod at a time, so the list is a
/// choice rather than a set of switches: picking a row picks what the game will run.
/// Vanilla is a row like any other, which is what makes "play without mods" the same
/// gesture as "play this mod" instead of a separate button hidden elsewhere.
/// </summary>
public sealed class ModListItem : Observable
{
    /// <summary>Null for the Vanilla row.</summary>
    public InstalledMod? Mod { get; }

    public ModListItem(InstalledMod? mod) => Mod = mod;

    public bool IsVanilla => Mod is null;
    public string Id => Mod?.Id ?? "";

    public string Name => IsVanilla
        ? "Vanilla"
        : string.IsNullOrWhiteSpace(Mod!.Name) ? Mod.Id : Mod.Name;

    public string Subtitle
    {
        get
        {
            if (IsVanilla) return "The game as Blizzard shipped it";

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Mod!.Version)) parts.Add("v" + Mod.Version);
            if (!string.IsNullOrWhiteSpace(Mod.Author)) parts.Add("by " + Mod.Author);
            parts.Add(Mod.ProvidedPaths.Count == 1 ? "1 file" : $"{Mod.ProvidedPaths.Count} files");
            return string.Join(" · ", parts);
        }
    }

    private bool _isActive;

    /// <summary>True for whatever is on disk right now, which is not always what is selected.</summary>
    public bool IsActive
    {
        get => _isActive;
        set { if (Set(ref _isActive, value)) Raise(nameof(Badge)); }
    }

    public string Badge => IsActive ? "playing" : "";
}

/// <summary>A file row in the detail pane.</summary>
public sealed record FileRow(string Marker, string Path, string Note);
