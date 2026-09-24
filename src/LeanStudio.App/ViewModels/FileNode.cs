using CommunityToolkit.Mvvm.ComponentModel;

namespace LeanStudio.App.ViewModels;

/// <summary>A file or folder in the explorer. Folders list their children the first time they are expanded.</summary>
/// <remarks>
/// Backs the Files panel's tree; <see cref="MainViewModel.Files"/> holds the project root's children. Hidden folders
/// and noise such as <c>.lake</c>, <c>build</c>, <c>bin</c>, <c>obj</c> and <c>node_modules</c> are left out. A
/// folder is read from disk, on the calling (UI) thread, when it is loaded; <see cref="MainViewModel.RefreshFiles"/>
/// rebuilds the tree after changes.
/// </remarks>
public sealed partial class FileNode : ObservableObject
{
    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal) { ".git", ".lake", "build", "node_modules", ".DS_Store", "bin", "obj" };
    private bool _loaded;
    private readonly Func<string, string>? _markOf;

    /// <summary>
    /// A node for a path. A folder gets a placeholder child until it is loaded, so the tree shows it can be expanded.
    /// </summary>
    /// <param name="path">The file or folder's full path.</param>
    /// <param name="isDirectory">It is a folder.</param>
    /// <param name="markOf">The build's mark for a path (see <see cref="BuildMark"/>), passed on to children.</param>
    public FileNode(string path, bool isDirectory, Func<string, string>? markOf = null)
    {
        Path = path;
        IsDirectory = isDirectory;
        _markOf = markOf;
        _buildMark = path.Length > 0 ? markOf?.Invoke(path) ?? "" : "";
        if (isDirectory)
        {
            // A placeholder so the expander shows before the children are read.
            Children.Add(new FileNode("", false));
        }
    }

    /// <summary>The file or folder's full path.</summary>
    public string Path { get; }
    /// <summary>It is a folder.</summary>
    public bool IsDirectory { get; }
    /// <summary>The name shown in the tree.</summary>
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
    /// <summary>Which icon the tree shows: a folder, a Lean file, or any other file.</summary>
    public string IconKey => IsDirectory ? "folder" : Path.EndsWith(".lean", StringComparison.OrdinalIgnoreCase) ? "lean" : Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "doc" : "file";
    /// <summary>A <c>.lean</c> file.</summary>
    public bool IsLean => !IsDirectory && Path.EndsWith(".lean", StringComparison.OrdinalIgnoreCase);
    /// <summary>A file that is not a Lean file.</summary>
    public bool IsPlain => !IsDirectory && !IsLean;
    /// <summary>A folder's folders then files, each sorted by name; empty for a file.</summary>
    public ObservableList<FileNode> Children { get; } = new();

    /// <summary>
    /// What the last build says about it: ✓ built, ⋯ being compiled, ◐ uses sorry, ✗ has errors; empty when it
    /// says nothing. A folder shows the worst of ⋯, ◐ and ✗ inside it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuildMarkTip), nameof(IsMarkOk), nameof(IsMarkWarn), nameof(IsMarkBad))]
    private string _buildMark;

    /// <summary>What <see cref="BuildMark"/> means, for its tooltip.</summary>
    public string BuildMarkTip => BuildMark switch
    {
        "✓" => "Built by the last build",
        "⋯" => "Being compiled now",
        "◐" => "Built, but something in it uses sorry",
        "✗" => "The build found errors " + (IsDirectory ? "in here" : "in this file"),
        _ => "",
    };

    /// <summary>The mark is ✓.</summary>
    public bool IsMarkOk => BuildMark == "✓";

    /// <summary>The mark is ⋯ or ◐.</summary>
    public bool IsMarkWarn => BuildMark is "⋯" or "◐";

    /// <summary>The mark is ✗.</summary>
    public bool IsMarkBad => BuildMark == "✗";

    /// <summary>Read the build's marks again, for this node and the loaded ones under it.</summary>
    public void RefreshMarks()
    {
        if (Path.Length == 0 || _markOf is null)
        {
            return;
        }
        BuildMark = _markOf(Path);
        if (_loaded)
        {
            foreach (FileNode c in Children)
            {
                c.RefreshMarks();
            }
        }
    }

    /// <summary>The folder is open in the tree. Opening it the first time reads its children from disk.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
        {
            Load();
        }
    }

    /// <summary>
    /// Read a folder's children from disk, replacing the placeholder or the old list; children are not expanded. A folder
    /// that cannot be read is left empty.
    /// </summary>
    public void Load()
    {
        _loaded = true;
        if (!IsDirectory)
        {
            return;
        }
        var items = new List<FileNode>();
        try
        {
            items.AddRange(Directory.EnumerateDirectories(Path)
                .Where(d => !Hidden.Contains(System.IO.Path.GetFileName(d)) && !System.IO.Path.GetFileName(d).StartsWith('.'))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => new FileNode(d, true, _markOf)));
            items.AddRange(Directory.EnumerateFiles(Path)
                .Where(f => !Hidden.Contains(System.IO.Path.GetFileName(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => new FileNode(f, false, _markOf)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        Children.Reset(items);
    }

    /// <summary>
    /// Read a loaded folder's children again, keeping the ones that were expanded expanded. An unloaded folder is left
    /// as it is.
    /// </summary>
    public void Refresh()
    {
        if (_loaded)
        {
            var expanded = Children.Where(c => c.IsExpanded).Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
            Load();
            foreach (FileNode c in Children.Where(c => expanded.Contains(c.Path)))
            {
                c.IsExpanded = true;
                c.Refresh();
            }
        }
    }
}
