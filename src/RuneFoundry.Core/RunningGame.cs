using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RuneFoundry.Core.Formats;

namespace RuneFoundry.Core;

/// <summary>
/// Applies a mod's campaign victory conditions to the game while it runs.
///
/// The conditions live in a table of 52 numbers inside the executable, read into the
/// objective state when a mission loads. Editing that table in the file does not work —
/// this build verifies its own image at startup and refuses to run if a single byte
/// differs, which was measured rather than assumed (see
/// <see cref="CampaignObjectives.ExecutableAcceptsEdits"/>). So the file is left alone and
/// the number is changed in the copy Windows has already loaded into memory.
///
/// The whole intervention is one 2-byte write per changed mission, into a data table. The
/// game then picks the matching condition and runs its own code from there; nothing is
/// hooked and no foreign code enters the process. It lives only as long as the process
/// does — closing the game undoes it, and there is nothing on disk to undo.
///
/// This has to happen before the mission is started, because the game copies the value out
/// of the table when the mission loads. Doing it at launch, once, covers every mission in
/// that session.
/// </summary>
public static class RunningGame
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessQueryInformation = 0x0400;

    /// <summary>The base every address in the notes is relative to.</summary>
    private const uint PreferredBase = 0x00400000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr written);

    public sealed record Result(bool Applied, int Changed, string Message);

    /// <summary>
    /// Waits for the game to start, then sets the objectives. The game has to be launched
    /// by Battle.net or it will not sign in, so this waits for the process rather than
    /// starting it.
    /// </summary>
    /// <summary>One table in the executable, and what this mod wants in it.</summary>
    private sealed record Patch(
        string What, uint Address, int EntrySize, uint[] OnDisk, IReadOnlyDictionary<int, long> Wanted);

    /// <summary>
    /// Waits for the game to start, then sets the campaign values this mod changes. The
    /// game has to be launched by Battle.net or it will not sign in, so this waits for the
    /// process rather than starting it.
    ///
    /// Every table is checked whole against the game's own file before a single byte is
    /// written: if one entry disagrees, this is either not the table or something else has
    /// already changed it, and writing would be writing somewhere unknown.
    ///
    /// Only the objective and threshold tables are set this way. Build restrictions used to
    /// be too, and are not any more: a map's ALOW chunk overrides them, so that is done in
    /// the mod's own file instead and never touches the running process.
    /// </summary>
    public static Result Apply(
        GameInstall game,
        IReadOnlyDictionary<int, int> objectives,
        IReadOnlyDictionary<int, int> thresholds,
        TimeSpan timeout,
        CancellationToken cancel = default)
    {
        if (objectives.Count == 0 && thresholds.Count == 0)
            return new Result(true, 0, "");

        // Before anything else: is this the build these addresses describe? Checking the
        // memory against the file cannot answer that — if a table moved, both sides read
        // the same wrong address and agree. Only the file's identity answers it.
        if (!GameBuild.Recognises(game, out var unknown))
            return new Result(false, 0, unknown);

        var process = WaitForGame(timeout, cancel);
        if (process is null)
            return new Result(false, 0, "The game did not start in time, so its campaign was left alone.");

        var patches = new List<Patch>();
        try
        {
            var exe = File.ReadAllBytes(game.ExecutablePath);

            patches.Add(new Patch("victory condition", CampaignObjectives.TableAddress, 2,
                CampaignObjectives.Read(exe).Select(v => (uint)v).ToArray(),
                objectives.ToDictionary(pair => pair.Key, pair => (long)pair.Value)));

            patches.Add(new Patch("target", CampaignObjectives.ThresholdTableAddress, 2,
                CampaignObjectives.ReadThresholds(exe).Select(v => (uint)v).ToArray(),
                thresholds.ToDictionary(pair => pair.Key, pair => (long)pair.Value)));
        }
        catch (Exception ex)
        {
            return new Result(false, 0, "Could not read the game's campaign tables: " + ex.Message);
        }

        IntPtr baseAddress;
        try
        {
            baseAddress = process.MainModule!.BaseAddress;
        }
        catch (Exception ex)
        {
            return new Result(false, 0, "Could not find the game in memory: " + ex.Message);
        }

        var delta = baseAddress.ToInt64() - PreferredBase;

        var handle = OpenProcess(
            ProcessVmRead | ProcessVmWrite | ProcessVmOperation | ProcessQueryInformation, false, process.Id);

        if (handle == IntPtr.Zero)
            return new Result(false, 0, $"Could not open the game (error {Marshal.GetLastWin32Error()}).");

        try
        {
            var changed = 0;

            foreach (var patch in patches)
            {
                if (patch.Wanted.Count == 0) continue;

                var result = Write(handle, delta, patch, ref changed);
                if (result is not null) return result;
            }

            return new Result(true, changed, changed == 0
                ? ""
                : $"Set {changed} campaign value(s) for this session.");
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Writes one table's changes, or explains why it did not. Returns null when all is
    /// well, so the caller can keep going.
    /// </summary>
    private static Result? Write(IntPtr handle, long delta, Patch patch, ref int changed)
    {
        var address = new IntPtr(delta + patch.Address);
        var size = patch.OnDisk.Length * patch.EntrySize;

        var buffer = new byte[size];
        if (!ReadProcessMemory(handle, address, buffer, size, out var read) || read.ToInt64() != size)
            return new Result(false, changed,
                $"Could not read the {patch.What} table (error {Marshal.GetLastWin32Error()}).");

        for (var i = 0; i < patch.OnDisk.Length; i++)
        {
            if (ValueAt(buffer, i * patch.EntrySize, patch.EntrySize) == patch.OnDisk[i]) continue;

            return new Result(false, changed,
                $"The game's {patch.What} table does not match its own file, so it was left alone.");
        }

        foreach (var (slot, value) in patch.Wanted)
        {
            if (slot < 0 || slot >= patch.OnDisk.Length) continue;
            if (patch.OnDisk[slot] == (uint)value) continue;

            var bytes = new byte[patch.EntrySize];
            if (patch.EntrySize == 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value);

            var at = new IntPtr(address.ToInt64() + slot * patch.EntrySize);

            if (!WriteProcessMemory(handle, at, bytes, patch.EntrySize, out var written)
                || written.ToInt64() != patch.EntrySize)
                return new Result(false, changed,
                    $"Could not set the {patch.What} for slot {slot} (error {Marshal.GetLastWin32Error()}).");

            // Read back rather than trusting the call.
            var check = new byte[patch.EntrySize];
            if (!ReadProcessMemory(handle, at, check, patch.EntrySize, out _)
                || ValueAt(check, 0, patch.EntrySize) != (uint)value)
                return new Result(false, changed, $"The {patch.What} for slot {slot} did not stick.");

            changed++;
        }

        return null;
    }

    private static uint ValueAt(byte[] buffer, int offset, int size) =>
        size == 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset))
            : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));

    private static Process? WaitForGame(TimeSpan timeout, CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
        {
            var found = Process.GetProcessesByName("Warcraft II").FirstOrDefault();
            if (found is not null)
            {
                // Let the image finish mapping before reading it.
                Thread.Sleep(4000);
                return found;
            }

            Thread.Sleep(500);
        }

        return null;
    }
}
