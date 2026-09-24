using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Workflow;
using LeanStudio.Lsp;

namespace LeanStudio.Tests;

public sealed class RemoteTests
{
    private static readonly string Local = OperatingSystem.IsWindows() ? @"C:\mnt\box\Proofs" : "/mnt/box/Proofs";
    private static readonly RemoteTarget Box = new("me@box", "/home/me/Proofs", Local);

    private static string L(string rel) => Path.Combine(Local, rel.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void MapsPathsAndUrisBothWays()
    {
        Assert.True(Box.Covers(L("Proofs/Basic.lean")));
        Assert.True(Box.Covers(Local));
        Assert.False(Box.Covers(Local + "2"));
        Assert.Equal("/home/me/Proofs/Proofs/Basic.lean", Box.ToRemotePath(L("Proofs/Basic.lean")));
        Assert.Equal("/home/me/Proofs", Box.ToRemotePath(Local));

        string uri = LeanServer.UriOf(L("Proofs/Basic.lean"));
        string msg = "{\"textDocument\":{\"uri\":\"" + uri + "\"}}";
        string sent = Box.ToRemote(msg);
        Assert.Equal("""{"textDocument":{"uri":"file:///home/me/Proofs/Proofs/Basic.lean"}}""", sent);
        Assert.Equal(msg, Box.ToLocal(sent));

        // Build output: whole paths only, so a sibling folder with a longer name is left alone.
        string built = Box.ToLocal("error: /home/me/Proofs/Proofs/Basic.lean:3:2: unknown identifier\nsee /home/me/Proofs2/x");
        Assert.StartsWith("error: " + Local + "/Proofs/Basic.lean:3:2:", built, StringComparison.Ordinal);
        Assert.EndsWith("see /home/me/Proofs2/x", built, StringComparison.Ordinal);
    }

    [Fact]
    public void RunsLeanToolsInTheRemoteFolderThroughElan()
    {
        (string file, IReadOnlyList<string> args) = Box.Command("/Users/me/.elan/bin/lake", ["env", "lean", L("Proofs/It's here.lean")], L("Proofs"));
        Assert.Equal("ssh", file);
        Assert.Equal(["-o", "BatchMode=yes", "-o", "ServerAliveInterval=30", "me@box"], args.Take(5));
        Assert.Equal("""cd /home/me/Proofs/Proofs && PATH="$HOME/.elan/bin:$PATH" exec lake env lean '/home/me/Proofs/Proofs/It'\''s here.lean'""", args[5]);
        Assert.True(RemoteTarget.IsLeanTool("lake"));
        Assert.True(RemoteTarget.IsLeanTool(@"C:\Users\me\.elan\bin\lean.exe"));
        Assert.False(RemoteTarget.IsLeanTool("git"));
        Assert.Equal(("me@box.example.org", "/srv/lean/Proofs"), RemoteTarget.ParseDestination(" me@box.example.org:/srv/lean/Proofs "));
        Assert.Equal(("box", "~/Proofs"), RemoteTarget.ParseDestination("box:~/Proofs"));
        Assert.Null(RemoteTarget.ParseDestination("/just/a/path"));
    }

    [Fact]
    public void FindsTheRemoteForAPath()
    {
        var inner = new RemoteTarget("other", "/srv/Inner", L("Inner"));
        RemoteTargets.Register(Box);
        RemoteTargets.Register(inner);
        try
        {
            Assert.Same(Box, RemoteTargets.For(L("Proofs/Basic.lean")));
            Assert.Same(inner, RemoteTargets.For(L("Inner/A.lean")));
            Assert.Null(RemoteTargets.For(Path.GetTempPath()));
        }
        finally
        {
            RemoteTargets.Unregister(Box.LocalRoot);
            RemoteTargets.Unregister(inner.LocalRoot);
        }
        Assert.Null(RemoteTargets.For(L("Proofs/Basic.lean")));
    }
}

/// <summary>
/// A remote project for real, without a second machine: a stand-in for ssh runs the command here, in the "remote"
/// folder, and the "mount" is a symbolic link to it, so every path Lean sees and reports differs from the editor's.
/// </summary>
[Collection(Lean.Collection)]
public sealed class RemoteLeanTests : IDisposable
{
    private readonly string _scratch = Lint.RealPath(Path.Combine(Path.GetTempPath(), "leanstudio-remote-" + Guid.NewGuid().ToString("N")[..8]));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, true);
        }
        catch (IOException)
        {
        }
    }

    private string SshLog => Path.Combine(_scratch, "ssh.log");

    private RemoteTarget Setup()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in for ssh is a shell script");
        Lean.RequireLean();
        string remote = Path.Combine(_scratch, "remote", "Proofs");
        Directory.CreateDirectory(remote);
        foreach (string f in new[] { "lakefile.toml", "lean-toolchain", "lake-manifest.json", "Proofs.lean" })
        {
            File.Copy(Lean.Sample("Proofs", f), Path.Combine(remote, f));
        }
        Directory.CreateDirectory(Path.Combine(remote, "Proofs"));
        File.Copy(Lean.Sample("Proofs", "Proofs", "Basic.lean"), Path.Combine(remote, "Proofs", "Basic.lean"));
        Directory.CreateDirectory(Path.Combine(_scratch, "mnt"));
        string local = Path.Combine(_scratch, "mnt", "Proofs");
        Directory.CreateSymbolicLink(local, remote);
        string ssh = Path.Combine(_scratch, "fake-ssh");
        File.WriteAllText(ssh, $"#!/bin/sh\nwhile [ \"$1\" = \"-o\" ]; do shift 2; done\nshift\necho \"$1\" >> '{SshLog}'\nexec sh -c \"$1\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var target = new RemoteTarget("me@box", remote, local) { Ssh = ssh, SshOptions = ["-o", "BatchMode=yes"] };
        RemoteTargets.Register(target);
        return target;
    }

    [Fact]
    public async Task LeansServerRunsRemotelyAndTheEditorSeesLocalPaths()
    {
        RemoteTarget target = Setup();
        try
        {
            string file = Path.Combine(target.LocalRoot, "Proofs", "Basic.lean");
            string uri = LeanServer.UriOf(file);
            await using var server = new LeanServer(new LeanProject(target.LocalRoot).ServerCommand());
            var got = new TaskCompletionSource<IReadOnlyList<Diagnostic>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var uris = new List<string>();
            IReadOnlyList<Diagnostic> last = [];
            server.DiagnosticsPublished += (u, d) =>
            {
                lock (uris)
                {
                    uris.Add(u);
                }
                if (u == uri)
                {
                    last = d;
                }
            };
            server.FileProgress += (u, p) =>
            {
                if (u == uri && p.Count == 0)
                {
                    got.TrySetResult(last);
                }
            };
            await server.StartAsync(TestContext.Current.CancellationToken);
            await server.OpenAsync(uri, await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
            IReadOnlyList<Diagnostic> diags = await got.Task.WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
            Assert.Contains(diags, d => d.Message.Contains("sorry", StringComparison.Ordinal));
            lock (uris)
            {
                Assert.All(uris, u => Assert.DoesNotContain("/remote/", u, StringComparison.Ordinal));
            }
            // Go to definition answers with a local path too.
            string text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            int line = text.Split('\n').ToList().FindIndex(l => l.Contains("unfold double", StringComparison.Ordinal));
            var defs = await server.DefinitionAsync(uri, new Position(line, text.Split('\n')[line].IndexOf("double", StringComparison.Ordinal) + 1), TestContext.Current.CancellationToken);
            Assert.Contains(defs, d => d.Uri == uri);
            Assert.Contains("exec lake serve", File.ReadAllText(SshLog), StringComparison.Ordinal);
        }
        finally
        {
            RemoteTargets.Unregister(target.LocalRoot);
        }
    }

    [Fact]
    public async Task LakeRunsRemotelyAndItsOutputNamesLocalFiles()
    {
        RemoteTarget target = Setup();
        try
        {
            File.WriteAllText(Path.Combine(target.LocalRoot, "Proofs", "Broken.lean"), "theorem oops : 1 = 2 := rfl\n");
            ProcessResult r = await ProcessRunner.RunAsync("lake", ["env", "lean", Path.Combine(target.LocalRoot, "Proofs", "Broken.lean")], target.LocalRoot, ct: TestContext.Current.CancellationToken);
            Assert.False(r.Success);
            Assert.Contains(Path.Combine(target.LocalRoot, "Proofs", "Broken.lean") + ":1:", r.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("/remote/", r.Output, StringComparison.Ordinal);
            Assert.Contains("/remote/Proofs && PATH=\"$HOME/.elan/bin:$PATH\" exec lake env lean /", File.ReadAllText(SshLog), StringComparison.Ordinal);
            // Programs other than Lean's still run here.
            ProcessResult pwd = await ProcessRunner.RunAsync("/bin/pwd", [], target.LocalRoot, ct: TestContext.Current.CancellationToken);
            Assert.DoesNotContain("fake-ssh", pwd.Output, StringComparison.Ordinal);
        }
        finally
        {
            RemoteTargets.Unregister(target.LocalRoot);
        }
    }
}
