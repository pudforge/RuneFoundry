namespace RuneFoundry.Core;

/// <summary>
/// Which build of the game RuneFoundry knows the insides of.
///
/// Every hard-coded address in W2R-RE-NOTES.md — the campaign tables, the icon table, the
/// art paths — describes one exact executable. They are not derived at run time and there
/// is nothing in the file that announces where they moved to, so a patched or updated build
/// puts different data at the same address.
///
/// That matters most for the live patcher, which writes into the running game. Comparing
/// what is in memory against the same address in the file on disk does *not* catch a moved
/// table: both sides read the same wrong place and agree with each other. Only the identity
/// of the whole file catches it, which is what this is for.
///
/// The rule is refuse, not guess: an unknown build means the notes no longer describe it.
/// </summary>
public static class GameBuild
{
    /// <summary>
    /// Builds whose layout matches the notes, newest last.
    ///
    /// Adding one here is a claim that its addresses were re-checked — at minimum that the
    /// campaign table tests pass against it — not that it merely launched.
    /// </summary>
    public static readonly IReadOnlyList<string> Known = new[]
    {
        // Warcraft II Remastered, x86\Warcraft II.exe, 5,558,992 bytes.
        "1a396a77b123bbae46c6a2d92adc2ec6714134c24023ed8218f7a160a80ec162",
    };

    public static bool IsKnown(string sha256) =>
        Known.Any(known => string.Equals(known, sha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether this install is the build the notes describe. False with a reason when it is
    /// not, so the caller can say so rather than writing somewhere unknown.
    /// </summary>
    public static bool Recognises(GameInstall game, out string reason)
    {
        try
        {
            var path = game.ExecutablePath;
            if (!File.Exists(path))
            {
                reason = "The game's program file could not be found.";
                return false;
            }

            var sha = Hashing.Sha256File(path);
            if (IsKnown(sha))
            {
                reason = "";
                return true;
            }

            reason = "This is not a build of Warcraft II that RuneFoundry knows its way "
                     + "around (" + sha[..12] + "…), most likely because the game has been "
                     + "updated. Anything that depends on the game's own tables was left alone.";
            return false;
        }
        catch (Exception ex)
        {
            reason = "The game's program file could not be read: " + ex.Message;
            return false;
        }
    }
}
