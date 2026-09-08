namespace RuneFoundry.Core.Formats;

/// <summary>
/// The game's music, and the two recordings of each track.
///
/// Every track ships twice: <c>_r</c> is the remastered orchestration and <c>_opl</c> is the
/// 1995 Adlib synthesis. The game picks between them from a setting, which the executable
/// spells <c>musicversion_remastered</c> and <c>musicversion_dos</c>, so replacing one and
/// not the other changes the music for some players and not others. Both are offered, and
/// which is which is said on the screen.
///
/// Five tracks have no Adlib recording: the first and last of each campaign, and the credits
/// piece. Those play the remastered version whichever setting is chosen.
///
/// Names come from the table at <c>0x8418bc</c>, which the game joins to <c>Music\%s</c>.
/// They are spelled in capitals there and in capitals on disk, and both are kept, because a
/// mod's file has to sit where the game looks for it.
/// </summary>
public static class GameMusic
{
    /// <summary>A track: the file stem, what to call it, and whether Adlib shipped.</summary>
    private sealed record Track(string Stem, string Label, bool HasAdlib = true);

    private static readonly Track[] Tracks =
    {
        new("HUMAN1", "Human 1", HasAdlib: false),
        new("HUMAN2", "Human 2"),
        new("HUMAN3", "Human 3"),
        new("HUMAN4", "Human 4"),
        new("HUMAN5", "Human 5"),
        new("HUMAN6", "Human 6", HasAdlib: false),
        new("HWARROOM", "Human briefing room"),
        new("HVICTORY", "Human victory"),
        new("HDEFEAT", "Human defeat"),

        new("ORC1", "Orc 1", HasAdlib: false),
        new("ORC2", "Orc 2"),
        new("ORC3", "Orc 3"),
        new("ORC4", "Orc 4"),
        new("ORC5", "Orc 5"),
        new("ORC6", "Orc 6", HasAdlib: false),
        new("OWARROOM", "Orc briefing room"),
        new("OVICTORY", "Orc victory"),
        new("ODEFEAT", "Orc defeat"),

        new("DISCOWC", "Credits", HasAdlib: false),
    };

    /// <summary>How many tracks the game has. Nineteen, of which fourteen ship twice.</summary>
    public static int Count => Tracks.Length;

    private static MediaFile File(string stem, string suffix, string label) =>
        new(label, $"Music/{stem}_{suffix}.wav", MediaFile.AudioFilter);

    /// <summary>Every track, in the order the game lists them.</summary>
    public static IReadOnlyList<MediaCue> All { get; } = Tracks
        .Select(track =>
        {
            var files = new List<MediaFile> { File(track.Stem, "r", "Remastered") };
            if (track.HasAdlib) files.Add(File(track.Stem, "opl", "Adlib"));

            return new MediaCue(
                track.Label,
                track.HasAdlib
                    ? "Ships twice. Which one plays depends on the music setting, so replace both."
                    : "Ships once. This plays whichever music setting is chosen.",
                files);
        })
        .ToArray();
}
