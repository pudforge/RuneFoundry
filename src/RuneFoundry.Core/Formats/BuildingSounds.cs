namespace RuneFoundry.Core.Formats;

/// <summary>One of the game's building sounds, and whether the game ever plays it.</summary>
/// <param name="FileName">The .wav, as the registry names it.</param>
/// <param name="SoundId">Its index in the sound registry at <c>0x008C1FFC</c>. Human-side
/// sounds sit near <c>0x030</c> and their orc counterparts <c>0x163</c> above.</param>
/// <param name="Label">What it is, in the words a player would use.</param>
public sealed record BuildingSound(string FileName, int SoundId, string Label)
{
    /// <summary>Whether the executable refers to this file at all.</summary>
    public bool Played => SoundId >= 0;
}

/// <summary>
/// The noise each building makes, and the reason it is a hand-written map.
///
/// A unit's sounds are chosen by its type: <c>[0x008C36C8 + type*4]</c> is a picker function
/// per type. Those tables are **58 entries long** and buildings start at type 58, so they do
/// not cover buildings at all — which is why selecting one used to show nothing.
///
/// Tracing it (§5i) shows there is no building equivalent to find: each building sound is a
/// constant compiled into the routine for one event. <c>0x004C7950</c> pushes <c>0x42</c>
/// (<c>wzrdtowr.wav</c>) and <c>0x004C7A77</c> pushes <c>0x3C</c> (<c>lumbmill.wav</c>),
/// each straight into the registry-backed players at <c>0x004F9950</c>/<c>0x004F99A0</c>.
/// Nothing is indexed by building type, so nothing can be re-pointed and nothing can be
/// derived. The association below is therefore a judgement, not a decoding — the same
/// judgement PUDForge makes in <c>overrides/unit_sounds.cpp</c>, and kept for the same
/// reason: no rule produces it, so it is written down once and checked.
///
/// <para>
/// Both races are here, and they are genuinely different sounds. The registry holds two
/// parallel banks with the orc one exactly <c>0x163</c> above the human: <c>Hfarm</c> is
/// <c>0x033</c> and <c>Ofarm</c> is <c>0x196</c>, <c>aviary</c> is <c>0x03F</c> and
/// <c>dragon</c> is <c>0x1A2</c>. A Pig Farm does not sound like a Farm.
/// </para>
///
/// <para>
/// Buildings that are not listed make no sound of their own — barracks, halls, keeps,
/// towers, walls, the Circle of Power, the Dark Portal, the Runestone. The editor says so
/// rather than showing an empty area.
/// </para>
/// </summary>
public static class BuildingSounds
{
    /// <summary>Registry id, or Unreferenced when the executable never names the file.</summary>
    private const int Unreferenced = -1;

    /// <summary>
    /// The building sounds, by the unit type that makes them.
    ///
    /// Shared entries are shared in the game too: both shipyards ring the same bell, and
    /// both lumber mills, foundries, blacksmiths, refineries and oil rigs use one file.
    /// </summary>
    private static readonly (int Unit, BuildingSound Sound)[] Map =
    {
        (0x3A, new BuildingSound("Hfarm.wav", 0x033, "Farm")),
        (0x3B, new BuildingSound("Ofarm.wav", 0x196, "Pig Farm")),
        (0x3E, new BuildingSound("Hchant.wav", 0x031, "Church")),
        (0x3F, new BuildingSound("Ochant.wav", 0x194, "Altar of Storms")),
        (0x42, new BuildingSound("Stables.wav", 0x032, "Stables")),
        (0x43, new BuildingSound("Ogrecamp.wav", 0x195, "Ogre Mound")),
        (0x44, new BuildingSound("Inventor.wav", 0x041, "Gnomish Inventor")),
        (0x45, new BuildingSound("Alchemst.wav", 0x1A4, "Goblin Alchemist")),
        (0x46, new BuildingSound("Aviary.wav", 0x03F, "Gryphon Aviary")),
        (0x47, new BuildingSound("Dragon.wav", 0x1A2, "Dragon Roost")),
        (0x48, new BuildingSound("Shipbell.wav", 0x038, "Human Shipyard")),
        (0x49, new BuildingSound("Shipbell.wav", 0x19B, "Orc Shipyard")),
        (0x4C, new BuildingSound("Lumbmill.wav", 0x03C, "Elven Lumber Mill")),
        (0x4D, new BuildingSound("Lumbmill.wav", 0x19F, "Troll Lumber Mill")),
        (0x4E, new BuildingSound("Foundry.wav", 0x040, "Human Foundry")),
        (0x4F, new BuildingSound("Foundry.wav", 0x1A3, "Orc Foundry")),
        (0x50, new BuildingSound("Wzrdtowr.wav", 0x042, "Mage Tower")),
        (0x51, new BuildingSound("Dthtower.wav", 0x1A5, "Temple of the Damned")),
        (0x52, new BuildingSound("Smith.wav", 0x030, "Human Blacksmith")),
        (0x53, new BuildingSound("Smith.wav", 0x193, "Orc Blacksmith")),
        (0x54, new BuildingSound("Oilrefin.wav", 0x03B, "Human Refinery")),
        (0x55, new BuildingSound("Oilrefin.wav", 0x19E, "Orc Refinery")),
        (0x56, new BuildingSound("Oilplat.wav", 0x03A, "Human Oil Well")),
        (0x57, new BuildingSound("Oilplat.wav", 0x19D, "Orc Oil Well")),
        (0x5C, new BuildingSound("Mine.wav", 0x034, "Gold Mine")),
    };

    /// <summary>Every distinct building sound, in registry order, played ones first.</summary>
    public static readonly IReadOnlyList<BuildingSound> All =
        Map.Select(entry => entry.Sound)
           .GroupBy(sound => sound.SoundId)
           .Select(group => group.First())
           .OrderBy(sound => sound.SoundId)
           .ToList();

    /// <summary>The ones the executable refers to, which is now all of them.</summary>
    public static IEnumerable<BuildingSound> Played => All.Where(sound => sound.Played);

    /// <summary>
    /// The first unit type that is a building. The picker tables stop here, which is the
    /// whole reason buildings need their own answer.
    /// </summary>
    public const int FirstBuildingType = 58;

    public static bool IsBuilding(int unitType) => unitType >= FirstBuildingType;

    /// <summary>The noise this building makes, or null when it has none.</summary>
    public static BuildingSound? For(int unitType)
    {
        foreach (var (unit, sound) in Map)
            if (unit == unitType)
                return sound;

        return null;
    }
}
