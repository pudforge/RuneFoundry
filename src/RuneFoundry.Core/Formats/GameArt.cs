namespace RuneFoundry.Core.Formats;

/// <summary>
/// Pictures the whole game shows, belonging to no campaign: the logo, the loading screen,
/// the menu behind it, and the win and lose screens for each race.
///
/// Separate from <see cref="CampaignArt"/> because the distinction is the point. A campaign's
/// artwork changes one campaign; these change the game, in every campaign and in skirmish, so
/// they belong with the mod's own details rather than inside a campaign someone is editing.
///
/// All whole files under <c>Backgrounds/</c> — no sheet, no frame — so replacing one is
/// replacing the file, at the size and format the game ships.
/// </summary>
public static class GameArt
{
    public static IReadOnlyList<CampaignPicture> All { get; } = new[]
    {
        // Backgrounds/ratings-kr.png is absent too. It is the Korean age-rating notice, a
        // legal marque rather than artwork, and replacing it is nobody's idea of modding.
        //
        // Backgrounds/Logo.png is deliberately absent. It is a 640x360 title logo nobody
        // sets out to change, and this page is short enough to be read at a glance only if
        // everything on it earns its place. Still replaceable like any other game file.
        new CampaignPicture("Loading screen", "Backgrounds/loadscreen.png"),
        new CampaignPicture("Menu backdrop", "Backgrounds/main_background.png"),
        new CampaignPicture("Menu characters", "Backgrounds/main_background_characters.png"),
        new CampaignPicture("Victory, Human", "Backgrounds/victory_human.png"),
        new CampaignPicture("Victory, Orc", "Backgrounds/victory_orc.png"),
        new CampaignPicture("Defeat, Human", "Backgrounds/defeat_human.png"),
        new CampaignPicture("Defeat, Orc", "Backgrounds/defeat_orc.png"),
    };
}
