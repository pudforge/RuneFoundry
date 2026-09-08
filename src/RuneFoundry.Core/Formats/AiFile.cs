using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace RuneFoundry.Core.Formats;

public enum AiOpcode : byte
{
    Var = 0x00,
    Goto = 0x01,
    Sleep = 0x02,
    Wait = 0x03,
}

/// <summary>One decoded instruction. Unknown opcodes keep their raw bytes so nothing is lost.</summary>
public sealed class AiInstruction
{
    public byte Opcode { get; init; }
    public byte[] Operands { get; init; } = Array.Empty<byte>();

    /// <summary>Set when the opcode is not one we know how to read.</summary>
    public bool IsUnknown { get; init; }

    public int Length => 1 + Operands.Length;
}

public sealed class AiScript
{
    public int Index { get; init; }
    public string Name => AiTables.AiName(Index);

    /// <summary>Where this script sits in the original file.</summary>
    public int Offset { get; init; }

    /// <summary>
    /// The two uint16s every script begins with, both pointers into the file.
    /// Header0 is the AI's build list — a run of item codes (Town Hall, Barracks,
    /// upgrades) that "var items = N" walks a cursor through. Header1 is its rate table,
    /// one uint16 per rate slot with $FFFF meaning disabled. Neither is editable yet, so
    /// both are carried through untouched.
    /// </summary>
    public ushort Header0 { get; set; }
    public ushort Header1 { get; set; }

    public List<AiInstruction> Instructions { get; init; } = new();

    /// <summary>
    /// Bytes after the script's terminating goto.
    ///
    /// Emphatically not padding. Scripts tile the file, so whatever lies between one
    /// script's last instruction and the next script's start belongs to this region — and
    /// that is where the build lists and rate tables live. 91 of the 95 pointers in the
    /// file aim into some script's trailer. It is preserved verbatim and, crucially, at
    /// its original byte position.
    /// </summary>
    public byte[] Trailer { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Where the trailer starts, measured from the script's own offset. Re-encoding pads
    /// up to here rather than appending, so shortening the code cannot drag the trailer —
    /// and everything pointing into it — backwards.
    /// </summary>
    public int TrailerStart { get; internal set; }

    /// <summary>The exact bytes this script occupied, used when nothing about it changed.</summary>
    public byte[] OriginalBytes { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// How many bytes the code may take, header included.
    ///
    /// A script with a trailer stops at the trailer, not at the end of the slot: the bytes
    /// after <see cref="TrailerStart"/> are somebody's build list or rate table, addressed
    /// by absolute offset, so code that runs into them is code that does not fit even
    /// though the slot looks big enough. Without a trailer the whole slot is code.
    /// </summary>
    public int CodeCapacity => Trailer.Length > 0 ? TrailerStart : OriginalBytes.Length;

    /// <summary>The bytes the code takes as it stands, header included.</summary>
    public int CodeLength => HeaderLength + Instructions.Sum(i => i.Length);

    /// <summary>The two uint16s at the head of every script.</summary>
    public const int HeaderLength = 4;

    /// <summary>
    /// The build list Header0 points at: item codes the AI works through, with "items = N"
    /// moving a cursor along it. Read as a bounded window rather than to a terminator,
    /// because nothing in the file marks where a list ends.
    /// </summary>
    public byte[] BuildList { get; internal set; } = Array.Empty<byte>();

    /// <summary>
    /// False when Header0 does not point at anything resembling a build list. One script
    /// in the stock file is like this: $53 "Custom (compiled) AI" has Header0 = 255, which
    /// lands in the offset table. Naming items from that would be inventing them.
    /// </summary>
    public bool HasBuildList { get; internal set; }

    /// <summary>The item at a build-list position, or null if there is nothing to read.</summary>
    public byte? BuildItemAt(int index)
        => HasBuildList && index >= 0 && index < BuildList.Length ? BuildList[index] : null;

    /// <summary>
    /// Says in words what a build limit means for this script.
    ///
    /// Variable 0x22 is not a position in the list, it is how far down the list the
    /// computer may work: the game loops while the entry number is below it (0x4da300).
    /// So the useful thing to say is where the work stops, not which single entry the
    /// number lands on.
    /// </summary>
    public string? DescribeBuildLimit(int limit)
    {
        if (!HasBuildList) return null;

        // A list that begins with the terminator is a script that builds nothing, and the
        // game reads it that way: the walk at 0x4da300 stops before its first entry. Orc 4
        // is like this. Saying "build everything" of an empty list would be a lie.
        if (BuildList.Length == 0) return "This script has no build list, so this does nothing";

        if (limit <= 0) return "Build nothing from the build list";
        if (limit >= BuildList.Length) return "Build everything in the build list";

        var last = BuildList[limit - 1];
        return AiTables.Items.ContainsKey(last)
            ? $"Build the list down to {AiTables.ItemName(last)}"
            : null;
    }

    /// <summary>
    /// What sits at a position in this script's build list, named.
    ///
    /// Null unless the byte there is really an item code. The list is read as a bounded
    /// window because nothing marks where it ends, so a high position can land past the
    /// real list in bytes that are something else. Naming those "item $E8" would be
    /// dressing up a misread as a fact.
    /// </summary>
    public string? BuildItemName(int index)
        => BuildItemAt(index) is { } code && AiTables.Items.ContainsKey(code)
            ? AiTables.ItemName(code)
            : null;

    public bool IsModified { get; set; }

    /// <summary>True when another slot points at these same bytes — editing one edits both.</summary>
    public List<int> SharedWith { get; } = new();

    /// <summary>
    /// Where this script's terminating goto lands, when that is outside its own bytes.
    /// A script whose only instruction is such a goto is a stub: the behaviour is
    /// somewhere else, and saying so beats showing one baffling row.
    /// </summary>
    public int? JumpsAwayTo { get; internal set; }

    public bool IsStub => JumpsAwayTo is not null && Instructions.Count <= 1;

    /// <summary>
    /// An unused slot: its offset is zero. The community AI sets all leave slot $53
    /// ("Custom (compiled) AI") empty this way, so it is a normal state, not damage.
    /// </summary>
    public bool IsEmpty { get; init; }
}

/// <summary>
/// Warcraft II's AI scripts (Rez\ai.bin — MAINDAT.WAR entry 277 in the original game).
///
/// Layout, derived from the file and cross-checked against WarDraft's compiler include:
///
///   0..167    84 uint16 offsets, one per AI slot, in AIEd.ini's $00-$53 order
///   ...       each script: two uint16 pointers, then a bytecode stream
///
/// The bytecode is a handful of fixed-width instructions:
///
///   00 vv nn        var  &lt;variable&gt; = &lt;value&gt;
///   01 llll         goto &lt;offset&gt;              — ends the script; the AI loops
///   02 llllllll     sleep &lt;ticks&gt;
///   03 nn           wait &lt;condition&gt;
///   04 nn           do   &lt;item&gt;
///   05 nn llll      rate &lt;item&gt; = &lt;value&gt;
///   06 nn vv        item &lt;item&gt; = &lt;value&gt;
///
/// Decoding the shipped file yields 5,683 instructions with a single byte unaccounted for.
/// Note the shipped scripts never use do/rate/item, so those three opcode numbers are read
/// from WarDraft's ordering rather than observed. Everything not understood is kept, and
/// <see cref="ToBytes"/> reproduces an unedited file exactly.
/// </summary>
public sealed class AiFile
{
    public const int ScriptCount = 84;

    /// <summary>
    /// The most build-list entries the game will ever look at. Its walk at 0x4ae160
    /// stops at 64 whatever the list holds.
    /// </summary>
    public const int BuildListMax = 64;

    /// <summary>
    /// The highest variable number a script may set.
    ///
    /// "Var n, value" writes one byte at offset n of the player's AI state (0x4ca140:
    /// mov byte ptr [eax + esi], cl). There is no bounds check, and the states are 48
    /// bytes apart, so a number of 0x30 or more writes into the next player's state
    /// instead. Nothing in the game complains; the other computer just starts behaving
    /// oddly. The editor refuses rather than let that be written.
    /// </summary>
    public const byte MaxVariable = 0x2F;
    public const int HeaderSize = ScriptCount * 2;

    private readonly byte[] _original;

    /// <summary>
    /// Bytes between the offset table and the first script.
    ///
    /// Not padding: this holds shared script code. Five of the 84 slots are three-byte
    /// stubs whose only instruction is a goto into this region — Passive, for one, jumps
    /// to byte 170, where the standard setup block is followed by "sleep 65535; goto"
    /// looping on itself. Preserved verbatim, and reported so the editor can explain a
    /// script that appears to do nothing.
    /// </summary>
    private readonly byte[] _gap;

    /// <summary>Where the shared code region begins, or 0 if there is none.</summary>
    public int SharedCodeStart { get; private set; }

    /// <summary>
    /// The routine the stub scripts jump into, as an editable script of its own.
    ///
    /// It is not a slot in the offset table and has no header words — the code simply
    /// starts where the stubs point. Five scripts share it, so an edit here reaches all
    /// of them, which the editor says out loud.
    /// </summary>
    public AiScript? SharedRoutine { get; private set; }

    /// <summary>Bytes covered by the shared region, which several scripts jump into.</summary>
    public int SharedCodeLength => _gap.Length;

    public IReadOnlyList<AiScript> Scripts { get; }

    /// <summary>The size of the file as it was read.</summary>
    public int Length => _original.Length;

    /// <summary>A span of the original bytes, for the rebuild to carry across untouched.</summary>
    internal byte[] Slice(int from, int to) => _original[from..to];

    private AiFile(byte[] original, byte[] gap, List<AiScript> scripts)
    {
        _original = original;
        _gap = gap;
        Scripts = scripts;
    }

    public static bool LooksLikeAiFile(byte[] data)
    {
        if (data.Length < HeaderSize + 8) return false;

        var used = 0;
        for (var i = 0; i < ScriptCount; i++)
        {
            var offset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2));
            if (offset == 0) continue;                                  // empty slot
            if (offset < HeaderSize || offset >= data.Length) return false;
            used++;
        }
        return used > 0;
    }

    public static AiFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static AiFile Parse(byte[] data)
    {
        if (!LooksLikeAiFile(data))
            throw new InvalidDataException("Not an ai.bin: the first 168 bytes are not 84 valid offsets.");

        var offsets = new int[ScriptCount];
        for (var i = 0; i < ScriptCount; i++)
            offsets[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2));

        // Scripts tile the file from the first offset onward; each runs to the next one.
        // Empty slots hold zero and take part in none of this.
        var sorted = offsets.Where(o => o > 0).Distinct().OrderBy(o => o).ToList();
        var gap = data[HeaderSize..sorted[0]];

        var scripts = new List<AiScript>(ScriptCount);
        for (var i = 0; i < ScriptCount; i++)
        {
            var start = offsets[i];
            if (start == 0)
            {
                scripts.Add(new AiScript { Index = i, Offset = 0, IsEmpty = true });
                continue;
            }

            var next = sorted.FirstOrDefault(o => o > start, data.Length);
            var raw = data[start..next];

            var script = new AiScript
            {
                Index = i,
                Offset = start,
                OriginalBytes = raw,
                Header0 = BinaryPrimitives.ReadUInt16LittleEndian(raw),
                Header1 = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(2)),
            };

            Decode(raw, script);

            // A goto that leaves the script's own bytes means the code lives elsewhere.
            var final = script.Instructions.LastOrDefault();
            if (final is { Opcode: (byte)AiOpcode.Goto })
            {
                var destination = BinaryPrimitives.ReadUInt16LittleEndian(final.Operands);
                if (destination < start || destination >= next) script.JumpsAwayTo = destination;
            }

            // The game walks the list at 0x4ae160 and 0x4da300: it stops at the first
            // 0xFF and never looks past 64 entries. Reading a fixed window instead used to
            // run off the end into whatever followed, which is where "item $E8" came from.
            if (script.Header0 > HeaderSize && script.Header0 < data.Length)
            {
                var end = script.Header0;
                while (end < data.Length && end - script.Header0 < BuildListMax
                       && data[end] != 0xFF)
                    end++;

                script.BuildList = data[script.Header0..end];

                // Judged on the raw bytes, not on the terminated list. A list that begins
                // with the terminator is empty and still a list: Orc 4 has one, and reading
                // the plausibility off the empty span called it "not a list at all", which
                // sent every build limit in that script to the fallback wording.
                var window = Math.Min(8, data.Length - script.Header0);
                var head = data[script.Header0..(script.Header0 + window)];
                script.HasBuildList = head.Count(AiTables.IsPlausibleItem) >= 6;
            }

            scripts.Add(script);
        }

        // Slots pointing at the same bytes are the same script — Human and Orc variants of
        // a mission usually are. Editing one changes both, which the editor must say.
        foreach (var group in scripts.Where(s => !s.IsEmpty).GroupBy(s => s.Offset).Where(g => g.Count() > 1))
        {
            foreach (var script in group)
                script.SharedWith.AddRange(group.Where(s => s.Index != script.Index).Select(s => s.Index));
        }

        var file = new AiFile(data, gap, scripts) { SharedCodeStart = HeaderSize };

        // Wherever the stubs jump is where the shared code actually begins; the couple of
        // bytes before it belong to neither the table nor the routine.
        var entry = scripts.Where(s => s.JumpsAwayTo is not null)
            .Select(s => s.JumpsAwayTo!.Value)
            .Where(target => target >= HeaderSize && target < sorted[0])
            .DefaultIfEmpty(0)
            .Min();

        if (entry > 0)
        {
            var routine = new AiScript
            {
                Index = -1,
                Offset = entry,
                OriginalBytes = data[entry..sorted[0]],
            };
            Decode(routine.OriginalBytes, routine, start: 0);

            file.SharedCodeStart = entry;
            file.SharedRoutine = routine;
        }

        return file;
    }

    private static int OperandLength(byte opcode) => opcode switch
    {
        (byte)AiOpcode.Var => 2,
        (byte)AiOpcode.Goto => 2,
        (byte)AiOpcode.Sleep => 4,
        (byte)AiOpcode.Wait => 1,
        _ => -1,
    };

    private static void Decode(byte[] raw, AiScript script, int start = 4)
    {
        var p = start;
        while (p < raw.Length)
        {
            var opcode = raw[p];
            var operands = OperandLength(opcode);

            if (operands < 0 || p + 1 + operands > raw.Length)
            {
                // Not something we can read: keep the rest byte for byte.
                script.Instructions.Add(new AiInstruction
                {
                    Opcode = opcode,
                    Operands = raw[(p + 1)..],
                    IsUnknown = true,
                });
                script.TrailerStart = raw.Length;
                return;
            }

            script.Instructions.Add(new AiInstruction
            {
                Opcode = opcode,
                Operands = raw[(p + 1)..(p + 1 + operands)],
            });
            p += 1 + operands;

            if (opcode == (byte)AiOpcode.Goto)
            {
                // The script ends here. What follows is not ours to move.
                script.TrailerStart = p;
                script.Trailer = raw[p..];
                return;
            }
        }
    }

    /// <summary>
    /// Rebuilds the file, keeping every script at the offset it already had.
    ///
    /// This is not an optimisation, it is the only safe layout. The file is full of 16-bit
    /// pointers into itself that nothing here can reliably rewrite: each script's two
    /// header words point at its build list and rate table, goto targets are absolute byte
    /// offsets, and the build lists overlap — several scripts start at different points in
    /// one shared run of item codes. Relocating a script by even one byte invalidates all
    /// of it silently, which is exactly what an earlier version of this did.
    ///
    /// So a rewritten script must fit in the space it already occupied. It is padded with
    /// zeroes if it is shorter — the decoder stops at the terminating goto, so trailing
    /// bytes are never read — and refused if it is longer.
    /// </summary>
    public byte[] ToBytes()
    {
        if (Scripts.All(s => !s.IsModified) && SharedRoutine?.IsModified != true)
            return (byte[])_original.Clone();

        var output = (byte[])_original.Clone();
        var written = new HashSet<int>();

        foreach (var script in Scripts)
        {
            if (script.IsEmpty || !script.IsModified) continue;

            // Shared slots are one script; write it once.
            if (!written.Add(script.Offset)) continue;

            var encoded = Encode(script);
            var room = script.OriginalBytes.Length;

            if (encoded.Length > room)
            {
                throw new InvalidOperationException(
                    $"\"{script.Name}\" no longer fits. It has {room} bytes to work with and now " +
                    $"needs {encoded.Length}.\n\nThe scripts sit at fixed positions that the rest of " +
                    "the file points at, so one cannot grow into another. Remove " +
                    $"{(encoded.Length - room + 2) / 3} or so instructions and try again.");
            }

            encoded.CopyTo(output, script.Offset);

            // Anything left over stays zeroed rather than holding the old script's tail.
            for (var i = script.Offset + encoded.Length; i < script.Offset + room; i++)
                output[i] = 0;
        }

        if (SharedRoutine is { IsModified: true })
        {
            var encoded = Encode(SharedRoutine, includeHeader: false);
            var room = SharedRoutine.OriginalBytes.Length;

            if (encoded.Length > room)
                throw new InvalidOperationException(
                    $"The shared routine no longer fits: {room} bytes available, {encoded.Length} needed.");

            encoded.CopyTo(output, SharedRoutine.Offset);
            for (var i = SharedRoutine.Offset + encoded.Length; i < SharedRoutine.Offset + room; i++)
                output[i] = 0;
        }

        return output;
    }

    private static byte[] Encode(AiScript script) => Encode(script, includeHeader: true);

    private static byte[] Encode(AiScript script, bool includeHeader)
        => EncodeScript(script, includeHeader, includeTrailer: true);

    /// <summary>
    /// The script as bytes.
    ///
    /// With <paramref name="includeTrailer"/> the code is padded out to where the trailer
    /// already sits and the trailer follows it, which is what keeps the fixed layout
    /// honest. Without it, the code is emitted at its natural length and the trailer is
    /// somebody else's problem — which is what the rebuild wants, since it places the
    /// trailer itself and fixes up whatever points into it.
    /// </summary>
    internal static byte[] EncodeScript(AiScript script, bool includeHeader, bool includeTrailer = true)
    {
        var buffer = new MemoryStream();

        if (includeHeader)
        {
            var head = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(head, script.Header0);
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(2), script.Header1);
            buffer.Write(head);
        }

        if (includeTrailer) return EncodeBody(script, buffer);

        foreach (var instruction in script.Instructions)
        {
            buffer.WriteByte(instruction.Opcode);
            buffer.Write(instruction.Operands);
        }
        return buffer.ToArray();
    }

    private static byte[] EncodeBody(AiScript script, MemoryStream buffer)
    {
        foreach (var instruction in script.Instructions)
        {
            buffer.WriteByte(instruction.Opcode);
            buffer.Write(instruction.Operands);
        }

        if (script.Trailer.Length == 0) return buffer.ToArray();

        // Pad up to where the trailer already sits. Appending it after shorter code would
        // move data that other scripts address by absolute offset.
        var target = script.TrailerStart;
        if (buffer.Length > target)
            throw new InvalidOperationException(
                $"\"{script.Name}\" needs {buffer.Length} bytes of code but only {target} are available " +
                "before the data that follows it.\n\nThat data belongs to other scripts. They are build " +
                "lists and rate tables the scripts point at, so it cannot be pushed along. Remove an " +
            "instruction or two.");

        while (buffer.Length < target) buffer.WriteByte(0);

        buffer.Write(script.Trailer);
        return buffer.ToArray();
    }

    // ---- text form ------------------------------------------------------

    public static ushort ReadWord(AiInstruction instruction)
        => BinaryPrimitives.ReadUInt16LittleEndian(instruction.Operands);

    public static uint ReadDword(AiInstruction instruction)
        => BinaryPrimitives.ReadUInt32LittleEndian(instruction.Operands);

    /// <summary>
    /// Renders one script as the text the editor shows. Each line carries its plain-English
    /// reading as a trailing comment, which the assembler ignores on the way back in.
    /// </summary>
    public static string ToText(AiScript script)
    {
        var lines = new List<(string Code, string Note)>();

        foreach (var instruction in script.Instructions)
        {
            var code = instruction.IsUnknown
                ? $"raw {instruction.Opcode:X2} {Convert.ToHexString(instruction.Operands)}"
                : (AiOpcode)instruction.Opcode switch
                {
                    AiOpcode.Var => $"var {AiTables.VariableName(instruction.Operands[0])} = {instruction.Operands[1]}",
                    AiOpcode.Goto => $"goto {ReadWord(instruction)}",
                    AiOpcode.Sleep => $"sleep {ReadDword(instruction)}",
                    AiOpcode.Wait => $"wait {instruction.Operands[0]}",

                    _ => $"raw {instruction.Opcode:X2}",
                };

            lines.Add((code, AiText.Describe(instruction, script.DescribeBuildLimit)));
        }

        // One comment column, so the descriptions read as a paragraph down the right.
        var column = lines.Count == 0 ? 0 : lines.Max(l => l.Code.Length) + 2;

        var text = new StringBuilder();
        foreach (var (code, note) in lines)
            text.AppendLine($"{code.PadRight(column)}; {note}");

        return text.ToString();
    }

    /// <summary>
    /// What came of reading a pasted script: the instructions, what they weigh, and the
    /// reason it cannot be used if it cannot.
    /// </summary>
    /// <param name="Problem">Null when the script is usable.</param>
    public sealed record TextCheck(List<AiInstruction>? Instructions, int Bytes, string? Problem)
    {
        public bool IsUsable => Problem is null && Instructions is not null;
    }

    /// <summary>
    /// Reads a pasted script and says whether it could be used in a slot of a given size.
    ///
    /// The two ways it can fail are the two the save makes: the text may not assemble, and
    /// it may assemble into more bytes than the slot holds. Kept here rather than in the
    /// window that shows it, so the rules can be tested without one.
    /// </summary>
    /// <param name="capacity">Bytes the instructions may take, header excluded.</param>
    public static TextCheck CheckText(string text, int capacity)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TextCheck(null, 0, "Paste a script to replace this one.");

        List<AiInstruction> instructions;
        try
        {
            instructions = FromText(text);
        }
        catch (Exception ex)
        {
            return new TextCheck(null, 0, ex.Message);
        }

        if (instructions.Count == 0)
            return new TextCheck(null, 0, "That has no instructions in it.");

        var bytes = instructions.Sum(i => i.Length);

        if (bytes > capacity)
            return new TextCheck(null, bytes,
                $"That script needs {bytes} bytes and this slot holds {capacity}. "
                + $"Take out {(bytes - capacity + 2) / 3} instructions or so, "
                + "or paste it into a roomier slot.");

        return new TextCheck(instructions, bytes, null);
    }

    /// <summary>
    /// Parses the editor's text back into instructions. Throws with the line number on the
    /// first thing it cannot read, rather than writing a half-understood script into a game.
    /// </summary>
    public static List<AiInstruction> FromText(string text)
    {
        var instructions = new List<AiInstruction>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var comment = line.IndexOf(';');
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length == 0) continue;

            try
            {
                instructions.Add(ParseLine(line));
            }
            catch (Exception ex)
            {
                throw new FormatException($"Line {i + 1}: {ex.Message}");
            }
        }
        return instructions;
    }

    private static AiInstruction ParseLine(string line)
    {
        var equals = line.IndexOf('=');
        var head = (equals >= 0 ? line[..equals] : line).Trim();
        var tail = equals >= 0 ? line[(equals + 1)..].Trim() : "";

        var parts = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keyword = parts[0].ToLowerInvariant();

        switch (keyword)
        {
            case "raw":
            {
                if (parts.Length < 2) throw new FormatException("raw needs an opcode byte.");
                var opcode = byte.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var operands = parts.Length > 2 ? Convert.FromHexString(parts[2]) : Array.Empty<byte>();
                return new AiInstruction { Opcode = opcode, Operands = operands, IsUnknown = true };
            }

            case "var":
            {
                if (parts.Length < 2) throw new FormatException("var needs a variable.");
                var number = ResolveVariable(parts[1]);

                // Past the end of the state this writes into the next player's, and the
                // game does not check. Refusing here beats a mod that quietly breaks
                // another computer.
                if (number > MaxVariable)
                    throw new FormatException(
                        $"${number:X2} is past the end of a computer's own settings "
                        + $"(${MaxVariable:X2}). Writing it would change another player's.");

                return Build(AiOpcode.Var, number, ParseByte(tail, "value"));
            }



            case "goto":
            {
                var operands = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(operands, ParseWord(Argument(parts), "destination"));
                return new AiInstruction { Opcode = (byte)AiOpcode.Goto, Operands = operands };
            }

            case "sleep":
            {
                var operands = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(operands, ParseDword(Argument(parts), "ticks"));
                return new AiInstruction { Opcode = (byte)AiOpcode.Sleep, Operands = operands };
            }

            case "wait":
                return new AiInstruction
                {
                    Opcode = (byte)AiOpcode.Wait,
                    Operands = new[] { ParseByte(Argument(parts), "condition") },
                };


            default:
                throw new FormatException($"'{parts[0]}' is not an AI instruction.");
        }
    }

    private static AiInstruction Build(AiOpcode opcode, byte a, byte b)
        => new() { Opcode = (byte)opcode, Operands = new[] { a, b } };

    private static string Argument(string[] parts)
        => parts.Length > 1 ? parts[1] : throw new FormatException($"{parts[0]} needs a value.");

    private static byte ResolveVariable(string token)
    {
        foreach (var (number, name) in AiTables.Variables)
            if (string.Equals(name, token, StringComparison.OrdinalIgnoreCase))
                return number;

        return ParseByte(token, "variable");
    }

    private static byte ParseByte(string token, string what)
    {
        var value = ParseNumber(token, what);
        if (value > byte.MaxValue) throw new FormatException($"{what} must be 0-255.");
        return (byte)value;
    }

    private static ushort ParseWord(string token, string what)
    {
        var value = ParseNumber(token, what);
        if (value > ushort.MaxValue) throw new FormatException($"{what} must be 0-65535.");
        return (ushort)value;
    }

    private static uint ParseDword(string token, string what) => ParseNumber(token, what);

    private static uint ParseNumber(string token, string what)
    {
        token = token.Trim();
        if (token.Length == 0) throw new FormatException($"missing {what}.");

        var hex = token.StartsWith('$') || token.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var digits = token.StartsWith('$') ? token[1..] : hex ? token[2..] : token;

        if (uint.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var value))
            return value;

        throw new FormatException($"'{token}' is not a number ({what}).");
    }

    public string Describe()
    {
        var unknown = Scripts.SelectMany(s => s.Instructions).Count(i => i.IsUnknown);
        var total = Scripts.Sum(s => s.Instructions.Count);
        var used = Scripts.Count(s => !s.IsEmpty);

        var text = $"{used} AI scripts, {total} instructions";
        if (used < ScriptCount) text += $", {ScriptCount - used} empty";
        if (unknown > 0) text += $" ({unknown} not decoded)";
        return text;
    }
}
