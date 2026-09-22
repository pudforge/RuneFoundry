using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuneFoundry.Core.Scenarios;

/// <summary>Why the engine is not watching, in words a person can act on.</summary>
public sealed record ScenarioRefusal(string Reason)
{
    public override string ToString() => Reason;
}

/// <summary>What the engine is doing, for a UI that has to show it.</summary>
public enum ScenarioState
{
    /// <summary>Not attached to anything.</summary>
    Idle,

    /// <summary>Attached and looking, but this mission has no rules of ours.</summary>
    Watching,

    /// <summary>This mission has rules and they are being tested every poll.</summary>
    Armed,

    /// <summary>The rules were met and the mission has been decided.</summary>
    Fired,

    /// <summary>Attached to nothing, and the reason is worth showing.</summary>
    Refused,
}

/// <summary>
/// What was written into which game, so a crashed session can be recognised on the way back.
///
/// A process id alone is reusable within minutes of a process ending, so the start time is
/// kept with it. The pair is not reusable, and matching both is what stops a later, entirely
/// unrelated game being treated as ours.
/// </summary>
public sealed class ScenarioSession
{
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("startedUtc")] public DateTime StartedUtc { get; set; }
    [JsonPropertyName("modId")] public string ModId { get; set; } = "";
    [JsonPropertyName("slots")] public List<int> Slots { get; set; } = new();

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RuneFoundry", "scenario-session.json");
}

/// <summary>
/// Watches a running game and, when the author's rules are met, tells the game the mission
/// is over.
///
/// <para>
/// The whole design in one paragraph: the Launcher writes an inert objective id for the
/// slots a mod has rules for, so the game stops deciding those missions for itself and
/// waits. This polls the game a few times a second, and when a rule set holds it sets one
/// function pointer to the game's own WIN or LOSE routine. Nothing is injected, nothing on
/// disk changes, and the game finishes the mission through its own code.
/// </para>
///
/// <para>
/// It refuses far more often than it acts, and every refusal has a reason a person can
/// read. An unrecognised build, a skirmish, a mission with no rules, a pointer that is not
/// where we left it: all of them stop the engine rather than prompting it to guess.
/// </para>
/// </summary>
public sealed class ScenarioEngine : IDisposable
{
    /// <summary>How often to look. Fast enough to feel immediate, slow enough to be free.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How many consecutive polls a rule must hold for.
    ///
    /// Two, because a single poll can catch the game mid-update: a building removed but its
    /// counter not yet decremented, or the reverse. Requiring the same answer twice costs a
    /// quarter of a second and removes the whole class of half-written reads.
    /// </summary>
    public const int Debounce = 2;

    private readonly IReadOnlyDictionary<int, ScenarioRules> _rules;
    private readonly Func<IGameSnapshot?> _look;
    private readonly Action<uint, uint>? _write;
    private readonly Action<uint, ushort>? _poke;

    private CancellationTokenSource? _stopping;
    private Task? _loop;

    // The episode: one run at one mission. Leaving game state 3 ends it.
    private bool _inEpisode;
    private int _episodeSlot = -1;
    private bool _firedThisEpisode;
    private uint? _firedTerminal;
    private bool _firedTerminalSeen;
    private int _victoryHeld;
    private int _defeatHeld;

    public ScenarioState State { get; private set; } = ScenarioState.Idle;
    public string? Detail { get; private set; }
    public int? ArmedSlot { get; private set; }

    public event Action<int>? Armed;
    public event Action<string>? Disarmed;
    public event Action<bool>? Fired;
    public event Action<ScenarioRefusal>? Refused;

    /// <summary>
    /// Builds an engine over a source of snapshots.
    ///
    /// Taking the source as a delegate rather than a process is what lets the state machine
    /// be driven by a script in a test. The part with the states is the part worth testing,
    /// and it never needs a game to exercise.
    /// </summary>
    public ScenarioEngine(IReadOnlyDictionary<int, ScenarioRules> rules,
                          Func<IGameSnapshot?> look,
                          Action<uint, uint>? write = null,
                          Action<uint, ushort>? poke = null)
    {
        _rules = rules;
        _look = look;
        _write = write;
        _poke = poke;
    }

    /// <summary>
    /// Attaches to a running game, if everything about it is recognised.
    ///
    /// Returns the reason when it will not, which the caller is expected to show rather
    /// than swallow: an engine that quietly does nothing is the failure this design is most
    /// exposed to.
    /// </summary>
    public static ScenarioEngine? TryAttach(GameInstall game, Process process,
                                            IReadOnlyDictionary<int, ScenarioRules> rules,
                                            string modId,
                                            out ScenarioRefusal? refusal)
    {
        refusal = null;

        if (rules.Count == 0)
        {
            refusal = new ScenarioRefusal("This mod has no rules of its own to watch for.");
            return null;
        }

        if (!GameBuild.Recognises(game, out var why))
        {
            refusal = new ScenarioRefusal(
                "RuneFoundry does not recognise this build of the game, so it will not read "
                + "its memory. " + why);
            return null;
        }

        var memory = GameMemory.Open(process);
        if (memory is null)
        {
            refusal = new ScenarioRefusal("RuneFoundry could not open the running game.");
            return null;
        }

        var engine = new ScenarioEngine(rules,
            () => memory.IsRunning ? new LiveSnapshot(memory) : null,
            (address, value) => memory.TryWriteCodePointer(address, value),
            (address, value) => memory.TryWriteWord(address, value))
        {
            _memory = memory,
        };

        engine.WriteSession(process, modId, rules.Keys);
        return engine;
    }

    private GameMemory? _memory;

    /// <summary>Starts the poll loop.</summary>
    public void Start()
    {
        if (_loop is not null) return;

        _stopping = new CancellationTokenSource();
        var token = _stopping.Token;

        _loop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { Poll(); }
                catch (Exception) { /* one bad poll is not a reason to stop watching */ }

                try { await Task.Delay(PollInterval, token); }
                catch (TaskCanceledException) { return; }
            }
        }, token);
    }

    /// <summary>One look at the game, and everything that follows from it.</summary>
    public void Poll()
    {
        var game = _look();

        if (game is null)
        {
            EndEpisode("The game has closed.");
            State = ScenarioState.Idle;
            return;
        }

        // The episode edge: entering game state 3 is a new run at a mission, and leaving it
        // ends whatever was being watched. Restart, quit to menu and load all pass through
        // a state that is not 3.
        var playing = game.GameState == 3;

        if (!playing)
        {
            EndEpisode("No mission is being played.");
            State = ScenarioState.Watching;
            return;
        }

        if (!_inEpisode)
        {
            _inEpisode = true;
            _firedThisEpisode = false;
            _victoryHeld = 0;
            _defeatHeld = 0;
            _episodeSlot = game.MissionSlot ?? -1;
        }
        else if (game.MissionSlot is { } slot && slot != _episodeSlot)
        {
            // The slot changed without the state leaving 3. Treat it as a new episode
            // rather than carrying stale latches into a different mission.
            _firedThisEpisode = false;
            _victoryHeld = 0;
            _defeatHeld = 0;
            _episodeSlot = slot;
        }

        // A mission can be replayed inside one game process: win it, advance, come back.
        // The episode reset above only fires if a poll catches the brief non-playing frame
        // or a slot change between the two runs, and advancing-then-returning can slip
        // between polls, leaving the fired latch set so the replay never re-arms. The
        // signal that cannot be missed is the game resetting the victory pointer off the
        // terminal we wrote: when the mission reloads, the pointer leaves our WIN/LOSE and
        // becomes the game's own again. Treat that as a fresh run and re-arm.
        if (_firedThisEpisode)
        {
            // Confirm our write landed before trusting the pointer to tell us about a
            // reload: until the game reflects the terminal we wrote, "different" only means
            // the write is in flight, not that the mission restarted.
            if (game.VictoryFunction == _firedTerminal) _firedTerminalSeen = true;

            if (_firedTerminalSeen && game.VictoryFunction is { } current && current != _firedTerminal)
            {
                _firedThisEpisode = false;
                _firedTerminal = null;
                _firedTerminalSeen = false;
                _victoryHeld = 0;
                _defeatHeld = 0;
            }
            else
            {
                return;
            }
        }

        if (Guard(game) is { } refusal)
        {
            if (State != ScenarioState.Refused || Detail != refusal.Reason)
            {
                State = ScenarioState.Refused;
                Detail = refusal.Reason;
                ArmedSlot = null;
                Refused?.Invoke(refusal);
            }
            return;
        }

        var rules = _rules[_episodeSlot];
        var local = game.LocalPlayer ?? 0;

        if (State != ScenarioState.Armed || ArmedSlot != _episodeSlot)
        {
            State = ScenarioState.Armed;
            ArmedSlot = _episodeSlot;
            Detail = null;
            Armed?.Invoke(_episodeSlot);
        }

        // Nothing is judged until the map's units have loaded. On a replay the objective
        // is already ours from the first run, so the watcher arms on the first load frame,
        // before any unit has registered: every count reads zero, and an "owns exactly 0"
        // rule fires on the empty map. This is the "lost to my rules on replay" report.
        // Holding costs a fraction of a second and removes the whole class of it.
        if (game.MapLoaded != true) return;

        // Defeat first. A mission that is both won and lost in the same tick is a mission
        // the author wrote badly, and losing is the safer reading of it.
        _defeatHeld = ScenarioEvaluator.Evaluate(rules.Defeat, game, local) == true ? _defeatHeld + 1 : 0;
        if (_defeatHeld >= Debounce) { FireOnce(false); return; }

        _victoryHeld = ScenarioEvaluator.Evaluate(rules.Victory, game, local) == true ? _victoryHeld + 1 : 0;
        if (_victoryHeld >= Debounce) FireOnce(true);
    }

    /// <summary>
    /// Every reason not to act, checked before anything is read for a rule.
    ///
    /// All of them are about being sure we are looking at the thing we set up. The cost of
    /// being wrong is somebody's mission ending for no visible reason, which is worse than
    /// the rule never firing.
    /// </summary>
    private ScenarioRefusal? Guard(IGameSnapshot game)
    {
        if (game.IsCampaign != true)
            return new ScenarioRefusal("Rules apply in the single-player campaign only.");

        if (game.IsCustomGame != false)
            return new ScenarioRefusal("This is a custom or multiplayer game, so rules are off.");

        if (game.IsResolved == true)
            return new ScenarioRefusal("The game has already decided this mission.");

        if (_episodeSlot < 0 || !_rules.ContainsKey(_episodeSlot))
            return new ScenarioRefusal("This mission has no rules of its own.");

        // The objective id has to be the inert one. The Launcher writes it before launch,
        // but a straight-to-mission (quick) launch can load the mission and install the
        // game's own objective before that write lands, losing a race: OBJ_STATE is then
        // the game's, and the author's rules never get a vote. This is the intermittent
        // "nothing fires" reported from quick launch.
        //
        // So take it over. When the game has armed its own condition function for a slot we
        // own, write our inert objective and park the victory pointer on the inert check;
        // the next poll sees inert and arms. Gating on the pointer being a real condition
        // function is also what keeps this from seizing mid-load: while the map is still
        // loading the pointer is garbage, out of range, and we wait rather than act.
        if (game.ObjectiveState != GameAddresses.InertObjective)
        {
            if (_write is not null && _poke is not null
                && game.VictoryFunction is { } seizing
                && seizing >= GameAddresses.ConditionsLow && seizing < GameAddresses.ConditionsHigh)
            {
                _poke(GameAddresses.ObjectiveState, GameAddresses.InertObjective);
                _write(GameAddresses.VictoryFunction, GameAddresses.InertTerminal);
                return new ScenarioRefusal("Taking this mission over from the game's own rule…");
            }

            return new ScenarioRefusal(
                "This mission is using one of the game's own rules, so RuneFoundry is standing back.");
        }

        // And the pointer has to still be where we left it.
        if (game.VictoryFunction is not { } pointer)
            return new ScenarioRefusal("RuneFoundry cannot read the game's mission state.");

        if (pointer < GameAddresses.ConditionsLow || pointer >= GameAddresses.ConditionsHigh)
            return new ScenarioRefusal("The game's mission state is not where RuneFoundry expects it.");

        return null;
    }

    private void FireOnce(bool win)
    {
        if (_firedThisEpisode) return;
        _firedThisEpisode = true;

        var terminal = win ? GameAddresses.WinTerminal : GameAddresses.LoseTerminal;
        _firedTerminal = terminal;
        _firedTerminalSeen = false;

        if (_write is null)
        {
            State = ScenarioState.Fired;
            Fired?.Invoke(win);
            return;
        }

        _write(GameAddresses.VictoryFunction, terminal);

        // The game runs its victory check once every 50 ticks, so a win we have just
        // written waits up to a couple of seconds to take effect. Forcing the countdown to
        // one makes it run on the very next tick: "kill the unit" becomes "see the victory
        // screen" with no perceptible lag. After the pointer, so the check finds it in place.
        _poke?.Invoke(GameAddresses.Countdown, 1);

        State = ScenarioState.Fired;
        Detail = win ? "The mission was won by your rules." : "The mission was lost by your rules.";
        Fired?.Invoke(win);
    }

    private void EndEpisode(string why)
    {
        if (!_inEpisode) return;

        _inEpisode = false;
        _episodeSlot = -1;
        _firedThisEpisode = false;
        _firedTerminal = null;
        _firedTerminalSeen = false;
        _victoryHeld = 0;
        _defeatHeld = 0;
        ArmedSlot = null;
        Detail = why;

        Disarmed?.Invoke(why);
    }

    // ---- crash recovery ---------------------------------------------------------------

    private void WriteSession(Process process, string modId, IEnumerable<int> slots)
    {
        try
        {
            var record = new ScenarioSession
            {
                Pid = process.Id,
                StartedUtc = process.StartTime.ToUniversalTime(),
                ModId = modId,
                Slots = slots.ToList(),
            };

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ScenarioSession.Path)!);
            File.WriteAllText(ScenarioSession.Path,
                JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Losing the record costs the ability to resume after a crash, and nothing else.
        }
    }

    /// <summary>Forgets the session record. Called on a clean stop.</summary>
    public static void ClearSession()
    {
        try
        {
            if (File.Exists(ScenarioSession.Path)) File.Delete(ScenarioSession.Path);
        }
        catch (Exception) { /* it is swept on the next write */ }
    }

    /// <summary>The record a previous run left, or null.</summary>
    public static ScenarioSession? ReadSession()
    {
        try
        {
            return File.Exists(ScenarioSession.Path)
                ? JsonSerializer.Deserialize<ScenarioSession>(File.ReadAllText(ScenarioSession.Path))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a running game is the one a previous session set up.
    ///
    /// The pid and the start time both have to match. A pid on its own is reused within
    /// minutes, and arming against a game we did not prepare would mean watching a mission
    /// whose objective id is the game's own.
    /// </summary>
    public static bool IsOurs(ScenarioSession session, Process process)
    {
        try
        {
            return session.Pid == process.Id
                   && Math.Abs((session.StartedUtc - process.StartTime.ToUniversalTime()).TotalSeconds) < 2;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Stop()
    {
        _stopping?.Cancel();

        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* a loop that will not stop is not worth hanging a close on */ }

        _loop = null;
        _stopping?.Dispose();
        _stopping = null;

        EndEpisode("Stopped watching.");
        State = ScenarioState.Idle;
    }

    public void Dispose()
    {
        Stop();
        _memory?.Dispose();
        ClearSession();
    }
}
