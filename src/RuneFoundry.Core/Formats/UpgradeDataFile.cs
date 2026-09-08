namespace RuneFoundry.Core.Formats;

/// <summary>
/// `Data/Rez/upgrades.dat` — research time, cost, icon and researched-slot for the 52
/// upgrades. Ids 0-33 are researches, 34-51 are spells.
///
/// Layout is <see cref="DatSchema.UpgradeFields"/>, the same one the PUD `UGRD` section
/// carries. Costs here are whole numbers, unlike the unit table's tens.
/// </summary>
public sealed class UpgradeDataFile : DatTable
{
    private UpgradeDataFile(byte[] bytes)
        : base(bytes, DatSchema.UpgradeFields, DatSchema.UpgradeCount) { }

    public static bool LooksLikeUpgradeData(byte[] data) => data.Length == DatSchema.UpgradeSize;

    public static UpgradeDataFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static UpgradeDataFile Parse(byte[] data)
    {
        if (data.Length != DatSchema.UpgradeSize)
            throw new InvalidDataException(
                $"Not an upgrade table: expected {DatSchema.UpgradeSize} bytes, got {data.Length}.");
        return new UpgradeDataFile((byte[])data.Clone());
    }

    /// <summary>True for the spell half of the table.</summary>
    public static bool IsSpell(int upgrade) => upgrade >= DatSchema.FirstSpellUpgrade;

    public int ResearchTime(int upgrade) => (int)GetRaw("upgradeTime", upgrade);
    public void SetResearchTime(int upgrade, int value) => Set("upgradeTime", upgrade, value);

    public int GoldCost(int upgrade) => (int)GetRaw("goldCost", upgrade);
    public void SetGoldCost(int upgrade, int value) => Set("goldCost", upgrade, value);

    public int LumberCost(int upgrade) => (int)GetRaw("lumberCost", upgrade);
    public void SetLumberCost(int upgrade, int value) => Set("lumberCost", upgrade, value);

    public int OilCost(int upgrade) => (int)GetRaw("oilCost", upgrade);
    public void SetOilCost(int upgrade, int value) => Set("oilCost", upgrade, value);

    public int Icon(int upgrade) => (int)GetRaw("icon", upgrade);
    public void SetIcon(int upgrade, int value) => Set("icon", upgrade, value);

    public int Group(int upgrade) => (int)GetRaw("group", upgrade);
    public void SetGroup(int upgrade, int value) => Set("group", upgrade, value);

    /// <summary>
    /// The upgrade's slot in the player's researched mask. Read-only: the value is an index
    /// the engine, saved games and a map's ALOW section all agree on, so it is not a knob.
    /// </summary>
    public uint ResearchedBit(int upgrade) => GetRaw("flags", upgrade);

    /// <summary>
    /// Bit position of <see cref="ResearchedBit"/>, or -1 if the value is not a single bit.
    /// Retail stores exactly one bit for all 52.
    /// </summary>
    public int ResearchedBitIndex(int upgrade)
    {
        var value = ResearchedBit(upgrade);
        return value != 0 && (value & (value - 1)) == 0 ? System.Numerics.BitOperations.TrailingZeroCount(value) : -1;
    }

    /// <summary>
    /// Display name, preferring the game's own strings. The upgrade names sit at
    /// `stat_txt.tbl` entries 106-157 and embed newlines for the button layout, which are
    /// flattened to spaces here.
    /// </summary>
    public static string UpgradeName(int upgrade, TblFile? statTxt = null)
    {
        if (statTxt is not null)
        {
            var index = DatNames.FirstUpgradeString + upgrade;
            if (index >= 0 && index < statTxt.Strings.Count)
            {
                var name = statTxt.Strings[index];
                if (!string.IsNullOrWhiteSpace(name))
                    return name.Replace("\r", " ").Replace("\n", " ").Replace("  ", " ").Trim();
            }
        }
        return DatNames.UpgradeName(upgrade);
    }

    public string Describe() => $"{DatSchema.UpgradeCount} upgrades ({DatSchema.FirstSpellUpgrade} researches, "
                                + $"{DatSchema.UpgradeCount - DatSchema.FirstSpellUpgrade} spells)";
}
