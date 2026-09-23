using CommunityToolkit.Mvvm.ComponentModel;

namespace LeanStudio.App.ViewModels;

/// <summary>A file or folder in the explorer. Folders list their children the first time they are expanded.</summary>
public sealed partial class FileNode : ObservableObject
{
    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal) { ".git", ".lake", "build", "node_modules", ".DS_Store", "bin", "obj" };
    private bool _loaded;

    public FileNode(string path, bool isDirectory)
    {
        Path = path;
        IsDirectory = isDirectory;
        if (isDirectory)
        {
            // A placeholder so the expander shows before the children are read.
            Children.Add(new FileNode("", false));
        }
    }

    public string Path { get; }
    public bool IsDirectory { get; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
    /// <summary>Which icon the tree shows: a folder, a Lean file, or any other file.</summary>
    public string IconKey => IsDirectory ? "folder" : Path.EndsWith(".lean", StringComparison.OrdinalIgnoreCase) ? "lean" : Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "doc" : "file";
    public bool IsLean => !IsDirectory && Path.EndsWith(".lean", StringComparison.OrdinalIgnoreCase);
    public bool IsPlain => !IsDirectory && !IsLean;
    public ObservableList<FileNode> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
        {
            Load();
        }
    }

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
                .Select(d => new FileNode(d, true)));
            items.AddRange(Directory.EnumerateFiles(Path)
                .Where(f => !Hidden.Contains(System.IO.Path.GetFileName(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => new FileNode(f, false)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        Children.Reset(items);
    }

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
