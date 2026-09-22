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

    /// <summary>
    /// Whether the mission's units have loaded: any player owns at least one unit or
    /// building. False during the load window between OBJ_STATE arming and unit
    /// registration, when every count reads zero and an "owns exactly 0" rule would fire
    /// on an empty map. Null when it could not be read.
    /// </summary>
    bool? MapLoaded { get; }

    /// <summary>
    /// How many units of any of these types are alive, for one player or for everybody.
    /// A walk over the unit records, not a counter: the counters lump a hero in with the
    /// unit it is built on, and this is how the game's own hero rules ask the question.
    /// Null when the records could not be read.
    /// </summary>
    int? UnitsOfTypes(IReadOnlyList<byte> types, int? player);
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
    public uint? VictoryFunction => _memory.CodePointer(GameAddresses.VictoryFunction);

    public bool? IsCampaign => _memory.Byte(GameAddresses.GameMode) is { } mode ? mode == 1 : null;

    public bool? IsCustomGame =>
        _memory.Byte(GameAddresses.CustomGame) is { } custom ? custom != 0 : null;

    public bool? IsResolved =>
        _memory.Dword(GameAddresses.Flags) is { } flags
            ? (flags & GameAddresses.ResolvedBit) != 0
            : null;

    public ushort? Counter(uint array, int player) => _memory.Counter(array, player);
    public uint? Resource(uint array, int player) => _memory.Resource(array, player);

    public bool? MapLoaded
    {
        get
        {
            var any = false;
            for (var p = 0; p < GameAddresses.PlayerCount; p++)
            {
                var units = _memory.Counter(GameAddresses.UnitsAlive, p);
                var bldgs = _memory.Counter(GameAddresses.BuildingsAlive, p);
                if (units is null || bldgs is null) return null;
                if (units > 0 || bldgs > 0) any = true;
            }
            return any;
        }
    }

    public int? UnitsOfTypes(IReadOnlyList<byte> types, int? player)
    {
        if (types.Count == 0) return 0;
        if (_memory.Dword(GameAddresses.UnitArray) is not { } baseAddress) return null;
        if (_memory.Dword(GameAddresses.UnitCount) is not { } count) return null;
        if (baseAddress == 0 || count == 0) return 0;
        if (count > GameAddresses.MaxUnits) return null;

        // The whole array in one read, then the walk happens in our own memory: the
        // per-player lists link records inside this same array, so a next pointer is
        // turned into an index and followed without touching the process again.
        //
        // The lists, not the array. The array's count is its capacity, and a slot the
        // game has freed keeps its bytes until reused; a hero who died an hour ago would
        // still read as alive from the array. The game unlinks the dead from the lists,
        // which is why its own hero rules walk them.
        var span = (long)count * GameAddresses.UnitStride;
        var records = _memory.BytesAt(baseAddress, (int)span);
        if (records is null) return null;

        var alive = 0;
        for (var owner = 0; owner < GameAddresses.PlayerCount; owner++)
        {
            if (player is { } only && owner != only) continue;

            var next = _memory.Dword(GameAddresses.UnitListHeads + (uint)(owner * 4));
            if (next is null) return null;

            var hops = 0;
            while (next is { } address && address != 0 && hops++ < count)
            {
                var offset = (long)address - baseAddress;
                if (offset < 0 || offset + GameAddresses.UnitStride > span
                    || offset % GameAddresses.UnitStride != 0) break;   // not one of ours: stop, do not guess

                var at = (int)offset;
                if (types.Contains(records[at + GameAddresses.UnitTypeOffset])
                    && records[at + GameAddresses.UnitOwnerOffset] == owner
                    && (records[at + GameAddresses.UnitStateOffset] & GameAddresses.UnitDyingMask) == 0)
                    alive++;

                next = BitConverter.ToUInt32(records, at + GameAddresses.UnitNextOffset);
            }
        }

        return alive;
    }
}
