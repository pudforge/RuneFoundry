namespace RuneFoundry.Core.Scenarios;

/// <summary>
/// Warcraft II's player colours, in slot order.
///
/// A rule names a player by number, but on the map a player is a colour: "player 7" means
/// nothing at a glance, "player 7 (White)" is the side you can see. The two are the same
/// thing said two ways, so every place that prints a player number prints the colour with
/// it. The game paints eight; a slot past those has no colour of its own.
/// </summary>
public static class PlayerColors
{
    private static readonly string[] Colours =
        { "Red", "Blue", "Green", "Violet", "Orange", "Black", "White", "Yellow" };

    /// <summary>The colour of a zero-based slot, or null beyond the eight the game paints.</summary>
    public static string? Of(int slot) => slot >= 0 && slot < Colours.Length ? Colours[slot] : null;

    /// <summary>"Player 7 (White)" for a zero-based slot; no colour past slot 8.</summary>
    public static string Label(int slot) =>
        Of(slot) is { } colour ? $"Player {slot + 1} ({colour})" : $"Player {slot + 1}";
}
