using System.Buffers.Binary;
using System.Text;

namespace RuneFoundry.Core.Formats;

/// <summary>
/// Warcraft II string table: a uint16 count, then that many uint16 offsets from the
/// start of the file, then null-terminated strings. Text is single-byte, so it round
/// trips through Latin-1 without loss.
///
/// This one is fully writable — retexturing is nice, but rewriting unit names and
/// mission briefings is most of what a text mod actually is.
/// </summary>
public sealed class TblFile
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    public List<string> Strings { get; }

    public TblFile(IEnumerable<string>? strings = null) => Strings = strings?.ToList() ?? new List<string>();

    public static TblFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static bool LooksLikeTbl(byte[] data)
    {
        try
        {
            Parse(data);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static TblFile Parse(byte[] data)
    {
        if (data.Length < 2) throw new InvalidDataException("Not a TBL: file is shorter than its count field.");

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0));
        var headerSize = 2 + count * 2;
        if (headerSize > data.Length)
            throw new InvalidDataException($"Not a TBL: {count} entries need a {headerSize}-byte offset table but the file is {data.Length} bytes.");

        var strings = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2 + i * 2));
            if (offset < headerSize || offset >= data.Length)
                throw new InvalidDataException($"Not a TBL: entry {i} points outside the file.");

            var end = Array.IndexOf(data, (byte)0, offset);
            if (end < 0) end = data.Length;
            strings.Add(Latin1.GetString(data, offset, end - offset));
        }

        return new TblFile(strings);
    }

    public byte[] ToBytes()
    {
        if (Strings.Count > ushort.MaxValue)
            throw new InvalidOperationException($"A TBL holds at most {ushort.MaxValue} strings.");

        var encoded = Strings.Select(s => Latin1.GetBytes(s)).ToList();
        var headerSize = 2 + encoded.Count * 2;
        var total = headerSize + encoded.Sum(b => b.Length + 1);

        if (total > ushort.MaxValue)
            throw new InvalidOperationException(
                $"The strings total {total} bytes; a TBL addresses offsets with 16 bits, so it cannot exceed {ushort.MaxValue}.");

        var output = new byte[total];
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0), (ushort)encoded.Count);

        var cursor = headerSize;
        for (var i = 0; i < encoded.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2 + i * 2), (ushort)cursor);
            encoded[i].CopyTo(output, cursor);
            cursor += encoded[i].Length;
            output[cursor++] = 0;
        }
        return output;
    }

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ToBytes());
        File.Move(temp, path, overwrite: true);
    }

    public string Describe()
    {
        var preview = Strings.FirstOrDefault() ?? "";
        if (preview.Length > 40) preview = preview[..40] + "...";
        return Strings.Count == 1 ? $"1 string: \"{preview}\"" : $"{Strings.Count} strings";
    }
}
