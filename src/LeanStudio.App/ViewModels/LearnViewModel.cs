using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Editing;
using LeanStudio.Core.Learn;

namespace LeanStudio.App.ViewModels;

public sealed partial class LessonView : ObservableObject
{
    public LessonView(Lesson lesson, int number)
    {
        Lesson = lesson;
        Number = number;
    }

    public Lesson Lesson { get; }
    public int Number { get; }
    public string Title => $"{Number}. {Lesson.Title}";
    public string Summary => Lesson.Summary;
    public string ExerciseLabel => Lesson.Exercises == 1 ? "1 exercise" : $"{Lesson.Exercises} exercises";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mark))]
    private bool _done;

    public string Mark => Done ? "✓" : "○";
}

public sealed record SymbolButton(string Symbol, string Name, string Typing)
{
    public string Tip => $"{Name} — type {Typing}";
}

public sealed record SymbolGroup(string Title, IReadOnlyList<SymbolButton> Symbols);

/// <summary>
/// The Learn tab, for someone new to Lean: the tutorial and its progress, the playground, famous theorems to
/// meet, a palette of the symbols Lean uses (and how to type them), and snippets of common code.
/// </summary>
public sealed partial class LearnViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public LearnViewModel(MainViewModel main)
    {
        _main = main;
        Lessons.Reset(Tutorial.Lessons.Select((l, i) => new LessonView(l, i + 1) { Done = main.Settings.CompletedLessons.Contains(l.FileName) }));
        Theorems.Reset(TheoremGallery.All);
        UpdateProgress();
    }

    public ObservableList<LessonView> Lessons { get; } = new();
    public ObservableList<FamousTheorem> Theorems { get; } = new();

    public IReadOnlyList<SymbolGroup> SymbolGroups { get; } = BuildSymbols();

    [ObservableProperty]
    private FamousTheorem? _selectedTheorem;

    [ObservableProperty]
    private bool _hasSelectedTheorem;

    [ObservableProperty]
    private string _progress = "";

    [ObservableProperty]
    private string _status = "";

    partial void OnSelectedTheoremChanged(FamousTheorem? value) => HasSelectedTheorem = value is not null;

    private void UpdateProgress()
    {
        int done = Lessons.Count(l => l.Done);
        Progress = done == 0 ? "Ten short lessons, no experience needed."
            : done == Lessons.Count ? "All ten lessons done. 🎉"
            : $"{done} of {Lessons.Count} lessons done";
    }

    [RelayCommand]
    private async Task StartTutorialAsync()
    {
        LessonView next = Lessons.FirstOrDefault(l => !l.Done) ?? Lessons[0];
        await OpenLessonAsync(next);
    }

    [RelayCommand]
    private async Task OpenLessonAsync(LessonView? lesson)
    {
        if (lesson is null)
        {
            return;
        }
        string folder = await Tutorial.CreateAsync();
        await _main.OpenFileAsync(System.IO.Path.Combine(folder, lesson.Lesson.FileName));
        Status = $"Lesson {lesson.Number}: replace each sorry. The ✓ appears when Lean accepts the whole file.";
    }

    [RelayCommand]
    private async Task OpenPlaygroundAsync()
    {
        string file = await Playground.CreateAsync();
        DocumentViewModel? d = await _main.OpenFileAsync(file);
        if (d is not null)
        {
            d.Reveal(Math.Max(0, d.Document.LineCount - 1), 0);
        }
    }

    [RelayCommand]
    private async Task TryTheoremAsync()
    {
        if (SelectedTheorem is not FamousTheorem t)
        {
            return;
        }
        if (t.NeedsMathlib && _main.Project?.DependsOnMathlib != true)
        {
            Status = $"{t.LeanName} is in Mathlib. Open a project that uses Mathlib (File ▸ New Project, template \"Library using Mathlib\"), then try again.";
            return;
        }
        if (t.NeedsMathlib && _main.Project is not null)
        {
            // Inside a Mathlib project the Library tab has it, with its statement, axioms and source.
            _main.Navigator.Query = t.LeanName;
            await _main.Navigator.ShowAsync(t.LeanName);
            _main.SidebarTab = MainViewModel.LibraryTab;
            return;
        }
        // Into the open playground, where the person is looking; it is saved so Lean and the disk agree.
        string file = await Playground.CreateAsync();
        DocumentViewModel? d = await _main.OpenFileAsync(file);
        if (d is not null)
        {
            string code = "\n" + t.PlaygroundCode;
            d.Document.Insert(d.Document.TextLength, d.Document.Text.EndsWith('\n') ? code : "\n" + code);
            await d.SaveAsync();
            d.Reveal(Math.Max(0, d.Document.LineCount - 3), 0);
        }
        Status = $"Added {t.LeanName} to the playground: #check shows its statement, #print axioms what it rests on.";
    }

    [RelayCommand]
    private void InsertSymbol(SymbolButton? s)
    {
        if (s is not null)
        {
            _main.RequestInsert(s.Symbol);
        }
    }

    /// <summary>Called when Lean finishes a file: a tutorial lesson with no errors and no sorry is done.</summary>
    public void FileChecked(DocumentViewModel doc)
    {
        if (Tutorial.LessonFor(doc.Path) is not Lesson lesson
            || !string.Equals(System.IO.Path.GetDirectoryName(doc.Path), Tutorial.DefaultFolder, StringComparison.Ordinal))
        {
            return;
        }
        bool solved = !doc.Diagnostics.Any(d => d.Severity == Lsp.DiagnosticSeverity.Error || d.Message.Contains("sorry", StringComparison.Ordinal));
        LessonView view = Lessons.First(l => l.Lesson == lesson);
        if (solved && !view.Done)
        {
            view.Done = true;
            _main.Settings.CompletedLessons.Add(lesson.FileName);
            _main.Settings.Save();
            UpdateProgress();
            int next = view.Number < Lessons.Count ? view.Number + 1 : 0;
            Status = next > 0 ? $"Lesson {view.Number} done! Next: {Lessons[next - 1].Title}" : "You finished the tutorial! 🎉";
            _main.Log($"Tutorial: lesson {view.Number} ({lesson.Title}) solved.");
        }
    }

    [RelayCommand]
    private async Task ResetLessonAsync(LessonView? lesson)
    {
        if (lesson is null)
        {
            return;
        }
        string folder = await Tutorial.CreateAsync();
        Tutorial.Reset(folder, lesson.Lesson);
        lesson.Done = false;
        _main.Settings.CompletedLessons.Remove(lesson.Lesson.FileName);
        _main.Settings.Save();
        UpdateProgress();
        Status = $"Lesson {lesson.Number} is back to its start.";
    }

    private static IReadOnlyList<SymbolGroup> BuildSymbols()
    {
        SymbolButton S(string symbol, string name)
        {
            IReadOnlyList<string> names = Abbreviations.NamesFor(symbol);
            return new SymbolButton(symbol, name, names.Count == 0 ? "(no shortcut)" : "\\" + names[0]);
        }
        return
        [
            new("Logic", [S("∀", "for all"), S("∃", "there exists"), S("→", "implies"), S("↔", "if and only if"), S("∧", "and"), S("∨", "or"), S("¬", "not"), S("⊢", "goal (turnstile)"), S("⊤", "true / top"), S("⊥", "false / bottom")]),
            new("Numbers", [S("ℕ", "natural numbers"), S("ℤ", "integers"), S("ℚ", "rationals"), S("ℝ", "real numbers"), S("ℂ", "complex numbers"), S("≤", "at most"), S("≥", "at least"), S("≠", "not equal"), S("∣", "divides"), S("√", "square root"), S("∑", "sum"), S("∏", "product")]),
            new("Sets", [S("∈", "is in"), S("∉", "is not in"), S("⊆", "subset"), S("⊂", "strict subset"), S("∩", "intersection"), S("∪", "union"), S("∅", "empty set"), S("ᶜ", "complement"), S("×", "product")]),
            new("Brackets and functions", [S("⟨", "anonymous constructor ⟨a, b⟩"), S("⟩", "close ⟩"), S("λ", "function (fun)"), S("↦", "maps to"), S("∘", "composition"), S("⁻¹", "inverse"), S("•", "scalar multiplication"), S("·", "placeholder / bullet")]),
            new("Greek", [S("α", "alpha"), S("β", "beta"), S("γ", "gamma"), S("δ", "delta"), S("ε", "epsilon"), S("θ", "theta"), S("λ", "lambda"), S("μ", "mu"), S("π", "pi"), S("σ", "sigma"), S("φ", "phi"), S("ω", "omega")]),
            new("Subscripts", [S("₀", "sub 0"), S("₁", "sub 1"), S("₂", "sub 2"), S("ₙ", "sub n"), S("²", "squared"), S("³", "cubed")]),
        ];
    }
}
