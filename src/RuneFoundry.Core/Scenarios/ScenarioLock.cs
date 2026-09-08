namespace RuneFoundry.Core.Scenarios;

/// <summary>
/// Makes sure only one process watches a game at a time.
///
/// The Editor's Save and test and the Launcher can both be open, and both would happily
/// attach to the same game. Two watchers would each write the victory pointer, and the
/// second write would land after the mission was already decided. The same shape as
/// <see cref="VaultLock"/>: a named mutex, held for the session, and the loser reports that
/// somebody else has it rather than waiting.
/// </summary>
public sealed class ScenarioLock : IDisposable
{
    private const string Name = @"Global\RuneFoundry.ScenarioWatcher";

    private readonly Mutex? _mutex;
    private bool _released;

    public bool Held { get; }

    private ScenarioLock(Mutex? mutex, bool held)
    {
        _mutex = mutex;
        Held = held;
    }

    /// <summary>Takes the lock, or comes back not holding it. Never blocks for long.</summary>
    public static ScenarioLock TryAcquire()
    {
        Mutex? mutex = null;
        var held = false;

        try
        {
            mutex = new Mutex(false, Name);

            try
            {
                held = mutex.WaitOne(TimeSpan.Zero, false);
            }
            catch (AbandonedMutexException)
            {
                // The last holder died without releasing it, which is exactly the crash
                // this has to survive. The lock is ours.
                held = true;
            }
        }
        catch (Exception)
        {
            // A machine that will not give us a mutex is one where we do not watch.
            mutex?.Dispose();
            return new ScenarioLock(null, false);
        }

        if (held) return new ScenarioLock(mutex, true);

        mutex.Dispose();
        return new ScenarioLock(null, false);
    }

    public void Dispose()
    {
        if (_released || _mutex is null) return;
        _released = true;

        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* not ours to release */ }

        _mutex.Dispose();
    }
}
