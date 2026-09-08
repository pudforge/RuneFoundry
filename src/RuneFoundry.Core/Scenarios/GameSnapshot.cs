namespace RuneFoundry.Core.Scenarios;

/// <summary>
/// One look at the running game.
///
/// The evaluator sees only this, never a process. That is what lets the rules be tested
/// against a scripted game rather than a real one, and it keeps the part with the states
/// separate from the part that talks to Windows.
///
/// <para>
/// Every accessor returns null when the value could not be read. Null is never treated as
/// zero and never satisfies a condition: a rule must be shown to be true, not merely fail
/// to be shown false.
/// </para>
/// </summary>
public interface IGameSnapshot
{
    /// <summary>3 while a mission is being played.</summary>
    ushort? GameState { get; }

    /// <summary>The mission slot, 0 to 51.</summary>
    ushort? MissionSlot { get; }

    /// <summary>Whether the game is in the single-player campaign.</summary>
    bool? IsCampaign { get; }

    /// <summary>Whether this is a skirmish or a multiplayer game.</summary>
    bool? IsCustomGame { get; }

    /// <summary>The player at the keyboard.</summary>
    byte? LocalPlayer { get; }

    /// <summary>Whether the game has already decided this mission.</summary>
    bool? IsResolved { get; }

    /// <summary>The objective id the game is switching on.</summary>
    ushort? ObjectiveState { get; }

    /// <summary>Where the victory function pointer currently points.</summary>
    uint? VictoryFunction { get; }

    /// <summary>A player's tally from one of the counter arrays.</summary>
    ushort? Counter(uint array, int player);

    /// <summary>A player's entry in one of the dword arrays.</summary>
    uint? Resource(uint array, int player);
}

/// <summary>A snapshot taken from a live process.</summary>
public sealed class LiveSnapshot : IGameSnapshot
{
    private readonly GameMemory _memory;

    public LiveSnapshot(GameMemory memory) => _memory = memory;

    public ushort? GameState => _memory.Word(GameAddresses.GameState);
    public ushort? MissionSlot => _memory.Word(GameAddresses.MissionSlot);
    public byte? LocalPlayer => _memory.Byte(GameAddresses.LocalPlayer);
    public ushort? ObjectiveState => _memory.Word(GameAddresses.ObjectiveState);
    public uint? VictoryFunction => _memory.Dword(GameAddresses.VictoryFunction);

    public bool? IsCampaign => _memory.Byte(GameAddresses.GameMode) is { } mode ? mode == 1 : null;

    public bool? IsCustomGame =>
        _memory.Byte(GameAddresses.CustomGame) is { } custom ? custom != 0 : null;

    public bool? IsResolved =>
        _memory.Dword(GameAddresses.Flags) is { } flags
            ? (flags & GameAddresses.ResolvedBit) != 0
            : null;

    public ushort? Counter(uint array, int player) => _memory.Counter(array, player);
    public uint? Resource(uint array, int player) => _memory.Resource(array, player);
}
