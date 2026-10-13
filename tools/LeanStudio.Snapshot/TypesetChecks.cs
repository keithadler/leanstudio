using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using LeanStudio.App.Editor;
using LeanStudio.App.Services;
using LeanStudio.App.Views;
using LeanStudio.Core.Editing;

namespace LeanStudio.Snapshot;

/// <summary>
/// Typeset math in comments, drawn for real in the headless window: <c>LeanStudio.Snapshot --typeset [OUT DIR]</c>.
/// Checks what the unit tests cannot see: that every symbol takes room, that fractions, limits and roots change the
/// size of a formula the way they should, that the editor swaps <c>$…$</c> for a drawn formula and shows the source
/// under the caret, that the setting turns it off, and that a large file stays fast. Needs neither Lean nor git.
/// </summary>
internal static class TypesetChecks
{
    private static int _failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok)
        {
            _failures++;
        }
    }

    private static Size Measure(string latex, bool display = false, double size = 16)
    {
        var view = new MathView(TexMath.Parse(latex), size, Brushes.White, display);
        view.Measure(Size.Infinity);
        return view.DesiredSize;
    }

    private static bool Finite(Size s) => double.IsFinite(s.Width) && double.IsFinite(s.Height) && s.Width >= 0 && s.Height > 0;

    public static async Task<int> RunAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine("layout");
        Layout();
        Console.WriteLine("drawing");
        Drawing(outDir);
        Console.WriteLine("editor");
        Editor(outDir);
        Console.WriteLine("settings");
        await SettingsAsync();
        Console.WriteLine("large file");
        LargeFile();
        return _failures;
    }

    private static void Layout()
    {
        // Every symbol takes room: the width grows with each one added (a zero-width symbol once piled formulas up).
        string[] chain = ["a", "a+b", "a+b+c", "a+b+c+d"];
        double last = 0;
        bool grows = true;
        foreach (string f in chain)
        {
            double w = Measure(f).Width;
            grows &= w > last;
            last = w;
        }
        Check(grows, "a formula gets wider with each symbol added");
        Check(Measure("a+b").Width > Measure("ab").Width, "a binary operator has space around it");
        Check(Measure("a=b").Width > Measure("a+b").Width, "a relation has more space than an operator");
        Check(Measure("-a").Width < Measure("b-a").Width - Measure("b").Width + Measure("a").Width + 1, "a leading minus is unary, with no space");

        Size plain = Measure("x"), frac = Measure(@"\frac{x}{y}"), sup = Measure("x^2"), sub = Measure("x_2");
        Check(frac.Height > plain.Height * 1.5, "a fraction is taller than one line of text");
        Check(sup.Height > plain.Height, "a superscript raises the formula");
        Check(sub.Height > plain.Height, "a subscript lowers the formula");
        Check(Measure("x^{2}_{k}").Width < Measure("x^2 x_k").Width, "a sub- and superscript share one column");

        Size inlineSum = Measure(@"\sum_{k=1}^n k"), displaySum = Measure(@"\sum_{k=1}^n k", display: true);
        Check(displaySum.Height > inlineSum.Height, "display style stacks the limits above and below the sum");
        Check(displaySum.Width < inlineSum.Width, "display style puts the limits over the sum, so it is narrower");
        Check(Measure(@"\sqrt{x}").Width > plain.Width, "a root is wider than its body");
        Check(Measure(@"\left( \frac{a}{b} \right)").Width > Measure(@"\frac{a}{b}").Width, "brackets add width");
        Check(Measure(@"\left( \frac{a}{b} \right)").Height >= Measure(@"\frac{a}{b}").Height, "brackets grow with what they hold");
        Check(Measure("x", size: 32).Height > Measure("x", size: 16).Height * 1.5, "twice the font size is about twice the height");

        // Ascent is the baseline's distance from the top: inside the box, and higher for taller formulas.
        var tall = new MathView(TexMath.Parse(@"\frac{a}{b}"), 16, Brushes.White, false);
        var one = new MathView(TexMath.Parse("a"), 16, Brushes.White, false);
        tall.Measure(Size.Infinity);
        one.Measure(Size.Infinity);
        Check(tall.Ascent > one.Ascent && tall.Ascent < tall.DesiredSize.Height, "a fraction's baseline is inside its box and below the numerator");

        // Environments, stacks and limits.
        Size m1 = Measure(@"\begin{pmatrix} a & b \end{pmatrix}"), m2 = Measure(@"\begin{pmatrix} a & b \\ c & d \end{pmatrix}"), m3 = Measure(@"\begin{pmatrix} a & b & c \\ d & e & f \\ g & h & i \end{pmatrix}");
        Check(m2.Height > m1.Height && m3.Height > m2.Height, "each row of a matrix makes it taller");
        Check(m3.Width > m2.Width && m2.Width > m1.Width - 1, "each column of a matrix makes it wider");
        Check(Measure(@"\begin{pmatrix} a & b \\ c & d \end{pmatrix}").Width > Measure(@"\begin{matrix} a & b \\ c & d \end{matrix}").Width, "a matrix's brackets add width");
        Check(Measure(@"\begin{cases} 1 & x > 0 \\ 0 & x \le 0 \end{cases}").Height > plain.Height * 2, "cases stack their rows under a brace");
        Check(Measure(@"\begin{aligned} a &= b \\ &= c \end{aligned}").Height > Measure(@"\begin{aligned} a &= b \end{aligned}").Height, "aligned lines stack");
        double aligned1 = Measure(@"\begin{aligned} a &= bbbbbb \\ aaaaaa &= c \end{aligned}").Width;
        Check(aligned1 < Measure("a = bbbbbb").Width + Measure("aaaaaa = c").Width, "aligned lines share their columns, so they are narrower side by side than apart");
        Check(Measure(@"\underbrace{a+b+c}_{n}").Height > Measure("a+b+c").Height, "a label under a brace makes the formula taller");
        Check(Measure(@"\overbrace{a+b+c}^{n}").Height > Measure("a+b+c").Height, "a label over a brace makes the formula taller");
        Check(Measure(@"\overset{!}{=}").Height > Measure("=").Height, "an overset label sits above its symbol");
        Check(Measure(@"\underline{abc}").Height > Measure("abc").Height, "underline adds room below");
        Check(Measure(@"\lim_{n \to \infty} a_n", display: true).Height > Measure(@"\lim_{n \to \infty} a_n").Height, "display style puts the limit under lim");
        Check(Measure(@"\int_0^1 f", display: true).Height < Measure(@"\sum_0^1 f", display: true).Height, "an integral keeps its limits at the side in display style, where a sum stacks them");
        Check(Measure(@"\mathcal{A}\mathfrak{g}\mathbf{x}").Width > Measure("x").Width * 2, "script, fraktur and bold letters take room");

        // The formulas from the Emacs screenshot, and some hostile ones.
        string[] real =
        [
            @"\sum_{k=1}^n k", @"\sum_{k=1}^n k^3", @"\sum_{k=1}^n k = \frac{n(n+1)}{2}",
            @"1^3 + 2^3 + \cdots + n^3 = (1 + 2 + \cdots + n)^2", @"1 + 8 + 27 + 64 = 100 = 10^2",
            @"\mathbb{N} \to \mathbb{R}", @"\binom{n}{k}", @"\sqrt[3]{x^2+1}", @"\overline{AB} \perp \vec{v}", @"\forall \epsilon > 0, \exists \delta > 0",
            @"\frac{\frac{a}{b}}{\frac{c}{d}}", @"x_{i_j}^{k^l}", @"\left\{ x \mid x \in \mathbb{Z} \right\}", @"\text{if } n \text{ is even}",
            @"\int_0^1 f(x)\,dx", @"\prod_{p \le n} p", @"\lim_{n \to \infty} a_n",
            @"\begin{pmatrix} 1 & 0 \\ 0 & 1 \end{pmatrix}", @"\begin{cases} x^2 & x \ge 0 \\ -x & x < 0 \end{cases}", @"\begin{aligned} (a+b)^2 &= a^2 + 2ab + b^2 \\ &= c \end{aligned}",
            @"\underbrace{1 + 2 + \cdots + n}_{n \text{ terms}} = \frac{n(n+1)}{2}", @"\mathcal{L}(\mathfrak{g}) \subseteq \mathbf{GL}_n", @"e^{i\pi} + 1 = 0", @"\left\{ \begin{matrix} a \\ b \end{matrix} \right\}",
        ];
        Check(real.All(f => Finite(Measure(f)) && Finite(Measure(f, display: true))), "every real-world formula measures to a finite size, inline and display");
        string[] hostile = ["", " ", "{", "}", @"\frac", @"\frac{1}", @"\sqrt", @"\left", @"\left( x", @"x^", @"x_", "^^^", @"\\", @"\unknown", string.Concat(Enumerable.Repeat("{", 500)), string.Concat(Enumerable.Repeat("x^", 300)) + "x", @"\begin{pmatrix} a & b", @"\begin{cases}", @"\underbrace", @"\overset{a}", string.Concat(Enumerable.Repeat(@"\begin{matrix} ", 60)) + string.Concat(Enumerable.Repeat(@" \end{matrix}", 60))];
        bool survived = true;
        foreach (string f in hostile)
        {
            try
            {
                Size s = Measure(f);
                survived &= double.IsFinite(s.Width) && double.IsFinite(s.Height);
            }
            catch (Exception e)
            {
                Console.WriteLine($"    {Show(f)} threw {e.GetType().Name}: {e.Message}");
                survived = false;
            }
        }
        Check(survived, "malformed formulas, 500 open braces and 300 nested scripts lay out without throwing");
    }

    private static string Show(string s) => s.Length > 30 ? s[..30] + "…" : s;

    private static void Drawing(string outDir)
    {
        var rng = new Random(11);
        string[] pieces = [@"\sum", @"\frac{a}{b}", "x^2", "y_i", @"\sqrt{z}", "+", "=", @"\cdots", @"\alpha", @"\left(", @"\right)", "{", "}", "12", @"\mathbb{R}", @"\int", @"\prod", "^", "_"];
        int drawn = 0;
        try
        {
            for (int n = 0; n < 300; n++)
            {
                string f = string.Concat(Enumerable.Range(0, rng.Next(1, 9)).Select(_ => pieces[rng.Next(pieces.Length)] + " "));
                var view = new MathView(TexMath.Parse(f), 16, Brushes.White, rng.Next(2) == 0);
                view.Measure(Size.Infinity);
                view.Arrange(new Rect(view.DesiredSize));
                using var bmp = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)view.DesiredSize.Width), Math.Max(1, (int)view.DesiredSize.Height)));
                bmp.Render(view);
                drawn++;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("    " + e);
        }
        Check(drawn == 300, "300 random formulas render to a bitmap without throwing");

        var gauss = new MathView(TexMath.Parse(@"\sum_{k=1}^n k = \frac{n(n+1)}{2}"), 32, Brushes.White, true);
        gauss.Measure(Size.Infinity);
        var host = new Window { Width = gauss.DesiredSize.Width + 16, Height = gauss.DesiredSize.Height + 16, Background = Brushes.Black, Content = new Border { Padding = new Thickness(8), Child = gauss } };
        host.Show();
        string png = Path.Combine(outDir, "typeset-gauss.png");
        WriteableBitmap? frame = Snap(host, png);
        var digits = new MathView(TexMath.Parse(@"1+8+27+64=100=10^2,\ x=7,\ \mathbb{N},\ \text{e.g.}"), 32, Brushes.White, false);
        digits.Measure(Size.Infinity);
        var host2 = new Window { Width = digits.DesiredSize.Width + 16, Height = digits.DesiredSize.Height + 16, Background = Brushes.Black, Content = new Border { Padding = new Thickness(8), Child = digits } };
        host2.Show();
        Snap(host2, Path.Combine(outDir, "typeset-digits.png"));
        host2.Close();
        Check(frame is not null && Ink(frame) > 200, "the Gauss sum draws ink (saved to " + png + ")");
        host.Close();
    }

    private static WriteableBitmap? Snap(Window window, string path)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
#pragma warning disable CS0618 // the replacement overload needs encoder options this tool has no use for
        frame?.Save(path);
#pragma warning restore CS0618
        return frame;
    }

    /// <summary>How many pixels of <paramref name="bmp"/> are not black.</summary>
    private static int Ink(WriteableBitmap bmp)
    {
        using ILockedFramebuffer fb = bmp.Lock();
        var row = new byte[fb.RowBytes];
        int ink = 0;
        for (int y = 0; y < fb.Size.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, fb.RowBytes);
            for (int x = 0; x < fb.Size.Width * 4; x += 4)
            {
                if (row[x] > 40 || row[x + 1] > 40 || row[x + 2] > 40)
                {
                    ink++;
                }
            }
        }
        return ink;
    }

    private const string Sample = "/--\n  The sum of the first $n$ numbers and **Gauss** said *so*:\n\n  $\\sum_{k=1}^n k$\n-/\ndef f := 1 -- $not math$\n/- two $a$ and $b$ -/\n";

    private static (TextEditor Editor, DocMarkupGenerator Gen, Window Window) Open(string text)
    {
        var gen = new DocMarkupGenerator { Foreground = Brushes.Green, FontSize = 14 };
        var editor = new TextEditor { Text = text, FontSize = 14, FontFamily = new FontFamily("Menlo") };
        editor.TextArea.TextView.ElementGenerators.Add(gen);
        var window = new Window { Width = 900, Height = 500, Content = editor };
        window.Show();
        editor.TextArea.TextView.EnsureVisualLines();
        return (editor, gen, window);
    }

    private static List<InlineObjectElement> Drawn(TextView view, int line)
    {
        view.EnsureVisualLines();
        VisualLine? vl = view.GetVisualLine(line);
        return vl is null ? [] : [.. vl.Elements.OfType<InlineObjectElement>()];
    }

    private static void Editor(string outDir)
    {
        (TextEditor editor, DocMarkupGenerator gen, Window window) = Open(Sample);
        TextView view = editor.TextArea.TextView;
        try
        {
            gen.MoveCaret(editor.Document, editor.Document.TextLength); // caret far away, at the end
            List<InlineObjectElement> l2 = Drawn(view, 2);
            Check(l2.Count == 3, $"line 2 shows three drawn pieces ($n$, **Gauss**, *so*), found {l2.Count}");
            Check(l2.Count(e => e.Element is MathView) == 1 && l2.Count(e => e.Element is TextBlock) == 2, "one of them is math, two are emphasis");
            Check(l2.Where(e => e.Element is TextBlock).Select(e => ((TextBlock)e.Element).FontWeight == FontWeight.Bold).OrderBy(b => b).SequenceEqual([false, true]), "Gauss is bold and so is italic");
            List<InlineObjectElement> l4 = Drawn(view, 4);
            Check(l4.Count == 1 && l4[0].Element is MathView, "the sum on line 4 is drawn");
            Check(Drawn(view, 6).Count == 0, "math in a line comment after code is left alone");
            Check(Drawn(view, 7).Count == 2, "a plain comment's math is drawn too");
            Check(l4[0].Element is MathView mv && mv.PlainText.Contains('∑', StringComparison.Ordinal), "the drawn formula is the sum");
            Check(editor.Text == Sample, "the document text is untouched");

            // The caret shows the source of the span it is in, and only that span.
            int dollar = Sample.IndexOf("$n$", StringComparison.Ordinal);
            IReadOnlyList<DocSpan> changed = gen.MoveCaret(editor.Document, dollar + 1);
            editor.CaretOffset = dollar + 1;
            Check(changed.Count == 1 && changed[0].Start == dollar, "moving the caret into $n$ asks for a redraw of that span only");
            List<InlineObjectElement> inside = Drawn(view, 2);
            view.Redraw();
            inside = Drawn(view, 2);
            Check(inside.Count == 2 && inside.All(e => e.Element is TextBlock), "with the caret in $n$ its source shows and the emphasis stays drawn");
            changed = gen.MoveCaret(editor.Document, Sample.IndexOf("$\\sum", StringComparison.Ordinal));
            Check(changed.Count == 2, "moving to the next span redraws the one left and the one entered");
            Check(gen.MoveCaret(editor.Document, Sample.IndexOf("$\\sum", StringComparison.Ordinal)).Count == 0, "a caret that has not changed span redraws nothing");
            gen.MoveCaret(editor.Document, 0);
            view.Redraw();
            Check(Drawn(view, 4).Count == 1, "the sum is drawn again when the caret leaves it");

            // The caret on either edge of the span counts as in it.
            int sumStart = Sample.IndexOf("$\\sum", StringComparison.Ordinal);
            int sumEnd = sumStart + "$\\sum_{k=1}^n k$".Length;
            gen.MoveCaret(editor.Document, sumEnd);
            view.Redraw();
            Check(Drawn(view, 4).Count == 0, "the caret just after a formula shows its source");
            gen.MoveCaret(editor.Document, sumEnd + 1);
            view.Redraw();
            Check(Drawn(view, 4).Count == 1, "one character further, it is typeset");

            // Editing: spans follow the text.
            gen.MoveCaret(editor.Document, 0);
            editor.Document.Insert(0, "$x$ ");
            Check(gen.Spans(editor.Document).Count == 6, "an edit re-finds the spans (a new $x$ outside a comment is not one)");
            editor.Document.Text = "/-- $a$ -/";
            view.Redraw();
            Check(Drawn(view, 1).Count == 1, "a replaced document is typeset");
            editor.Document.Text = "";
            view.Redraw();
            Check(Drawn(view, 1).Count == 0, "an empty document draws nothing and does not throw");

            gen.Enabled = false;
            editor.Document.Text = "/-- $a$ **b** -/";
            view.Redraw();
            Check(Drawn(view, 1).Count == 0, "turned off, nothing is drawn and the source shows");
            gen.Enabled = true;
            view.Redraw();
            Check(Drawn(view, 1).Count == 2, "turned back on, it is drawn again");

            // A span cut across by a line break stays source; one with wrapped text still draws.
            editor.Document.Text = "/-- $a\nb$ **c\nd** -/";
            view.Redraw();
            Check(Drawn(view, 1).Count == 0 && Drawn(view, 2).Count == 0, "spans over two lines are left as source");

            // The pixels: a screenshot of the editor to look at.
            editor.Document.Text = Sample;
            gen.MoveCaret(editor.Document, Sample.Length);
            view.Redraw();
            window.UpdateLayout();
            string png = Path.Combine(outDir, "typeset-editor.png");
            Snap(window, png);
            Check(File.Exists(png), "a screenshot of the editor is saved to " + png);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task SettingsAsync()
    {
        string dir = Directory.CreateTempSubdirectory("leanstudio-typeset-").FullName;
        try
        {
            string path = Path.Combine(dir, "A.lean");
            await File.WriteAllTextAsync(path, "/-- $x$ and **y** -/\ndef a := 1\n");
            var settings = new Settings();
            var window = new MainWindow(settings) { Width = 1200, Height = 800 };
            window.Taskbar.UseNative = false;
            window.OpenOnStartup = dir;
            window.Show();
            var vm = window.ViewModel;
            await WaitFor(() => vm.Project is not null);
            var doc = await vm.OpenFileAsync(path);
            Check(doc is not null, "a Lean file opens");
            if (doc is null)
            {
                return;
            }
            await Task.Delay(500);
            Check(settings.TypesetComments, "typesetting is on by default");
            LeanEditor? editor = window.GetVisualDescendants().OfType<LeanEditor>().FirstOrDefault();
            TextView? view = window.GetVisualDescendants().OfType<TextView>().FirstOrDefault(v => v.Document is not null);
            Check(editor is not null && view is not null, "the editor is in the window");
            if (editor is null || view is null)
            {
                return;
            }
            view.EnsureVisualLines();
            Check(Drawn(view, 1).Count == 2, $"the comment's math and bold are drawn in the real editor (found {Drawn(view, 1).Count})");
            settings.TypesetComments = false;
            editor.ApplySettings(settings);
            view.EnsureVisualLines();
            Check(Drawn(view, 1).Count == 0, "turning the setting off shows the source");
            settings.TypesetComments = true;
            editor.ApplySettings(settings);
            view.EnsureVisualLines();
            Check(Drawn(view, 1).Count == 2, "turning it on again typesets it");
            Check(new Settings { TypesetComments = false }.TypesetComments == false && new Settings().TypesetComments, "the setting defaults to on");
            window.Close();
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(100);
        }
    }

    private static void LargeFile()
    {
        // 20 000 lines, one docstring with three spans in every ten.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 2000; i++)
        {
            sb.Append($"/-- Lemma {i}: $\\sum_{{k=1}}^{{n}} k^{i}$ and **bold** and $x_{i} = \\frac{{a}}{{b}}$. -/\n");
            for (int j = 0; j < 9; j++)
            {
                sb.Append($"theorem t{i}_{j} (n : Nat) : n + {j} = {j} + n := by omega\n");
            }
        }
        string text = sb.ToString();
        var sw = Stopwatch.StartNew();
        IReadOnlyList<DocSpan> spans = DocMarkup.Find(text);
        long find = sw.ElapsedMilliseconds;
        Check(spans.Count == 6000, $"finds all 6000 spans in a 20000-line file (found {spans.Count})");
        Check(find < 250, $"finding them takes {find} ms (limit 250)");

        (TextEditor editor, DocMarkupGenerator gen, Window window) = Open(text);
        try
        {
            TextView view = editor.TextArea.TextView;
            sw.Restart();
            for (int i = 0; i < 40; i++)
            {
                editor.Document.Insert(0, "x"); // an edit invalidates the spans...
                view.Redraw();
                view.EnsureVisualLines(); // ...and the next draw finds them again
            }
            long perEdit = sw.ElapsedMilliseconds / 40;
            Check(perEdit < 120, $"an edit and redraw in a 20000-line file takes {perEdit} ms (limit 120)");
            sw.Restart();
            for (int i = 0; i < 200; i++)
            {
                editor.ScrollToLine(1 + i * 90);
                view.EnsureVisualLines();
            }
            long perScroll = sw.ElapsedMilliseconds / 200;
            Check(perScroll < 60, $"scrolling to a new place takes {perScroll} ms (limit 60)");
        }
        finally
        {
            window.Close();
        }
    }
}
