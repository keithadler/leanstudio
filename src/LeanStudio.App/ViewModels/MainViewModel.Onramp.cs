using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Editing;
using LeanStudio.Core.Learn;

namespace LeanStudio.App.ViewModels;

/// <summary>The onramp for a beginner: hints, puzzles, practice, badges, plain-English readings and warnings (the Learn menu).</summary>
public sealed partial class MainViewModel
{
    private string? _hintGoal;
    private int _hintStep;
    private int? _nextPuzzle;
    private readonly Dictionary<int, int> _puzzleHints = new();

    /// <summary>
    /// A hint for the goal at the cursor, a little bigger each time it is asked for the same goal: first what the goal says in
    /// words, then what kind of goal it is, then the tactic to try. Never the whole proof.
    /// </summary>
    [RelayCommand]
    public void HintForGoal()
    {
        if (!Info.HasGoals || Info.Goals.Count == 0)
        {
            Log("Hint: put the cursor in a proof, on a line after `by`, so there is a goal to give a hint about.");
            return;
        }
        string goal = Info.Goals[0].Target;
        IReadOnlyList<string> hints = GoalHints.For(goal);
        _hintStep = goal == _hintGoal ? Math.Min(_hintStep + 1, hints.Count) : 1;
        _hintGoal = goal;
        Log($"Hint {_hintStep} of {hints.Count}: {hints[_hintStep - 1]}" + (_hintStep < hints.Count ? "   (ask again for a bigger one)" : ""));
    }

    /// <summary>Read the declaration at the cursor aloud in plain English: what it takes, what it gives, what it claims.</summary>
    [RelayCommand]
    public void ExplainDeclaration()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        Log(DeclExplain.ReadAt(d.Document.Text, d.CaretLine) is string s
            ? "In plain words: " + s
            : "Explain: put the cursor on a `def`, `theorem`, `structure` or `inductive` (or in its body).");
    }

    /// <summary>Look for Lean 3 in the active file (it looks much like Lean 4 and does not run in it) and say what each is now.</summary>
    [RelayCommand]
    public void CheckForLean3()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        IReadOnlyList<Lean3Finding> found = Lean3.Find(d.Document.Text);
        foreach (Lean3Finding f in found)
        {
            Log($"  line {f.Line + 1}: {f.Found}: {f.Lean4}");
        }
        Log(found.Count == 0 ? "Lean 3: nothing here looks like Lean 3."
            : $"Lean 3: {found.Count} thing{(found.Count == 1 ? "" : "s")} here {(found.Count == 1 ? "is" : "are")} Lean 3 (most tutorials online are). Lean 4 says it the way described above.");
    }

    /// <summary>List the surprises in the active file that catch beginners: natural-number subtraction, rounding division, == in a statement.</summary>
    [RelayCommand]
    public void ShowGotchas()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        IReadOnlyList<Gotcha> found = Gotchas.Find(d.Document.Text);
        foreach (Gotcha g in found)
        {
            Log($"  line {g.Line + 1}: {g.Title}. {g.Explanation}");
        }
        Log(found.Count == 0 ? "Surprises: nothing here that usually surprises a beginner." : $"Surprises: {found.Count} found. None of them is a mistake; they are how Lean works.");
    }

    /// <summary>A cheat sheet of the tactics and keywords the active file uses, each explained, written to Output.</summary>
    [RelayCommand]
    public void MakeCheatSheet()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        foreach (string line in CheatSheet.ToMarkdown(d.Document.Text).TrimEnd().Split('\n'))
        {
            Log(line);
        }
    }

    /// <summary>Open a worksheet for practising the unicode symbols: type what each line asks for, in the editor, with the real abbreviation input.</summary>
    [RelayCommand]
    public async Task PracticeSymbolsAsync()
    {
        string anchor = await Playground.CreateAsync();
        string file = Path.Combine(Path.GetDirectoryName(anchor)!, "SymbolPractice.lean");
        await File.WriteAllTextAsync(file, SymbolTrainer.Worksheet(8, DateTime.Now.DayOfYear));
        if (await OpenFileAsync(file) is DocumentViewModel d)
        {
            d.Reveal(8, 0);
        }
    }

    /// <summary>Mark the symbol worksheet in the active file: what was typed right, and what each wrong one should have been.</summary>
    [RelayCommand]
    public void CheckSymbolPractice()
    {
        if (ActiveDocument is not { } d)
        {
            return;
        }
        (int right, int answered, IReadOnlyList<string> wrong) = SymbolTrainer.Grade(d.Document.Text);
        foreach (string w in wrong)
        {
            Log("  " + w);
        }
        Log(answered == 0 ? "Symbols: type a symbol after an arrow in the practice sheet first (Learn ▸ Practice Typing Symbols)."
            : $"Symbols: {right} of {answered} right." + (right == answered ? " Well done." : " Type the backslash word and a space, and the symbol appears."));
    }

    /// <summary>Show which small firsts the Lean files (the project's, the playground's and the one open) have earned, and what to try next.</summary>
    [RelayCommand]
    public void ShowBadges()
    {
        var texts = new List<string>();
        void Add(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                try
                {
                    texts.Add(File.ReadAllText(path));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // a file that cannot be read earns nothing
                }
            }
        }
        if (Project is not null)
        {
            Add(ProjectSearch.Files(Project.Root, leanOnly: true));
        }
        if (Directory.Exists(Playground.DefaultFolder))
        {
            Add(Directory.EnumerateFiles(Playground.DefaultFolder, "*.lean"));
        }
        texts.AddRange(Documents.Where(x => x.IsLean).Select(x => x.Document.Text));
        foreach (string line in Achievements.ToText(Achievements.Earned(texts)).Split('\n'))
        {
            Log(line);
        }
    }

    /// <summary>Open the next proof puzzle (today's first) in the playground's folder: replace its sorry so Lean accepts it.</summary>
    [RelayCommand]
    public async Task NewPuzzleAsync()
    {
        int index = _nextPuzzle ?? Puzzles.DailyIndex(DateOnly.FromDateTime(DateTime.Today));
        _nextPuzzle = (index + 1) % Puzzles.All.Count;
        string anchor = await Playground.CreateAsync();
        string file = Path.Combine(Path.GetDirectoryName(anchor)!, $"Puzzle{index + 1}.lean");
        if (!File.Exists(file))
        {
            await File.WriteAllTextAsync(file, Puzzles.File(index));
        }
        _puzzleHints[index] = 0;
        if (await OpenFileAsync(file) is DocumentViewModel d)
        {
            d.Reveal(3, 0);
        }
        Log($"Puzzle {index + 1} of {Puzzles.All.Count}: {Puzzles.All[index].Title}. Replace the sorry. Stuck? Learn ▸ Puzzle Hint.");
    }

    /// <summary>The next hint for the puzzle in the active file, each a little bigger than the one before.</summary>
    [RelayCommand]
    public void PuzzleHint()
    {
        if (ActiveDocument is not { } d || Puzzles.IndexOf(d.Document.Text) is not int index)
        {
            Log("Puzzle hint: open a puzzle first (Learn ▸ New Puzzle).");
            return;
        }
        IReadOnlyList<string> hints = Puzzles.All[index].Hints;
        int step = Math.Min(_puzzleHints.GetValueOrDefault(index) + 1, hints.Count);
        _puzzleHints[index] = step;
        Log($"Puzzle {index + 1}, hint {step} of {hints.Count}: {hints[step - 1]}");
    }

    /// <summary>Show how the puzzle in the active file is solved. For when you have really tried.</summary>
    [RelayCommand]
    public void ShowPuzzleSolution()
    {
        if (ActiveDocument is not { } d || Puzzles.IndexOf(d.Document.Text) is not int index)
        {
            Log("Puzzle solution: open a puzzle first (Learn ▸ New Puzzle).");
            return;
        }
        Log($"Puzzle {index + 1}, one solution: replace `sorry` with");
        foreach (string line in Puzzles.All[index].Solution.Split('\n'))
        {
            Log("    " + line.Trim());
        }
    }

    /// <summary>The ideas of another language written out in Lean, for someone who already programs: python, haskell, javascript or rust.</summary>
    /// <param name="language">The language.</param>
    [RelayCommand]
    public void ShowRosetta(string? language)
    {
        string? md = Rosetta.ToMarkdown(language ?? "");
        if (md is null)
        {
            Log("Coming from: python, haskell, javascript or rust.");
            return;
        }
        foreach (string line in md.TrimEnd().Split('\n'))
        {
            Log(line);
        }
    }

    /// <summary>A small thing that makes Lean nicer: the tip of the day.</summary>
    [RelayCommand]
    public void ShowTip() => Log("Tip of the day: " + Tips.ForDate(DateOnly.FromDateTime(DateTime.Today)));
}
