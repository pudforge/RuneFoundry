using System.IO;
using RuneFoundry.Core;
using RuneFoundry.UI;

namespace RuneFoundry.Editor;

/// <summary>
/// One entry in the game data tree. The whole tree is built once from the ~1,950 loose
/// files in x86\Data — small enough that building it eagerly is simpler and faster than
/// lazy expansion, and it lets the filter work across the whole tree at once.
/// </summary>
public sealed class TreeNode : Observable
{
    public string Name { get; }

    /// <summary>What a screen reader reads out for the row, and what UI Automation sees.</summary>
    public override string ToString() => Name;

    /// <summary>Empty for the synthetic root and for folders above the data root.</summary>
    public string RelativePath { get; }

    public bool IsFolder { get; }
    public List<TreeNode> Children { get; } = new();
    public TreeNode? Parent { get; private set; }

    private TreeNode(string name, string relativePath, bool isFolder)
    {
        Name = name;
        RelativePath = relativePath;
        IsFolder = isFolder;
    }

    private bool _isOverridden;

    /// <summary>True when the project supplies this file. Folders show it if any descendant does.</summary>
    public bool IsOverridden
    {
        get => _isOverridden;
        set { if (Set(ref _isOverridden, value)) { Raise(nameof(Marker)); Raise(nameof(HasMarker)); } }
    }

    private bool _isNewFile;

    /// <summary>True for an override with no counterpart in the game — content the mod adds.</summary>
    public bool IsNewFile
    {
        get => _isNewFile;
        set { if (Set(ref _isNewFile, value)) { Raise(nameof(Marker)); Raise(nameof(HasMarker)); } }
    }

    public string Marker => !IsOverridden ? "" : IsNewFile ? "+" : "●";

    /// <summary>
    /// The Lucide glyph for this row, looked up from the merged icon dictionary. Chosen by
    /// extension rather than by sniffing the file, because the tree lists ~1,950 entries
    /// and reading each one to decide on an icon would be absurd.
    /// </summary>
    public System.Windows.Media.Geometry? Icon
    {
        get
        {
            var key = IsFolder
                ? IsExpanded ? "IconFolderOpen" : "IconFolder"
                : System.IO.Path.GetExtension(Name).ToLowerInvariant() switch
                {
                    ".png" or ".bmp" or ".pcx" or ".grp" => "IconImage",
                    ".wav" or ".flac" => "IconMusic",
                    ".webm" or ".smk" or ".bik" => "IconClapperboard",
                    ".json" => "IconBraces",
                    ".pud" => "IconMap",
                    ".tbl" or ".txt" or ".lst" or ".ini" => "IconFileText",
                    _ => "IconFile",
                };

            return System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Geometry;
        }
    }
    public bool HasMarker => IsOverridden;

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>
    /// Bound two-way to the TreeViewItem, so selection can be restored after the tree is
    /// rebuilt — a TreeView offers no way to select an item by value on its own.
    /// </summary>
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    /// <summary>Children that survive the current filter, which is what the TreeView binds to.</summary>
    public IEnumerable<TreeNode> VisibleChildren => Children.Where(c => c.IsVisible);

    public void RaiseVisibleChildren() => Raise(nameof(VisibleChildren));

    /// <summary>
    /// Builds the tree from the game's files plus any project overrides that have no
    /// game counterpart, so added content shows up alongside replaced content.
    /// </summary>
    public static TreeNode Build(GameInstall game, IEnumerable<string> extraPaths)
    {
        var root = new TreeNode("Data", "", isFolder: true) { IsExpanded = true };
        var folders = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };

        var all = game.EnumerateDataFiles()
            .Concat(extraPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var path in all)
        {
            var segments = path.Split('/');
            var parent = root;

            for (var i = 0; i < segments.Length - 1; i++)
            {
                var folderPath = string.Join('/', segments[..(i + 1)]);
                if (!folders.TryGetValue(folderPath, out var folder))
                {
                    folder = new TreeNode(segments[i], folderPath, isFolder: true) { Parent = parent };
                    folders[folderPath] = folder;
                    parent.Children.Add(folder);
                }
                parent = folder;
            }

            parent.Children.Add(new TreeNode(segments[^1], path, isFolder: false) { Parent = parent });
        }

        SortRecursive(root);
        return root;
    }

    private static void SortRecursive(TreeNode node)
    {
        // Folders first, then files, each alphabetically — how Explorer does it, so the
        // tree matches what the user sees when they go looking for the same file on disk.
        node.Children.Sort((a, b) =>
            a.IsFolder != b.IsFolder
                ? (a.IsFolder ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        foreach (var child in node.Children) SortRecursive(child);
    }

    public IEnumerable<TreeNode> DescendantFiles()
    {
        foreach (var child in Children)
        {
            if (!child.IsFolder) yield return child;
            else foreach (var file in child.DescendantFiles()) yield return file;
        }
    }

    public IEnumerable<TreeNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.SelfAndDescendants())
                yield return node;
    }

    /// <summary>
    /// Applies a search filter. A file matches on substring; a folder survives if any
    /// descendant does, and opens itself so the match is on screen without hunting.
    /// </summary>
    public bool ApplyFilter(string filter)
    {
        if (!IsFolder)
        {
            IsVisible = filter.Length == 0 || RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase);
            return IsVisible;
        }

        var anyVisible = false;
        foreach (var child in Children)
            anyVisible |= child.ApplyFilter(filter);

        IsVisible = anyVisible || filter.Length == 0;
        if (filter.Length > 0 && anyVisible) IsExpanded = true;
        RaiseVisibleChildren();
        return IsVisible;
    }

    public void RefreshOverrideMarks(ModProject project, GameInstall game)
    {
        foreach (var node in SelfAndDescendants().Where(n => !n.IsFolder))
        {
            node.IsOverridden = project.HasOverride(node.RelativePath);
            node.IsNewFile = node.IsOverridden && !File.Exists(game.ResolveDataPath(node.RelativePath));
        }

        // A folder is marked when anything under it is, so a change is findable from the root.
        foreach (var node in SelfAndDescendants().Where(n => n.IsFolder))
        {
            node.IsOverridden = node.DescendantFiles().Any(f => f.IsOverridden);
            node.IsNewFile = false;
        }
    }
}
