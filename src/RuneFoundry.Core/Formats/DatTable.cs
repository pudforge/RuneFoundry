using System.Buffers.Binary;

namespace RuneFoundry.Core.Formats;

/// <summary>
/// A struct-of-arrays .dat table held as its original bytes.
///
/// These files store every field as one contiguous run covering all records, so a single
/// unit's stats are scattered across thirty segments. Keeping the raw buffer and writing
/// through it means anything this build does not model — unused flag bits, the frame
/// tables, a field a later patch adds meaning to — survives a load/save untouched. A file
/// that is opened and saved without an edit is byte-identical to the one that went in.
/// </summary>
public abstract class DatTable
{
    private readonly byte[] _bytes;
    private readonly Dictionary<string, int> _offsets = new(StringComparer.Ordinal);

    /// <summary>The segments this table carries, in file order.</summary>
    public IReadOnlyList<DatField> Fields { get; }

    /// <summary>Records addressed by the table — 110 units, or 52 upgrades.</summary>
    public int RecordCount { get; }

    protected DatTable(byte[] bytes, IReadOnlyList<DatField> fields, int recordCount)
    {
        _bytes = bytes;
        Fields = fields;
        RecordCount = recordCount;

        var at = 0;
        foreach (var field in fields)
        {
            _offsets[field.Key] = at;
            at += field.Count * field.Width;
        }

        if (at != bytes.Length)
            throw new InvalidDataException($"Segment list covers {at} bytes but the file is {bytes.Length}.");
    }

    /// <summary>Looks up a segment, or throws naming the key that was asked for.</summary>
    public DatField Field(string key) =>
        Fields.FirstOrDefault(f => f.Key == key)
        ?? throw new ArgumentException($"No field '{key}' in this table.", nameof(key));

    /// <summary>True if the table carries a segment under this key.</summary>
    public bool Has(string key) => _offsets.ContainsKey(key);

    private int ByteOffset(DatField field, int record, int component)
    {
        if (record < 0 || record >= field.Records)
            throw new ArgumentOutOfRangeException(
                nameof(record), record, $"Field '{field.Key}' covers records 0..{field.Records - 1}.");
        if (component < 0 || component >= field.PerRecord)
            throw new ArgumentOutOfRangeException(
                nameof(component), component, $"Field '{field.Key}' has {field.PerRecord} component(s) per record.");

        return _offsets[field.Key] + (record * field.PerRecord + component) * field.Width;
    }

    /// <summary>Reads a field's stored value, before <see cref="DatField.DisplayScale"/>.</summary>
    public uint GetRaw(string key, int record, int component = 0)
    {
        var field = Field(key);
        var at = ByteOffset(field, record, component);
        return field.Width switch
        {
            1 => _bytes[at],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(at)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(at)),
            _ => throw new InvalidOperationException($"Field '{key}' has an impossible width of {field.Width}."),
        };
    }

    /// <summary>
    /// Writes a field's stored value. Rejects anything the storage cannot hold rather than
    /// truncating, so a slider that has been mis-scaled fails loudly instead of wrapping a
    /// 300 hit point unit round to 44.
    /// </summary>
    public void SetRaw(string key, int record, uint value, int component = 0)
    {
        var field = Field(key);
        if (value > field.RawMax)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"Field '{key}' stores {field.Width} byte(s); its maximum is {field.RawMax}.");

        var at = ByteOffset(field, record, component);
        switch (field.Width)
        {
            case 1: _bytes[at] = (byte)value; break;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(_bytes.AsSpan(at), (ushort)value); break;
            case 4: BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(at), value); break;
            default: throw new InvalidOperationException($"Field '{key}' has an impossible width of {field.Width}.");
        }
    }

    /// <summary>Reads a field as the user sees it, with <see cref="DatField.DisplayScale"/> applied.</summary>
    public long Get(string key, int record, int component = 0) =>
        (long)GetRaw(key, record, component) * Field(key).DisplayScale;

    /// <summary>
    /// Writes a field from a displayed value. A scaled field only addresses multiples of
    /// its scale, so a cost that is not a multiple of ten is refused rather than rounded —
    /// silently moving the number the user typed is worse than telling them.
    /// </summary>
    public void Set(string key, int record, long value, int component = 0)
    {
        var field = Field(key);
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Field '{key}' cannot be negative.");
        if (field.DisplayScale != 1 && value % field.DisplayScale != 0)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"Field '{key}' is stored in steps of {field.DisplayScale}.");
        if (value > field.DisplayMax)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"Field '{key}' tops out at {field.DisplayMax}.");

        SetRaw(key, record, (uint)(value / field.DisplayScale), component);
    }

    /// <summary>True if <paramref name="option"/>'s bit is set. For <see cref="DatControlKind.Flags"/> fields.</summary>
    public bool HasFlag(string key, int record, DatOption option) => (GetRaw(key, record) & option.Value) != 0;

    /// <summary>Sets or clears one bit, leaving the rest of the field alone.</summary>
    public void SetFlag(string key, int record, DatOption option, bool on)
    {
        var current = GetRaw(key, record);
        SetRaw(key, record, on ? current | option.Value : current & ~option.Value);
    }

    /// <summary>
    /// The bits of a flags field with no name in the schema. A view should surface these
    /// as a raw hex value so an unknown bit can still be read and written.
    /// </summary>
    public uint UnnamedBits(string key, int record)
    {
        var field = Field(key);
        var known = 0u;
        foreach (var option in field.Options ?? Array.Empty<DatOption>()) known |= option.Value;
        return GetRaw(key, record) & ~known;
    }

    /// <summary>Every value of one record, keyed by field, for diffing two tables.</summary>
    public IReadOnlyDictionary<string, uint[]> Record(int record)
    {
        var result = new Dictionary<string, uint[]>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            if (record >= field.Records) continue;
            var values = new uint[field.PerRecord];
            for (var c = 0; c < field.PerRecord; c++) values[c] = GetRaw(field.Key, record, c);
            result[field.Key] = values;
        }
        return result;
    }

    /// <summary>
    /// Every value that differs from <paramref name="other"/>, as (field, record, component,
    /// mine, theirs). This is what turns "I changed some things" into a mod manifest, and
    /// what showed that retail's shipped unit table differs from its backup in nine values.
    /// </summary>
    public IEnumerable<(string Key, int Record, int Component, uint Mine, uint Theirs)> Diff(DatTable other)
    {
        foreach (var field in Fields)
        {
            if (!other.Has(field.Key)) continue;
            for (var record = 0; record < field.Records; record++)
                for (var component = 0; component < field.PerRecord; component++)
                {
                    var mine = GetRaw(field.Key, record, component);
                    var theirs = other.GetRaw(field.Key, record, component);
                    if (mine != theirs) yield return (field.Key, record, component, mine, theirs);
                }
        }
    }

    /// <summary>The table's bytes. A copy, so callers cannot write behind the accessors.</summary>
    public byte[] ToBytes() => (byte[])_bytes.Clone();

    /// <summary>Writes via a temp file so an interrupted save cannot leave a half-written table in the install.</summary>
    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ToBytes());
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Direct access for callers that need the whole buffer without a copy.</summary>
    protected byte[] Buffer => _bytes;
}
