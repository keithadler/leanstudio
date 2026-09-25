using LeanStudio.Core.Processes;

namespace LeanStudio.Core.Ai;

/// <summary>
/// Where API keys are kept: the macOS Keychain, the Secret Service on Linux (through <c>secret-tool</c>), and
/// otherwise a file only the person can read, in Lean Studio's settings folder. Keys are never written to
/// settings.json. An environment variable (<c>ANTHROPIC_API_KEY</c>, <c>OPENAI_API_KEY</c>) is used when no key is
/// stored.
/// </summary>
public sealed class SecretStore
{
    private const string Service = "com.keithadler.leanstudio";
    private readonly string _folder;

    /// <summary>A store whose fallback files go in <paramref name="folder"/> (Lean Studio's settings folder).</summary>
    public SecretStore(string folder)
    {
        _folder = folder;
    }

    /// <summary>For tests: keep keys only in files, never in the system's keychain.</summary>
    public bool FilesOnly { get; init; }

    /// <summary>The environment variable that holds the key named <paramref name="name"/>, if there is a standard one.</summary>
    public static string? EnvironmentVariable(string name) => name switch
    {
        "anthropic" => "ANTHROPIC_API_KEY",
        "openai" => "OPENAI_API_KEY",
        "gemini" => "GEMINI_API_KEY",
        "openrouter" => "OPENROUTER_API_KEY",
        _ => null,
    };

    /// <summary>The key named <paramref name="name"/>, from the store or else its environment variable; null if there is none.</summary>
    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        string? stored = null;
        if (!FilesOnly && OperatingSystem.IsMacOS())
        {
            ProcessResult r = await ProcessRunner.RunAsync("security", ["find-generic-password", "-s", Service, "-a", name, "-w"], ct: ct).ConfigureAwait(false);
            stored = r.Success ? r.Output.Trim() : null;
        }
        else if (!FilesOnly && OperatingSystem.IsLinux() && HasSecretTool())
        {
            ProcessResult r = await ProcessRunner.RunAsync("secret-tool", ["lookup", "service", Service, "account", name], ct: ct).ConfigureAwait(false);
            stored = r.Success ? r.Output.Trim() : null;
        }
        if (string.IsNullOrEmpty(stored))
        {
            string file = FileFor(name);
            stored = File.Exists(file) ? (await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)).Trim() : null;
        }
        if (string.IsNullOrEmpty(stored) && EnvironmentVariable(name) is string v)
        {
            stored = Environment.GetEnvironmentVariable(v);
        }
        return string.IsNullOrWhiteSpace(stored) ? null : stored.Trim();
    }

    /// <summary>Keep <paramref name="value"/> as the key named <paramref name="name"/>; an empty value forgets it.</summary>
    /// <returns>Where it was kept, for people: <c>the Keychain</c>, <c>the keyring</c> or <c>a private file</c>.</returns>
    public async Task<string> SetAsync(string name, string? value, CancellationToken ct = default)
    {
        value = value?.Trim();
        string file = FileFor(name);
        if (string.IsNullOrEmpty(value))
        {
            if (!FilesOnly && OperatingSystem.IsMacOS())
            {
                await ProcessRunner.RunAsync("security", ["delete-generic-password", "-s", Service, "-a", name], ct: ct).ConfigureAwait(false);
            }
            else if (!FilesOnly && OperatingSystem.IsLinux() && HasSecretTool())
            {
                await ProcessRunner.RunAsync("secret-tool", ["clear", "service", Service, "account", name], ct: ct).ConfigureAwait(false);
            }
            if (File.Exists(file))
            {
                File.Delete(file);
            }
            return "nowhere (forgotten)";
        }
        if (!FilesOnly && OperatingSystem.IsMacOS())
        {
            // -U updates a key that is already there.
            ProcessResult r = await ProcessRunner.RunAsync("security", ["add-generic-password", "-U", "-s", Service, "-a", name, "-l", "Lean Studio: " + name, "-w", value], ct: ct).ConfigureAwait(false);
            if (r.Success)
            {
                return "the Keychain";
            }
        }
        else if (!FilesOnly && OperatingSystem.IsLinux() && HasSecretTool())
        {
            ProcessResult r = await RunWithInputAsync("secret-tool", ["store", "--label=Lean Studio: " + name, "service", Service, "account", name], value, ct).ConfigureAwait(false);
            if (r.Success)
            {
                return "the keyring";
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        // Create it private before the key is written into it.
        await File.WriteAllTextAsync(file, "", ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        await File.WriteAllTextAsync(file, value, ct).ConfigureAwait(false);
        return "a private file";
    }

    private string FileFor(string name) => Path.Combine(_folder, "keys", name + ".key");

    private static bool? _secretTool;

    private static bool HasSecretTool() =>
        _secretTool ??= (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(d => d.Length > 0 && File.Exists(Path.Combine(d, "secret-tool")));

    private static async Task<ProcessResult> RunWithInputAsync(string file, IEnumerable<string> args, string input, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        try
        {
            using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
            await p.StandardInput.WriteAsync(input).ConfigureAwait(false);
            p.StandardInput.Close();
            string output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return new ProcessResult(p.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return new ProcessResult(-1, e.Message);
        }
    }
}
