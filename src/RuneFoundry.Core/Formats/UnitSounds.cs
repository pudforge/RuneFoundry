namespace RuneFoundry.Core.Formats;

/// <summary>What a sound is for.</summary>
public enum UnitSoundKind
{
    /// <summary>Played when the unit finishes training.</summary>
    Ready,

    /// <summary>Played when the unit is given an order.</summary>
    Acknowledge,

    /// <summary>Played when the unit is selected.</summary>
    Select,

    /// <summary>Played when the unit is selected over and over.</summary>
    Annoyed,

    /// <summary>Played when a worker finishes a job.</summary>
    WorkDone,

    /// <summary>Played when the unit is attacked.</summary>
    Help,

    /// <summary>Played when the unit dies.</summary>
    Death,
}

/// <summary>One sound a unit can play: which list it belongs to, and the file name.</summary>
public sealed record UnitSound(UnitSoundKind Kind, string FileName);

/// <summary>Everything one speaker says. Empty lists are speakers the game never recorded for.</summary>
public sealed record Voice(
    string Name,
    string[] Ready,
    string[] Acknowledge,
    string[] Select,
    string[] Annoyed,
    string[] WorkDone,
    string[] Help,
    string[] Death);

/// <summary>
/// Which sounds each unit plays.
///
/// A hand-kept map, in the shape PUDForge uses in <c>overrides/unit_sounds.cpp</c>, because
/// no rule derives it: the file names are irregular (the Footman is <c>Hready.wav</c>, the
/// Knight <c>Knready.wav</c>, the Archer <c>Eready.wav</c> but <c>Ewhat1.wav</c>) and
/// several units share a voice — a Paladin rides a Knight and sounds like one, Cho'gall is
/// an Ogre-Mage. That judgement is the point of writing it down.
///
/// <para>
/// Race decides the file, not just the folder. The sound registry at <c>0x008C1FFC</c> holds
/// two parallel banks and the orc one sits exactly <c>0x163</c> above the human one —
/// <c>Hyessir1</c> is id <c>0x012</c> and <c>Oyessir1</c> is <c>0x175</c>, <c>Hfarm</c> is
/// <c>0x033</c> and <c>Ofarm</c> is <c>0x196</c>. A Grunt and a Footman share a picker and
/// still say different things, so orc units name the orc folder's files here (§5i).
/// </para>
///
/// <para>
/// The tables are read from the installed game rather than guessed: every name below exists
/// in <c>Gamesfx/</c>, and a test checks that it still does.
/// </para>
/// </summary>
public static class UnitSounds
{
    private static readonly string[] Empty = Array.Empty<string>();

    /// <summary>Every speaker the game recorded, and everything it says.</summary>
    public static readonly IReadOnlyList<Voice> Voices = new[]
    {
        new Voice("Human",
            new[] { "Hready.wav" },
            new[] { "Hyessir1.wav", "Hyessir2.wav", "Hyessir3.wav", "Hyessir4.wav" },
            new[] { "Hwhat1.wav", "Hwhat2.wav", "Hwhat3.wav", "Hwhat4.wav", "Hwhat5.wav", "Hwhat6.wav" },
            new[] { "Hdempis4.wav", "Hdempis5.wav", "Hdempis6.wav", "Hdempis7.wav", "Hpissed1.wav", "Hpissed2.wav", "Hpissed3.wav", "Hpissed4.wav", "Hpissed5.wav", "Hpissed6.wav", "Hpissed7.wav" },
            new[] { "Hwrkdone.wav" }, new[] { "Hhelp1.wav", "Hhelp2.wav" }, new[] { "Hdead.wav" }),
        new Voice("Orc",
            new[] { "Oready.wav" },
            new[] { "Oyessir1.wav", "Oyessir2.wav", "Oyessir3.wav", "Oyessir4.wav" },
            new[] { "Owhat1.wav", "Owhat2.wav", "Owhat3.wav", "Owhat4.wav", "Owhat5.wav", "Owhat6.wav" },
            new[] { "Odempis4.wav", "Odempis5.wav", "Odempis6.wav", "Odempis7.wav", "Opissed1.wav", "Opissed2.wav", "Opissed3.wav", "Opissed4.wav", "Opissed5.wav", "Opissed6.wav", "Opissed7.wav" },
            new[] { "Owrkdone.wav" }, new[] { "Ohelp1.wav", "Ohelp2.wav" }, new[] { "Odead.wav" }),
        new Voice("Peasant",
            new[] { "Psready.wav" },
            new[] { "Psyessr1.wav", "Psyessr2.wav", "Psyessr3.wav", "Psyessr4.wav" },
            new[] { "Pswhat1.wav", "Pswhat2.wav", "Pswhat3.wav", "Pswhat4.wav" },
            new[] { "Pspissd1.wav", "Pspissd2.wav", "Pspissd3.wav", "Pspissd4.wav", "Pspissd5.wav", "Pspissd6.wav", "Pspissd7.wav" },
            new[] { "Pswrkdon.wav" }, Empty, Empty),
        new Voice("Peon",
            new[] { "Pnready.wav" },
            Empty,
            Empty,
            Empty,
            Empty, Empty, Empty),
        new Voice("Knight",
            new[] { "Knready.wav" },
            new[] { "Knyessr1.wav", "Knyessr2.wav", "Knyessr3.wav", "Knyessr4.wav" },
            new[] { "Knwhat1.wav", "Knwhat2.wav", "Knwhat3.wav", "Knwhat4.wav" },
            new[] { "Knpissd1.wav", "Knpissd2.wav", "Knpissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Ogre",
            new[] { "Ogready.wav" },
            new[] { "Ogyessr1.wav", "Ogyessr2.wav", "Ogyessr3.wav" },
            new[] { "Ogwhat1.wav", "Ogwhat2.wav", "Ogwhat3.wav", "Ogwhat4.wav" },
            new[] { "Ogpissd1.wav", "Ogpissd2.wav", "Ogpissd3.wav", "Ogpissd4.wav", "Ogpissd5.wav" },
            Empty, Empty, Empty),
        new Voice("Elves",
            new[] { "Eready.wav" },
            new[] { "Eyessir1.wav", "Eyessir2.wav", "Eyessir3.wav", "Eyessir4.wav" },
            new[] { "Ewhat1.wav", "Ewhat2.wav", "Ewhat3.wav", "Ewhat4.wav" },
            new[] { "Epissed1.wav", "Epissed2.wav", "Epissed3.wav" },
            Empty, Empty, Empty),
        new Voice("Troll",
            new[] { "Trready.wav" },
            new[] { "Tryessr1.wav", "Tryessr2.wav", "Tryessr3.wav" },
            new[] { "Trwhat1.wav", "Trwhat2.wav", "Trwhat3.wav" },
            new[] { "Trpissd1.wav", "Trpissd2.wav", "Trpissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Wizard",
            new[] { "Wzready.wav" },
            new[] { "Wzyessr1.wav", "Wzyessr2.wav", "Wzyessr3.wav" },
            new[] { "Wzwhat1.wav", "Wzwhat2.wav", "Wzwhat3.wav" },
            new[] { "Wzpissd1.wav", "Wzpissd2.wav", "Wzpissd3.wav" },
            Empty, Empty, Empty),
        new Voice("DeathKnt",
            new[] { "Dkready.wav" },
            new[] { "Dkyessr1.wav", "Dkyessr2.wav", "Dkyessr3.wav" },
            new[] { "Dkwhat1.wav", "Dkwhat2.wav" },
            new[] { "Dkpissd1.wav", "Dkpissd2.wav", "Dkpissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Paladin",
            new[] { "Pkready.wav" },
            new[] { "Pkyessr1.wav", "Pkyessr2.wav", "Pkyessr3.wav", "Pkyessr4.wav" },
            new[] { "Pkwhat1.wav", "Pkwhat2.wav", "Pkwhat3.wav", "Pkwhat4.wav" },
            new[] { "Pkpissd1.wav", "Pkpissd2.wav", "Pkpissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Ogremage",
            new[] { "Omready.wav" },
            new[] { "Omyessr1.wav", "Omyessr2.wav", "Omyessr3.wav" },
            new[] { "Omwhat1.wav", "Omwhat2.wav", "Omwhat3.wav", "Omwhat4.wav" },
            new[] { "Ompissd1.wav", "Ompissd2.wav", "Ompissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Dwarf",
            new[] { "Dwready.wav" },
            new[] { "Dwyessr1.wav", "Dwyessr2.wav", "Dwyessr3.wav", "Dwyessr4.wav", "Dwyessr5.wav" },
            new[] { "Dwhat1.wav", "Dwhat2.wav" },
            new[] { "Dwpissd1.wav", "Dwpissd2.wav", "Dwpissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Goblin",
            new[] { "Goready.wav" },
            new[] { "Goyessr1.wav", "Goyessr2.wav", "Goyessr3.wav", "Goyessr4.wav" },
            new[] { "Gowhat1.wav", "Gowhat2.wav", "Gowhat3.wav", "Gowhat4.wav" },
            new[] { "Gopissd1.wav", "Gopissd2.wav", "Gopissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Gnome",
            new[] { "Gnready.wav" },
            new[] { "Gnyessr1.wav" },
            Empty,
            new[] { "Gnpissd1.wav", "Gnpissd2.wav", "Gnpissd3.wav", "Gnpissd4.wav", "Gnpissd5.wav" },
            Empty, Empty, Empty),
        new Voice("Zeppelin",
            new[] { "Gbready.wav" },
            new[] { "Gbyessr1.wav" },
            Empty,
            new[] { "Gbpissd1.wav", "Gbpissd2.wav" },
            Empty, Empty, Empty),
        new Voice("Griffon",
            Empty,
            Empty,
            new[] { "Grwhat.wav" },
            Empty,
            Empty, Empty, Empty),
        new Voice("Dragon",
            new[] { "Drready.wav" },
            new[] { "Dryessr1.wav", "Dryessr2.wav" },
            new[] { "Drwhat.wav" },
            Empty,
            Empty, Empty, Empty),
        new Voice("ShipHuman",
            Empty,
            new[] { "Hshpyes1.wav", "Hshpyes2.wav", "Hshpyes3.wav" },
            new[] { "Hshpwht1.wav", "Hshpwht2.wav", "Hshpwht3.wav" },
            Empty,
            Empty, Empty, Empty),
        new Voice("ShipOrc",
            Empty,
            new[] { "Oshpyes1.wav", "Oshpyes2.wav", "Oshpyes3.wav" },
            new[] { "Oshpwht1.wav", "Oshpwht2.wav", "Oshpwht3.wav" },
            Empty,
            Empty, Empty, Empty),
        new Voice("Aleria",
            Empty,
            new[] { "Alyessr1.wav", "Alyessr2.wav", "Alyessr3.wav" },
            new[] { "Alwhat1.wav", "Alwhat2.wav", "Alwhat3.wav" },
            new[] { "Alpissd1.wav", "Alpissd2.wav", "Alpissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Danath",
            Empty,
            new[] { "Dnyessr1.wav", "Dnyessr2.wav", "Dnyessr3.wav" },
            new[] { "Dnwhat1.wav", "Dnwhat2.wav", "Dnwhat3.wav" },
            new[] { "Dnpisd1.wav", "Dnpisd2.wav", "Dnpisd3.wav" },
            Empty, Empty, Empty),
        new Voice("Kargath",
            Empty,
            new[] { "Kayessr1.wav", "Kayessr2.wav", "Kayessr3.wav" },
            new[] { "Kawhat1.wav", "Kawhat2.wav", "Kawhat3.wav" },
            new[] { "Kapissd1.wav", "Kapissd2.wav", "Kapissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Khadgar",
            Empty,
            new[] { "Khyessr1.wav", "Khyessr2.wav", "Khyessr3.wav" },
            new[] { "Khwhat1.wav", "Khwhat2.wav", "Khwhat3.wav" },
            new[] { "Khpissd1.wav", "Khpissd2.wav", "Khpissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Kurdran",
            Empty,
            new[] { "Kuyessr1.wav", "Kuyessr2.wav", "Kuyessr3.wav" },
            new[] { "Kuwhat1.wav", "Kuwhat2.wav", "Kuwhat3.wav" },
            new[] { "Kupissd1.wav", "Kupissd2.wav", "Kupissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Teron",
            Empty,
            new[] { "Teyessr1.wav", "Teyessr2.wav", "Teyessr3.wav" },
            new[] { "Tewhat1.wav", "Tewhat2.wav", "Tewhat3.wav" },
            new[] { "Tepissd1.wav", "Tepissd2.wav", "Tepissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Turalyon",
            Empty,
            new[] { "Tuyessr1.wav", "Tuyessr2.wav", "Tuyessr3.wav" },
            new[] { "Tuwhat1.wav", "Tuwhat2.wav", "Tuwhat3.wav" },
            new[] { "Tupissd1.wav", "Tupissd2.wav", "Tupissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Grom",
            Empty,
            new[] { "Gryessr1.wav", "Gryessr2.wav", "Gryessr3.wav" },
            new[] { "Grwhat1.wav", "Grwhat2.wav", "Grwhat3.wav" },
            new[] { "Grpissd1.wav", "Grpissd2.wav", "Grpissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Dentarg",
            Empty,
            new[] { "Odyessr1.wav", "Odyessr2.wav", "Odyessr3.wav" },
            new[] { "Odwhat1.wav", "Odwhat2.wav", "Odwhat3.wav" },
            new[] { "Odpissd1.wav", "Odpissd2.wav", "Odpissd3.wav" },
            Empty, Empty, Empty),
        new Voice("Skeleton",
            Empty,
            new[] { "Skeleton Move.wav" },
            Empty,
            Empty,
            Empty, Empty, new[] { "Skeleton Death.wav" }),
        new Voice("DeathWng",
            Empty,
            new[] { "Deyessr1.wav", "Deyessr2.wav", "Deyessr3.wav" },
            new[] { "Dewhat1.wav", "Dewhat2.wav", "Dewhat3.wav" },
            new[] { "Depissd1.wav", "Depissd2.wav", "Depissd3.wav" },
            Empty, Empty, Empty),
    };

    /// <summary>Which voice each unit answers in. -1 is a unit with no speaker's voice.</summary>
    private static readonly Dictionary<int, int> UnitVoice = new()
    {
        [0x00] =  0,   // Footman
        [0x01] =  1,   // Grunt
        [0x02] =  2,   // Peasant
        [0x03] =  3,   // Peon
        [0x04] = -1,   // Ballista — a machine; the game gives it Catyessr.wav
        [0x05] = -1,   // Catapult — likewise
        [0x06] =  4,   // Knight
        [0x07] =  5,   // Ogre
        [0x08] =  6,   // Archer
        [0x09] =  7,   // Axethrower
        [0x0A] =  8,   // Mage
        [0x0B] =  9,   // Death Knight
        [0x0C] = 10,   // Paladin
        [0x0D] = 11,   // Ogre-Mage
        [0x0E] = 12,   // Dwarven Demolition Squad
        [0x0F] = 13,   // Goblin Sappers
        [0x10] =  2,   // Attack Peasant — the same worker with a sword
        [0x11] =  3,   // Attack Peon
        [0x12] =  6,   // Ranger — an Elven Archer, and sounds like one
        [0x13] =  7,   // Berserker
        [0x14] = 20,   // Alleria
        [0x15] = 25,   // Teron Gorefiend
        [0x16] = 24,   // Kurdran and Sky'ree
        [0x17] = 28,   // Dentarg
        [0x18] = 23,   // Khadgar
        [0x19] = 27,   // Grom Hellscream
        [0x1A] = 18,   // Human Oil Tanker
        [0x1B] = 19,   // Orc Oil Tanker
        [0x1C] = 18,   // Human Transport
        [0x1D] = 19,   // Orc Transport
        [0x1E] = 18,   // Elven Destroyer
        [0x1F] = 19,   // Troll Destroyer
        [0x20] = 18,   // Battleship
        [0x21] = 19,   // Ogre Juggernaught
        [0x23] = 30,   // Deathwing — his own voice, in Gamesfx/DeathWng
        [0x26] = 18,   // Gnomish Submarine
        [0x27] = 19,   // Giant Turtle
        [0x28] = 14,   // Gnomish Flying Machine
        [0x29] = 15,   // Goblin Zeppelin
        [0x2A] = 16,   // Gryphon Rider
        [0x2B] = 17,   // Dragon
        [0x2C] = 26,   // Turalyon
        [0x2D] = -1,   // Eye of Kilrogg — one movement loop, not speech
        [0x2E] = 21,   // Danath
        [0x2F] = 22,   // Korgath Bladefist
        [0x31] = 11,   // Cho'gall — an Ogre-Mage, and sounds like one
        [0x32] =  4,   // Lothar — rides a Knight
        [0x33] =  9,   // Gul'dan
        [0x34] = 10,   // Uther Lightbringer
        [0x35] =  7,   // Zul'jin
        [0x37] = 29,   // Skeleton — two clips under Misc, and no speech at all
        [0x38] = -1,   // Daemon — the game gives it the interface click
        [0x39] = -1,   // Critter — sheep, seals and pigs, under Misc
    };

    /// <summary>Units whose sound is one file rather than a speaker's whole voice.</summary>
    private static readonly Dictionary<int, (UnitSoundKind Kind, string File)> OneOffs = new()
    {
        [0x04] = (UnitSoundKind.Acknowledge, "Catyessr.wav"),
        [0x05] = (UnitSoundKind.Acknowledge, "Catyessr.wav"),
        [0x2D] = (UnitSoundKind.Acknowledge, "Eye of Killrog Move.wav"),
        [0x38] = (UnitSoundKind.Acknowledge, "Button.wav"),
        [0x39] = (UnitSoundKind.Acknowledge, "WARTHOG.wav"),
    };

    /// <summary>The sounds a unit plays, in the order a player meets them.</summary>
    public static IReadOnlyList<UnitSound> For(int unit)
    {
        var sounds = new List<UnitSound>();

        if (OneOffs.TryGetValue(unit, out var one))
        {
            sounds.Add(new UnitSound(one.Kind, one.File));
            return sounds;
        }

        if (!UnitVoice.TryGetValue(unit, out var index) || index < 0) return sounds;

        var voice = Voices[index];
        void Add(UnitSoundKind kind, string[] names)
        {
            foreach (var name in names) sounds.Add(new UnitSound(kind, name));
        }

        Add(UnitSoundKind.Ready, voice.Ready);
        Add(UnitSoundKind.Acknowledge, voice.Acknowledge);
        Add(UnitSoundKind.Select, voice.Select);
        Add(UnitSoundKind.Annoyed, voice.Annoyed);
        Add(UnitSoundKind.WorkDone, voice.WorkDone);
        Add(UnitSoundKind.Help, voice.Help);
        Add(UnitSoundKind.Death, voice.Death);

        return sounds;
    }

    /// <summary>Whether anything is known about this unit's sounds.</summary>
    public static bool Known(int unit) => OneOffs.ContainsKey(unit)
                                          || (UnitVoice.TryGetValue(unit, out var i) && i >= 0);

    /// <summary>The speaker this unit answers in, for saying so on screen.</summary>
    public static Voice? VoiceOf(int unit) =>
        UnitVoice.TryGetValue(unit, out var i) && i >= 0 ? Voices[i] : null;
}