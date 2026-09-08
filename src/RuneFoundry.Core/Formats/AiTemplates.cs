namespace RuneFoundry.Core.Formats;

/// <summary>
/// The setup block almost every AI script opens with.
///
/// 78 of the 84 shipped scripts begin with these same 26 variables in this same order —
/// it is the game's own convention, and it differs from WarDraft's template, which omits
/// "items" and "tankers". The values here are the ones most scripts use; each mission
/// then tunes them, so this is a starting point rather than boilerplate.
/// </summary>
public static class AiTemplates
{
    /// <summary>Variable, value — in the order the game's own scripts write them.</summary>
    private static readonly (byte Variable, byte Value)[] Setup =
    {
        (0x22, 0),    // restart the build list
        (0x09, 0),    // rearm land attack
        (0x0A, 0),    // rearm naval attack
        (0x0B, 0),    // rearm air attack
        (0x0C, 1),    // strategy on
        (0x21, 0),    // aggressiveness low
        (0x0D, 0), (0x0E, 1),   // land party size, count
        (0x0F, 0), (0x10, 1),   // naval
        (0x11, 0), (0x12, 1),   // air
        (0x14, 0), (0x15, 0), (0x17, 0), (0x16, 0),
        (0x19, 0), (0x1B, 0), (0x1A, 0), (0x1C, 0),
        (0x1D, 0), (0x1F, 0), (0x1E, 0), (0x20, 0),
        (0x13, 1), (0x18, 0),
    };

    public static List<AiInstruction> StandardSetup()
        => Setup.Select(entry => new AiInstruction
        {
            Opcode = (byte)AiOpcode.Var,
            Operands = new[] { entry.Variable, entry.Value },
        }).ToList();

    /// <summary>Variables the setup block covers, for deciding where it ends.</summary>
    public static readonly HashSet<byte> SetupVariables = Setup.Select(e => e.Variable).ToHashSet();

    public enum AttackDomain { Land, Naval, Air }

    /// <summary>
    /// One attack wave, in the shape the game's own scripts use.
    ///
    /// Taken from the 480 waves in ai.bin rather than invented: sleep, set the party size,
    /// wait for the parties, launch. That exact sequence is the most common single shape
    /// (112 waves), and 322 of the 480 wait immediately before launching.
    ///
    /// The numbers are typical values — a 22,000 tick pause and parties of three — meant
    /// to be edited, not obeyed.
    /// </summary>
    /// <param name="partySize">How many units gather before the wave is sent.</param>
    /// <param name="delaySeconds">
    /// How long the computer waits first. Converted with the same ticks-per-second the
    /// script reader uses, so a wave written as three minutes reads back as three minutes.
    /// </param>
    /// <summary>
    /// The units each kind of attack is made of, in the order the game's tables list them.
    ///
    /// A wave that never says what to build has nothing to gather: the party size and the
    /// wait are about assembling units, not about producing them. These are the variables
    /// that tell the computer what to keep making.
    /// </summary>
    public static IReadOnlyList<byte> UnitsFor(AttackDomain domain) => domain switch
    {
        AttackDomain.Naval => new byte[] { 0x19, 0x1B, 0x1C, 0x1A },   // destroyers, battleships, subs, transports
        AttackDomain.Air => new byte[] { 0x1E, 0x20 },                 // flying machines, gryphons
        _ => new byte[] { 0x14, 0x15, 0x17, 0x16, 0x1D },              // footmen, archers, knights, catapults, mages
    };

    /// <param name="builds">
    /// How many of each unit to keep making, by variable number. A count of zero is left
    /// out rather than written, since setting a unit to zero tells the computer to stop
    /// making it, which is a different instruction from not mentioning it.
    /// </param>
    /// <summary>One part of a wave: what to build, how many to gather, where to send them.</summary>
    public readonly record struct AttackPart(
        AttackDomain Domain, byte PartySize, IReadOnlyDictionary<byte, byte>? Builds);

    public static List<AiInstruction> AttackWave(
        AttackDomain domain, byte partySize = 3, int delaySeconds = 0,
        IReadOnlyDictionary<byte, byte>? builds = null)
        => AttackWave(new[] { new AttackPart(domain, partySize, builds) }, delaySeconds);

    /// <summary>
    /// A wave that attacks in more than one way at once.
    ///
    /// The domains are separate settings, so nothing stops a wave gathering a land party
    /// and an air party and sending both. The game does it: 50 of its own stretches launch
    /// more than one attack, and slot $00, the default AI on most maps, sends land and air
    /// together. One sleep opens the wave, then each part builds, gathers, waits and goes.
    /// </summary>
    public static List<AiInstruction> AttackWave(
        IReadOnlyList<AttackPart> parts, int delaySeconds = 0)
    {
        var ticks = delaySeconds > 0
            ? (uint)(delaySeconds * AiText.TicksPerSecond)
            : 22000u;

        var sleep = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(sleep, ticks);

        var wave = new List<AiInstruction>
        {
            new() { Opcode = (byte)AiOpcode.Sleep, Operands = sleep },
        };

        foreach (var part in parts)
            wave.AddRange(Part(part));

        return wave;
    }

    /// <summary>The instructions for one domain, without the sleep that opens the wave.</summary>
    private static IEnumerable<AiInstruction> Part(AttackPart part)
    {
        // The wait multiplies the party size by the setting next to it (0x4ca240 and
        // its two neighbours: imul ecx, eax). Setting the size without the multiplier
        // leaves whatever was there, so a party of 3 could mean 3, 6 or nothing at all.
        var (size, partner, trigger, condition) = part.Domain switch
        {
            AttackDomain.Naval => ((byte)0x0F, (byte)0x10, (byte)0x0A, (byte)0x05),
            AttackDomain.Air => ((byte)0x11, (byte)0x12, (byte)0x0B, (byte)0x06),
            _ => ((byte)0x0D, (byte)0x0E, (byte)0x09, (byte)0x04),
        };

        // What to make, before the size of the party made from it.
        foreach (var (unit, count) in part.Builds ?? new Dictionary<byte, byte>())
        {
            if (count == 0) continue;
            yield return new AiInstruction
                { Opcode = (byte)AiOpcode.Var, Operands = new[] { unit, count } };
        }

        yield return new AiInstruction
            { Opcode = (byte)AiOpcode.Var, Operands = new[] { size, part.PartySize } };
        yield return new AiInstruction
            { Opcode = (byte)AiOpcode.Var, Operands = new[] { partner, (byte)1 } };
        yield return new AiInstruction
            { Opcode = (byte)AiOpcode.Wait, Operands = new[] { condition } };
        yield return new AiInstruction
            { Opcode = (byte)AiOpcode.Var, Operands = new[] { trigger, (byte)1 } };
    }
}
