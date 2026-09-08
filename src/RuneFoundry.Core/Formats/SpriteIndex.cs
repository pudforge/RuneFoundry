using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuneFoundry.Core.Formats;

/// <summary>What one sprite draws with: an atlas group and the prefix its frames share.</summary>
public readonly record struct SpriteSource(string Atlas, string Prefix);

/// <summary>
/// <c>Art/hd/sprites.json</c>: which atlas each of the game's sprites is drawn from.
///
/// The engine still thinks in terms of the classic files — it asks for
/// <c>art/unit/human/knight.grp</c> — and this file says where the remastered art for that
/// path lives: an atlas group, and the prefix its frames are named with (<c>knight_0</c>,
/// <c>knight_1</c>, …). Which sprite a unit uses is compiled into the game, so it cannot be
/// repointed; what a sprite *reads from* is this file, and that is a mod's way in.
///
/// The atlases are per era, so a swap holds in every season without doing anything extra:
/// the group name is the same in all four, only the file behind it changes.
/// </summary>
public sealed class SpriteIndex
{
    public const string Path = "Art/hd/sprites.json";

    private readonly JsonObject _document;
    private readonly JsonObject _sprites;

    private SpriteIndex(JsonObject document, JsonObject sprites)
    {
        _document = document;
        _sprites = sprites;
        Paths = sprites.Select(pair => pair.Key).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Every sprite path in the file, as the file spells them.</summary>
    public IReadOnlyList<string> Paths { get; }

    public static SpriteIndex Load(string path) => Parse(File.ReadAllText(path));

    public static SpriteIndex Parse(string json)
    {
        var document = JsonNode.Parse(json) as JsonObject
                       ?? throw new InvalidDataException("Not a sprite index: the file is not a JSON object.");

        if (document["sprites"] is not JsonObject sprites)
            throw new InvalidDataException("Not a sprite index: it has no \"sprites\".");

        return new SpriteIndex(document, sprites);
    }

    public SpriteSource? Source(string path)
    {
        if (_sprites[path] is not JsonObject entry) return null;

        var atlas = entry["atlas"]?.GetValue<string>();
        var prefix = entry["prefix"]?.GetValue<string>();
        return atlas is null || prefix is null ? null : new SpriteSource(atlas, prefix);
    }

    /// <summary>
    /// Makes one sprite draw another's art. Returns false when either path is missing.
    ///
    /// The art is taken from <paramref name="stock"/> — the game's own copy — rather than
    /// from this one. Otherwise "draw the Knight with the Ogre's art" would mean whatever
    /// the Ogre happens to point at now, and choosing a sprite's own name could not put it
    /// back, because by then its entry is the art it was swapped to.
    /// </summary>
    public bool Remap(string path, string from, SpriteIndex? stock = null)
    {
        var source = (stock ?? this)._sprites[from] as JsonObject;
        if (source is null || _sprites[path] is not JsonObject) return false;

        _sprites[path] = source.DeepClone();
        return true;
    }

    public bool RestoreFrom(SpriteIndex stock, string path)
    {
        if (stock._sprites[path] is not JsonObject was || _sprites[path] is not JsonObject mine) return false;
        if (JsonNode.DeepEquals(was, mine)) return false;

        _sprites[path] = was.DeepClone();
        return true;
    }

    /// <summary>The sprites that differ from another copy of the file.</summary>
    public IReadOnlyList<string> ChangedFrom(SpriteIndex stock) =>
        Paths.Where(path => stock._sprites[path] is JsonObject was
                            && _sprites[path] is JsonObject mine
                            && !JsonNode.DeepEquals(was, mine)).ToArray();

    /// <summary>
    /// The atlas files for one group in one era, as the file names them — the frame data and
    /// the picture it describes.
    /// </summary>
    public (string Json, string Image)? Atlas(string era, string group)
    {
        if (_document["eras"]?[era]?[group] is not JsonObject files) return null;

        var json = files["atlas"]?.GetValue<string>();
        var image = files["atlas_texture"]?.GetValue<string>();
        return json is null || image is null ? null : (json, image);
    }

    /// <summary>
    /// The team-colour files for one group in one era, or null when the group has none.
    ///
    /// Player colour is not painted into the sprite; it comes from a parallel greyscale sheet
    /// whose frames carry a <c>_team</c> suffix. Seventeen of the eighteen units in the human
    /// sheet have one, so it is usual rather than exceptional — but not universal, hence the
    /// null.
    /// </summary>
    public (string Json, string Image)? MaskAtlas(string era, string group)
    {
        if (_document["eras"]?[era]?[group] is not JsonObject files) return null;

        var json = files["atlas_mask"]?.GetValue<string>();
        var image = files["atlas_mask_texture"]?.GetValue<string>();
        return json is null || image is null ? null : (json, image);
    }

    /// <summary>
    /// The other sprites that draw the same thing in the seasons this one does not.
    ///
    /// A building is not one sprite drawn four ways. It is three: <c>rock</c> covers forest
    /// and swamp, <c>s_rock</c> is winter and <c>x_rock</c> is the expansion swamp. Thirty
    /// four things in the game are split this way, so replacing art in one of them and
    /// finding it unchanged on a winter map is the normal first surprise.
    ///
    /// Matched on the name, which is the only thing that relates them: nothing in the file
    /// links a sprite to its seasonal twins.
    /// </summary>
    public IReadOnlyList<string> Family(string path)
    {
        if (Source(path) is not { } source) return Array.Empty<string>();

        var stem = Stem(source.Prefix);

        // The same atlas group as well as the same name. The human and orc Knights share
        // the prefix knight_ and are two different units drawn from two different atlases,
        // not one thing drawn in two seasons.
        return Paths
            .Where(other => other != path
                            && Source(other) is { } theirs
                            && theirs.Atlas == source.Atlas
                            && Stem(theirs.Prefix) == stem)
            .ToArray();
    }

    /// <summary>A prefix without the season marker, so twins share one.</summary>
    private static string Stem(string prefix) =>
        prefix.StartsWith("s_", StringComparison.Ordinal)
        || prefix.StartsWith("x_", StringComparison.Ordinal)
            ? prefix[2..]
            : prefix;

    public IReadOnlyList<string> Eras =>
        (_document["eras"] as JsonObject)?.Select(pair => pair.Key).ToArray() ?? Array.Empty<string>();

    public string ToJson() =>
        _document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson(), new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// A readable name for a sprite path: "Human / knight" out of
    /// "art/unit/human/knight.grp".
    /// </summary>
    public static string Describe(string path)
    {
        var parts = path.Split('/', '\\');
        var file = System.IO.Path.GetFileNameWithoutExtension(parts[^1]);
        var folder = parts.Length >= 2 ? parts[^2] : "";

        if (folder.Length == 0) return file;
        return char.ToUpperInvariant(folder[0]) + folder[1..] + " / " + file;
    }
}
