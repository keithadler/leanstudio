using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LeanStudio.Core.Editing;
using LeanStudio.Core.Git;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Proofs;
using LeanStudio.Core.Toolchains;
using LeanStudio.Core.Workflow;
using LeanStudio.Lsp;

namespace LeanStudio.Tests;

/// <summary>
/// Random input, thousands of times, from fixed seeds (a failure names its seed, so it can be replayed): every
/// parser and editing engine must never throw on it, and the properties that make it correct must hold.
/// </summary>
public sealed class FuzzTests
{
    /// <summary>Seeds per test: 2,000, or <c>LEANSTUDIO_FUZZ_SEEDS</c> for a deeper run.</summary>
    private static readonly int Seeds = int.TryParse(Environment.GetEnvironmentVariable("LEANSTUDIO_FUZZ_SEEDS"), out int n) && n > 0 ? n : 2000;

    private static readonly string[] Pieces =
    [
        "theorem", "lemma", "def", "example", "by", ":=", "import", "namespace", "end", "sorry", "simp", "exact", "intro",
        "rfl", "·", "|", "=>", "∀", "∃", "→", "ℕ", "α", "⟨", "⟩", "(", ")", "[", "]", "{", "}", "⦃", "⦄", "/-", "-/", "--",
        "/--", "/-!", "\"", "\\", "\\\\", "\\alpha", "\\to", "\\N", "$", "$$", "\\frac{a}{b}", "_", "^", "`", "'", "@[", "]",
        " ", " ", " ", "  ", "\t", "\n", "\n", "\n", "\r\n", "\n\n", "x", "h", "hp", "foo.bar", "Nat.add_comm", "0", "42",
        "😀", "𝔽", "é", "\u0301", "error: ", "warning: ", "✔ [1/2] Built X (1s)", ".lean:3:4: ", "\\begin{lemma}",
        "\\end{lemma}", "\\lean{A.b}", "\\leanok", "%", "{", "}", ",", ";", "#eval", "#check", "mutual", "@[extern \"f\"]",
        "opaque", "IO", "UInt32", "String", "@&", "lean_obj_res", "LEAN_EXPORT", "(void)", "{ return 0; }",
    ];

    /// <summary>Random text made of Lean-ish pieces, Unicode (surrogate pairs and combining marks too) and noise.</summary>
    private static string Text(Random r, int maxPieces = 60)
    {
        var sb = new StringBuilder();
        int n = r.Next(0, maxPieces);
        for (int i = 0; i < n; i++)
        {
            sb.Append(r.Next(10) == 0 ? ((char)r.Next(32, 0x2FFF)).ToString() : Pieces[r.Next(Pieces.Length)]);
        }
        return sb.ToString();
    }

    private static void ForSeeds(Action<Random, int> check)
    {
        for (int seed = 0; seed < Seeds; seed++)
        {
            try
            {
                check(new Random(seed), seed);
            }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
        }
    }

    /// <summary>An editor buffer for the Vim and Emacs engines, with undo by snapshots.</summary>
    private sealed class Buffer(string text) : IVimHost
    {
        private readonly Stack<(string, int)> _undo = new(), _redo = new();
        private int _depth;

        public string Text { get; private set; } = text;
        public int Caret { get; set; }
        public (int Start, int End) Selection { get; private set; }

        public void Replace(int offset, int length, string text)
        {
            Assert.InRange(offset, 0, Text.Length);
            Assert.InRange(offset + length, offset, Text.Length);
            if (_depth == 0)
            {
                _undo.Push((Text, Caret));
                _redo.Clear();
            }
            Text = Text[..offset] + text + Text[(offset + length)..];
        }

        public void Select(int start, int end)
        {
            Assert.InRange(Math.Min(start, end), 0, Text.Length);
            Assert.InRange(Math.Max(start, end), 0, Text.Length);
            Selection = (start, end);
        }

        public void BeginChange()
        {
            if (_depth++ == 0)
            {
                _undo.Push((Text, Caret));
                _redo.Clear();
            }
        }

        public void EndChange() => _depth = Math.Max(0, _depth - 1);

        public void Undo()
        {
            if (_undo.Count > 0)
            {
                _redo.Push((Text, Caret));
                (Text, Caret) = _undo.Pop();
            }
        }

        public void Redo()
        {
            if (_redo.Count > 0)
            {
                _undo.Push((Text, Caret));
                (Text, Caret) = _redo.Pop();
            }
        }

        public bool Ex(string command) => true;

        /// <summary>What the editor does with a key the engine leaves alone: types it.</summary>
        public void Type(string key)
        {
            if (key.Length == 1 || key == "<CR>")
            {
                Replace(Caret, 0, key == "<CR>" ? "\n" : key);
                Caret += key == "<CR>" ? 1 : key.Length;
            }
        }
    }

    private static readonly string[] VimKeys =
    [
        "h", "j", "k", "l", "w", "b", "e", "W", "B", "E", "0", "^", "$", "gg", "G", "f", "t", "F", "T", ";", ",", "%", "{", "}",
        "d", "c", "y", ">", "<lt>", "i", "a", "I", "A", "o", "O", "x", "X", "D", "C", "Y", "s", "S", "r", "J", "~", "p", "P",
        "u", "<C-r>", ".", "/", "?", "n", "N", "*", "#", "v", "V", "<Esc>", "<CR>", "<BS>", "<C-d>", "<C-u>", ":", "w", "q",
        "2", "3", "9", "(", ")", "⟨", "iw", "aw", "ip", "i(", "a⟨", "\"", "x", "α", " ", "\n",
    ];

    [Fact]
    public void VimNeverBreaksTheTextOrLosesTheCaret()
    {
        ForSeeds((r, seed) =>
        {
            string original = Text(r);
            var b = new Buffer(original) { Caret = original.Length == 0 ? 0 : r.Next(original.Length) };
            var vim = new VimEngine(b);
            var pressed = new List<string>();
            for (int i = 0; i < 150; i++)
            {
                string key = VimKeys[r.Next(VimKeys.Length)];
                foreach (string k in key.Length > 1 && !key.StartsWith('<') ? key.Select(ch => ch.ToString()) : [key])
                {
                    string before = b.Text;
                    int caret = b.Caret;
                    pressed.Add(k);
                    try
                    {
                        if (!vim.Key(k))
                        {
                            b.Type(k);
                        }
                    }
                    catch (Exception e) when (e is not Xunit.Sdk.XunitException)
                    {
                        throw new Xunit.Sdk.XunitException($"seed {seed}: {e.GetType().Name} on key '{k}' after [{string.Join(" ", pressed.TakeLast(12))}], "
                            + $"caret {caret} in {JsonSerializer.Serialize(before)}\n{e.StackTrace}");
                    }
                    Assert.InRange(b.Caret, 0, b.Text.Length);
                }
            }
            // Every change can be undone: back to the start.
            vim.Key("<Esc>");
            vim.Key("<Esc>");
            for (int i = 0; i < 2000 && b.Text != original; i++)
            {
                vim.Key("u");
            }
            Assert.True(b.Text == original, $"seed {seed}: undo did not bring the text back");
        });
    }

    private static readonly string[] EmacsKeys =
    [
        "C-/", "C-SPC", "C-a", "C-b", "C-d", "C-e", "C-f", "C-g", "C-k", "C-l", "C-n", "C-o", "C-p", "C-r", "C-s", "C-t", "C-v",
        "C-w", "C-x", "C-y", "M-<", "M-<backspace>", "M->", "M-b", "M-d", "M-f", "M-v", "M-w", "C-x", "C-s", "u", "a", " ",
    ];

    [Fact]
    public void EmacsKeysNeverBreakTheTextOrLoseTheCaret()
    {
        ForSeeds((r, seedNumber) =>
        {
            string original = Text(r);
            var b = new Buffer(original) { Caret = original.Length == 0 ? 0 : r.Next(original.Length) };
            var emacs = new EmacsEngine(b);
            for (int i = 0; i < 150; i++)
            {
                string key = EmacsKeys[r.Next(EmacsKeys.Length)];
                if (!emacs.Key(key))
                {
                    b.Type(key);
                }
                Assert.InRange(b.Caret, 0, b.Text.Length);
            }
        });
    }

    [Fact]
    public void SeveralCursorsEditConsistently()
    {
        ForSeeds((r, seedNumber) =>
        {
            string text = Text(r, 40);
            for (int step = 0; step < 40; step++)
            {
                var cursors = MultiCursor.Normalize(Enumerable.Range(0, r.Next(1, 6)).Select(_ =>
                {
                    int a = r.Next(text.Length + 1), p = r.Next(10) < 7 ? a : r.Next(text.Length + 1);
                    return new Cursor(a, p);
                }));
                Assert.All(cursors, c => Assert.InRange(c.End, c.Start, text.Length));
                for (int k = 1; k < cursors.Count; k++)
                {
                    Assert.True(cursors[k - 1].End <= cursors[k].Start, "normalized cursors are sorted and apart");
                }
                (IReadOnlyList<Replacement> edits, IReadOnlyList<Cursor> after) = r.Next(3) switch
                {
                    0 => MultiCursor.Type(cursors, Pieces[r.Next(Pieces.Length)]),
                    1 => MultiCursor.Backspace(text, cursors),
                    _ => MultiCursor.Delete(text, cursors),
                };
                string next = MultiCursor.ApplyTo(text, edits);
                Assert.All(after, c => Assert.InRange(c.End, c.Start, next.Length));
                text = next;
            }
            // Finding occurrences never throws and finds real matches.
            if (text.Length > 0)
            {
                Cursor? w = MultiCursor.WordAt(text, r.Next(text.Length + 1));
                if (w is Cursor word && !word.IsEmpty)
                {
                    string s = text[word.Start..word.End];
                    Assert.All(MultiCursor.AllOccurrences(text, word), c => Assert.Equal(s, text[c.Start..c.End]));
                    Assert.NotEmpty(MultiCursor.AddNextOccurrence(text, [word]));
                }
                _ = MultiCursor.OnAdjacentLine(text, Cursor.At(r.Next(text.Length + 1)), r.Next(2) == 0);
            }
        });
    }

    [Fact]
    public void TextToolsNeverThrowAndKeepTheirPromises()
    {
        ForSeeds((r, seedNumber) =>
        {
            string text = Text(r, 120);
            string[] lines = text.Split('\n');

            // Comments to fold: each spans more than one line, in order, inside the text.
            var folds = LeanText.CommentFolds(text);
            int lineCount = lines.Length;
            Assert.All(folds, f => Assert.InRange(f.StartLine, 0, f.EndLine - 1));
            Assert.All(folds, f => Assert.InRange(f.EndLine, 1, lineCount - 1));
            Assert.Equal(folds.OrderBy(f => f.StartLine), folds);

            // Math in docstrings: text without a dollar sign is left alone.
            string math = LatexText.ToUnicode(text);
            if (!text.Contains('$', StringComparison.Ordinal))
            {
                Assert.Equal(text, math);
            }
            _ = LatexText.Math(text);

            // Abbreviations as they are typed.
            foreach (char c in text.Take(50))
            {
                _ = Abbreviations.OnType(text.Length > 5 ? text[..5] : text, c);
            }
            _ = Abbreviations.Candidates(text.Length > 3 ? text[..3] : text).Take(5).ToList();

            // Proofs, their steps, and the walkthrough.
            for (int line = 0; line < Math.Min(lines.Length, 30); line++)
            {
                TacticProof? p = ProofSteps.Find(lines, line);
                if (p is not null)
                {
                    Assert.All(p.Steps, s => Assert.InRange(s.Line, 0, lines.Length - 1));
                }
            }
            _ = Walkthrough.Proofs(lines);

            // Imports: adding one then removing it leaves it out; ImportLines and Remove never throw.
            string withImport = LibraryRoot.AddImport(text, "Fuzz.Module");
            Assert.Contains("Fuzz.Module", LibraryRoot.ImportsOf(withImport));
            Assert.DoesNotContain("Fuzz.Module", LibraryRoot.ImportsOf(LibraryRoot.RemoveImport(withImport, "Fuzz.Module")));
            _ = ImportCheck.ImportLines(text);
            _ = ImportCheck.Remove(text, ["Fuzz.Module", "Std"]);

            // Renames of deprecated names keep the line count, whatever the positions say.
            var uses = Enumerable.Range(0, r.Next(4)).Select(_ => new DeprecatedUse("F.lean", r.Next(-2, lines.Length + 2), r.Next(-1, 40), "Nat.foo", "Nat.bar")).ToList();
            Assert.Equal(lines.Length, DependencyBump.Rename(text, uses).Split('\n').Length);
            _ = Deprecation.AddAlias(text, r.Next(-1, lines.Length + 1), "old", "new", r.Next(2) == 0, new DateOnly(2026, 9, 24));

            // Heartbeats' instrumented copy, and reading Lean's messages back.
            var (instrumented, after, inserted) = Heartbeats.Instrument(text);
            Assert.Contains("#leanstudio_heartbeats", instrumented, StringComparison.Ordinal);
            _ = Heartbeats.Parse("""{"data":"leanstudio-heartbeats 12","pos":{"line":3}}""" + "\n" + text, lines, after, inserted);

            // Prove It's sorry sites and instrumented file, the REPL's context, the blueprint, FFI.
            var sites = ProofSearch.Sites(text);
            Assert.All(sites, s => Assert.InRange(s.Offset + s.Length, 0, text.Length));
            _ = ProofSearch.Instrument(text, sites);
            _ = ProofSearch.HeaderEnd(text);
            _ = LeanRepl.Context(text, r.Next(-1, lines.Length + 1));
            _ = LeanRepl.AsCommand(text);
            _ = Blueprint.Parse(text, "x.tex");
            _ = Ffi.ExternsIn("x.lean", lines).ToList();
            _ = Ffi.CFunctionsIn("x.c", text).ToList();
            _ = Ffi.CNameAt(lines, r.Next(-1, lines.Length + 1));
            try
            {
                _ = Ffi.Parse(text);
            }
            catch (FormatException)
            {
                // a signature it can't read is refused, not crashed on
            }
        });
    }

    [Fact]
    public void OutputReadersNeverThrow()
    {
        ForSeeds((r, seedNumber) =>
        {
            string output = Text(r, 200);
            _ = LakeOutput.Parse(output, "/p");
            var reader = new ProgressReader();
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            foreach (string line in output.Split('\n'))
            {
                reader.Feed(line, now = now.AddSeconds(r.Next(5)));
                Assert.InRange(reader.Progress.Done, 0, int.MaxValue);
                Assert.True(reader.Progress.Fraction is null or (>= 0 and <= 1));
            }
            _ = reader.Progress.Detail;
            _ = TaskProgress.Format(TimeSpan.FromSeconds(r.Next(0, 1_000_000)));
            _ = LeanProcesses.ParsePs(output);
            _ = LeanProcesses.ParsePs(output, LeanProcesses.CompiledFile);
            _ = GitRepository.ParseStatus(output.Replace('\n', '\0'));
            _ = GitRepository.ParseLineChanges(output);
            _ = Profiler.Parse(output, output.Split('\n'));
            _ = Profiler.Parse([(r.Next(-1, 5), output)], output.Split('\n'));
            _ = Blame.Parse(output);
            _ = DependencyBump.DeprecatedUses([new BuildMessage("/p/A.lean", 1, 1, false, output)]);
        });
    }

    [Fact]
    public void JsonFilesAndAnswersNeverThrow()
    {
        string[] shapes =
        [
            "null", "[]", "{}", "1", "\"x\"", "[{}]", "[[{}]]", """{"hits":[{"name":1}]}""", """[[{"result":{"name":["A","b"]},"distance":"x"}]]""",
            """{"imports":[{"module":3}]}""", """[{"title":1,"program":[],"args":{}}]""", """[{"key":null,"command":5}]""",
            """{"a":"b"}""", """{"error":"bad"}""", """[{"title":"t","program":"p","args":[1,null,"x"]}]""",
        ];
        ForSeeds((r, seedNumber) =>
        {
            string json = r.Next(3) == 0 ? Text(r, 20) : shapes[r.Next(shapes.Length)];
            _ = ProjectCommands.Parse(json);
            _ = KeyBindingsFile.Parse(json);
            _ = Abbreviations.ParseCustom(json);
            try
            {
                _ = ImportCheck.Parse(json);
            }
            catch (InvalidOperationException)
            {
                // an unreadable report is refused with the one exception its callers expect
            }
            JsonElement root;
            try
            {
                root = JsonDocument.Parse(json).RootElement;
            }
            catch (JsonException)
            {
                return;
            }
            _ = Loogle.Parse(root);
            _ = LeanSearch.Parse(root);
            _ = KeyChord.Parse(Text(r, 4), r.Next(2) == 0);
        });
    }

    [Fact]
    public void RemotePathsGoThereAndComeBackTheSame()
    {
        string local = OperatingSystem.IsWindows() ? @"C:\mnt\box\Proofs" : "/mnt/box/Proofs";
        var target = new RemoteTarget("me@box", "/home/me/Proofs", local);
        string[] parts = [local, local + Path.DirectorySeparatorChar + "A.lean", LeanServer.UriOf(Path.Combine(local, "B.lean")), "/home/me/Proofs2", "file:///x", "\"", ":", "{\"uri\":\"", "\"}", " ", "\n", "é", "😀", "%20"];
        ForSeeds((r, seed) =>
        {
            string s = string.Concat(Enumerable.Range(0, r.Next(12)).Select(_ => parts[r.Next(parts.Length)]));
            string remote = target.ToRemote(s);
            if (!OperatingSystem.IsWindows())
            {
                // On a POSIX machine every local path maps to a remote one and back unchanged.
                Assert.True(target.ToLocal(remote) == s, $"seed {seed}: '{s}' came back as '{target.ToLocal(remote)}'");
            }
            Assert.DoesNotContain("/mnt/box/Proofs/", remote.Replace('\\', '/'), StringComparison.Ordinal);
            _ = RemoteTarget.Quote(s);
            _ = RemoteTarget.ParseDestination(s);
        });
    }

    [Fact]
    public void BigInputsStayFast()
    {
        // Nothing that runs as you type may be quadratic: a large file (Mathlib has 5,000-line ones) stays quick.
        var r = new Random(7);
        string big = string.Concat(Enumerable.Range(0, 20000).Select(i => i % 50 == 0 ? "/- a comment\n  on lines -/\n" : $"theorem t{i} (n : Nat) : n + {i} = {i} + n := by\n  omega\n"));
        string[] lines = big.Split('\n');
        var times = new List<string>();
        var total = Stopwatch.StartNew();
        void Timed(string what, Action run)
        {
            var sw = Stopwatch.StartNew();
            run();
            times.Add($"{what} {sw.Elapsed.TotalSeconds:F2} s");
        }
        Timed("comment folds", () => LeanText.CommentFolds(big));
        Timed("proof steps", () => ProofSteps.Find(lines, lines.Length / 2));
        Timed("sorry sites", () => ProofSearch.Sites(big));
        Timed("build output", () => LakeOutput.Parse(string.Join('\n', Enumerable.Range(0, 100000).Select(i => $"warning: A{i % 300}.lean:{i}:1: declaration uses 'sorry'")), "/p"));
        Timed("every occurrence", () => MultiCursor.AllOccurrences(big, new Cursor(8, 10)));
        Timed("library root", () => LibraryRoot.AddImport(big, "X.Y"));
        Timed("vim", () =>
        {
            var b = new Buffer(big) { Caret = big.Length / 2 };
            var vim = new VimEngine(b);
            foreach (string k in new[] { "w", "b", "e", "}", "{", "G", "gg", "%", "j", "k", "dd", "u", "yy", "p", "u", "/", "t", "7", "<CR>", "n" }.SelectMany(k => Enumerable.Repeat(k, 20)))
            {
                foreach (string ch in k.Length > 1 && !k.StartsWith('<') ? k.Select(c => c.ToString()) : [k])
                {
                    if (!vim.Key(ch))
                    {
                        b.Type(ch);
                    }
                }
            }
        });
        Assert.True(total.Elapsed < TimeSpan.FromSeconds(20), $"took {total.Elapsed.TotalSeconds:F1} s on a 40,000-line file: {string.Join(", ", times)}");
        Assert.InRange(r.Next(), 0, int.MaxValue);
    }
}
