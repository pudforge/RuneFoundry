namespace RuneFoundry.Core.Formats;

/// <summary>
/// Things worth saying about a script before it is played.
///
/// Split in two on purpose. <see cref="About"/> is for what the file itself proves wrong,
/// and it stays silent on every script the game ships. <see cref="Caution"/> is for what
/// only looks wrong: a script cannot see the map, and a map can hand its player a starting
/// army or a finished Keep, so "this never builds any" is an observation and not a verdict.
///
/// The reason either matters is that Wait does not fall through. On a false condition the
/// interpreter steps the instruction pointer back by one and sleeps a tick (0x4ca2d0), so
/// it runs the same test forever. A wait that is never met stalls that computer for the
/// rest of the mission.
/// </summary>
public static class AiAdvice
{
    /// <summary>Build-list codes for the two keep upgrades the waits ask about.</summary>
    private const byte UpgradeToKeep = 0x98;
    private const byte UpgradeToCastle = 0x99;

    /// <summary>Party size and its partner, per attack domain.</summary>
    private static readonly Dictionary<byte, (byte Size, string Where)> Parties = new()
    {
        [0x04] = (0x0D, "land"),
        [0x05] = (0x0F, "sea"),
        [0x06] = (0x11, "air"),
    };

    /// <summary>Unit counts that can make a party of each domain.</summary>
    private static readonly Dictionary<byte, byte[]> DomainUnits = new()
    {
        [0x04] = new byte[] { 0x14, 0x15, 0x16, 0x17, 0x1D, 0x1F },
        [0x05] = new byte[] { 0x18, 0x19, 0x1A, 0x1B, 0x1C },
        [0x06] = new byte[] { 0x1E, 0x20 },
    };

    /// <summary>
    /// What is wrong with this instruction, whatever map it runs on. Null when nothing is.
    /// </summary>
    public static string? About(AiScript script, AiInstruction instruction)
    {
        if (instruction.IsUnknown) return null;
        if ((AiOpcode)instruction.Opcode != AiOpcode.Var) return null;

        var number = instruction.Operands[0];

        // "Var n, value" writes one byte at offset n of this player's AI state (0x4ca140:
        // mov byte ptr [eax + esi], cl). There is no bounds check and the states are 48
        // bytes apart, so anything past 0x2F lands in the next player's state. The game
        // does not complain; the other computer just starts behaving strangely.
        return number > AiFile.MaxVariable
            ? $"${number:X2} is past the end of this computer's own settings, so this "
              + "writes into another player's. Use $09 to $2F."
            : null;
    }

    /// <summary>
    /// What looks wrong but depends on the map. Null when there is nothing to say.
    /// </summary>
    public static string? Caution(AiScript script, AiInstruction instruction)
    {
        if (instruction.IsUnknown) return null;
        if ((AiOpcode)instruction.Opcode != AiOpcode.Wait) return null;

        var condition = instruction.Operands[0];

        if (condition is 0x01 or 0x02)
        {
            var wanted = condition == 0x01 ? UpgradeToKeep : UpgradeToCastle;
            var what = condition == 0x01 ? "a Keep / Stronghold" : "a Castle / Fortress";

            if (script.HasBuildList && !script.BuildList.Contains(wanted))
                return $"Waits for {what}, which the build list never upgrades to. It only "
                       + "ends if the map starts this player with one.";
        }

        if (Parties.TryGetValue(condition, out var party))
        {
            var asks = false;
            var builds = false;

            foreach (var other in script.Instructions)
            {
                if (other.IsUnknown || other.Opcode != (byte)AiOpcode.Var) continue;
                if (other.Operands[0] == party.Size && other.Operands[1] > 0) asks = true;
                if (DomainUnits[condition].Contains(other.Operands[0]) && other.Operands[1] > 0)
                    builds = true;
            }

            if (asks && !builds)
                return $"Waits for a {party.Where} party, but the script never asks for "
                       + $"{party.Where} units. It only ends if the map starts this player "
                       + "with some.";
        }

        return null;
    }
}
