namespace LeanStudio.Plugins;

/// <summary>
/// A Lean Studio plugin: a .NET class library that references LeanStudio.Plugins and has a public class with a
/// parameterless constructor implementing this. Lean Studio loads plugins at start from the <c>plugins</c> folder
/// in its settings folder: either <c>plugins/Name.dll</c>, or <c>plugins/Name/Name.dll</c> with the assemblies it
/// depends on beside it (what <c>dotnet build -o plugins/Name</c> writes). Each plugin gets its own load context,
/// so plugins can depend on different versions of the same library.
/// </summary>
/// <remarks>
/// Plugins run with Lean Studio's permissions: install only plugins you trust, as you would an editor extension.
/// Everything a plugin is given runs on the UI thread, and its commands are called there; do slow work with
/// <c>await</c> or on a background thread, and come back before touching a document.
/// </remarks>
public interface ILeanStudioPlugin
{
    /// <summary>The plugin's name, as the Output panel reports it when it loads.</summary>
    string Name { get; }

    /// <summary>
    /// Called once, at start, on the UI thread: add commands and subscribe to events here. An exception is
    /// reported in the Output panel and the plugin is left out.
    /// </summary>
    /// <param name="host">Lean Studio, as a plugin sees it.</param>
    void Initialize(IPluginHost host);
}

/// <summary>Lean Studio, as a plugin sees it.</summary>
public interface IPluginHost
{
    /// <summary>The version of Lean Studio (<c>0.8.0</c>).</summary>
    string AppVersion { get; }

    /// <summary>The open project's folder, or null when no project is open.</summary>
    string? ProjectRoot { get; }

    /// <summary>The file in the editor, or null when none is open.</summary>
    IPluginDocument? ActiveDocument { get; }

    /// <summary>
    /// Add a command to the command palette. Name it as the palette names things, <c>Area: Action</c> (such as
    /// <c>Hello Lean: Check Selection</c>); like any palette command it can be bound to a key in keybindings.json.
    /// </summary>
    /// <param name="title">What the palette shows.</param>
    /// <param name="run">What it does.</param>
    void AddCommand(string title, Func<Task> run);

    /// <summary>Append a line to the Output panel. Safe from any thread.</summary>
    /// <param name="line">The line, without a newline.</param>
    void Log(string line);

    /// <summary>Open a file in the editor, with the caret at a 0-based line and column.</summary>
    /// <param name="path">The file.</param>
    /// <param name="line">The 0-based line.</param>
    /// <param name="column">The 0-based column.</param>
    Task OpenFileAsync(string path, int line = 0, int column = 0);

    /// <summary>What Lean says about a file open in the editor: its errors, warnings and information messages.</summary>
    /// <param name="path">The file.</param>
    IReadOnlyList<PluginMessage> MessagesOf(string path);

    /// <summary>Run a program in the project's folder (or the current folder without a project) and collect what it prints.</summary>
    /// <param name="program">The program: a path, or a name found on the PATH (<c>lake</c> and <c>lean</c> are found through elan).</param>
    /// <param name="arguments">Its arguments, one string each.</param>
    /// <param name="cancellationToken">Stops the program.</param>
    Task<PluginProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check some Lean with the project's Lean and dependencies (<c>lake env lean</c>), as a file of its own:
    /// start it with the imports it needs. Returns what Lean printed, messages first.
    /// </summary>
    /// <param name="code">The Lean source.</param>
    /// <param name="cancellationToken">Stops Lean.</param>
    Task<PluginProcessResult> CheckLeanAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>A file was opened in the editor (its full path).</summary>
    event Action<string>? FileOpened;

    /// <summary>A file was saved (its full path).</summary>
    event Action<string>? FileSaved;
}

/// <summary>A file in the editor. Use it on the UI thread only.</summary>
public interface IPluginDocument
{
    /// <summary>The file's full path.</summary>
    string Path { get; }

    /// <summary>The text in the editor, saved or not.</summary>
    string Text { get; }

    /// <summary>The caret's 0-based line.</summary>
    int CaretLine { get; }

    /// <summary>The caret's 0-based column.</summary>
    int CaretColumn { get; }

    /// <summary>The selected text, or empty.</summary>
    string Selection { get; }

    /// <summary>Replace <paramref name="length"/> characters from <paramref name="offset"/> with <paramref name="text"/>, as one undoable edit.</summary>
    /// <param name="offset">Where, as an index into <see cref="Text"/>.</param>
    /// <param name="length">How many characters to replace; 0 to insert.</param>
    /// <param name="text">The new text.</param>
    void Replace(int offset, int length, string text);

    /// <summary>Insert text at the caret.</summary>
    /// <param name="text">The text.</param>
    void Insert(string text);
}

/// <summary>A message from Lean about a file.</summary>
/// <param name="Line">The 0-based line.</param>
/// <param name="Column">The 0-based column.</param>
/// <param name="Severity"><c>error</c>, <c>warning</c> or <c>information</c>.</param>
/// <param name="Text">The message.</param>
public sealed record PluginMessage(int Line, int Column, string Severity, string Text);

/// <summary>How a program ran.</summary>
/// <param name="ExitCode">Its exit code: 0 for success.</param>
/// <param name="Output">What it printed, standard output and error together.</param>
public sealed record PluginProcessResult(int ExitCode, string Output)
{
    /// <summary>Whether it exited with 0.</summary>
    public bool Success => ExitCode == 0;
}
