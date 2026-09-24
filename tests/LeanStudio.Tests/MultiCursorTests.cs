using LeanStudio.Core.Editing;

namespace LeanStudio.Tests;

/// <summary>Several cursors at once: what each keystroke does at every one, and where new ones go.</summary>
public sealed class MultiCursorTests
{
    private static string Run(string text, IReadOnlyList<Cursor> cursors, Func<IReadOnlyList<Cursor>, (IReadOnlyList<Replacement>, IReadOnlyList<Cursor>)> op, out IReadOnlyList<Cursor> after)
    {
        (IReadOnlyList<Replacement> edits, after) = op(cursors);
        return MultiCursor.ApplyTo(text, edits);
    }

    [Fact]
    public void TypesAndDeletesAtEveryCursor()
    {
        const string text = "have h1 := a\nhave h2 := b\nhave h3 := c";
        Cursor[] ends = [Cursor.At(12), Cursor.At(25), Cursor.At(38)];
        string typed = Run(text, ends, c => MultiCursor.Type(c, " x"), out var after);
        Assert.Equal("have h1 := a x\nhave h2 := b x\nhave h3 := c x", typed);
        Assert.Equal([14, 29, 44], after.Select(c => c.Position));

        string back = Run(typed, after, c => MultiCursor.Backspace(typed, c), out after);
        Assert.Equal("have h1 := a \nhave h2 := b \nhave h3 := c ", back);
        Assert.Equal([13, 27, 41], after.Select(c => c.Position));

        // Selections are replaced: h1 h2 h3 → g1 g2 g3
        Cursor[] names = [new(5, 7), new(18, 20), new(31, 33)];
        Assert.Equal("have g := a\nhave g := b\nhave g := c", Run(text, names, c => MultiCursor.Type(c, "g"), out after));
        Assert.Equal([6, 18, 30], after.Select(c => c.Position));

        Assert.Equal("ave h1 := a\nave h2 := b\nave h3 := c", Run(text, [Cursor.At(0), Cursor.At(13), Cursor.At(26)], c => MultiCursor.Delete(text, c), out _));
    }

    [Fact]
    public void DeletesALetterOutsideTheBasicPlaneWhole()
    {
        const string text = "𝕜 → 𝕜";
        Assert.Equal(" → ", Run(text, [Cursor.At(2), Cursor.At(7)], c => MultiCursor.Backspace(text, c), out _));
        Assert.Equal(" → ", Run(text, [Cursor.At(0), Cursor.At(5)], c => MultiCursor.Delete(text, c), out _));
    }

    [Fact]
    public void MergesCursorsThatMeet()
    {
        Assert.Equal([new Cursor(0, 5)], MultiCursor.Normalize([new(0, 3), new(2, 5)]));
        Assert.Equal([Cursor.At(4)], MultiCursor.Normalize([Cursor.At(4), Cursor.At(4)]));
        Assert.Equal([Cursor.At(1), Cursor.At(4)], MultiCursor.Normalize([Cursor.At(4), Cursor.At(1)]));
        // Typing at two cursors that end up in one place types once there.
        Assert.Single(MultiCursor.Type([Cursor.At(3), Cursor.At(3)], "x").Edits);
    }

    [Fact]
    public void AddsTheNextOccurrenceAndEveryOne()
    {
        const string text = "simp [foo, bar_foo] at foo_eq ⊢\nexact foo.le";
        // Nothing selected: ⌘D selects the word, Lean-style (primes, dots inside names, not a trailing dot).
        Assert.Equal([new Cursor(6, 9)], MultiCursor.AddNextOccurrence(text, [Cursor.At(7)]));
        Assert.Equal(new Cursor(38, 44), MultiCursor.WordAt(text, 40)); // foo.le
        Assert.Equal(new Cursor(0, 1), MultiCursor.WordAt("h.", 1)); // `h.` is h
        IReadOnlyList<Cursor> two = MultiCursor.AddNextOccurrence(text, [new Cursor(6, 9)]);
        Assert.Equal([new Cursor(6, 9), new Cursor(15, 18)], two);
        IReadOnlyList<Cursor> all = MultiCursor.AllOccurrences(text, new Cursor(6, 9));
        Assert.Equal([6, 15, 23, 38], all.Select(c => c.Start));
        // It wraps around, and stops when every occurrence has a cursor.
        IReadOnlyList<Cursor> wrap = MultiCursor.AddNextOccurrence(text, [new Cursor(38, 41)]);
        Assert.Equal([38, 6], wrap.Select(c => c.Start));
        Assert.Equal(all, MultiCursor.Normalize(MultiCursor.AddNextOccurrence(text, all)));
    }

    [Fact]
    public void AddsCursorsOnTheLinesAboveAndBelow()
    {
        const string text = "theorem t :\n  a = a := by\n  rfl\n";
        Assert.Equal(Cursor.At(16), MultiCursor.OnAdjacentLine(text, Cursor.At(4), down: true)); // column 4 of line 2
        Assert.Equal(Cursor.At(31), MultiCursor.OnAdjacentLine(text, Cursor.At(20), down: true)); // line 3 is short: its end
        Assert.Equal(Cursor.At(4), MultiCursor.OnAdjacentLine(text, Cursor.At(16), down: false));
        Assert.Null(MultiCursor.OnAdjacentLine(text, Cursor.At(3), down: false));
        Assert.Equal(Cursor.At(32), MultiCursor.OnAdjacentLine(text, Cursor.At(30), down: true)); // the empty last line
        Assert.Null(MultiCursor.OnAdjacentLine(text, Cursor.At(32), down: true));
    }
}
