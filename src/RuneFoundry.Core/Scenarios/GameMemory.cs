using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RuneFoundry.Core.Scenarios;

/// <summary>
/// Reads the running game's memory, and writes one pointer when asked.
///
/// Two handles rather than one. The read handle is opened for the session and is read-only,
/// which is what the watcher needs a few times a second. A write handle is opened for the
/// moment of the write and closed again, so the process spends almost none of its life
/// holding the right to change another program's memory.
///
/// <para>
/// Every address is a virtual address from <see cref="GameAddresses"/>, adjusted by the
/// slide between the executable's preferred base and where Windows actually loaded it.
/// </para>
/// </summary>
public sealed class GameMemory : IDisposable
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessQueryInformation = 0x0400;

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

    private readonly IntPtr _read;
    private readonly int _pid;
    private bool _closed;

    /// <summary>Where the image actually sits, minus where it wanted to sit.</summary>
    public long Slide { get; }

    public Process Game { get; }

    private GameMemory(Process game, IntPtr read, long slide)
    {
        Game = game;
        _pid = game.Id;
        _read = read;
        Slide = slide;
    }

    /// <summary>
    /// Opens a running game for reading. Null when the process is gone, when it cannot be
    /// opened, or when its main module cannot be found.
    /// </summary>
    public static GameMemory? Open(Process game)
    {
        try
        {
            if (game.HasExited) return null;

            var module = game.MainModule;
            if (module is null) return null;

            var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, game.Id);
            if (handle == IntPtr.Zero) return null;

            var slide = module.BaseAddress.ToInt64() - GameAddresses.PreferredBase;
            return new GameMemory(game, handle, slide);
        }
        catch (Exception)
        {
            // A process that exits mid-open, or one this account may not touch. Both are
            // "no game to read", which the caller already has to handle.
            return null;
        }
    }

    private IntPtr At(uint address) => new(address + Slide);

    /// <summary>Reads bytes. Null when the read fails, which is never treated as a zero.</summary>
    public byte[]? Bytes(uint address, int count)
    {
        if (_closed) return null;

        var buffer = new byte[count];
        return ReadProcessMemory(_read, At(address), buffer, count, out var read)
               && read.ToInt64() == count
            ? buffer
            : null;
    }

    public byte? Byte(uint address) => Bytes(address, 1) is { } b ? b[0] : null;

    public ushort? Word(uint address) =>
        Bytes(address, 2) is { } b ? BitConverter.ToUInt16(b) : null;

    public uint? Dword(uint address) =>
        Bytes(address, 4) is { } b ? BitConverter.ToUInt32(b) : null;

    /// <summary>One player's entry in a word[16] array.</summary>
    public ushort? Counter(uint array, int player) =>
        player < 0 || player >= GameAddresses.PlayerCount
            ? null
            : Word(array + (uint)(player * GameAddresses.CounterStride));

    /// <summary>One player's entry in a dword[16] array.</summary>
    public uint? Resource(uint array, int player) =>
        player < 0 || player >= GameAddresses.PlayerCount
            ? null
            : Dword(array + (uint)(player * 4));

    /// <summary>
    /// Writes one dword, opening a write handle for the moment and closing it again.
    ///
    /// The only write the engine ever makes is the victory function pointer, and it is
    /// deliberately awkward to reach: anything that wants to change the game's memory has
    /// to come through here and say so.
    /// </summary>
    public bool TryWritePointer(uint address, uint value)
    {
        if (_closed) return false;

        var handle = OpenProcess(ProcessVmWrite | ProcessVmOperation | ProcessQueryInformation,
                                 false, _pid);
        if (handle == IntPtr.Zero) return false;

        try
        {
            var bytes = BitConverter.GetBytes(value);
            return WriteProcessMemory(handle, At(address), bytes, bytes.Length, out var written)
                   && written.ToInt64() == bytes.Length;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public bool IsRunning
    {
        get
        {
            try { return !_closed && !Game.HasExited; }
            catch (Exception) { return false; }
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;

        if (_read != IntPtr.Zero) CloseHandle(_read);
    }
}
