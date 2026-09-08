using System.Text.Json.Nodes;

namespace RuneFoundry.UI;

/// <summary>
/// A JSON document presented as a flat, aligned table of rows rather than a nested tree.
///
/// Two reasons it is flat. Columns line up, which a TreeView cannot do because each level
/// starts at a different indent. And a flat list virtualizes: the largest file the game
/// ships (Art\hd\classic\classic_xswamp.json) is 45,552 nodes, and rendering an editable
/// control per node froze the window for seconds. Only the rows that are actually visible
/// — expanded, and matching the filter — ever reach the UI.
///
/// Everything starts collapsed for the same reason: opening a file should cost one screen
/// of rows, not the whole document.
/// </summary>
public sealed class JsonTable : Observable
{
    public JsonNode? Document { get; }
    public JsonTreeNode Root { get; }

    private List<JsonTreeNode> _rows = new();

    /// <summary>The rows to display: expanded, filtered, in document order.</summary>
    public IReadOnlyList<JsonTreeNode> Rows => _rows;

    /// <summary>Total nodes in the document, for the summary line.</summary>
    public int NodeCount { get; }

    public event Action? Edited;

    private JsonTable(JsonNode? document, JsonTreeNode root, int nodeCount)
    {
        Document = document;
        Root = root;
        NodeCount = nodeCount;
        root.Edited += () => Edited?.Invoke();
    }

    /// <summary>
    /// Parses a document. Safe to call on a background thread — nothing here touches the
    /// UI — which is what keeps a half-megabyte atlas from stalling the window.
    /// </summary>
    public static JsonTable Parse(string json, bool readOnly = false)
    {
        var document = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = false });
        var root = JsonTreeNode.Wrap("(root)", document, 0, null, null, null, 0);
        root.IsReadOnly = readOnly;

        var count = root.SelfAndDescendants().Count();

        var table = new JsonTable(document, root, count);
        table.Rebuild();
        return table;
    }

    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            ApplyFilter();
            Rebuild();
        }
    }

    public void Toggle(JsonTreeNode node)
    {
        if (!node.HasChildren) return;
        node.IsExpanded = !node.IsExpanded;
        Rebuild();
    }

    /// <summary>Opens every branch. Guarded by the caller, since on a big file it is a lot of rows.</summary>
    public void ExpandAll()
    {
        foreach (var node in Root.SelfAndDescendants()) node.IsExpanded = node.HasChildren;
        Rebuild();
    }

    public void CollapseAll()
    {
        foreach (var node in Root.SelfAndDescendants()) node.IsExpanded = false;
        foreach (var child in Root.Children) child.IsExpanded = false;
        Rebuild();
    }

    private void ApplyFilter()
    {
        if (_filter.Length == 0)
        {
            foreach (var node in Root.SelfAndDescendants()) node.MatchesFilter = true;
            return;
        }

        Mark(Root);

        bool Mark(JsonTreeNode node)
        {
            var self = node.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                       || (node.IsLeaf && node.Value.Contains(_filter, StringComparison.OrdinalIgnoreCase));

            var anyChild = false;
            foreach (var child in node.Children) anyChild |= Mark(child);

            node.MatchesFilter = self || anyChild;
            return node.MatchesFilter;
        }
    }

    /// <summary>
    /// Walks the tree once and collects the rows that should be on screen.
    ///
    /// While a filter is active, matching branches are treated as open without actually
    /// setting IsExpanded on them. That matters twice over: writing IsExpanded across
    /// thousands of nodes fired a property change per node and made typing crawl, and
    /// leaving it alone means clearing the filter returns the tree exactly as it was
    /// rather than sprawled open along every path the search happened to touch.
    /// </summary>
    public void Rebuild()
    {
        var rows = new List<JsonTreeNode>();
        var filtering = _filter.Length > 0;

        foreach (var child in Root.Children) Collect(child, rows, filtering);

        _rows = rows;
        Raise(nameof(Rows));
    }

    private static void Collect(JsonTreeNode node, List<JsonTreeNode> rows, bool filtering)
    {
        if (!node.MatchesFilter) return;

        rows.Add(node);
        if (!node.IsExpanded && !filtering) return;

        foreach (var child in node.Children) Collect(child, rows, filtering);
    }

    public string ToJsonString()
        => Document?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "{}";
}
