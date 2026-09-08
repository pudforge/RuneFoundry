using System.Buffers.Binary;

namespace RuneFoundry.Core.Formats;

/// <summary>What a rebuild did, so the editor can say it rather than just doing it.</summary>
public sealed class AiRebuildReport
{
    public int OldSize { get; init; }
    public int NewSize { get; init; }

    /// <summary>Scripts that ended up at a different byte offset than they started at.</summary>
    public int ScriptsMoved { get; set; }

    /// <summary>Aliased slots given their own copy, so they can be edited apart.</summary>
    public int SlotsSeparated { get; set; }

    public int PointersRewritten { get; set; }

    public List<string> Notes { get; } = new();

    public string Summary =>
        $"{OldSize} → {NewSize} bytes, {ScriptsMoved} script(s) moved, {PointersRewritten} pointer(s) rewritten"
        + (SlotsSeparated > 0 ? $", {SlotsSeparated} shared slot(s) separated" : "");
}

/// <summary>
/// Rebuilds an ai.bin from scratch so scripts can change size.
///
/// The default writer (<see cref="AiFile.ToBytes"/>) keeps every script exactly where it
/// was and pads, because the file is full of 16-bit pointers into itself. That is safe and
/// it is also a ceiling: a script cannot grow by one byte. This lifts the ceiling by doing
/// what a linker does — lay everything out afresh, then fix up every pointer.
///
/// It is tractable because the pointer set is small and completely enumerable. In the
/// shipped file there are exactly three classes:
///
///   84   slot offsets in the table at 0..167
///   162  header words, two per script, aiming at its build list and its rate table
///   80   goto operands
///
/// And because of one measured fact: <b>no goto crosses a script</b>. All 75 in-script
/// gotos target their own script's code and the other 5 target the shared routine at 170.
/// So code is self-contained, and moving a script only means rewriting its own jumps.
///
/// The data those header words point at is a different matter. Build lists and rate tables
/// are pooled — 58 of the 162 land inside slot $53's extent, the rest in various scripts'
/// trailers — and several scripts start at different points within one shared run of item
/// codes. Nothing marks where one ends. So this never tries to own that data: it moves each
/// region as an opaque block and maps the pointers through, which needs no idea of where
/// the boundaries are.
/// </summary>
public static class AiLinker
{
    /// <summary>Offsets are 16-bit, so this is the hard ceiling on the whole file.</summary>
    public const int MaxSize = ushort.MaxValue;

    /// <summary>
    /// One span of the file, in emission order. Either a block of bytes that moves as a
    /// unit, or a script's code, which may also change length.
    /// </summary>
    private sealed class Chunk
    {
        public required string Name { get; init; }
        public int OldStart { get; init; }
        public int OldLength { get; init; }
        public int NewStart { get; set; }
        public byte[] Bytes { get; set; } = Array.Empty<byte>();

        /// <summary>The script this chunk holds the code of, if it is code.</summary>
        public AiScript? Script { get; init; }

        /// <summary>
        /// A second copy of an aliased script's code. It occupies no old address of its
        /// own, so it takes no part in mapping — it is only ever a destination.
        /// </summary>
        public bool IsCopy { get; init; }

        public bool Resized => !IsCopy && Bytes.Length != OldLength;
        public int Delta => NewStart - OldStart;
    }

    /// <summary>
    /// Lays the file out again and returns the new bytes.
    ///
    /// <paramref name="separateAliasedSlots"/> gives each slot its own copy of the code
    /// where several point at the same script today, which is what makes them separately
    /// editable. The shared trailer stays with the first slot: the data in it is pointed at
    /// by absolute address from elsewhere, so there can only be one of it.
    /// </summary>
    public static byte[] Rebuild(AiFile file, bool separateAliasedSlots, out AiRebuildReport report)
    {
        var chunks = Plan(file, separateAliasedSlots, out var slotChunks, out var separated);

        var newSize = Place(chunks);
        if (newSize > MaxSize)
            throw new InvalidOperationException(
                $"The rebuilt file would be {newSize} bytes. Offsets in this format are 16-bit, so "
                + $"{MaxSize} is the most it can hold. Remove some instructions.");

        report = new AiRebuildReport { OldSize = file.Length, NewSize = newSize, SlotsSeparated = separated };

        foreach (var chunk in chunks)
        {
            if (chunk.Script is not null && !chunk.IsCopy && chunk.NewStart != chunk.OldStart)
                report.ScriptsMoved++;
        }

        Relink(file, chunks, slotChunks, report);

        var output = new byte[newSize];
        foreach (var chunk in chunks) chunk.Bytes.CopyTo(output, chunk.NewStart);

        Verify(file, output, chunks, slotChunks);
        return output;
    }

    // ---- layout ---------------------------------------------------------

    /// <summary>
    /// Cuts the file into the spans that move independently, in the order they are emitted.
    /// Keeping the original order is what makes an unedited rebuild produce the same bytes
    /// back, which is the regression test the whole thing is gated on.
    /// </summary>
    private static List<Chunk> Plan(AiFile file, bool separateAliasedSlots,
        out Chunk?[] slotChunks, out int separated)
    {
        var chunks = new List<Chunk>();
        slotChunks = new Chunk?[AiFile.ScriptCount];
        separated = 0;

        chunks.Add(new Chunk
        {
            Name = "offset table",
            OldStart = 0,
            OldLength = AiFile.HeaderSize,
            Bytes = new byte[AiFile.HeaderSize],   // filled in during relink
        });

        var live = file.Scripts.Where(s => !s.IsEmpty).ToList();
        var firstScript = live.Count > 0 ? live.Min(s => s.Offset) : file.Length;

        // Between the table and the shared routine sit a couple of bytes that belong to
        // neither. Verbatim, and they never move, but they are still a span.
        var sharedStart = file.SharedRoutine?.Offset ?? firstScript;
        if (sharedStart > AiFile.HeaderSize)
        {
            chunks.Add(new Chunk
            {
                Name = "pre-shared",
                OldStart = AiFile.HeaderSize,
                OldLength = sharedStart - AiFile.HeaderSize,
                Bytes = file.Slice(AiFile.HeaderSize, sharedStart),
            });
        }

        if (file.SharedRoutine is { } routine)
        {
            // Split like any script: its code can grow, and whatever follows the goto is
            // data that other things address, so it moves as a block of its own.
            var routineCode = routine.Trailer.Length > 0
                ? routine.TrailerStart
                : routine.OriginalBytes.Length;

            chunks.Add(new Chunk
            {
                Name = "shared routine",
                OldStart = routine.Offset,
                OldLength = routineCode,
                Bytes = AiFile.EncodeScript(routine, includeHeader: false, includeTrailer: false),
                Script = routine,
            });

            if (routine.Trailer.Length > 0)
            {
                chunks.Add(new Chunk
                {
                    Name = "shared routine trailer",
                    OldStart = routine.Offset + routine.TrailerStart,
                    OldLength = routine.Trailer.Length,
                    Bytes = routine.Trailer,
                });
            }
        }

        // Scripts in file order. Slots that name the same offset are one script; the extra
        // slots get a copy of the code only if asked for, since the trailer cannot be
        // duplicated — other scripts point into it by absolute address.
        foreach (var group in live.GroupBy(s => s.Offset).OrderBy(g => g.Key))
        {
            var slots = group.OrderBy(s => s.Index).ToList();

            // Aliased slots are separate objects parsed from the same bytes, so an edit
            // lands on one of them and not the others. Keeping them together means
            // choosing, and choosing between two different edits would be guessing.
            var edited = slots.Where(s => s.IsModified).ToList();
            if (!separateAliasedSlots && edited.Count > 1)
                throw new InvalidOperationException(
                    $"{string.Join(" and ", edited.Select(s => $"\"{s.Name}\""))} are the same script "
                    + "and have both been edited. Turn on separating shared slots, or undo one of them.");

            // When they are being separated every slot emits its own code below, so the
            // first one is simply the first one. When they are staying together, the
            // edited one is the whole point.
            var script = separateAliasedSlots ? slots[0] : edited.FirstOrDefault() ?? slots[0];

            var codeLength = script.Trailer.Length > 0 ? script.TrailerStart : script.OriginalBytes.Length;

            var code = new Chunk
            {
                Name = $"{script.Name} code",
                OldStart = script.Offset,
                OldLength = codeLength,
                Bytes = AiFile.EncodeScript(script, includeHeader: true, includeTrailer: false),
                Script = script,
            };
            chunks.Add(code);

            foreach (var slot in slots) slotChunks[slot.Index] = code;

            if (script.Trailer.Length > 0)
            {
                chunks.Add(new Chunk
                {
                    Name = $"{script.Name} trailer",
                    OldStart = script.Offset + script.TrailerStart,
                    OldLength = script.Trailer.Length,
                    Bytes = script.Trailer,
                });
            }

            if (!separateAliasedSlots || slots.Count == 1) continue;

            // Each separated slot is emitted from its own parsed script, which is what
            // makes an edit to one of them survive rather than being overwritten by the
            // copy the others still hold.
            foreach (var slot in slots.Skip(1))
            {
                var copy = new Chunk
                {
                    Name = $"{slot.Name} code (separated copy)",
                    OldStart = script.Offset,
                    OldLength = codeLength,
                    Bytes = AiFile.EncodeScript(slot, includeHeader: true, includeTrailer: false),
                    Script = slot,
                    IsCopy = true,
                };
                chunks.Add(copy);
                slotChunks[slot.Index] = copy;
                separated++;
            }
        }

        return chunks;
    }

    private static int Place(List<Chunk> chunks)
    {
        var at = 0;
        foreach (var chunk in chunks)
        {
            chunk.NewStart = at;
            at += chunk.Bytes.Length;
        }
        return at;
    }

    // ---- pointer fixups -------------------------------------------------

    private static void Relink(AiFile file, List<Chunk> chunks, Chunk?[] slotChunks, AiRebuildReport report)
    {
        var table = chunks[0];

        for (var i = 0; i < AiFile.ScriptCount; i++)
        {
            var target = slotChunks[i]?.NewStart ?? 0;
            BinaryPrimitives.WriteUInt16LittleEndian(table.Bytes.AsSpan(i * 2), (ushort)target);
            if (slotChunks[i] is not null) report.PointersRewritten++;
        }

        foreach (var chunk in chunks)
        {
            if (chunk.Script is null) continue;

            var script = chunk.Script;
            var hasHeader = script.Index >= 0;

            if (hasHeader)
            {
                PatchHeader(file, chunks, chunk, script, offsetInChunk: 0, script.Header0,
                    isBuildList: true, report);
                PatchHeader(file, chunks, chunk, script, offsetInChunk: 2, script.Header1,
                    isBuildList: false, report);
            }

            // Goto operands, found by walking the instruction list the bytes were built from.
            var at = hasHeader ? 4 : 0;
            foreach (var instruction in script.Instructions)
            {
                if (instruction.Opcode == (byte)AiOpcode.Goto && !instruction.IsUnknown)
                {
                    var old = BinaryPrimitives.ReadUInt16LittleEndian(instruction.Operands);
                    var mapped = MapGoto(chunks, chunk, script, old, report);
                    BinaryPrimitives.WriteUInt16LittleEndian(chunk.Bytes.AsSpan(at + 1), (ushort)mapped);
                }
                at += instruction.Length;
            }
        }
    }

    private static void PatchHeader(AiFile file, List<Chunk> chunks, Chunk chunk, AiScript script,
        int offsetInChunk, ushort value, bool isBuildList, AiRebuildReport report)
    {
        // Slot $53 "Custom (compiled) AI" is not a script: its first word is 255, which
        // lands in the offset table, and its second is past the end of the file. Those are
        // not pointers, and resolving them would be inventing a meaning. Carried through.
        int pointer;
        bool moved;
        string? problem;

        if (isBuildList && !script.HasBuildList)
        {
            pointer = value;
            moved = false;
            problem = null;
        }
        else
        {
            pointer = Map(chunks, value, out moved, out problem);
        }

        if (problem is not null)
            throw new InvalidOperationException(
                $"\"{script.Name}\" points at byte {value} for its "
                + (isBuildList ? "build list" : "rate table")
                + $", which {problem}. The rebuild was abandoned and nothing was written.");

        if (moved) report.PointersRewritten++;
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.Bytes.AsSpan(offsetInChunk), (ushort)pointer);
    }

    /// <summary>
    /// Where a goto should now aim.
    ///
    /// Its own script first, and against the code as it stands in memory rather than as it
    /// was on disk: the editor renumbers rows as they are added and removed, so a jump the
    /// user picked refers to the layout in front of them. Since the code within a script is
    /// contiguous and its relative layout is the same before and after, that is one delta.
    /// </summary>
    private static int MapGoto(List<Chunk> chunks, Chunk chunk, AiScript script, int value,
        AiRebuildReport report)
    {
        if (value >= script.Offset && value < script.Offset + chunk.Bytes.Length)
        {
            report.PointersRewritten++;
            return value - script.Offset + chunk.NewStart;
        }

        var mapped = Map(chunks, value, out var moved, out var problem);

        if (problem is not null)
            throw new InvalidOperationException(
                $"\"{script.Name}\" jumps to byte {value}, which {problem}. "
                + "The rebuild was abandoned and nothing was written.");

        if (moved) report.PointersRewritten++;
        return mapped;
    }

    /// <summary>
    /// Follows an address through the move. Anything outside every span — past the end of
    /// the file, or a word that was never a pointer — is passed through untouched, because
    /// changing it would be a guess.
    /// </summary>
    private static int Map(List<Chunk> chunks, int value, out bool moved, out string? problem)
    {
        moved = false;
        problem = null;

        foreach (var chunk in chunks)
        {
            if (chunk.IsCopy) continue;
            if (value < chunk.OldStart || value >= chunk.OldStart + chunk.OldLength) continue;

            // A span whose length changed can only be aimed at by its first byte: anything
            // further in was an offset into content that is no longer the same shape.
            if (chunk.Resized && value != chunk.OldStart)
            {
                problem = $"is inside \"{chunk.Name}\", which changed size";
                return value;
            }

            moved = chunk.Delta != 0;
            return value + chunk.Delta;
        }

        return value;
    }

    // ---- the gate -------------------------------------------------------

    /// <summary>
    /// Reads the result back before anyone is allowed to keep it.
    ///
    /// A linker that gets a fixup wrong produces a file that looks fine and plays wrong, so
    /// the output is re-parsed and checked against what went in: every slot must land on
    /// its script, every script must decode to the instructions it was built from, and
    /// every pointer must land inside the file.
    /// </summary>
    /// <summary>An instruction in the words a failure message can use.</summary>
    private static string Describe(AiInstruction instruction) =>
        instruction.Operands.Length == 0
            ? $"opcode {instruction.Opcode:X2}"
            : $"opcode {instruction.Opcode:X2} with {instruction.Operands.Length} operand byte(s)";

    private static void Verify(AiFile file, byte[] output, List<Chunk> chunks, Chunk?[] slotChunks)
    {
        AiFile reparsed;
        try
        {
            reparsed = AiFile.Parse(output);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The rebuilt file could not be read back: " + ex.Message
                + "\n\nNothing was written.", ex);
        }

        for (var i = 0; i < AiFile.ScriptCount; i++)
        {
            var chunk = slotChunks[i];
            var script = reparsed.Scripts[i];

            if (chunk is null)
            {
                if (!script.IsEmpty)
                    throw new InvalidOperationException($"Slot {i} should be empty but is not.");
                continue;
            }

            if (script.Offset != chunk.NewStart)
                throw new InvalidOperationException(
                    $"Slot {i} was written pointing at {script.Offset}, not {chunk.NewStart}.");

            var wanted = chunk.Script!.Instructions;
            if (script.Instructions.Count != wanted.Count)
                throw new InvalidOperationException(
                    $"\"{script.Name}\" read back with {script.Instructions.Count} instructions, "
                    + $"not the {wanted.Count} it was built from.");

            // Counting was not enough. A script can come back with the right number of
            // instructions and the wrong bytes in them, which is the failure that would
            // reach the game looking healthy.
            for (var at = 0; at < wanted.Count; at++)
            {
                var was = wanted[at];
                var now = script.Instructions[at];

                if (now.Opcode == was.Opcode && now.Operands.AsSpan().SequenceEqual(was.Operands))
                    continue;

                // A goto is the one instruction whose operand is meant to change: it points
                // at a byte, and the byte moved. Its target is checked below instead.
                if (was.Opcode == (byte)AiOpcode.Goto && now.Opcode == was.Opcode) continue;

                throw new InvalidOperationException(
                    $"\"{script.Name}\" instruction {at} read back as "
                    + $"{Describe(now)}, not the {Describe(was)} it was built from. "
                    + "Nothing was written.");
            }
        }

        foreach (var script in reparsed.Scripts)
        {
            foreach (var instruction in script.Instructions)
            {
                if (instruction.Opcode != (byte)AiOpcode.Goto || instruction.IsUnknown) continue;

                var target = AiFile.ReadWord(instruction);

                // Only that it lands inside the file. Checking it lands on an instruction
                // boundary was tried and does not hold: ai.bin carries one instruction the
                // parser cannot size, so the walk drifts after it and the game's own
                // untouched file reports as corrupt. A boundary check is worth having when
                // every opcode is known and not before.
                if (target < AiFile.HeaderSize || target >= output.Length)
                    throw new InvalidOperationException(
                        $"\"{script.Name}\" would jump to byte {target}, which is outside the "
                        + $"{output.Length}-byte file. Nothing was written.");
            }
        }

        if (output.Length > MaxSize)
            throw new InvalidOperationException($"The rebuilt file is {output.Length} bytes.");
    }
}
