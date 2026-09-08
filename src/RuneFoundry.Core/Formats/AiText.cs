namespace RuneFoundry.Core.Formats;

/// <summary>
/// Turns an AI instruction into a sentence.
///
/// The bytecode reads as arithmetic — "var $14 = 5" — but almost none of it is. A script
/// sets the AI's *targets* and lets the game work toward them, so the useful reading is
/// "keep 5 Footmen", not "assign 5 to variable 20". These phrasings come from what the
/// variables actually do, cross-checked against WarDraft's own descriptions.
/// </summary>
public static class AiText
{
    /// <summary>
    /// Game ticks per second, used only to show sleeps as a duration.
    ///
    /// Deliberately a setting rather than a constant, because the rate depends on the
    /// speed the game is played at and the available numbers disagree. The default, 52,
    /// comes from a community measurement at the faster speed setting: sleep 27000 timed
    /// at 518 seconds. Stratagus, the open reimplementation, uses 30 for its base rate,
    /// and a second report of sleep 2000 as 17 seconds implies nearer 118 — so this is a
    /// starting point to calibrate against, not a fact.
    ///
    /// The tick counts in the editor are exact; only the durations beside them move.
    /// </summary>
    public static int TicksPerSecond { get; set; } = 52;

    /// <summary>Ticks as a duration a person can judge, e.g. "2 min 47 s".</summary>
    public static string DescribeTicks(uint ticks)
    {
        var seconds = ticks / (double)TicksPerSecond;
        if (seconds < 60) return $"{seconds:0.#} s";

        var minutes = (int)(seconds / 60);
        var rest = (int)Math.Round(seconds - minutes * 60.0);
        if (rest == 60) { minutes++; rest = 0; }

        return rest == 0 ? $"{minutes} min" : $"{minutes} min {rest} s";
    }

    // Attack triggers: writing 1 launches, writing 0 rearms.
    private const byte LandAttack = 0x09;
    private const byte NavalAttack = 0x0A;
    private const byte AirAttack = 0x0B;
    private const byte Strategy = 0x0C;
    private const byte Aggression = 0x21;
    private const byte BuildCursor = 0x22;

    // Party shape: an odd/even pair per domain — size, then how many parties.
    private const byte SizeLand = 0x0D;
    private const byte PartiesLand = 0x0E;
    private const byte SizeWater = 0x0F;
    private const byte PartiesWater = 0x10;
    private const byte SizeAir = 0x11;
    private const byte PartiesAir = 0x12;

    /// <summary>The unit-count variables, which all read as "keep N of these".</summary>
    public static bool IsUnitCountVariable(byte variable) => variable is >= 0x13 and <= 0x20;

    /// <param name="buildItem">
    /// Says what a build limit means for this script, when the caller knows the script.
    /// Without it the instruction can only say the limit moved, because how far down the
    /// list a number reaches depends on the list.
    /// </param>
    public static string Describe(AiInstruction instruction, Func<int, string?>? buildItem = null)
    {
        if (instruction.IsUnknown)
            return $"Unrecognised instruction ({instruction.Opcode:X2})";

        return (AiOpcode)instruction.Opcode switch
        {
            AiOpcode.Var => DescribeVar(instruction.Operands[0], instruction.Operands[1], buildItem),
            AiOpcode.Wait => $"Wait until {AiNames.Wait(instruction.Operands[0])}",
            AiOpcode.Sleep => $"Pause for ~{DescribeTicks(AiFile.ReadDword(instruction))} " +
                              $"({AiFile.ReadDword(instruction):N0} ticks)",
            AiOpcode.Goto => $"Jump to byte {AiFile.ReadWord(instruction)}. The script loops here",
            _ => $"Opcode {instruction.Opcode:X2}",
        };
    }

    public static string DescribeVar(byte variable, byte value,
                                     Func<int, string?>? buildItem = null) => variable switch
    {
        LandAttack => value == 0 ? "Rearm the land attack trigger" : "Launch the land attack",
        NavalAttack => value == 0 ? "Rearm the naval attack trigger" : "Launch the naval attack",
        AirAttack => value == 0 ? "Rearm the air attack trigger" : "Launch the air attack",

        Strategy => value == 0 ? "Strategy off" : "Strategy on",

        Aggression => value switch
        {
            0x00 => "Play less aggressively",
            0xFF => "Play more aggressively",
            _ => $"Set aggressiveness to {value}",
        },

        // Written one step at a time in the shipped scripts, so each write appears to
        // queue a single item rather than raise a ceiling. The build list belongs to the
        // script, so only a caller that knows the script can say what was queued.
        // Not a position but a limit: the computer works the list while the entry number
        // is below this. Naming the entry it stops at is the only reading that is true.
        BuildCursor => buildItem?.Invoke(value) is { } limit
            ? limit
            : value == 0
                ? "Build nothing from the build list"
                : "Build further down the build list",

        SizeLand => $"Land attack parties of {value}",
        SizeWater => $"Naval attack parties of {value}",
        SizeAir => $"Air attack parties of {value}",

        PartiesLand => $"Form {value} land attack {Party(value)}",
        PartiesWater => $"Form {value} naval attack {Party(value)}",
        PartiesAir => $"Form {value} air attack {Party(value)}",

        // "Build up to", not "keep": the scripts clearly set a target rather than order a
        // batch — every header zeroes all 24 unit variables, and one script ramps peasants
        // 1, 9, 15, 19, 25, which as cumulative training would exceed the unit cap. Whether
        // the AI also replaces losses is not something the file says, so it is not claimed.
        // Zero is not "build none of these". It clears a standing request, which is why
        // every script header zeroes all of them before asking for anything.
        _ when IsUnitCountVariable(variable) => value == 0
            ? $"Stop building {AiNames.Variable(variable)}"
            : $"Build and ensure there's always {value} {AiNames.Variable(variable)} in the army",

        // The shipped scripts write variables as high as 0x4A, which are build-item codes
        // rather than unit counts: buildings the computer is told to put up.
        _ when AiNames.IsBuildItem(variable) => value == 0
            ? $"Stop building {AiNames.Variable(variable)}"
            : $"Build and ensure there's always {value} {AiNames.Variable(variable)}",

        _ => $"Set {AiNames.Variable(variable)} to {value}",
    };

    private static string Party(byte count) => count == 1 ? "party" : "parties";
}
