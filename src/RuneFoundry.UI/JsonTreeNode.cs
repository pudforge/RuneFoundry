using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace RuneFoundry.UI;

/// <summary>
/// One row of a JSON document.
///
/// The game's JSON files are sprite atlases — nested objects of frame rectangles — where
/// the *type* of each value matters: writing 5 back as "5" gives you an atlas the game
/// cannot read. So each leaf remembers the kind it was parsed as and writes back the same
/// kind, rejecting an edit that would change it rather than silently corrupting the file.
/// </summary>
public sealed class JsonTreeNode : Observable
{
    private enum LeafKind { String, Number, Boolean, Null }

    private readonly JsonObject? _parentObject;
    private readonly JsonArray? _parentArray;
    private readonly string? _key;
    private readonly int _index;
    private readonly LeafKind _kind;

    public string Name { get; }
    public List<JsonTreeNode> Children { get; } = new();
    public bool IsLeaf { get; }

    /// <summary>How deep in the document this sits; drives the row's indent.</summary>
    public int Depth { get; private set; }

    /// <summary>Raised when any descendant value actually changes, so the view can offer a save.</summary>
    public event Action? Edited;

    private JsonTreeNode(
        string name, JsonNode? node, bool isLeaf, LeafKind kind,
        JsonObject? parentObject, JsonArray? parentArray, string? key, int index)
    {
        Name = name;
        IsLeaf = isLeaf;
        _kind = kind;
        _parentObject = parentObject;
        _parentArray = parentArray;
        _key = key;
        _index = index;

        if (isLeaf) _value = Format(node);
    }

    private string _value = "";

    /// <summary>The leaf's value as text. Setting it writes straight back into the document.</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (IsReadOnly || !IsLeaf || _value == value) return;

            if (!TryConvert(value, out var converted, out var problem))
            {
                Error = problem;
                Raise(nameof(Error));
                Raise(nameof(HasError));
                return;
            }

            _value = value;
            if (_parentObject is not null && _key is not null) _parentObject[_key] = converted;
            else if (_parentArray is not null) _parentArray[_index] = converted;

            Error = null;
            Raise();
            Raise(nameof(Error));
            Raise(nameof(HasError));
            Edited?.Invoke();
        }
    }

    /// <summary>Set when the last edit was rejected; shown beside the field.</summary>
    public string? Error { get; private set; }
    public bool HasError => Error is not null;

    /// <summary>
    /// The kind badge. Strings are left blank: nearly every leaf in these files is a
    /// string, so labelling them all adds noise and hides the ones that are not.
    /// </summary>
    public string TypeLabel => IsLeaf
        ? _kind == LeafKind.String ? "" : _kind.ToString().ToLowerInvariant()
        : Children.Count == 1 ? "1 item" : $"{Children.Count} items";

    public bool HasChildren => Children.Count > 0;

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value)) Raise(nameof(ExpanderGlyph)); }
    }

    public string ExpanderGlyph => !HasChildren ? "" : IsExpanded ? "▾" : "▸";

    /// <summary>
    /// Row indent, applied directly so no value converter is needed. Depth 1 is the
    /// top level on screen — the synthetic root is never shown — so it sits flush left.
    /// </summary>
    public Thickness Indent => new(Math.Max(0, Depth - 1) * 14, 0, 0, 0);

    /// <summary>
    /// Set while the file being shown is still the stock game file. The table stays fully
    /// browsable — you can search it and read every value — but nothing can be typed into
    /// it, because a change here would have to invent an override the author never asked
    /// for. Copying the original into the mod is what makes it writable.
    /// </summary>
    private bool _isReadOnly;
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set
        {
            if (!Set(ref _isReadOnly, value)) return;
            foreach (var child in Children) child.IsReadOnly = value;
        }
    }

    /// <summary>Cleared and recomputed by the filter; a row is listed only when it matches.</summary>
    internal bool MatchesFilter { get; set; } = true;

    private bool TryConvert(string text, out JsonNode? node, out string problem)
    {
        problem = "";
        node = null;

        switch (_kind)
        {
            case LeafKind.Number:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                {
                    node = JsonValue.Create(whole);
                    return true;
                }
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
                {
                    node = JsonValue.Create(real);
                    return true;
                }
                problem = "must be a number";
                return false;

            case LeafKind.Boolean:
                if (bool.TryParse(text, out var flag))
                {
                    node = JsonValue.Create(flag);
                    return true;
                }
                problem = "must be true or false";
                return false;

            case LeafKind.Null:
                if (text.Length == 0 || text == "null") return true;   // stays null
                node = JsonValue.Create(text);
                return true;

            default:
                node = JsonValue.Create(text);
                return true;
        }
    }

    private static string Format(JsonNode? node)
    {
        if (node is null) return "null";
        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => node.ToJsonString(),
        };
    }

    private static LeafKind KindOf(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.Number => LeafKind.Number,
        JsonValueKind.True or JsonValueKind.False => LeafKind.Boolean,
        JsonValueKind.Null or null => LeafKind.Null,
        _ => LeafKind.String,
    };

    internal static JsonTreeNode Wrap(
        string name, JsonNode? node, int depth,
        JsonObject? parentObject, JsonArray? parentArray, string? key, int index)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var branch = new JsonTreeNode(name, node, false, LeafKind.Null, parentObject, parentArray, key, index)
                {
                    Depth = depth,
                };
                foreach (var pair in obj)
                    branch.Adopt(Wrap(pair.Key, pair.Value, depth + 1, obj, null, pair.Key, 0));
                return branch;
            }
            case JsonArray array:
            {
                var branch = new JsonTreeNode(name, node, false, LeafKind.Null, parentObject, parentArray, key, index)
                {
                    Depth = depth,
                };
                for (var i = 0; i < array.Count; i++)
                    branch.Adopt(Wrap($"[{i}]", array[i], depth + 1, null, array, null, i));
                return branch;
            }
            default:
                return new JsonTreeNode(name, node, true, KindOf(node), parentObject, parentArray, key, index)
                {
                    Depth = depth,
                };
        }
    }

    private void Adopt(JsonTreeNode child)
    {
        child.Edited += () => Edited?.Invoke();
        Children.Add(child);
    }

    public IEnumerable<JsonTreeNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.SelfAndDescendants())
                yield return node;
    }
}
