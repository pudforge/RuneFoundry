using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuneFoundry.Core.Formats;

/// <summary>
/// A TexturePacker sheet addressed by frame name.
///
/// <see cref="IconAtlas"/> reads the portrait sheets, whose frames are named
/// <c>&lt;tileset&gt;_&lt;id&gt;</c> and are asked for by number. Most of the game's other
/// sheets are not like that: <c>skins/Maps.json</c> names its frames <c>human_act1</c>, and
/// <c>skins/Modern_Graphics.json</c> names one <c>sketch_orc2_hovered</c>. There is no id to
/// parse, so this asks by the name itself.
///
/// Read-only. Nothing here rewrites the JSON — replacing art means drawing into the sheet the
/// JSON describes, at the rectangle it gives, and leaving the description alone.
/// </summary>
public sealed class FrameAtlas
{
    private readonly JsonObject _frames;

    private FrameAtlas(JsonObject frames)
    {
        _frames = frames;
        Names = frames.Select(pair => pair.Key).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Every frame in the sheet, in name order.</summary>
    public IReadOnlyList<string> Names { get; }

    public static FrameAtlas Load(string path) => Parse(File.ReadAllText(path));

    public static FrameAtlas Parse(string json)
    {
        var document = JsonNode.Parse(json) as JsonObject
                       ?? throw new InvalidDataException("Not an atlas: the file is not a JSON object.");

        if (document["frames"] is not JsonObject frames)
            throw new InvalidDataException("Not an atlas: it has no \"frames\".");

        return new FrameAtlas(frames);
    }

    public bool Has(string frame) => _frames[frame] is JsonObject;

    /// <summary>Where a frame sits in the sheet, or null when the sheet has no such frame.</summary>
    public IconRect? Rect(string frame)
    {
        if (_frames[frame]?["frame"] is not JsonObject box) return null;

        var x = box["x"]?.GetValue<int>();
        var y = box["y"]?.GetValue<int>();
        var w = box["w"]?.GetValue<int>();
        var h = box["h"]?.GetValue<int>();

        return x is null || y is null || w is null || h is null
            ? null
            : new IconRect(x.Value, y.Value, w.Value, h.Value);
    }
}
