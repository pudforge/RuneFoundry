namespace RuneFoundry.Core.Formats;

/// <summary>Which unit table a file is.</summary>
public enum UnitTableVariant
{
    /// <summary>`unitdato.dat`, 5694 bytes. The pre-expansion table; no swamp tileset, so no swamp frames.</summary>
    WithoutSwampFrames,

    /// <summary>`unitdata.dat`, 5948 bytes. The remaster's table, with a trailing 127-entry swamp frame run.</summary>
    WithSwampFrames,
}

/// <summary>
/// `Data/Rez/unitdata.dat` and `unitdato.dat` — hit points, costs, damage, armor, ranges,
/// movement domain and flags for all 110 units.
///
/// The exe loads these as loose files by hardcoded path, so an edit here takes effect
/// without touching the executable or the MPQ. A PUD can carry the same table in its
/// `UDTA` section and override it for one map, which is the safer route for anything that
/// has to match across a multiplayer game.
///
/// Layout is <see cref="DatSchema.UnitFields"/>; see <see cref="DatTable"/> for why the
/// raw bytes are kept.
/// </summary>
public sealed class UnitDataFile : DatTable
{
    /// <summary>Which of the two sizes this file is.</summary>
    public UnitTableVariant Variant { get; }

    private UnitDataFile(byte[] bytes, IReadOnlyList<DatField> fields, UnitTableVariant variant)
        : base(bytes, fields, DatSchema.UnitCount) => Variant = variant;

    /// <summary>True if the length is one this table recognises.</summary>
    public static bool LooksLikeUnitData(byte[] data) =>
        data.Length is DatSchema.UnitSizeWithoutSwamp or DatSchema.UnitSizeWithSwamp;

    public static UnitDataFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static UnitDataFile Parse(byte[] data)
    {
        var variant = data.Length switch
        {
            DatSchema.UnitSizeWithSwamp => UnitTableVariant.WithSwampFrames,
            DatSchema.UnitSizeWithoutSwamp => UnitTableVariant.WithoutSwampFrames,
            _ => throw new InvalidDataException(
                $"Not a unit table: expected {DatSchema.UnitSizeWithoutSwamp} or {DatSchema.UnitSizeWithSwamp} bytes, got {data.Length}."),
        };

        var fields = FieldsFor(variant);
        return new UnitDataFile((byte[])data.Clone(), fields, variant);
    }

    /// <summary>The segment list for a variant — the full list, minus swamp frames for the short one.</summary>
    public static IReadOnlyList<DatField> FieldsFor(UnitTableVariant variant) =>
        variant == UnitTableVariant.WithSwampFrames
            ? DatSchema.UnitFields
            : DatSchema.UnitFields.Where(f => f.Key != DatSchema.SwampFramesKey).ToArray();

    // ---- typed accessors for the fields an editor touches most ---------------------

    public int HitPoints(int unit) => (int)GetRaw("hitPoints", unit);
    public void SetHitPoints(int unit, int value) => Set("hitPoints", unit, value);

    public int Armor(int unit) => (int)GetRaw("armor", unit);
    public void SetArmor(int unit, int value) => Set("armor", unit, value);

    public int BasicDamage(int unit) => (int)GetRaw("basicDamage", unit);
    public void SetBasicDamage(int unit, int value) => Set("basicDamage", unit, value);

    public int PiercingDamage(int unit) => (int)GetRaw("piercingDamage", unit);
    public void SetPiercingDamage(int unit, int value) => Set("piercingDamage", unit, value);

    /// <summary>Gold as the game shows it. Stored in tens, so this only addresses multiples of 10.</summary>
    public int GoldCost(int unit) => (int)Get("goldCost", unit);
    public void SetGoldCost(int unit, int value) => Set("goldCost", unit, value);

    public int LumberCost(int unit) => (int)Get("lumberCost", unit);
    public void SetLumberCost(int unit, int value) => Set("lumberCost", unit, value);

    public int OilCost(int unit) => (int)Get("oilCost", unit);
    public void SetOilCost(int unit, int value) => Set("oilCost", unit, value);

    public int Sight(int unit) => (int)GetRaw("sight", unit);
    public void SetSight(int unit, int value) => Set("sight", unit, value);

    public int AttackRange(int unit) => (int)GetRaw("attackRange", unit);
    public void SetAttackRange(int unit, int value) => Set("attackRange", unit, value);

    public int BuildTime(int unit) => (int)GetRaw("buildTime", unit);
    public void SetBuildTime(int unit, int value) => Set("buildTime", unit, value);

    public int PointValue(int unit) => (int)GetRaw("pointValue", unit);
    public void SetPointValue(int unit, int value) => Set("pointValue", unit, value);

    public uint Flags(int unit) => GetRaw("flags", unit);
    public void SetFlags(int unit, uint value) => SetRaw("flags", unit, value);

    public uint CanTarget(int unit) => GetRaw("canTarget", unit);
    public void SetCanTarget(int unit, uint value) => SetRaw("canTarget", unit, value);

    public int MissileWeapon(int unit) => (int)GetRaw("missileWeapon", unit);
    public void SetMissileWeapon(int unit, int value) => SetRaw("missileWeapon", unit, (uint)value);

    public int UnitType(int unit) => (int)GetRaw("unitType", unit);
    public void SetUnitType(int unit, int value) => SetRaw("unitType", unit, (uint)value);

    /// <summary>Width and height in pixels.</summary>
    public (int Width, int Height) UnitSize(int unit) =>
        ((int)GetRaw("unitSize", unit), (int)GetRaw("unitSize", unit, 1));

    public (int Width, int Height) BoxSize(int unit) =>
        ((int)GetRaw("boxSize", unit), (int)GetRaw("boxSize", unit, 1));

    /// <summary>
    /// True where the table holds nothing for this id. Five slots are empty in retail
    /// (34, 36, 37, 48, 54) and are free for a mod to define — the exe still indexes them.
    /// </summary>
    public bool IsEmpty(int unit) => Flags(unit) == 0 && HitPoints(unit) == 0;

    /// <summary>
    /// Display name for a unit, preferring the game's own strings when a `stat_txt.tbl` is
    /// supplied — a localised or mod-renamed install should read in its own words.
    /// </summary>
    public static string UnitName(int unit, TblFile? statTxt = null)
    {
        if (statTxt is not null)
        {
            var index = unit + 1;
            if (index >= 0 && index < statTxt.Strings.Count)
            {
                var name = statTxt.Strings[index];
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        return DatNames.UnitName(unit);
    }

    public string Describe() =>
        Variant == UnitTableVariant.WithSwampFrames
            ? $"{DatSchema.UnitCount} units, with swamp frames"
            : $"{DatSchema.UnitCount} units, no swamp frames";
}
