namespace RuneFoundry.Core.Scenarios;

/// <summary>
/// Every address the scenario engine reads or writes, in one place.
///
/// These are virtual addresses in the shipped Warcraft II Remastered executable, taken from
/// the preferred base 0x00400000. A running image can be loaded elsewhere, so every read
/// goes through the slide the module reports. Each entry names the section of
/// W2R-RE-NOTES.md or CUSTOM-SCENARIOS.md it came from, because an address without its
/// evidence is a number somebody will later be afraid to change.
///
/// <para>
/// Nothing here is used unless <see cref="GameBuild"/> recognises the executable. A build
/// we have not measured has different addresses, and reading them would be reading somebody
/// else's data.
/// </para>
/// </summary>
public static class GameAddresses
{
    /// <summary>The base the executable is linked for.</summary>
    public const uint PreferredBase = 0x00400000;

    // ---- context: what the game is doing right now (§3.4) ----------------------------

    /// <summary>word. 3 while a mission is being played.</summary>
    public const uint GameState = 0x0091C184;

    /// <summary>word. What the state was before this one.</summary>
    public const uint PreviousState = 0x0091C188;

    /// <summary>word. The mission slot, 0 to 51.</summary>
    public const uint MissionSlot = 0x009191AC;

    /// <summary>byte. 1 in the single-player campaign.</summary>
    public const uint GameMode = 0x00918BE8;

    /// <summary>byte. Set for skirmish and multiplayer.</summary>
    public const uint CustomGame = 0x00922F5B;

    /// <summary>byte. Which player is at the keyboard.</summary>
    public const uint LocalPlayer = 0x00918CCD;

    /// <summary>dword. Bit 0x200 means the mission is already resolved.</summary>
    public const uint Flags = 0x0091B270;

    /// <summary>The bit in <see cref="Flags"/> that says the mission is over.</summary>
    public const uint ResolvedBit = 0x200;

    // ---- the seam (§2) ---------------------------------------------------------------

    /// <summary>word. The objective id the dispatcher switches on.</summary>
    public const uint ObjectiveState = 0x009191C4;

    /// <summary>dword. The function the game calls to decide the mission. This is the seam.</summary>
    public const uint VictoryFunction = 0x0093766C;

    /// <summary>word. Ticks until the next evaluation.</summary>
    public const uint Countdown = 0x00937668;

    /// <summary>The game's own WIN terminal.</summary>
    public const uint WinTerminal = 0x004F4100;

    /// <summary>The game's own LOSE terminal.</summary>
    public const uint LoseTerminal = 0x004F40C0;

    /// <summary>
    /// The condition function that can never win: it falls straight through to the defeat
    /// check. Writing this as a mission's objective leaves the game playable and waiting,
    /// which is what a mission with author-written rules needs.
    /// </summary>
    public const uint InertTerminal = 0x004F43E0;

    /// <summary>
    /// The range every shipped condition function lives in. A pointer outside it is not one
    /// of the game's, and is a sign that we are reading a build we do not understand.
    /// </summary>
    public const uint ConditionsLow = 0x004F40C0;

    /// <summary>Exclusive upper bound of <see cref="ConditionsLow"/>.</summary>
    public const uint ConditionsHigh = 0x004F5940;

    /// <summary>
    /// The objective id that selects <see cref="InertTerminal"/>. Written at launch for a
    /// slot the mod has rules for, so the game stops deciding and waits for us.
    /// </summary>
    public const ushort InertObjective = 0x003F;

    // ---- scoreboard arrays (§3.3) ----------------------------------------------------

    /// <summary>dword[16]. Gold held.</summary>
    public const uint Gold = 0x00919128;

    /// <summary>dword[16]. Lumber held.</summary>
    public const uint Lumber = 0x009190E8;

    /// <summary>dword[16]. Oil held.</summary>
    public const uint Oil = 0x00919168;

    /// <summary>word[16]. Units killed.</summary>
    public const uint Kills = 0x00919478;

    /// <summary>word[16]. Buildings razed.</summary>
    public const uint Razings = 0x00919498;

    /// <summary>word[16]. Units rescued.</summary>
    public const uint Rescued = 0x009191F0;

    /// <summary>
    /// word[16]. Units delivered to the Circle of Power. A delivery adds 1 and a hero adds
    /// a further 0x1000, so the two are readable apart.
    /// </summary>
    public const uint Delivered = 0x009191D0;

    /// <summary>The bit weight a hero adds to <see cref="Delivered"/>.</summary>
    public const int HeroWeight = 0x1000;

    // ---- the counters the defeat check itself uses (§3.1) ----------------------------

    public const uint UnitsAlive = 0x0091B38C;
    public const uint BuildingsAlive = 0x0091B3AC;
    public const uint Tankers = 0x0091B68C;
    public const uint Transports = 0x0091B78C;
    public const uint Flyers = 0x0091B80C;

    /// <summary>Players 0 to 15.</summary>
    public const int PlayerCount = 16;

    /// <summary>The stride between one player's entry and the next in a word[16] array.</summary>
    public const int CounterStride = 2;
}
