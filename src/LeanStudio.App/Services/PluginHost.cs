using LeanStudio.App.ViewModels;
using LeanStudio.Core.Plugins;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;
using LeanStudio.Core.Workflow;
using LeanStudio.Lsp;
using LeanStudio.Plugins;

namespace LeanStudio.App.Services;

/// <summary>
/// Lean Studio as its compiled plugins see it (<see cref="IPluginHost"/>): the editor, the Output panel, Lean,
/// and the commands they add to the palette.
/// </summary>
public sealed class PluginHost : IPluginHost
{
    private readonly MainViewModel _vm;
    private readonly List<(string Title, Func<Task> Run)> _commands = [];

    /// <summary>A host over the app's main view model.</summary>
    /// <param name="vm">The main view model.</param>
    public PluginHost(MainViewModel vm)
    {
        _vm = vm;
        vm.FileOpened += p => FileOpened?.Invoke(p);
        vm.FileSaved += p => FileSaved?.Invoke(p);
    }

    /// <summary>Where plugins are loaded from: <c>plugins</c> in the settings folder.</summary>
    public static string Folder => Path.Combine(Settings.Directory, PluginLoader.FolderName);

    /// <summary>The plugins that loaded.</summary>
    public IReadOnlyList<LoadedPlugin> Plugins { get; private set; } = [];

    /// <summary>The commands plugins added, in the order they added them.</summary>
    public IReadOnlyList<(string Title, Func<Task> Run)> Commands => _commands;

    /// <summary>Load the plugins in <paramref name="folder"/> (by default <see cref="Folder"/>), reporting in Output.</summary>
    public void Load(string? folder = null)
    {
        folder ??= Folder;
        var (plugins, problems) = PluginLoader.Load(folder, this);
        Plugins = [.. Plugins, .. plugins];
        foreach (LoadedPlugin p in plugins)
        {
            _vm.Log($"Plugin: {p.Plugin.Name} ({Path.GetFileName(p.Path)})");
        }
        foreach (string problem in problems)
        {
            _vm.Log("Plugin not loaded: " + problem);
        }
    }

    /// <inheritdoc/>
    public string AppVersion => typeof(PluginHost).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <inheritdoc/>
    public string? ProjectRoot => _vm.Project?.Root;

    /// <inheritdoc/>
    public IPluginDocument? ActiveDocument => _vm.ActiveDocument is DocumentViewModel d ? new Document(d, _vm) : null;

    /// <inheritdoc/>
    public void AddCommand(string title, Func<Task> run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(run);
        _commands.Add((title.Trim(), async () =>
        {
            try
            {
                await run();
            }
            catch (Exception e)
            {
                _vm.Log($"{title}: {e.GetType().Name}: {e.Message}");
            }
        }));
    }

    /// <inheritdoc/>
    public void Log(string line) => _vm.Log(line);

    /// <inheritdoc/>
    public Task OpenFileAsync(string path, int line = 0, int column = 0) => _vm.OpenFileAsync(path, line, column);

    /// <inheritdoc/>
    public IReadOnlyList<PluginMessage> MessagesOf(string path)
    {
        string full = Path.GetFullPath(path);
        DocumentViewModel? d = _vm.Documents.FirstOrDefault(x => string.Equals(x.Path, full, StringComparison.Ordinal));
        return d is null ? [] : d.Diagnostics.Where(m => m.IsSilent != true).Select(m => new PluginMessage(m.Range.Start.Line, m.Range.Start.Character,
            m.Severity switch { DiagnosticSeverity.Error => "error", DiagnosticSeverity.Warning => "warning", _ => "information" }, m.Message)).ToList();
    }

    /// <inheritdoc/>
    public async Task<PluginProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        string exe = program is "lake" or "lean" or "elan" ? Elan.FindExecutable(program) ?? program : program;
        ProcessResult r = await ProcessRunner.RunAsync(exe, arguments, _vm.Project?.Root, ct: cancellationToken).ConfigureAwait(false);
        return new PluginProcessResult(r.ExitCode, r.Output);
    }

    /// <inheritdoc/>
    public async Task<PluginProcessResult> CheckLeanAsync(string code, CancellationToken cancellationToken = default)
    {
        if (_vm.Project is not LeanProject p)
        {
            return new PluginProcessResult(-1, "Open a project first: Lean checks the code with the project's dependencies.");
        }
        string file = await LeanCli.MirrorAsync(p, Path.Combine(p.Root, "PluginCheck.lean"), code, "plugin", cancellationToken).ConfigureAwait(false);
        ProcessResult r = await LeanCli.RunAsync(p, [file], cancellationToken).ConfigureAwait(false);
        return new PluginProcessResult(r.ExitCode, r.Output.Replace(file, "PluginCheck.lean", StringComparison.Ordinal));
    }

    /// <inheritdoc/>
    public event Action<string>? FileOpened;

    /// <inheritdoc/>
    public event Action<string>? FileSaved;

    private sealed class Document(DocumentViewModel d, MainViewModel vm) : IPluginDocument
    {
        public string Path => d.Path;

        public string Text => d.Document.Text;

        public int CaretLine => d.CaretLine;

        public int CaretColumn => d.CaretColumn;

        public string Selection => vm.ActiveDocument == d ? vm.SelectionProvider?.Invoke() ?? "" : "";

        public void Replace(int offset, int length, string text) => d.Document.Replace(offset, length, text);

        public void Insert(string text)
        {
            int line = Math.Clamp(d.CaretLine + 1, 1, d.Document.LineCount);
            int column = Math.Clamp(d.CaretColumn + 1, 1, d.Document.GetLineByNumber(line).Length + 1);
            d.Document.Insert(d.Document.GetOffset(line, column), text);
        }
    }
}
