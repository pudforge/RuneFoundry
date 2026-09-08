using System.IO;

namespace RuneFoundry.Core;

/// <summary>
/// A file name that the installed game answers with more than one file.
///
/// The game ships some of these itself — every one of Turalyon's nine clips exists both in
/// <c>Gamesfx/Turalyon/</c> and in a stray <c>Gamesfx/Turalyon/New folder/</c> — so this is
/// a fact about the install, not necessarily a fault.
/// </summary>
/// <param name="FileName">The name the unit table asks for.</param>
/// <param name="Chosen">The file the rule picked, as a game path.</param>
/// <param name="Ignored">The others, as game paths.</param>
/// <param name="Ambiguous">
/// True when the rule had no reason to prefer the winner — same root, same depth. Those are
/// the ones worth a person's attention; a deeper duplicate is not.
/// </param>
public sealed record SoundCollision(
    string FileName, string Chosen, IReadOnlyList<string> Ignored, bool Ambiguous);

/// <summary>
/// Where the game keeps its sound files.
///
/// The unit-to-sound table names files, not paths — the .wav files sit in folders by
/// speaker (<c>Gamesfx/Knight/Knwhat1.wav</c>) and a couple live under <c>Sfx/</c> — so the
/// installed game is asked once where each name is, and the answer is kept for the session.
/// Names are matched without case, because the notes spell them lowercase and the files do
/// not.
///
/// <para>
/// The game ships duplicate names, so "the file called X" needs a stated rule rather than
/// whatever the directory walk happens to hand over first:
/// </para>
/// <list type="number">
///   <item>the first root that has the name wins, in <see cref="Roots"/> order;</item>
///   <item>within a root, the shallowest path wins — a file in a subfolder of the speaker's
///         folder is a stray copy, and the game plays the one beside it;</item>
///   <item>a tie is broken by path, so the answer never depends on directory order, and is
///         recorded in <see cref="Collisions"/> as ambiguous because nothing about the files
///         themselves justified the choice.</item>
/// </list>
/// <para>
/// Getting this wrong is silent: a mod would override a file the game never plays, and
/// nothing would look broken until someone listened for a line that never changed.
/// </para>
/// </summary>
public sealed class SoundLibrary
{
    private readonly Dictionary<string, string> _byName;

    private SoundLibrary(Dictionary<string, string> byName, IReadOnlyList<SoundCollision> collisions)
    {
        _byName = byName;
        Collisions = collisions;
    }

    /// <summary>The folders unit sounds are found in, in the order they are searched.</summary>
    private static readonly string[] Roots = { "Gamesfx", "Sfx", "Sound" };

    /// <summary>Names the install answers with more than one file. Usually empty.</summary>
    public IReadOnlyList<SoundCollision> Collisions { get; }

    /// <summary>The ones where the rule had to guess, which are the ones worth reporting.</summary>
    public IEnumerable<SoundCollision> AmbiguousCollisions => Collisions.Where(c => c.Ambiguous);

    public static SoundLibrary Scan(GameInstall game)
    {
        // Every candidate first, then one decision per name — picking as we walk would make
        // the answer depend on the order the file system hands directories over.
        var candidates = new Dictionary<string, List<(int Root, int Depth, string Path)>>(
            StringComparer.OrdinalIgnoreCase);

        for (var root = 0; root < Roots.Length; root++)
        {
            var folder = game.ResolveDataPath(Roots[root]);
            if (!Directory.Exists(folder)) continue;

            foreach (var file in Directory.EnumerateFiles(folder, "*.wav", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);

                // Stored the way the rest of the app talks about game files: relative to the
                // data folder, with forward slashes.
                var relative = Path.GetRelativePath(game.DataRoot, file).Replace('\\', '/');
                var depth = relative.Count(c => c == '/');

                if (!candidates.TryGetValue(name, out var list))
                    candidates[name] = list = new List<(int, int, string)>();

                list.Add((root, depth, relative));
            }
        }

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var collisions = new List<SoundCollision>();

        foreach (var (name, list) in candidates)
        {
            var ordered = list
                .OrderBy(c => c.Root)
                .ThenBy(c => c.Depth)
                .ThenBy(c => c.Path, StringComparer.Ordinal)
                .ToList();

            var best = ordered[0];
            found[name] = best.Path;

            if (ordered.Count == 1) continue;

            var ambiguous = ordered[1].Root == best.Root && ordered[1].Depth == best.Depth;
            collisions.Add(new SoundCollision(
                name, best.Path, ordered.Skip(1).Select(c => c.Path).ToList(), ambiguous));
        }

        return new SoundLibrary(found, collisions);
    }

    /// <summary>The game path for a sound file name, or null when the game has no such file.</summary>
    public string? Find(string fileName) => _byName.GetValueOrDefault(fileName);

    public int Count => _byName.Count;

    /// <summary>Every sound the game ships, as game paths.</summary>
    public IEnumerable<string> All => _byName.Values;
}
