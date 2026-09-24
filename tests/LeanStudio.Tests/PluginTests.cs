using LeanStudio.Core.Plugins;
using LeanStudio.Plugins;

namespace LeanStudio.Tests;

/// <summary>A plugin in this assembly, for loading it from a copy.</summary>
public sealed class CountingPlugin : ILeanStudioPlugin
{
    /// <inheritdoc/>
    public string Name => "Counting";

    /// <inheritdoc/>
    public void Initialize(IPluginHost host) => host.AddCommand("Counting: Count", () => { host.Log("counted"); return Task.CompletedTask; });
}

/// <summary>A plugin that fails to start.</summary>
public sealed class BrokenPlugin : ILeanStudioPlugin
{
    /// <inheritdoc/>
    public string Name => "Broken";

    /// <inheritdoc/>
    public void Initialize(IPluginHost host) => throw new InvalidOperationException("no config");
}

public sealed class PluginTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leanstudio-plugins-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // an assembly still loaded on Windows; the temp folder is cleaned later
        }
    }

    private sealed class FakeDocument(string path, string text) : IPluginDocument
    {
        public string Path => path;
        public string Text { get; private set; } = text;
        public int CaretLine => 0;
        public int CaretColumn => 0;
        public string Selection => "";
        public void Replace(int offset, int length, string t) => Text = Text[..offset] + t + Text[(offset + length)..];
        public void Insert(string t) => Replace(0, 0, t);
    }

    private sealed class FakeHost : IPluginHost
    {
        public List<(string Title, Func<Task> Run)> Commands { get; } = [];
        public List<string> Lines { get; } = [];
        public List<(string Path, int Line, int Column)> Opened { get; } = [];
        public string AppVersion => "0.0.0";
        public string? ProjectRoot => null;
        public IPluginDocument? ActiveDocument { get; set; }
        public void AddCommand(string title, Func<Task> run) => Commands.Add((title, run));
        public void Log(string line) => Lines.Add(line);
        public Task OpenFileAsync(string path, int line = 0, int column = 0)
        {
            Opened.Add((path, line, column));
            return Task.CompletedTask;
        }
        public IReadOnlyList<PluginMessage> MessagesOf(string path) => [];
        public Task<PluginProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PluginProcessResult(0, ""));
        public Task<PluginProcessResult> CheckLeanAsync(string code, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PluginProcessResult(0, ""));
        public event Action<string>? FileOpened;
        public event Action<string>? FileSaved;
        public void Save(string path) => FileSaved?.Invoke(path);
        public void Open(string path) => FileOpened?.Invoke(path);
    }

    /// <summary>The sample plugin's build output (built before the tests, in the same configuration).</summary>
    private static string HelloLeanOutput()
    {
        string config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name; // bin/<config>/net10.0/
        return Lean.Sample("Plugins", "HelloLean", "bin", config, "net10.0");
    }

    [Fact]
    public void FindsPluginsDirectlyInTheFolderAndInFoldersOfTheirOwn()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Nested"));
        Directory.CreateDirectory(Path.Combine(_folder, "Empty"));
        File.WriteAllText(Path.Combine(_folder, "Flat.dll"), "");
        File.WriteAllText(Path.Combine(_folder, "LeanStudio.Plugins.dll"), "");
        File.WriteAllText(Path.Combine(_folder, "Nested", "Nested.dll"), "");
        File.WriteAllText(Path.Combine(_folder, "Nested", "Dependency.dll"), "");
        Assert.Equal(["Flat.dll", "Nested.dll"], PluginLoader.Candidates(_folder).Select(Path.GetFileName));
        Assert.Empty(PluginLoader.Candidates(Path.Combine(_folder, "missing")));
    }

    [Fact]
    public async Task LoadsAPluginBuiltOnItsOwnAndRunsItsCommands()
    {
        string target = Path.Combine(_folder, "HelloLean");
        Directory.CreateDirectory(target);
        foreach (string f in Directory.EnumerateFiles(HelloLeanOutput()))
        {
            File.Copy(f, Path.Combine(target, Path.GetFileName(f)));
        }
        Assert.False(File.Exists(Path.Combine(target, "LeanStudio.Plugins.dll")), "the plugin shouldn't carry its own copy of the API");
        var host = new FakeHost();
        var (plugins, problems) = PluginLoader.Load(_folder, host);
        Assert.Empty(problems);
        Assert.Equal("Hello Lean", Assert.Single(plugins).Plugin.Name);
        Assert.Contains(host.Commands, c => c.Title == "Hello Lean: #check the Selection");

        string file = Path.Combine(_folder, "Basic.lean");
        host.ActiveDocument = new FakeDocument(file, "theorem a : True := by\n  trivial\n\ntheorem b : 1 = 1 := by\n  sorry\n");
        await host.Commands.Single(c => c.Title == "Hello Lean: Sorries in This File").Run();
        Assert.Equal("Hello Lean: 1 sorry in Basic.lean", host.Lines[0]);
        Assert.Equal((file, 4, 2), Assert.Single(host.Opened));

        host.Save(file);
        Assert.Equal("Hello Lean: Basic.lean saved, 1 sorry left.", host.Lines[^1]);
    }

    [Fact]
    public void SaysWhyAPluginDidNotLoadAndLoadsTheRest()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "NotDotNet.dll"), "just text");
        string self = typeof(PluginTests).Assembly.Location;
        File.Copy(self, Path.Combine(_folder, Path.GetFileName(self)));
        var host = new FakeHost();
        var (plugins, problems) = PluginLoader.Load(_folder, host);
        Assert.Equal("Counting", Assert.Single(plugins).Plugin.Name);
        Assert.Contains(problems, p => p.StartsWith("NotDotNet.dll: not a .NET assembly", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("BrokenPlugin failed to start: InvalidOperationException: no config", StringComparison.Ordinal));
        // Loaded from its own context, the plugin is still the same ILeanStudioPlugin as Lean Studio's.
        Assert.NotSame(typeof(CountingPlugin), plugins[0].Plugin.GetType());
        Assert.Equal("Counting: Count", Assert.Single(host.Commands).Title);
    }
}
