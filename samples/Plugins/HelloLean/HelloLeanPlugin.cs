using System.Text.RegularExpressions;
using LeanStudio.Plugins;

namespace HelloLean;

/// <summary>An example plugin: two commands, and a note in Output each time a Lean file is saved.</summary>
public sealed partial class HelloLeanPlugin : ILeanStudioPlugin
{
    private IPluginHost _host = null!;

    /// <inheritdoc/>
    public string Name => "Hello Lean";

    /// <inheritdoc/>
    public void Initialize(IPluginHost host)
    {
        _host = host;
        host.AddCommand("Hello Lean: Sorries in This File", SorriesAsync);
        host.AddCommand("Hello Lean: #check the Selection", CheckSelectionAsync);
        host.FileSaved += path =>
        {
            if (path.EndsWith(".lean", StringComparison.Ordinal) && host.ActiveDocument is { } d && d.Path == path)
            {
                int n = Sorry().Matches(d.Text).Count;
                host.Log($"Hello Lean: {Path.GetFileName(path)} saved, {(n == 0 ? "no sorry left" : n == 1 ? "1 sorry left" : $"{n} sorries left")}.");
            }
        };
    }

    [GeneratedRegex(@"\bsorry\b")]
    private static partial Regex Sorry();

    /// <summary>List the file's sorries in Output and go to the first.</summary>
    private async Task SorriesAsync()
    {
        if (_host.ActiveDocument is not { } d)
        {
            _host.Log("Hello Lean: open a Lean file first.");
            return;
        }
        string[] lines = d.Text.Split('\n');
        var found = lines.Select((l, i) => (Line: i, Column: Sorry().Match(l) is { Success: true } m ? m.Index : -1))
                         .Where(x => x.Column >= 0).ToList();
        _host.Log($"Hello Lean: {found.Count} sorr{(found.Count == 1 ? "y" : "ies")} in {Path.GetFileName(d.Path)}");
        foreach ((int line, int column) in found)
        {
            _host.Log($"  line {line + 1}: {lines[line].Trim()}");
        }
        if (found.Count > 0)
        {
            await _host.OpenFileAsync(d.Path, found[0].Line, found[0].Column);
        }
    }

    /// <summary>#check the selected term with the file's imports, and print what Lean says.</summary>
    private async Task CheckSelectionAsync()
    {
        if (_host.ActiveDocument is not { } d || d.Selection.Trim().Length == 0)
        {
            _host.Log("Hello Lean: select a term to #check.");
            return;
        }
        string imports = string.Join('\n', d.Text.Split('\n').Where(l => l.StartsWith("import ", StringComparison.Ordinal)));
        PluginProcessResult r = await _host.CheckLeanAsync(imports + "\n\n#check " + d.Selection.Trim() + "\n");
        _host.Log("Hello Lean: " + r.Output.Trim());
    }
}
