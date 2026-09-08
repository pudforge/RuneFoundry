using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuneFoundry.Core.Formats;

/// <summary>Where one frame sits on the sheet.</summary>
public readonly record struct IconRect(int X, int Y, int Width, int Height);

/// <summary>
/// The remaster's icon sheet: <c>Art/hd/HUD/Portrait-face.json</c> and its mask.
///
/// Every icon the game draws — unit portraits, building icons, command and spell buttons —
/// is a frame in one sheet, named <c>&lt;tileset&gt;_&lt;id&gt;</c>. The id is the game's own icon
/// number: <c>upgrades.dat</c>'s icon field indexes straight into it, which is how the
/// numbering was confirmed rather than guessed.
///
/// Which icon a unit draws is compiled into the executable, so it cannot be repointed. What
/// can be changed is where a frame *reads from*: giving frame 2 the rectangle of frame 9
/// makes everything that draws icon 2 draw the Ogre instead. That is the swap this class
/// exists for, and it is why editing happens here rather than in a table of unit fields.
///
/// The four tilesets each have their own copy of every frame, so a swap is applied to all
/// four — a portrait that changed only in the forest would be a bug, not a feature.
/// </summary>
public sealed class IconAtlas
{
    public const string FacePath = "Art/hd/HUD/Portrait-face.json";
    public const string FaceImagePath = "Art/hd/HUD/Portrait-face.png";
    public const string MaskPath = "Art/hd/HUD/Portrait-mask.json";
    public const string MaskImagePath = "Art/hd/HUD/Portrait-mask.png";

    /// <summary>The tileset prefixes, in the order the game names them.</summary>
    public static readonly string[] Tilesets = { "forest", "ice", "swamp", "xswamp" };

    private readonly JsonObject _document;
    private readonly JsonObject _frames;
    private readonly string _suffix;

    /// <summary>
    /// What the team-colour sheet adds to every frame name.
    ///
    /// The face sheet names a frame <c>forest_12</c>; the mask names the same portrait
    /// <c>forest_12_team</c>. Without this the mask's frames parse as id "team", which is not
    /// a number, so every one of them was silently dropped — and an icon swap changed the
    /// face while leaving the mask painting team colour in the old portrait's shape.
    /// </summary>
    public const string TeamMaskSuffix = "_team";

    private IconAtlas(JsonObject document, JsonObject frames, string suffix)
    {
        _document = document;
        _frames = frames;
        _suffix = suffix;

        Ids = frames
            .Select(pair => Split(pair.Key, suffix))
            .Where(split => split.Prefix == Tilesets[0])
            .Select(split => split.Id)
            .OrderBy(id => id)
            .ToArray();
    }

    /// <summary>Every icon id in the sheet, ascending.</summary>
    public IReadOnlyList<int> Ids { get; }

    /// <summary>
    /// Reads an atlas. Pass <see cref="TeamMaskSuffix"/> for the team-colour sheet, whose
    /// frames carry it on every name.
    /// </summary>
    public static IconAtlas Load(string path, string suffix = "") =>
        Parse(File.ReadAllText(path), suffix);

    public static IconAtlas Parse(string json, string suffix = "")
    {
        var document = JsonNode.Parse(json) as JsonObject
                       ?? throw new InvalidDataException("Not an atlas: the file is not a JSON object.");

        if (document["frames"] is not JsonObject frames)
            throw new InvalidDataException("Not an atlas: it has no \"frames\".");

        return new IconAtlas(document, frames, suffix);
    }

    private static (string Prefix, int Id) Split(string name, string suffix)
    {
        if (suffix.Length > 0)
        {
            if (!name.EndsWith(suffix, StringComparison.Ordinal)) return ("", -1);
            name = name[..^suffix.Length];
        }

        var cut = name.LastIndexOf('_');
        return cut < 0 || !int.TryParse(name[(cut + 1)..], out var id)
            ? ("", -1)
            : (name[..cut], id);
    }

    /// <summary>The frame name this sheet uses for one icon in one tileset.</summary>
    public string Name(string tileset, int id) => $"{tileset}_{id}{_suffix}";

    public bool Has(string tileset, int id) => _frames[Name(tileset, id)] is JsonObject;

    public IconRect? Rect(string tileset, int id)
    {
        if (_frames[Name(tileset, id)]?["frame"] is not JsonObject frame) return null;

        return new IconRect(
            frame["x"]?.GetValue<int>() ?? 0,
            frame["y"]?.GetValue<int>() ?? 0,
            frame["w"]?.GetValue<int>() ?? 0,
            frame["h"]?.GetValue<int>() ?? 0);
    }

    /// <summary>
    /// Makes <paramref name="id"/> draw the art that <paramref name="from"/> draws, in every
    /// tileset. Each tileset takes its own copy of the source, so a swapped portrait still
    /// changes with the season the way the original did.
    ///
    /// Returns how many frames were rewritten; zero means neither frame is in this sheet.
    /// </summary>
    public int Remap(int id, int from, IconAtlas? stock = null)
    {
        var written = 0;
        var art = stock ?? this;

        foreach (var tileset in Tilesets)
        {
            // From the game's own copy, so "draw icon 2 with icon 9" always means the same
            // thing, and picking an icon's own number puts it back.
            if (art._frames[$"{tileset}_{from}"] is not JsonObject source) continue;
            if (_frames[Name(tileset, id)] is not JsonObject) continue;

            _frames[Name(tileset, id)] = source.DeepClone();
            written++;
        }

        return written;
    }

    /// <summary>Puts a frame back to what another copy of the sheet says, in every tileset.</summary>
    public int RestoreFrom(IconAtlas stock, int id)
    {
        var written = 0;

        foreach (var tileset in Tilesets)
        {
            var key = Name(tileset, id);
            if (stock._frames[key] is not JsonObject was || _frames[key] is not JsonObject mine) continue;
            if (JsonNode.DeepEquals(was, mine)) continue;

            _frames[key] = was.DeepClone();
            written++;
        }

        return written;
    }

    /// <summary>The ids whose frames differ from another copy of the sheet.</summary>
    public IReadOnlyList<int> ChangedFrom(IconAtlas stock) =>
        Ids.Where(id => Tilesets.Any(tileset =>
        {
            var key = Name(tileset, id);
            return stock._frames[key] is JsonObject was
                   && _frames[key] is JsonObject mine
                   && !JsonNode.DeepEquals(was, mine);
        })).ToArray();

    public string ToJson() =>
        _document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson(), new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
