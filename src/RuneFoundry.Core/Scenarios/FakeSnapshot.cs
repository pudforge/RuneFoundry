namespace RuneFoundry.Core.Scenarios;

/// <summary>
/// A game that is not running, for tests and for driving the engine without one.
///
/// In Core rather than in the test project because the engine's own tests, the Probe and
/// the UI checks all need one, and three copies of a fake would drift apart. It is also the
/// only way to exercise "the read failed": leave a value unset and it reads as null, which
/// is the case a real game almost never produces on demand.
/// </summary>
public sealed class FakeSnapshot : IGameSnapshot
{
    private readonly Dictionary<(uint Array, int Player), ushort> _counters = new();
    private readonly Dictionary<(uint Array, int Player), uint> _resources = new();

    public ushort? GameState { get; set; } = 3;
    public ushort? MissionSlot { get; set; } = 0;
    public bool? IsCampaign { get; set; } = true;
    public bool? IsCustomGame { get; set; } = false;
    public byte? LocalPlayer { get; set; } = 0;
    public bool? IsResolved { get; set; } = false;
    public ushort? ObjectiveState { get; set; } = GameAddresses.InertObjective;
    public uint? VictoryFunction { get; set; } = GameAddresses.InertTerminal;

    /// <summary>Counters left unset read as zero rather than as unreadable.</summary>
    public bool MissingCountersAreZero { get; set; } = true;

    public FakeSnapshot Set(uint array, int player, int value)
    {
        _counters[(array, player)] = (ushort)value;
        return this;
    }

    public FakeSnapshot SetResource(uint array, int player, uint value)
    {
        _resources[(array, player)] = value;
        return this;
    }

    /// <summary>Makes one counter unreadable, which is what a failed read looks like.</summary>
    public FakeSnapshot Unreadable(uint array, int player)
    {
        _counters.Remove((array, player));
        Unreadables.Add((array, player));
        return this;
    }

    private HashSet<(uint Array, int Player)> Unreadables { get; } = new();

    public ushort? Counter(uint array, int player)
    {
        if (Unreadables.Contains((array, player))) return null;
        if (_counters.TryGetValue((array, player), out var value)) return value;

        return MissingCountersAreZero ? (ushort)0 : null;
    }

    public uint? Resource(uint array, int player)
    {
        if (Unreadables.Contains((array, player))) return null;
        if (_resources.TryGetValue((array, player), out var value)) return value;

        return MissingCountersAreZero ? 0u : null;
    }
}
