using System.Buffers.Binary;

namespace RuneFoundry.Core.Formats;

/// <summary>
/// What each campaign mission lets you build, research and cast.
///
/// A campaign mission does not start with the whole tech tree: Human 1 allows farms and
/// barracks and nothing else, and later missions unlock more. That is not in the map — it
/// is three tables inside the executable, one dword per mission slot, copied into the
/// per-player masks when the map loads (MAPHDR at <c>0x004D2B40</c>, see W2R-RE-NOTES §5a):
///
/// <code>
/// 0x008C1AE8  dword[52]  allowed units      -> 0x00919350 dword[16] per player
/// 0x008C1C88  dword[52]  allowed upgrades   -> 0x009192D0
/// 0x008C1D58  dword[52]  allowed spells     -> 0x00919250
/// </code>
///
/// They sit in the same grid as the objective and threshold tables, which is how they were
/// found: the gaps are exactly 52 entries wide.
///
/// Setting a mission's mask to every bit is what "let me build anything" means. Which bit
/// is which building is *not* decoded — the game maps a unit to a bit at run time through
/// state it builds at map load — so this offers the whole mask or the game's own value, and
/// does not pretend to name the bits.
/// </summary>
public static class CampaignTech
{
    public const uint UnitsAddress = 0x008C1AE8;
    public const uint UpgradesAddress = 0x008C1C88;
    public const uint SpellsAddress = 0x008C1D58;

    /// <summary>The same 52 campaign slots the objective table uses.</summary>
    public const int MissionCount = CampaignObjectives.MissionCount;

    /// <summary>Everything allowed — what a mission with no restrictions looks like.</summary>
    public const uint Everything = 0xFFFFFFFF;

    /// <summary>The three tables, in the order they appear in the executable.</summary>
    public enum Table
    {
        Units,
        Upgrades,
        Spells,
    }

    public static uint AddressOf(Table table) => table switch
    {
        Table.Units => UnitsAddress,
        Table.Upgrades => UpgradesAddress,
        Table.Spells => SpellsAddress,
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };

    public static string NameOf(Table table) => table switch
    {
        Table.Units => "buildings and units",
        Table.Upgrades => "upgrades",
        Table.Spells => "spells",
        _ => "",
    };

    /// <summary>Reads one table out of an executable image.</summary>
    public static uint[] Read(byte[] exe, Table table)
    {
        if (!CampaignObjectives.TryFileOffset(exe, AddressOf(table), out var offset, out var error))
            throw new InvalidDataException(error);

        if (offset + MissionCount * 4 > exe.Length)
            throw new InvalidDataException($"The {NameOf(table)} table runs past the end of the file.");

        var values = new uint[MissionCount];
        for (var i = 0; i < MissionCount; i++)
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(offset + i * 4));

        return values;
    }

    /// <summary>
    /// Whether a mission restricts anything. A slot with every bit set is one that has
    /// already been opened up, so there is nothing to say about it.
    /// </summary>
    public static bool Restricts(uint mask) => mask != Everything;

    // ---- the map's own copy -------------------------------------------------

    // Where each of the three masks sits in a PUD's ALOW chunk. The handler at 0x004D19D0
    // reads six arrays in this order; the odd ones are the "allowed" masks the fanout fills
    // from the tables above, and the even ones are the starting state that goes with each.
    public const int AllowUnitState = 0;      // 0x919210
    public const int AllowUnits = 1;          // 0x919250
    public const int AllowUpgradeState = 2;   // 0x919290
    public const int AllowUpgrades = 3;       // 0x9192D0
    public const int AllowSpellState = 4;     // 0x919310
    public const int AllowSpells = 5;         // 0x919350

    /// <summary>What the fanout writes into the upgrade state array for every player.</summary>
    public const uint UpgradeStateSeed = 0x4020;

    /// <summary>
    /// The ALOW chunk a mission would need to lift its build restrictions.
    ///
    /// Every value is what the game's own fanout at <c>0x004D2B40</c> would have written for
    /// this mission, so the chunk changes nothing except what it is meant to: the players
    /// listed in <paramref name="openFor"/> get all three masks opened, and everyone else
    /// keeps the campaign's. Nothing here is invented — the seeds are copied from the code
    /// that runs when no ALOW chunk is present.
    /// </summary>
    public static uint[][] AllowArrays(
        uint units, uint upgrades, uint spells, IReadOnlyCollection<int> openFor)
    {
        var arrays = new uint[PudFile.AllowArrayCount][];
        for (var i = 0; i < arrays.Length; i++) arrays[i] = new uint[PudFile.AllowPlayers];

        for (var player = 0; player < PudFile.AllowPlayers; player++)
        {
            var open = openFor.Contains(player);

            arrays[AllowUnitState][player] = 0;
            arrays[AllowUnits][player] = open ? Everything : units;
            arrays[AllowUpgradeState][player] = UpgradeStateSeed;
            arrays[AllowUpgrades][player] = open ? Everything : upgrades;
            arrays[AllowSpellState][player] = 0;
            arrays[AllowSpells][player] = open ? Everything : spells;
        }

        return arrays;
    }

    /// <summary>The slots a person plays, which are the ones worth opening up.</summary>
    public static IReadOnlyList<int> HumanPlayers(IReadOnlyList<byte> owners) =>
        owners.Select((owner, index) => (owner, index))
              .Where(pair => pair.owner == PudFile.HumanOwner)
              .Select(pair => pair.index)
              .ToList();

    /// <summary>
    /// Whether a map's ALOW chunk opens everything up for these players — which is how the
    /// checkbox knows its own state without a second place to keep it.
    /// </summary>
    public static bool OpensEverythingFor(uint[][]? arrays, IReadOnlyCollection<int> players)
    {
        if (arrays is null || players.Count == 0) return false;

        return players.All(player =>
            player >= 0 && player < PudFile.AllowPlayers
            && arrays[AllowUnits][player] == Everything
            && arrays[AllowUpgrades][player] == Everything
            && arrays[AllowSpells][player] == Everything);
    }
}
