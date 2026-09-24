using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Workflow;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

/// <summary>
/// Lean's C FFI: clangd for the project's C files (with Lean's headers on its include path), the bindings
/// checked against the C code in Problems, go to definition between an <c>@[extern]</c> and its C function
/// both ways, and C stubs with the signature Lean expects.
/// </summary>
public sealed partial class MainViewModel
{
    private CLanguageServer? _clangd;
    private string? _clangdRoot;
    private Task? _clangdStarting;
    private readonly Dictionary<DocumentViewModel, CancellationTokenSource> _pendingC = new();
    private IReadOnlyList<FfiProblem> _ffiProblems = [];
    private IReadOnlyList<ExternBinding> _externs = [];
    private IReadOnlyList<CFunctionSite> _cFunctions = [];
    private CancellationTokenSource? _ffiCts;

    /// <summary>clangd, once a C file has been opened and if it is installed.</summary>
    public CLanguageServer? CServer => _clangd;

    private string RootFor(DocumentViewModel d) => Project?.Root ?? Path.GetDirectoryName(d.Path)!;

    private async Task EnsureClangdAsync(DocumentViewModel d)
    {
        string root = RootFor(d);
        if (_clangd is { IsRunning: true } && _clangdRoot == root)
        {
            return;
        }
        if (_clangdStarting is not null)
        {
            await _clangdStarting;
            return;
        }
        string? exe = CLanguageServer.Find();
        if (exe is null)
        {
            Log("C files: install clangd for errors, completion and go to definition in them (it comes with LLVM, and with Xcode's command line tools on macOS).");
            return;
        }
        if (_clangd is not null)
        {
            await _clangd.DisposeAsync();
            _clangd = null;
        }
        var server = new CLanguageServer(exe, root, Ffi.CompileFlags(Project ?? new LeanProject(root)));
        server.DiagnosticsPublished += (uri, diags) => Dispatcher.UIThread.Post(() =>
        {
            if (Documents.FirstOrDefault(x => x.Uri == uri) is DocumentViewModel doc)
            {
                doc.Diagnostics = diags;
                UpdateProblems();
            }
        });
        _clangdStarting = server.StartAsync();
        try
        {
            await _clangdStarting;
            _clangd = server;
            _clangdRoot = root;
            Log($"clangd started for the C files in {root}, with Lean's headers on the include path.");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or JsonRpcException or System.ComponentModel.Win32Exception)
        {
            Log("clangd could not start: " + e.Message);
            await server.DisposeAsync();
        }
        finally
        {
            _clangdStarting = null;
        }
    }

    /// <summary>A C file was opened: give it to clangd.</summary>
    private async Task COpenedAsync(DocumentViewModel d)
    {
        await EnsureClangdAsync(d);
        if (_clangd is { IsRunning: true } c && !c.IsOpen(d.Uri))
        {
            await c.OpenAsync(d.Uri, d.Document.Text);
        }
    }

    private void CEdited(DocumentViewModel d)
    {
        if (_pendingC.TryGetValue(d, out CancellationTokenSource? old))
        {
            old.Cancel();
        }
        var cts = new CancellationTokenSource();
        _pendingC[d] = cts;
        _ = SendCChangeAsync(d, cts.Token);
    }

    private async Task SendCChangeAsync(DocumentViewModel d, CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
            if (_clangd is { IsRunning: true } c)
            {
                await (c.IsOpen(d.Uri) ? c.ChangeAsync(d.Uri, d.Document.Text) : c.OpenAsync(d.Uri, d.Document.Text));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
        {
        }
    }

    private async Task CClosedAsync(DocumentViewModel d)
    {
        if (_clangd is { IsRunning: true } c && c.IsOpen(d.Uri))
        {
            try
            {
                await c.CloseAsync(d.Uri);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
            }
        }
    }

    private async Task StopClangdAsync()
    {
        if (_clangd is not null)
        {
            await _clangd.DisposeAsync();
            _clangd = null;
            _clangdRoot = null;
        }
    }

    // ---- bindings checked against the C files ----

    /// <summary>Check every @[extern] against the C files, a moment after asked (saves come in bursts).</summary>
    public void ScheduleFfiCheck()
    {
        if (Project is not LeanProject p)
        {
            return;
        }
        _ffiCts?.Cancel();
        var cts = new CancellationTokenSource();
        _ffiCts = cts;
        // Open files count as they are in the editor.
        var open = Documents.Where(d => d.IsLean || d.IsC).ToDictionary(d => d.Path, d => d.Document.Text, StringComparer.Ordinal);
        _ = Task.Run(async () =>
        {
            await Task.Delay(400, cts.Token);
            var result = Ffi.Check(p.Root, f => open.TryGetValue(f, out string? t) ? t : null);
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }
                (_externs, _cFunctions, _ffiProblems) = result;
                UpdateProblems();
            });
        }, cts.Token).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Awaitable form, for scripts and tests.</summary>
    public async Task<IReadOnlyList<FfiProblem>> CheckFfiNowAsync()
    {
        if (Project is not LeanProject p)
        {
            return [];
        }
        var open = Documents.Where(d => d.IsLean || d.IsC).ToDictionary(d => d.Path, d => d.Document.Text, StringComparer.Ordinal);
        (_externs, _cFunctions, _ffiProblems) = await Task.Run(() => Ffi.Check(p.Root, f => open.TryGetValue(f, out string? t) ? t : null));
        UpdateProblems();
        return _ffiProblems;
    }

    private IEnumerable<ProblemItem> FfiProblems() =>
        _ffiProblems.Select(f => new ProblemItem(Documents.FirstOrDefault(d => d.Path == f.Path), f.Path, new Diagnostic(
            new Lsp.Range(new Position(f.Line, f.Column), new Position(f.Line, f.Column)),
            f.IsError ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
            f.Message, "ffi")));

    // ---- go to definition across the boundary ----

    /// <summary>
    /// From an @[extern] to its C function, or from a C function to the Lean declaration it implements.
    /// Returns whether it went somewhere.
    /// </summary>
    private async Task<bool> GoAcrossFfiAsync(DocumentViewModel d)
    {
        string? cName = d.IsLean ? Ffi.CNameAt(d.Lines(), d.CaretLine) : null;
        if (Project is null || (d.IsLean && cName is null))
        {
            return false;
        }
        if (_externs.Count == 0 && _cFunctions.Count == 0)
        {
            await CheckFfiNowAsync();
        }
        if (cName is not null)
        {
            CFunctionSite? site = _cFunctions.Where(f => f.Name == cName).OrderBy(f => f.IsDefinition ? 0 : 1).FirstOrDefault();
            if (site is null)
            {
                await CheckFfiNowAsync();
                site = _cFunctions.Where(f => f.Name == cName).OrderBy(f => f.IsDefinition ? 0 : 1).FirstOrDefault();
            }
            if (site is not null)
            {
                await OpenFileAsync(site.Path, site.Line, 0);
                return true;
            }
            Log($"{cName} is not in the project's C files" + (cName.StartsWith("lean_", StringComparison.Ordinal) ? " (it is part of Lean's runtime)." : ". Lean ▸ Write C Stub writes one."));
            return true;
        }
        if (d.IsC && WordAtCaret(d) is string word && _externs.FirstOrDefault(e => e.CName == word) is ExternBinding b)
        {
            await OpenFileAsync(b.Path, b.Line, b.Column);
            return true;
        }
        return false;
    }

    private static string? WordAtCaret(DocumentViewModel d)
    {
        string[] lines = d.Lines();
        if (d.CaretLine >= lines.Length)
        {
            return null;
        }
        string line = lines[d.CaretLine];
        int s = Math.Min(d.CaretColumn, line.Length), e = s;
        while (s > 0 && (char.IsLetterOrDigit(line[s - 1]) || line[s - 1] == '_'))
        {
            s--;
        }
        while (e < line.Length && (char.IsLetterOrDigit(line[e]) || line[e] == '_'))
        {
            e++;
        }
        return e > s ? line[s..e] : null;
    }

    // ---- stubs ----

    /// <summary>
    /// The C file new stubs go in: the first of the project's C files that includes lean.h, or c/ffi.c, made
    /// with the include when there is none.
    /// </summary>
    private async Task<string> StubFileAsync(string root)
    {
        foreach (string f in Ffi.CFiles(root).Where(f => f.EndsWith(".c", StringComparison.OrdinalIgnoreCase)))
        {
            if ((await File.ReadAllTextAsync(f)).Contains("lean/lean.h", StringComparison.Ordinal))
            {
                return f;
            }
        }
        string file = Path.Combine(root, "c", "ffi.c");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!File.Exists(file))
        {
            await File.WriteAllTextAsync(file, "#include <lean/lean.h>\n");
            Log($"Made {Path.GetRelativePath(root, file)} for the C side. To build it with the project, add it to the lakefile (an extern_lib target in lakefile.lean; see Lean's FFI documentation).");
        }
        return file;
    }

    /// <summary>Append a binding's stub to the project's C file and open it there.</summary>
    public async Task<string?> WriteStubAsync(ExternBinding b)
    {
        string root = Project?.Root ?? Path.GetDirectoryName(b.Path)!;
        string file = await StubFileAsync(root);
        DocumentViewModel? open = Documents.FirstOrDefault(d => d.Path == file);
        string text = open?.Document.Text ?? await File.ReadAllTextAsync(file);
        string stub = Ffi.Stub(b);
        int line = text.Count(c => c == '\n') + (text.EndsWith('\n') ? 1 : 2);
        if (open is not null)
        {
            open.Document.Insert(open.Document.TextLength, (text.EndsWith('\n') ? "\n" : "\n\n") + stub);
            await SaveDocumentAsync(open);
        }
        else
        {
            await File.WriteAllTextAsync(file, text + (text.EndsWith('\n') ? "\n" : "\n\n") + stub);
        }
        await OpenFileAsync(file, line, 0);
        Log($"Wrote a C stub for {b.LeanName} ({b.CName}) in {Path.GetRelativePath(root, file)}.");
        ScheduleFfiCheck();
        return file;
    }

    [RelayCommand]
    private async Task WriteCStubAsync()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        ExternBinding? b = Ffi.ExternsIn(d.Path, d.Lines())
            .Where(x => x.Line <= d.CaretLine + 1).OrderByDescending(x => x.Line).FirstOrDefault();
        if (b is null)
        {
            Log("Write C stub: put the cursor on an @[extern \"…\"] declaration.");
            return;
        }
        await CheckFfiNowAsync();
        if (_cFunctions.FirstOrDefault(f => f.Name == b.CName && f.IsDefinition) is CFunctionSite site)
        {
            Log($"{b.CName} is already written; opening it.");
            await OpenFileAsync(site.Path, site.Line, 0);
            return;
        }
        await WriteStubAsync(b);
    }

    /// <summary>A new binding: the Lean declaration at the cursor, and its C stub.</summary>
    [RelayCommand]
    private async Task NewFfiBindingAsync()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            Log("New C binding: open the Lean file it belongs in first.");
            return;
        }
        string? spec = await _dialogs.PromptAsync("New C binding",
            "The Lean name and type of a function to write in C, for example:  addU32 (a b : UInt32) : UInt32   or   greet (name : @& String) : IO Unit",
            "myFunction (x : UInt64) : UInt64");
        if (string.IsNullOrWhiteSpace(spec))
        {
            return;
        }
        spec = spec.Trim();
        int space = spec.IndexOfAny([' ', ':']);
        if (space <= 0)
        {
            Log("New C binding: write the name, then its type.");
            return;
        }
        string leanName = spec[..space];
        string signature = spec[space..].Trim();
        string suggested = System.Text.RegularExpressions.Regex.Replace((Project?.Name ?? "ffi") + "_" + leanName, @"[^\w]", "_").ToLowerInvariant();
        string? cName = await _dialogs.PromptAsync("New C binding", "The C function's name:", suggested);
        if (string.IsNullOrWhiteSpace(cName) || !System.Text.RegularExpressions.Regex.IsMatch(cName.Trim(), @"^[A-Za-z_]\w*$"))
        {
            return;
        }
        cName = cName.Trim();
        string decl = Ffi.LeanDeclaration(leanName, cName, signature);
        int at = d.Document.GetLineByNumber(Math.Clamp(d.CaretLine + 1, 1, d.Document.LineCount)).Offset;
        d.Document.Insert(at, decl + "\n");
        await WriteStubAsync(new ExternBinding(leanName, cName, d.Path, d.CaretLine, 0, signature));
    }
}
