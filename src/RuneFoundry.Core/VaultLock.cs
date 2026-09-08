namespace RuneFoundry.Core;

/// <summary>
/// The one thing two programs must not do at once.
///
/// The loader and the editor are separate processes that share a vault, an install-state
/// file and a game folder. Everything else they share is read-mostly and survives a race;
/// applying does not. An Apply moves the game's files into the vault and the mod's files
/// into their place, and a second Apply running against the same half-finished state can
/// back up a mod's file *as though it were the game's* — after which uninstalling restores
/// the wrong bytes and the player's game is quietly broken with no copy left to fix it.
///
/// So applying takes a machine-wide lock. It is held for the length of one apply, which is
/// seconds, and the second caller is told to wait rather than being left to interleave.
///
/// The name is global so it holds across sessions and across the elevated helper, which is
/// a different process again — and, being elevated, one that would otherwise not see a
/// session-local mutex at all.
/// </summary>
public sealed class VaultLock : IDisposable
{
    /// <summary>Global\ so it is shared across sessions and elevation.</summary>
    private const string Name = @"Global\RuneFoundry.Vault";

    private readonly Mutex? _mutex;
    private readonly bool _held;

    private VaultLock(Mutex? mutex, bool held)
    {
        _mutex = mutex;
        _held = held;
    }

    /// <summary>Whether this process may proceed with an apply.</summary>
    public bool Acquired => _held;

    /// <summary>
    /// Waits a short while for the other program to finish, then gives up rather than
    /// hanging: a caller that cannot get in has something useful to say to the person.
    /// </summary>
    public static VaultLock TryAcquire(TimeSpan timeout)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, Name);

            // A process that died mid-apply leaves the mutex abandoned. That is precisely
            // when the vault may be half-written, so the lock is granted — the caller is the
            // one who can repair it — but the exception must not escape.
            bool held;
            try { held = mutex.WaitOne(timeout, false); }
            catch (AbandonedMutexException) { held = true; }

            return new VaultLock(mutex, held);
        }
        catch (Exception)
        {
            // No lock is available at all (an unusual policy, or a locked-down account).
            // Refusing to work at all would be worse than working the way one program did.
            mutex?.Dispose();
            return new VaultLock(null, true);
        }
    }

    /// <summary>What to tell someone who could not get in.</summary>
    public const string BusyMessage =
        "Another RuneFoundry window is applying a mod right now. Wait for it to finish and try again.";

    public void Dispose()
    {
        if (_mutex is null) return;

        if (_held)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not ours to release */ }
        }

        _mutex.Dispose();
    }
}
