namespace RuneFoundry.Core.Formats;

/// <summary>
/// One file behind a movie or a sound cue.
///
/// Each movie is a single file. A <c>.wav</c> of the same name sits beside every one of
/// them, which looks like a soundtrack to be replaced separately and is not: the
/// <c>.webm</c> carries its own Opus audio, and replacing the <c>.wav</c> changes nothing
/// anybody hears. Whatever the game keeps those for, offering them would only waste an
/// afternoon, so they are left out.
/// </summary>
/// <param name="Label">What this file is, in the words the screen uses.</param>
/// <param name="Path">Where it lives under the game's data folder.</param>
/// <param name="Filter">The file dialog's filter for choosing a replacement.</param>
public sealed record MediaFile(string Label, string Path, string Filter)
{
    public const string VideoFilter = "WebM video (*.webm)|*.webm|All files (*.*)|*.*";
    public const string AudioFilter = "Wave audio (*.wav)|*.wav|All files (*.*)|*.*";

    public static MediaFile Video(string path) => new("Movie", path, VideoFilter);
    public static MediaFile Audio(string path) => new("Sound", path, AudioFilter);

    /// <summary>Whether this is a sound rather than a picture, which decides how it is read.</summary>
    public bool IsAudio => Path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One cue the game plays: its label, why it matters, and the files behind it.</summary>
/// <param name="Label">The cue, named as the player would recognise it.</param>
/// <param name="Note">What it is and where it is heard. Shown under the label.</param>
/// <param name="Files">The movie and its soundtrack, or just a sound.</param>
public sealed record MediaCue(string Label, string Note, IReadOnlyList<MediaFile> Files);

/// <summary>
/// The movies and cues the game plays around a campaign, and the one it plays for itself.
///
/// Found by reading the act screen at <c>0x524760</c>, which builds its background path from
/// <c>Backgrounds/</c> and then holds that screen in a loop — <c>0x4f8820</c> — that runs
/// while any of the eight audio channels is still busy. So the act title card is not on a
/// timer at all: it stays up for exactly as long as the act fanfare plays. That is worth
/// saying on the screen, because replacing <c>Sfx/Hact.wav</c> with a shorter clip makes
/// every human act card flick past, and nothing about the picture explains it.
///
/// Movies are video only. Naming is not uniform, so this is a mapping rather than a search:
/// <list type="bullet">
/// <item>Finales take the campaign's own id — <c>xhuman_finale</c>.</item>
/// <item>Intros take the release, not the campaign: <c>intro</c> for Tides of Darkness and
/// <c>xintro</c> for Beyond the Dark Portal, one each for both races.</item>
/// <item>Act cinematics take the race and the act — <c>human_act_2</c> — and only acts two
/// to four have one. Act one opens on its title card alone.</item>
/// </list>
/// </summary>
public static class GameMedia
{
    /// <summary>The first act has no cinematic; it opens on its title card and the fanfare.</summary>
    public const int FirstActWithCinematic = 2;

    /// <summary>
    /// The longest an act title card can be made to stay.
    ///
    /// A ceiling rather than a rule the game enforces — the card would hold for as long as
    /// the fanfare runs — but half a minute of a held still is already far past what anyone
    /// sitting down to play would sit through.
    /// </summary>
    public static readonly TimeSpan LongestActHold = TimeSpan.FromSeconds(30);

    private static string Race(string campaignId) =>
        campaignId.EndsWith("orc", StringComparison.Ordinal) ? "orc" : "human";

    private static bool IsExpansion(string campaignId) =>
        campaignId.StartsWith("x", StringComparison.Ordinal);

    /// <summary>
    /// The fanfare that plays over an act's title card — and, because the game holds that
    /// card until every channel falls silent, the thing that decides how long the card stays.
    /// </summary>
    public static MediaCue Fanfare(string campaignId)
    {
        var race = Race(campaignId);
        var file = race == "orc" ? "Sfx/Oact.wav" : "Sfx/Hact.wav";

        return new MediaCue(
            "Act fanfare",
            $"Plays over every act title card in the {race} campaigns. The game holds the card "
            + "until this finishes, so a shorter clip makes the card flick past and a longer "
            + "one holds it. Shared with the other " + race + " campaigns.",
            new[] { MediaFile.Audio(file) });
    }

    /// <summary>The movie that opens the release this campaign belongs to.</summary>
    public static MediaCue Intro(string campaignId)
    {
        var stem = IsExpansion(campaignId) ? "xintro" : "intro";
        var release = IsExpansion(campaignId) ? "Beyond the Dark Portal" : "Tides of Darkness";

        return new MediaCue(
            "Opening movie",
            $"Plays when {release} starts. Shared with the other campaign of that release, "
            + "since both races open on the same movie.",
            new[] { MediaFile.Video($"Movies/{stem}.webm") });
    }

    /// <summary>The movie that closes this campaign. One per campaign, shared with nothing.</summary>
    public static MediaCue Finale(string campaignId) =>
        new("Ending movie",
            "Plays when this campaign is won. This one belongs to this campaign alone.",
            new[] { MediaFile.Video($"Movies/{campaignId}_finale.webm") });

    /// <summary>
    /// The cinematic between acts, or null for act one, which has none.
    /// </summary>
    public static MediaCue? ActCinematic(string campaignId, int act)
    {
        if (act < FirstActWithCinematic || act > CampaignArt.Acts) return null;

        var race = Race(campaignId);

        return new MediaCue(
            $"Act {act} cinematic",
            $"Plays after act {act}'s title card. Shared with the other {race} campaigns.",
            new[] { MediaFile.Video($"Movies/{race}_act_{act}.webm") });
    }

    /// <summary>Every cue for one campaign, in the order the player meets them.</summary>
    public static IReadOnlyList<MediaCue> For(string campaignId)
    {
        var cues = new List<MediaCue> { Intro(campaignId), Fanfare(campaignId) };

        for (var act = FirstActWithCinematic; act <= CampaignArt.Acts; act++)
            if (ActCinematic(campaignId, act) is { } cinematic)
                cues.Add(cinematic);

        cues.Add(Finale(campaignId));
        return cues;
    }

    /// <summary>
    /// Cues belonging to the game rather than to a campaign — the same split as
    /// <see cref="GameArt"/>, and for the same reason.
    /// </summary>
    public static IReadOnlyList<MediaCue> Global { get; } = new[]
    {
        new MediaCue(
            "Startup movie",
            "The logo that plays before the main menu, in every campaign and in skirmish.",
            new[] { MediaFile.Video("Movies/logo.webm") }),
    };
}
