using System.Buffers.Binary;

namespace RuneFoundry.Core.Formats;

/// <summary>
/// The campaign's victory conditions, as chosen by the game executable.
///
/// Each of the 52 campaign slots has an objective id in a table of 52 uint16s at virtual
/// address 0x008C1BB8. The game reads it into its objective-state word before the map file
/// is even parsed, which is why no amount of PUD editing can change a mission's win
/// condition: the executable has already decided.
///
/// Writing <see cref="DestroyAllEnemies"/> into a slot repoints that mission at the check
/// the game already runs for the eighteen missions shipped with it — "no other player has
/// a building, or a unit that is not a flyer, transport or tanker". It is two bytes of
/// data, it changes no code, and putting the original value back undoes it completely.
///
/// The table's location is worked out from the PE section headers rather than hard-coded
/// as a file offset, so a patched or differently built executable is still handled or
/// cleanly refused.
/// </summary>
public static class CampaignObjectives
{
    /// <summary>
    /// Whether editing this build's executable is viable at all. It is not.
    ///
    /// Measured, not assumed. Three copies of Warcraft II.exe were placed beside the
    /// original and launched with the same arguments:
    ///
    ///   byte-identical copy, different filename   — runs
    ///   one byte changed in the objective table   — ACCESS_VIOLATION at startup
    ///   one byte changed in the DOS stub string   — ACCESS_VIOLATION at startup
    ///
    /// The third is the one that settles it: that text is never executed, so the game is
    /// not failing on what was changed but on the fact that anything was. Build 1.0.2.2818
    /// verifies its own image, and no data-only edit — however small, however correct —
    /// survives it. Patching a copy instead of the original, which is what the notes
    /// recommend, does not help: the copy is checked too.
    ///
    /// What works instead is changing the number in the copy Windows has already loaded
    /// into memory, which the file's own check has no view of. See <see cref="RunningGame"/>.
    /// Everything below is still used: the table is found, read and described from the
    /// file, which is how the editor knows what a mission's victory condition is and what
    /// the running game should be checked against before anything is written to it.
    /// </summary>
    public const bool ExecutableAcceptsEdits = false;

    /// <summary>The 52 campaign slots: 14 Human and 14 Orc interleaved, then 12 and 12.</summary>
    public const int MissionCount = 52;

    /// <summary>Where the table lives in the loaded image.</summary>
    public const uint TableAddress = 0x008C1BB8;

    /// <summary>
    /// The threshold table, one word per mission, immediately after the objective table.
    ///
    /// Only the counter objectives read it. Copied into the live global 0x009191C8 at
    /// mission load, then compared whole and unsigned at 0x004F437A:
    ///
    ///   low byte  how many units must reach the Circle of Power
    ///   bit 12    those deliveries only count from a unit whose type has the Hero flag
    ///
    /// Every delivery adds 1 to the counter; a Hero adds a further 0x1000, but only when
    /// bit 12 asks for it. So 0x1001 means "one delivery, and it must be a Hero" — no
    /// number of ordinary units can reach 4097.
    /// </summary>
    public const uint ThresholdTableAddress = 0x008C1C20;

    /// <summary>Bit 12: the delivery only counts from a Hero-flagged unit type.</summary>
    public const ushort HeroRequired = 0x1000;

    /// <summary>
    /// Builds a threshold word: how many units must arrive, and how many of those must be
    /// Heroes. The Heroes are counted within the total, not on top of it — a Hero adds
    /// 0x1000 as well as its own 1, so H heroes among T arrivals leave the counter at
    /// (H &lt;&lt; 12) | T, which is what the game compares against.
    /// </summary>
    public static ushort Threshold(int count, int heroes)
        => (ushort)(((heroes & 0xF) << 12) | (count & 0xFF));

    public static int CountOf(ushort threshold) => threshold & 0xFF;
    public static int HeroesOf(ushort threshold) => (threshold >> 12) & 0xF;

    /// <summary>
    /// Whether the game will actually grant the Hero bonus for this many Heroes.
    ///
    /// The bonus is gated on bit 12 alone — 0x004BE486 tests the threshold against 0x1000 —
    /// so an even number leaves that bit clear and no bonus is ever granted, which turns
    /// the mission into thousands of ordinary deliveries. Odd counts work.
    /// </summary>
    public static bool HeroCountWorks(int heroes) => heroes == 0 || (heroes & 1) == 1;

    public static ushort[] ReadThresholds(byte[] exe)
    {
        if (!TryLocate(exe, out _, out var error)) throw new InvalidDataException(error);
        if (!TryFileOffset(exe, ThresholdTableAddress, out var offset, out error))
            throw new InvalidDataException(error);

        var values = new ushort[MissionCount];
        for (var i = 0; i < MissionCount; i++)
            values[i] = BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(offset + i * 2));
        return values;
    }

    /// <summary>
    /// The objective id whose only requirement is that every other player is wiped out.
    /// Bit 8 is what the check at 0x004F4316 tests; eighteen shipped missions use it.
    /// </summary>
    public const ushort DestroyAllEnemies = 0x0100;

    /// <summary>
    /// Finds the table in an executable. False, with a reason, if this does not look like
    /// the executable the address was established against — which is the case worth being
    /// careful about, since the alternative is writing two bytes into the middle of
    /// something else.
    /// </summary>
    public static bool TryLocate(byte[] exe, out int offset, out string error)
    {
        offset = 0;
        error = "";

        if (!TryFileOffset(exe, TableAddress, out offset, out error)) return false;

        if (offset + MissionCount * 2 > exe.Length)
        {
            error = "The objective table would run past the end of the file.";
            return false;
        }

        // Every id the shipped table holds is either a small enum value or one of the two
        // flag values. Anything else means these are not the bytes we are looking for.
        for (var i = 0; i < MissionCount; i++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(offset + i * 2));
            if (value < 64 || value == 0x0100 || value == 0x0200) continue;

            error = $"The table does not look like objective ids (slot {i} holds {value}). "
                    + "This is probably not the executable these addresses were worked out from.";
            return false;
        }

        return true;
    }

    public static ushort[] Read(byte[] exe)
    {
        if (!TryLocate(exe, out var offset, out var error)) throw new InvalidDataException(error);

        var ids = new ushort[MissionCount];
        for (var i = 0; i < MissionCount; i++)
            ids[i] = BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(offset + i * 2));
        return ids;
    }

    /// <summary>Sets one slot's objective id in place. The caller writes the file back.</summary>
    public static void Write(byte[] exe, int mission, ushort id)
    {
        if (mission < 0 || mission >= MissionCount)
            throw new ArgumentOutOfRangeException(nameof(mission), $"There are {MissionCount} campaign slots.");

        if (!TryLocate(exe, out var offset, out var error)) throw new InvalidDataException(error);

        BinaryPrimitives.WriteUInt16LittleEndian(exe.AsSpan(offset + mission * 2), id);
    }

    /// <summary>Whether a slot is already set to win on destroying every enemy.</summary>
    public static bool IsDestroyAll(ushort id) => (id & DestroyAllEnemies) != 0;

    // ---- PE ---------------------------------------------------------------

    /// <summary>
    /// Turns a virtual address into a file offset by walking the section headers. Enough
    /// PE parsing for one lookup and no more.
    /// </summary>
    /// <summary>
    /// Where an address in the running image sits in the file. Shared with the other tables
    /// read out of the executable — see <see cref="IconNames"/>.
    /// </summary>
    public static bool TryFileOffset(byte[] image, uint address, out int offset, out string error)
    {
        offset = 0;
        error = "";

        try
        {
            if (image.Length < 0x40 || image[0] != 'M' || image[1] != 'Z')
            {
                error = "Not an executable.";
                return false;
            }

            var pe = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C));
            if (pe <= 0 || pe + 24 > image.Length
                || image[pe] != 'P' || image[pe + 1] != 'E' || image[pe + 2] != 0 || image[pe + 3] != 0)
            {
                error = "Not a PE executable.";
                return false;
            }

            var sections = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 6));
            var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20));
            var magic = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 24));

            if (magic != 0x10B)
            {
                error = "Only a 32-bit executable is understood, which is what the game ships.";
                return false;
            }

            var imageBase = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(pe + 52));
            if (address < imageBase)
            {
                error = $"Address 0x{address:X8} is below the image base 0x{imageBase:X8}.";
                return false;
            }

            var rva = address - imageBase;
            var table = pe + 24 + optionalSize;

            for (var i = 0; i < sections; i++)
            {
                var at = table + i * 40;
                if (at + 40 > image.Length) break;

                var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 8));
                var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 12));
                var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 16));
                var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 20));

                var span = Math.Max(virtualSize, rawSize);
                if (rva < virtualAddress || rva >= virtualAddress + span) continue;

                var within = rva - virtualAddress;
                if (within >= rawSize)
                {
                    error = "That address is in a section's uninitialised tail, which is not in the file.";
                    return false;
                }

                offset = (int)(rawOffset + within);
                return true;
            }

            error = $"Address 0x{address:X8} is not inside any section.";
            return false;
        }
        catch (Exception ex)
        {
            error = "Could not read the executable's headers: " + ex.Message;
            return false;
        }
    }
}
