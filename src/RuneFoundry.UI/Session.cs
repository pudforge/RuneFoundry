using RuneFoundry.Core;
using RuneFoundry.Core.Formats;

namespace RuneFoundry.UI;

/// <summary>
/// State shared by both modes: where the game is, the backup vault, and the installer.
///
/// Both views hold the same instance, so choosing a game folder in the editor is
/// immediately true in the library, and a mod built in the editor can be installed
/// without a second copy of any of this.
/// </summary>
public sealed class Session : Observable
{
    /// <summary>
    /// The game's own copy of a file.
    ///
    /// Not simply the game folder: while a mod is applied that folder holds the mod's
    /// files, so reading it would show one mod's work inside another — a new project would
    /// open with the last one's unit stats and names already in the boxes. The vault keeps
    /// what was displaced, so it is asked first.
    /// </summary>
    public string? StockFile(string relativePath) =>
        Game is null ? null : Vault.StockFile(Game, relativePath);

    /// <summary>
    /// The file a mod actually reads: its own copy if it has one, otherwise the game's.
    ///
    /// The rule itself lives on the vault, beside the one for what the game shipped. Null
    /// means there is no game folder set, which is the only way this has no answer.
    /// </summary>
    public string? EffectiveFile(ModProject? project, string relativePath) =>
        Game is null ? null : Vault.EffectiveFile(project, Game, relativePath);

    /// <summary>
    /// The same, for the screens that have already established there is a game folder.
    ///
    /// Most of the editor is unreachable without one, so most callers have checked before
    /// they ask and a null answer would be a bug in the caller rather than a state to
    /// handle. Saying so here keeps that check in one place instead of scattering a
    /// null-forgiving operator down every call site.
    /// </summary>
    public string RequireFile(ModProject? project, string relativePath) =>
        EffectiveFile(project, relativePath)
        ?? throw new InvalidOperationException(
            $"No game folder is set, so {relativePath} has no location.");

    /// <summary>The game's own copy, where the caller has already established there is one.</summary>
    public string RequireStock(string relativePath) =>
        StockFile(relativePath)
        ?? throw new InvalidOperationException(
            $"No game folder is set, so {relativePath} has no location.");

    private GameInstall? _game;
    public GameInstall? Game
    {
        get => _game;
        private set { Set(ref _game, value); Raise(nameof(HasGame)); }
    }

    public bool HasGame => Game is not null;

    public BackupVault Vault { get; } = BackupVault.Default();

    /// <summary>
    /// What has been changed in this session, and how to take it back.
    ///
    /// On the session rather than on one screen because a person's history is one history:
    /// they changed a stat, then a mission's text, then an icon, and Ctrl+Z should walk back
    /// through that in order regardless of which tab each happened on.
    /// </summary>
    public UndoStack Undo { get; } = new();

    /// <summary>
    /// Undo for whole files a mod replaces, kept beside the stack it pushes onto.
    ///
    /// One per session, because the stack is one per session: undoing means "the last thing
    /// I did", wherever it was done.
    /// </summary>
    public OverrideUndo FileUndo { get; }

    private ModInstaller? _installer;

    /// <summary>Null until a game folder is known; recreated when the folder changes.</summary>
    public ModInstaller? Installer
    {
        get => _installer;
        private set => Set(ref _installer, value);
    }

    /// <summary>Raised when the game folder changes, so both views can rebuild.</summary>
    public event Action? GameChanged;

    /// <summary>
    /// Raised after the contents of the game folder change — an apply, or a restore to
    /// vanilla. The editor previews stock files straight off disk, so those bytes are
    /// different afterwards and anything showing them is stale.
    /// </summary>
    public event Action? GameFilesChanged;

    public void NotifyGameFilesChanged() => GameFilesChanged?.Invoke();

    /// <summary>
    /// Whether applying a mod has to go through the elevated helper, and why.
    ///
    /// Two different things can be out of reach: the game folder, when Windows protects
    /// it, and the loader's own vault, when an earlier elevated run left it owned by
    /// Administrators. Both are fixed the same way — do the work as administrator once —
    /// so they are asked about together.
    /// </summary>
    public bool NeedsElevationToApply(out string reason)
    {
        if (Game is not null && !Game.CanWrite(out var gameError))
        {
            reason = gameError;
            return true;
        }

        if (!Vault.CanWrite(out var vaultError))
        {
            reason = vaultError;
            return true;
        }

        reason = "";
        return false;
    }

    public Settings Settings { get; } = Settings.Load();

    public Session()
    {
        FileUndo = new OverrideUndo(Undo);

        // The formatter is static, so push the stored preference into it once.
        AiText.TicksPerSecond = Math.Clamp(Settings.TicksPerSecond, 1, 1000);
    }

    /// <summary>Finds the install from settings, then the registry, then the usual paths.</summary>
    public void AutoDetect()
    {
        GameInstall? game = null;
        if (!string.IsNullOrWhiteSpace(Settings.GameRoot))
            GameInstall.TryOpen(Settings.GameRoot, out game, out _);
        game ??= GameInstall.Detect();

        if (game is not null) UseGame(game);
    }

    public bool TryUseGame(string path, out string error)
    {
        if (!GameInstall.TryOpen(path, out var game, out error)) return false;
        UseGame(game!);
        return true;
    }

    public void UseGame(GameInstall game)
    {
        Game = game;
        Settings.GameRoot = game.Root;
        Settings.Save();

        try
        {
            Installer = new ModInstaller(game, Vault);
        }
        catch
        {
            // A corrupt state file is reported by the library view, which can act on it;
            // the editor still works fine without an installer.
            Installer = null;
        }

        GameChanged?.Invoke();
    }
}

/// <summary>A row in the editor's overrides list.</summary>
public sealed record OverrideRow(string Path, string Note)
{
    // A record prints its whole shape, and that is what a screen reader reads out.
    public override string ToString() => Path;
}

/// <summary>
/// A row in the AI script list.
///
/// The note is kept as a tooltip rather than a second line: the slot number, the
/// instruction count and which other slots share the script are all true, and none of them
/// is what someone scanning 84 names is looking for.
/// </summary>
public sealed record AiRow(AiScript Script, string Title, string Note)
{
    /// <summary>Whether this script has been changed in this session.</summary>
    public bool IsEdited => Script.IsModified;

    /// <summary>
    /// Whether this mod carries a different script from the game's.
    ///
    /// Not the same as IsEdited, which forgets when the project is reopened. This is what
    /// makes "the ones I wrote" findable in a list of 84.
    /// </summary>
    public bool IsMine { get; init; }

    /// <summary>
    /// The bytes this script has to work with.
    ///
    /// Every script sits at a position the rest of the file points at, so it can only ever
    /// use its own room: emptying one script gives nothing to another. That makes the
    /// budget the thing to know before starting, and it was not shown anywhere. The way to
    /// write a script of your own is to take a roomy slot and clear it, and you cannot
    /// choose a roomy slot without being told which ones are roomy.
    /// </summary>
    /// <summary>
    /// Bytes the code may take. Measured to the trailer, not to the end of the slot: for
    /// thirty of the game's scripts the bytes behind the code are another script's build
    /// list, and counting them offered room that the save then refused.
    /// </summary>
    public int Room => Script.CodeCapacity - AiScript.HeaderLength;

    /// <summary>What the script would take if it were written now.</summary>
    public int Used => Script.Instructions.Sum(i => i.Length);

    public bool Overflows => Used > Room;

    /// <summary>Roughly how many more instructions would fit, at three bytes each.</summary>
    public int Spare => Math.Max(0, (Room - Used) / 3);

    /// <summary>
    /// How full the script is, short enough for a narrow list.
    ///
    /// A percentage rather than a count. "Room for about 1 more" was both too long for the
    /// sidebar and useless: one instruction is not room for anything. The bytes and what
    /// they mean belong beside the script, where there is width for them.
    /// </summary>
    public string Budget => $"{(Room == 0 ? 0 : Used * 100 / Room)}% full";

    /// <summary>The same in full, for the pane that has room to say it.</summary>
    public string BudgetDetail => Overflows
        ? $"{Used} of {Room} bytes, over by {Used - Room}"
        : $"{Used} of {Room} bytes, {Room - Used} free";

    // A record prints its whole shape, and that is what a screen reader reads out.
    public override string ToString() => Title;
}

/// <summary>A row in the .dat record list.</summary>
/// <summary>One sound a unit plays, as the unit view lists it.</summary>
public sealed class UnitSoundRow : Observable
{
    public required UnitSoundKind Kind { get; init; }
    public required string FileName { get; init; }

    /// <summary>Where the game keeps it, or null when this install has no such file.</summary>
    public required string? Path { get; init; }

    /// <summary>
    /// Set for a building sound, which belongs to an event rather than to the unit that is
    /// selected — see <see cref="BuildingSounds"/>.
    /// </summary>
    public string? What { get; init; }

    public string Label => $"{What ?? KindName(Kind)} · {FileName}";

    /// <summary>What the game plays this for, in the words a player would use.</summary>
    private static string KindName(UnitSoundKind kind) => kind switch
    {
        UnitSoundKind.Ready => "Ready",
        UnitSoundKind.Acknowledge => "Order",
        UnitSoundKind.Select => "Select",
        UnitSoundKind.Annoyed => "Annoyed",
        UnitSoundKind.WorkDone => "Work done",
        UnitSoundKind.Help => "Under attack",
        UnitSoundKind.Death => "Death",
        _ => "Sound",
    };

    private bool _overridden;

    public bool Overridden
    {
        get => _overridden;
        set => Set(ref _overridden, value);
    }

    private bool _isPlaying;

    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }
}

public sealed record DatRecordRow(int Index, string Title, string Note, bool Edited = false)
{
    // A record prints its whole shape, and that is what a screen reader reads out.
    public override string ToString() => Title;
}
