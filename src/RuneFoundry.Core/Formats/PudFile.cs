using System.Buffers.Binary;
using System.Text;

namespace RuneFoundry.Core.Formats;

public sealed record PudChunk(string Tag, int Offset, int Length);

public sealed record PudUnit(int X, int Y, byte Type, byte Player, ushort Data);

/// <summary>
/// Warcraft II map file. A flat stream of chunks — a four-character tag, a uint32
/// length, then that many bytes — starting with TYPE ("WAR2 MAP").
///
/// This reads the chunks that describe a map rather than the ones that define its
/// contents: enough to list, identify and sanity-check a .pud, which is what a mod
/// packager needs. The terrain and unit-data chunks are preserved but not interpreted,
/// so nothing here can corrupt a map it does not understand.
/// </summary>
public sealed class PudFile
{
    public const string Magic = "WAR2 MAP";

    private readonly byte[] _data;

    public IReadOnlyList<PudChunk> Chunks { get; }
    public int Version { get; }
    public string Description { get; }
    public int Width { get; }
    public int Height { get; }
    public int Tileset { get; }
    public IReadOnlyList<byte> PlayerOwners { get; }
    public IReadOnlyList<PudUnit> Units { get; }

    /// <summary>Set when the chunk stream did not end exactly at end-of-file.</summary>
    public string? StructureWarning { get; }

    private PudFile(byte[] data, List<PudChunk> chunks, string? warning)
    {
        _data = data;
        Chunks = chunks;
        StructureWarning = warning;

        Version = ReadUInt16("VER ") ?? 0;
        Description = ReadString("DESC") ?? "";
        Tileset = ReadUInt16("ERA ") ?? ReadUInt16("ERAX") ?? -1;

        var dim = Find("DIM ");
        if (dim is not null && dim.Length >= 4)
        {
            Width = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dim.Offset));
            Height = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dim.Offset + 2));
        }

        PlayerOwners = ReadBytes("OWNR") ?? Array.Empty<byte>();
        Units = ReadUnits();
    }

    public static PudFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static bool LooksLikePud(byte[] data)
        => data.Length >= 16
           && Encoding.ASCII.GetString(data, 0, 4) == "TYPE"
           && Encoding.ASCII.GetString(data, 8, 8) == Magic;

    public static PudFile Parse(byte[] data)
    {
        if (!LooksLikePud(data))
            throw new InvalidDataException("Not a PUD: the file does not begin with a TYPE chunk containing \"WAR2 MAP\".");

        var chunks = new List<PudChunk>();
        var offset = 0;
        string? warning = null;

        while (offset + 8 <= data.Length)
        {
            var tag = Encoding.ASCII.GetString(data, offset, 4);
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4));

            if (length < 0 || offset + 8L + length > data.Length)
            {
                warning = $"Chunk '{tag}' at byte {offset} declares {length} bytes, which runs past the end of the file. Stopped reading there.";
                break;
            }

            chunks.Add(new PudChunk(tag.TrimEnd(), offset + 8, length));
            offset += 8 + length;
        }

        if (warning is null && offset != data.Length)
            warning = $"{data.Length - offset} trailing bytes after the last chunk.";

        return new PudFile(data, chunks, warning);
    }

    private PudChunk? Find(string tag)
        => Chunks.FirstOrDefault(c => string.Equals(c.Tag, tag.TrimEnd(), StringComparison.Ordinal));

    private int? ReadUInt16(string tag)
    {
        var chunk = Find(tag);
        return chunk is not null && chunk.Length >= 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(chunk.Offset))
            : null;
    }

    private byte[]? ReadBytes(string tag)
    {
        var chunk = Find(tag);
        return chunk is null ? null : _data[chunk.Offset..(chunk.Offset + chunk.Length)];
    }

    private string? ReadString(string tag)
    {
        var chunk = Find(tag);
        if (chunk is null) return null;
        var span = _data.AsSpan(chunk.Offset, chunk.Length);
        var end = span.IndexOf((byte)0);
        if (end < 0) end = span.Length;
        return Encoding.Latin1.GetString(span[..end]).Trim();
    }

    private List<PudUnit> ReadUnits()
    {
        var units = new List<PudUnit>();
        var chunk = Find("UNIT");
        if (chunk is null) return units;

        // Each entry is x, y (uint16), type, player (byte), then a uint16 of extra data.
        const int entrySize = 8;
        for (var o = chunk.Offset; o + entrySize <= chunk.Offset + chunk.Length; o += entrySize)
        {
            units.Add(new PudUnit(
                BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(o)),
                BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(o + 2)),
                _data[o + 4],
                _data[o + 5],
                BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(o + 6))));
        }
        return units;
    }

    /// <summary>
    /// The map's own copy of the unit table, or null for a map that carries none.
    ///
    /// A PUD's UDTA chunk holds the whole table, and the game reads it in preference to
    /// rez\unitdata.dat for that map — which is why a stat edit that is only made to the
    /// .dat has no effect on a campaign mission. The chunk is the shorter of the two table
    /// layouts with two bytes in front of it, so the table starts at UDTA + 2.
    /// </summary>
    public const int UnitTableChunkPrefix = 2;

    public static UnitDataFile? ReadUnitTable(byte[] pud)
    {
        var (at, length) = FindChunk(pud, "UDTA");
        if (at < 0) return null;

        var size = length - UnitTableChunkPrefix;
        if (size != DatSchema.UnitSizeWithoutSwamp && size != DatSchema.UnitSizeWithSwamp) return null;

        return UnitDataFile.Parse(pud.AsSpan(at + UnitTableChunkPrefix, size).ToArray());
    }

    /// <summary>Writes a table back into a map, returning the new file. The chunk keeps its size.</summary>
    public static byte[] WriteUnitTable(byte[] pud, UnitDataFile table)
    {
        var (at, length) = FindChunk(pud, "UDTA");
        if (at < 0) throw new InvalidOperationException("This map has no UDTA chunk to write into.");

        var bytes = table.ToBytes();
        if (bytes.Length != length - UnitTableChunkPrefix)
            throw new InvalidOperationException(
                $"The map's unit table is {length - UnitTableChunkPrefix} bytes and the one being written is {bytes.Length}.");

        var copy = (byte[])pud.Clone();
        bytes.CopyTo(copy.AsSpan(at + UnitTableChunkPrefix));
        return copy;
    }

    // ---- ALOW: what each player may build, research and cast ----------------

    /// <summary>
    /// The tag holding the per-player tech masks.
    ///
    /// The game seeds these masks from the campaign tables when a map loads and then parses
    /// the map's chunks, so an ALOW chunk in the file has the last word (W2R-RE-NOTES §5a).
    /// That makes it the way to change what a mission lets you build without touching the
    /// running game.
    /// </summary>
    public const string AllowTag = "ALOW";

    public const int AllowPlayers = 16;
    public const int AllowArrayCount = 6;

    /// <summary>
    /// 0x180. The handler compares the chunk length against this and ignores the chunk
    /// outright if it differs, so the size is not negotiable.
    /// </summary>
    public const int AllowChunkSize = AllowArrayCount * AllowPlayers * 4;

    /// <summary>The OWNR code for a slot a person plays.</summary>
    public const byte HumanOwner = 5;

    public static bool HasAllow(byte[] pud) => ReadAllow(pud) is not null;

    /// <summary>
    /// The six arrays, or null when the map has no usable ALOW chunk. A chunk of the wrong
    /// size counts as unusable, because that is how the game treats it.
    /// </summary>
    public static uint[][]? ReadAllow(byte[] pud)
    {
        var (at, length) = FindChunk(pud, AllowTag);
        if (at < 0 || length != AllowChunkSize) return null;

        var arrays = new uint[AllowArrayCount][];
        for (var a = 0; a < AllowArrayCount; a++)
        {
            arrays[a] = new uint[AllowPlayers];
            for (var player = 0; player < AllowPlayers; player++)
                arrays[a][player] = BinaryPrimitives.ReadUInt32LittleEndian(
                    pud.AsSpan(at + (a * AllowPlayers + player) * 4));
        }
        return arrays;
    }

    /// <summary>
    /// Puts the six arrays into a map, replacing an existing chunk or adding one after UDTA
    /// — where the format puts it — and returns the new file.
    /// </summary>
    public static byte[] WriteAllow(byte[] pud, uint[][] arrays)
    {
        if (arrays.Length != AllowArrayCount || arrays.Any(a => a.Length != AllowPlayers))
            throw new ArgumentException(
                $"An ALOW chunk is {AllowArrayCount} arrays of {AllowPlayers} values.", nameof(arrays));

        var payload = new byte[AllowChunkSize];
        for (var a = 0; a < AllowArrayCount; a++)
            for (var player = 0; player < AllowPlayers; player++)
                BinaryPrimitives.WriteUInt32LittleEndian(
                    payload.AsSpan((a * AllowPlayers + player) * 4), arrays[a][player]);

        var (at, length) = FindChunk(pud, AllowTag);
        if (at >= 0 && length == AllowChunkSize)
        {
            var replaced = (byte[])pud.Clone();
            payload.CopyTo(replaced.AsSpan(at));
            return replaced;
        }

        if (at >= 0) pud = RemoveAllow(pud);

        var insertAt = AllowInsertPoint(pud);
        var chunk = new byte[8 + AllowChunkSize];
        Encoding.ASCII.GetBytes(AllowTag).CopyTo(chunk.AsSpan(0));
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), AllowChunkSize);
        payload.CopyTo(chunk.AsSpan(8));

        var result = new byte[pud.Length + chunk.Length];
        pud.AsSpan(0, insertAt).CopyTo(result);
        chunk.CopyTo(result.AsSpan(insertAt));
        pud.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + chunk.Length));
        return result;
    }

    /// <summary>Takes the chunk out again, giving the mission its campaign masks back.</summary>
    public static byte[] RemoveAllow(byte[] pud)
    {
        var (at, length) = FindChunk(pud, AllowTag);
        if (at < 0) return pud;

        var start = at - 8;
        var end = at + length;
        var result = new byte[pud.Length - (end - start)];
        pud.AsSpan(0, start).CopyTo(result);
        pud.AsSpan(end).CopyTo(result.AsSpan(start));
        return result;
    }

    /// <summary>Straight after UDTA, which is where the format keeps ALOW; else at the end.</summary>
    private static int AllowInsertPoint(byte[] pud)
    {
        var (at, length) = FindChunk(pud, "UDTA");
        return at < 0 ? pud.Length : at + length;
    }

    /// <summary>Where a chunk's payload starts, and how long it is. (-1, 0) when absent.</summary>
    private static (int At, int Length) FindChunk(byte[] data, string tag)
    {
        var offset = 0;
        while (offset + 8 <= data.Length)
        {
            var name = System.Text.Encoding.ASCII.GetString(data, offset, 4);
            var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            if (length < 0 || offset + 8 + length > data.Length) break;
            if (name == tag) return (offset + 8, length);
            offset += 8 + length;
        }
        return (-1, 0);
    }

    public static string TilesetName(int tileset) => tileset switch
    {
        0 => "Forest",
        1 => "Winter",
        2 => "Wasteland",
        3 => "Swamp",
        _ => $"Unknown ({tileset})",
    };

    /// <summary>Player slot codes as they appear in OWNR.</summary>
    public static string OwnerName(byte owner) => owner switch
    {
        0 => "unused",
        1 => "passive computer",
        2 => "computer",
        3 => "nobody",
        4 => "computer",
        5 => "human",
        6 => "rescue (passive)",
        7 => "rescue (active)",
        _ => $"code {owner}",
    };

    /// <summary>Playable slots, i.e. those a human or computer actually starts in.</summary>
    public int PlayerCount => PlayerOwners.Take(8).Count(o => o is 2 or 4 or 5);

    public string Describe()
    {
        var size = Width > 0 ? $"{Width}x{Height}" : "unknown size";
        return $"{size}, {TilesetName(Tileset)}, {PlayerCount} players, {Units.Count} placed units";
    }
}
