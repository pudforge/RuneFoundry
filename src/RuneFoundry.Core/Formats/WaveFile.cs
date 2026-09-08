using System.Buffers.Binary;
using System.Text;

namespace RuneFoundry.Core.Formats;

/// <summary>How a .wav is laid out: enough to describe one, and to write a matching one.</summary>
/// <param name="Channels">1 for every clip the game ships but one.</param>
/// <param name="SampleRate">22,050 Hz for all but three of them.</param>
/// <param name="BitsPerSample">16, again with three exceptions.</param>
/// <param name="DataBytes">The length of the sample data, which is what gives the duration.</param>
public sealed record WaveFormat(int Channels, int SampleRate, int BitsPerSample, int DataBytes)
{
    public int BytesPerSecond => SampleRate * Channels * BitsPerSample / 8;

    public TimeSpan Duration => BytesPerSecond == 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds((double)DataBytes / BytesPerSecond);
}

/// <summary>
/// Reading a .wav header, and writing a silent one.
///
/// Silence is a thing a mod wants often enough to be worth building in: a custom campaign
/// that wants its briefing read on screen rather than aloud, or a unit that should stop
/// saying the line the game gave it. Asking the author to produce an empty file themselves,
/// in the right format, is a poor answer to that.
///
/// Only plain PCM is written, because that is what the game ships — every one of its 596
/// clips is format tag 1 — and a compressed clip in a slot that expects PCM is a silence of
/// a different and less welcome kind.
/// </summary>
public static class WaveFile
{
    public const int PcmFormatTag = 1;

    /// <summary>What the game overwhelmingly ships, and so what silence defaults to.</summary>
    public static readonly WaveFormat Default = new(Channels: 1, SampleRate: 22050, BitsPerSample: 16, DataBytes: 0);

    /// <summary>
    /// The format of a .wav, or null when the file is not one we can describe.
    /// </summary>
    /// <param name="wav">The file, or just enough of its start to hold the headers.</param>
    /// <param name="wholeLength">
    /// How long the file really is, when <paramref name="wav"/> is only its beginning.
    /// Without it a header read on its own looks like a file cut short, and the clip is
    /// reported as a fraction of a second — which is how a six-second fanfare came to be
    /// shown as 0:00.
    /// </param>
    public static WaveFormat? Describe(byte[] wav, long wholeLength = -1)
    {
        var available = wholeLength < 0 ? wav.Length : Math.Max(wav.Length, wholeLength);

        if (wav.Length < 12
            || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            return null;

        int channels = 0, rate = 0, bits = 0, data = -1;

        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, offset, 4);
            var length = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4));
            if (length < 0) return null;

            var body = offset + 8;

            if (id == "fmt " && length >= 16 && body + 16 <= wav.Length)
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body)) != PcmFormatTag) return null;

                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14));
            }
            else if (id == "data")
            {
                // Trust the file's length over the declared one, which some editors leave
                // long — but the file's length, not this buffer's.
                data = (int)Math.Min(length, available - body);
            }

            offset = body + length + (length % 2);   // chunks are word-aligned
        }

        if (channels <= 0 || rate <= 0 || bits <= 0 || data < 0) return null;

        return new WaveFormat(channels, rate, bits, data);
    }

    /// <summary>
    /// Where the sample data sits, so it can be read out and written back.
    ///
    /// <see cref="Describe"/> gives the length but not the offset, and everything that
    /// reshapes a clip needs both.
    /// </summary>
    private static (int Offset, int Length)? DataChunk(byte[] wav)
    {
        if (wav.Length < 12
            || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            return null;

        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, offset, 4);
            var length = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4));
            if (length < 0) return null;

            var body = offset + 8;
            if (id == "data") return (body, Math.Min(length, wav.Length - body));

            offset = body + length + (length % 2);
        }

        return null;
    }

    /// <summary>
    /// The level a silent sample sits at: zero for signed 16-bit, mid-scale for unsigned
    /// 8-bit. Getting this wrong writes a loud click rather than quiet.
    /// </summary>
    private static byte Quiet(WaveFormat format) => format.BitsPerSample == 8 ? (byte)0x80 : (byte)0;

    /// <summary>A clip in the given format carrying exactly these samples.</summary>
    public static byte[] Rebuilt(WaveFormat format, ReadOnlySpan<byte> samples)
    {
        var wav = Silence(TimeSpan.Zero, format with { DataBytes = 0 });
        var blockAlign = format.Channels * format.BitsPerSample / 8;
        var bytes = samples.Length / blockAlign * blockAlign;

        var built = new byte[44 + bytes];
        wav.AsSpan(0, 44).CopyTo(built);
        samples[..bytes].CopyTo(built.AsSpan(44));

        BinaryPrimitives.WriteInt32LittleEndian(built.AsSpan(4), 36 + bytes);
        BinaryPrimitives.WriteInt32LittleEndian(built.AsSpan(40), bytes);

        return built;
    }

    /// <summary>
    /// The clip with any silence at its end removed.
    ///
    /// Wanted so that lengthening a clip can be done over and over without compounding:
    /// trimming first means holding a fanfare to eight seconds and then to five gives the
    /// same file as holding the original to five, rather than five seconds of padding on
    /// top of three.
    /// </summary>
    public static byte[] Trimmed(byte[] wav)
    {
        if (Describe(wav) is not { } format || DataChunk(wav) is not { } data) return wav;

        var step = format.BitsPerSample / 8;

        // A hair either side of the silent level, since a real recording's tail is rarely
        // dead flat. Both are well under the level of anything audible.
        const int Tolerance8 = 2;
        const int Tolerance16 = 64;

        var end = data.Length;
        while (end >= step)
        {
            var at = data.Offset + end - step;

            // 8-bit PCM is unsigned around mid-scale; 16-bit is signed around zero, so a
            // whisper below silence reads as 0xFFxx rather than 0x00xx and has to be read
            // as the number it is.
            var loud = step == 1
                ? Math.Abs(wav[at] - 0x80) > Tolerance8
                : Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(at))) > Tolerance16;

            if (loud) break;
            end -= step;
        }

        return Rebuilt(format, wav.AsSpan(data.Offset, end));
    }

    /// <summary>
    /// The clip padded with silence so that it runs for a given total length.
    ///
    /// The game holds an act's title card until every audio channel falls silent, so the
    /// length of the act fanfare is what decides how long that card stays up. Padding is
    /// how a mod sets it without having to author a clip by hand.
    ///
    /// A clip is never cut short: asking for less than the sound itself runs gives the
    /// sound, trimmed of trailing silence and no shorter. Cutting into it would need the
    /// author's own recording, not a guess about where a phrase ends.
    /// </summary>
    public static byte[] HeldFor(byte[] wav, TimeSpan total)
    {
        var trimmed = Trimmed(wav);
        if (Describe(trimmed) is not { } format || DataChunk(trimmed) is not { } data) return wav;

        var blockAlign = format.Channels * format.BitsPerSample / 8;
        var wanted = (int)Math.Round(total.TotalSeconds * format.BytesPerSecond);
        wanted = Math.Max(blockAlign, wanted / blockAlign * blockAlign);

        if (wanted <= data.Length) return trimmed;

        var samples = new byte[wanted];
        trimmed.AsSpan(data.Offset, data.Length).CopyTo(samples);
        samples.AsSpan(data.Length).Fill(Quiet(format));

        return Rebuilt(format, samples);
    }

    /// <summary>A silent clip of a given length, in the format the game uses.</summary>
    public static byte[] Silence(TimeSpan length, WaveFormat? like = null)
    {
        var format = like ?? Default;
        if (length < TimeSpan.Zero) length = TimeSpan.Zero;

        var blockAlign = format.Channels * format.BitsPerSample / 8;
        var bytes = (int)Math.Round(length.TotalSeconds * format.BytesPerSecond);

        // Never a partial sample, and never a data chunk of nothing: a zero-length clip is
        // not reliably the same thing to a player as a clip that is quiet.
        bytes = Math.Max(blockAlign, bytes / blockAlign * blockAlign);

        var wav = new byte[44 + bytes];
        var span = wav.AsSpan();

        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + bytes);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], PcmFormatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], format.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], format.BytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], (ushort)format.BitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], bytes);

        // 16-bit PCM silence is zero, and the array already is. 8-bit PCM is unsigned, so
        // its silence is 0x80 — getting this wrong gives a loud click, not quiet.
        if (format.BitsPerSample == 8) span[44..].Fill(0x80);

        return wav;
    }

    /// <summary>
    /// Silence shaped like the clip it replaces — same format, same length.
    ///
    /// Length is matched rather than chosen because some slots are timed against their
    /// audio: a briefing page is held on screen while its recording plays, and a half-second
    /// of quiet there would flick past before it could be read. Matching means a mod can
    /// silence anything without having to know which slots care.
    /// </summary>
    public static byte[] SilenceLike(byte[] original)
    {
        var format = Describe(original);
        return format is null
            ? Silence(TimeSpan.FromSeconds(1))
            : Silence(format.Duration, format);
    }
}
