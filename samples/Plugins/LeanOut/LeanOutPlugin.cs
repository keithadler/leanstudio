using System.Runtime.InteropServices;
using LeanStudio.Plugins;

namespace LeanOut;

/// <summary>
/// Palette commands built on <c>leanout</c>, a clean-room Lean 4 exporter (https://github.com/keithadler/leanout):
/// what the declaration at the caret rests on, a second-kernel check of it, and an export of the whole module.
/// </summary>
public sealed class LeanOutPlugin : ILeanStudioPlugin
{
    private const string DefaultRepoUrl = "https://github.com/keithadler/leanout";

    /// <summary>Where the exporter comes from: <c>LEANOUT_REPO</c> (a URL or a local clone) if set, else the public repository.</summary>
    private static string RepoUrl => Environment.GetEnvironmentVariable("LEANOUT_REPO") is { Length: > 0 } r ? r : DefaultRepoUrl;

    private IPluginHost _host = null!;

    /// <inheritdoc/>
    public string Name => "LeanOut";

    /// <inheritdoc/>
    public void Initialize(IPluginHost host)
    {
        _host = host;
        host.AddCommand("Export: Axioms of the Declaration at the Caret", () => RunGuarded(AxiomsAsync));
        host.AddCommand("Export: Check the Declaration at the Caret with Another Kernel", () => RunGuarded(CheckAsync));
        host.AddCommand("Export: This File's Module to NDJSON", () => RunGuarded(ExportModuleAsync));
        host.AddCommand("Export: Set Up the Exporter for This Project's Lean", () => RunGuarded(SetUpAsync));
    }

    /// <summary>Where Lean Studio keeps its settings, and so where the plugin keeps its exporter builds.</summary>
    internal static string SettingsFolder()
    {
        if (Environment.GetEnvironmentVariable("LEANOUT_HOME") is { Length: > 0 } custom)
        {
            return custom;
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(home, "Library", "Application Support", "LeanStudio");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeanStudio");
        }
        return Path.Combine(home, ".config", "LeanStudio");
    }

    private async Task RunGuarded(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            _host.Log($"LeanOut: {e.Message}");
        }
    }

    /// <summary>The project's toolchain as written in <c>lean-toolchain</c>, or null.</summary>
    private string? Toolchain()
    {
        if (_host.ProjectRoot is not { } root)
        {
            return null;
        }
        string file = Path.Combine(root, "lean-toolchain");
        return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    /// <summary>The exporter is built per toolchain, because it reads <c>.olean</c> files through that Lean.</summary>
    private static string BuildDirFor(string toolchain)
    {
        string tag = new string(toolchain.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());
        return Path.Combine(SettingsFolder(), "leanout", tag);
    }

    private static string ExePathFor(string toolchain)
    {
        string exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "leanout.exe" : "leanout";
        return Path.Combine(BuildDirFor(toolchain), ".lake", "build", "bin", exe);
    }

    /// <summary>Make sure an exporter built with the project's Lean exists, building it on first use. Null if it cannot.</summary>
    private async Task<string?> EnsureExporterAsync(bool announce = true)
    {
        if (_host.ProjectRoot is null)
        {
            _host.Log("LeanOut: open a Lean project first.");
            return null;
        }
        if (Toolchain() is not { Length: > 0 } tc)
        {
            _host.Log("LeanOut: the project has no lean-toolchain file.");
            return null;
        }
        string exe = ExePathFor(tc);
        if (File.Exists(exe))
        {
            return exe;
        }
        string dir = BuildDirFor(tc);
        if (announce)
        {
            _host.Log($"LeanOut: building the exporter for {tc} (once per toolchain) ...");
        }
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            PluginProcessResult clone = await _host.RunAsync("git", ["clone", "--depth", "1", RepoUrl, dir]);
            if (!clone.Success)
            {
                _host.Log("LeanOut: could not fetch leanout: " + clone.Output.Trim());
                return null;
            }
        }
        PluginProcessResult build = await _host.RunAsync("elan", ["run", tc, "lake", "-d", dir, "build", "leanout"]);
        if (!build.Success || !File.Exists(exe))
        {
            _host.Log("LeanOut: building the exporter failed:\n" + Tail(build.Output));
            return null;
        }
        _host.Log("LeanOut: exporter ready.");
        return exe;
    }

    private async Task SetUpAsync()
    {
        if (await EnsureExporterAsync() is { } exe)
        {
            _host.Log("LeanOut: " + exe);
        }
    }

    /// <summary>The module of the file in the editor and the exporter run that works for it.</summary>
    private async Task<PluginProcessResult?> RunExporterAsync(string exe, IPluginDocument doc, IReadOnlyList<string> rootArgs, IReadOnlyList<string> extra)
    {
        PluginProcessResult? last = null;
        foreach (string module in DeclarationLocator.ModuleCandidates(_host.ProjectRoot!, doc.Path))
        {
            var args = new List<string> { "env", exe };
            args.AddRange(extra);
            args.Add(module);
            args.AddRange(rootArgs);
            last = await _host.RunAsync("lake", args);
            if (last.Success)
            {
                return last;
            }
            if (last.Output.Contains("unknown constant", StringComparison.Ordinal))
            {
                return last;
            }
        }
        return last;
    }

    private static string OutFolder(string projectRoot)
    {
        string dir = Path.Combine(projectRoot, ".lake", "leanout");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The names to try for the declaration at the caret: a selection if there is one, else the enclosing declaration.</summary>
    private IReadOnlyList<string>? NamesAtCaret(IPluginDocument doc)
    {
        string sel = doc.Selection.Trim();
        if (sel.Length > 0 && !sel.Any(char.IsWhiteSpace))
        {
            return [sel];
        }
        DeclarationLocator.Located found = DeclarationLocator.At(doc.Text, doc.CaretLine);
        if (found.Candidates.Count == 0)
        {
            _host.Log("LeanOut: " + found.Reason);
            return null;
        }
        return found.Candidates;
    }

    /// <summary>Export the closure of the first candidate name that exists. Returns the name, the file and the exporter's report.</summary>
    private async Task<(string Name, string File, string Report)?> ExportDeclarationAsync(string exe, IPluginDocument doc, IReadOnlyList<string> names)
    {
        string outFile = Path.Combine(OutFolder(_host.ProjectRoot!), "declaration.ndjson");
        PluginProcessResult? last = null;
        foreach (string name in names)
        {
            last = await RunExporterAsync(exe, doc, ["--", name], ["--stats", "--axioms", "-o", outFile]);
            if (last is { Success: true })
            {
                return (name, outFile, last.Output);
            }
        }
        _host.Log("LeanOut: could not export " + string.Join(" or ", names) + ". Build the project first (the exporter reads the last build).\n"
            + Tail(last?.Output ?? ""));
        return null;
    }

    private async Task AxiomsAsync()
    {
        if (_host.ActiveDocument is not { } doc || NamesAtCaret(doc) is not { } names || await EnsureExporterAsync() is not { } exe)
        {
            return;
        }
        if (await ExportDeclarationAsync(exe, doc, names) is not { } r)
        {
            return;
        }
        string[] axioms = r.Report.Split('\n').Where(l => l.StartsWith("axiom ", StringComparison.Ordinal))
            .Select(l => l["axiom ".Length..].Trim()).ToArray();
        int decls = r.Report.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal) && l.Contains(':'))
            .Sum(l => int.TryParse(l[(l.LastIndexOf(':') + 1)..].Trim(), out int n) ? n : 0);
        _host.Log($"LeanOut: {r.Name} rests on {decls} declarations (itself included).");
        if (axioms.Length == 0)
        {
            _host.Log("  No axioms: it is proved from definitions alone.");
            return;
        }
        foreach (string a in axioms)
        {
            string note = a switch
            {
                "sorryAx" => "   <- a sorry, directly or in something it uses",
                "propext" or "Quot.sound" or "Classical.choice" => "   (standard)",
                _ => "   <- an axiom this development or a dependency adds",
            };
            _host.Log($"  axiom {a}{note}");
        }
    }

    private async Task CheckAsync()
    {
        if (_host.ActiveDocument is not { } doc || NamesAtCaret(doc) is not { } names || await EnsureExporterAsync() is not { } exe)
        {
            return;
        }
        if (await ExportDeclarationAsync(exe, doc, names) is not { } r)
        {
            return;
        }
        PluginProcessResult valid = await _host.RunAsync(exe, ["--validate", r.File]);
        if (!valid.Success)
        {
            _host.Log("LeanOut: the export is not well formed:\n" + Tail(valid.Output));
            return;
        }
        string? tenet = FindTenet();
        if (tenet is null)
        {
            _host.Log($"LeanOut: exported {r.Name} and its dependencies to {r.File}; it is well formed. "
                + "Install Tenet (dotnet tool install -g tenet) to have a second kernel check it here.");
            return;
        }
        PluginProcessResult check = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? await _host.RunAsync(tenet, ["check", r.File, "--quiet"])
            : await _host.RunAsync("sh", ["-c", "DOTNET_ROOT=\"${DOTNET_ROOT:-$HOME/.dotnet}\" \"$0\" check \"$1\" --quiet", tenet, r.File]);
        string verdict = check.Success ? "accepted" : "REJECTED";
        _host.Log($"LeanOut: Tenet {verdict} {r.Name} with everything it depends on.");
        _host.Log(Tail(check.Output));
    }

    private async Task ExportModuleAsync()
    {
        if (_host.ActiveDocument is not { } doc || await EnsureExporterAsync() is not { } exe)
        {
            return;
        }
        string module = DeclarationLocator.ModuleCandidates(_host.ProjectRoot!, doc.Path).FirstOrDefault() ?? "";
        string outFile = Path.Combine(OutFolder(_host.ProjectRoot!), "module.ndjson");
        PluginProcessResult? r = null;
        foreach (string candidate in DeclarationLocator.ModuleCandidates(_host.ProjectRoot!, doc.Path))
        {
            r = await _host.RunAsync("lake", ["env", exe, "--stats", "--axioms", "--module", candidate, "-o", outFile, candidate]);
            if (r.Success)
            {
                module = candidate;
                break;
            }
        }
        if (r is not { Success: true })
        {
            _host.Log("LeanOut: could not export the module. Build the project first.\n" + Tail(r?.Output ?? ""));
            return;
        }
        PluginProcessResult valid = await _host.RunAsync(exe, ["--validate", outFile]);
        _host.Log($"LeanOut: {module} exported to {outFile} ({(valid.Success ? "well formed" : "NOT well formed")}).");
        _host.Log(Tail(r.Output));
    }

    private static string? FindTenet()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "tenet.exe" : "tenet";
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                     .Append(Path.Combine(home, ".dotnet", "tools")))
        {
            string candidate = Path.Combine(dir, exe);
            if (dir.Length > 0 && File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string Tail(string output)
    {
        string[] lines = output.Trim().Split('\n');
        return string.Join('\n', lines.Length > 12 ? lines[^12..] : lines);
    }
}
