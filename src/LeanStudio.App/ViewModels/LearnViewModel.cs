using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Editing;
using LeanStudio.Core.Learn;

namespace LeanStudio.App.ViewModels;

/// <summary>A tutorial lesson in the Learn panel's list, with whether it is done.</summary>
public sealed partial class LessonView : ObservableObject
{
    /// <summary>A view of a lesson, not yet done.</summary>
    /// <param name="lesson">The lesson.</param>
    /// <param name="number">Its 1-based number in the tutorial.</param>
    public LessonView(Lesson lesson, int number)
    {
        Lesson = lesson;
        Number = number;
    }

    /// <summary>The lesson: its title, summary, file and exercises.</summary>
    public Lesson Lesson { get; }
    /// <summary>The lesson's 1-based number.</summary>
    public int Number { get; }
    /// <summary>The number and title, as <c>3. Title</c>.</summary>
    public string Title => $"{Number}. {Lesson.Title}";
    /// <summary>What the lesson teaches.</summary>
    public string Summary => Lesson.Summary;
    /// <summary>The number of exercises, as <c>N exercises</c>.</summary>
    public string ExerciseLabel => Lesson.Exercises == 1 ? "1 exercise" : $"{Lesson.Exercises} exercises";

    /// <summary>Lean accepted the lesson's file with no errors and no sorry. Remembered in the settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mark))]
    private bool _done;

    /// <summary>✓ when done, ○ otherwise.</summary>
    public string Mark => Done ? "✓" : "○";
}

/// <summary>A symbol in the Learn panel's palette.</summary>
/// <param name="Symbol">The symbol, inserted at the caret when clicked.</param>
/// <param name="Name">What it means, in words.</param>
/// <param name="Typing">How to type it in the editor, such as <c>\forall</c>, or <c>(no shortcut)</c>.</param>
public sealed record SymbolButton(string Symbol, string Name, string Typing)
{
    /// <summary>The tooltip: its meaning and how to type it.</summary>
    public string Tip => $"{Name} — type {Typing}";
}

/// <summary>A titled group of symbols in the palette.</summary>
/// <param name="Title">The group's title, such as <c>Logic</c>.</param>
/// <param name="Symbols">The symbols, in order.</param>
public sealed record SymbolGroup(string Title, IReadOnlyList<SymbolButton> Symbols);

/// <summary>
/// The Learn tab, for someone new to Lean: the tutorial and its progress, the playground, famous theorems to
/// meet, a palette of the symbols Lean uses (and how to type them), and snippets of common code.
/// </summary>
/// <remarks>
/// The lessons, the famous theorems and the symbols are built into Lean Studio; which lessons are done is kept in
/// <see cref="MainViewModel.Settings"/>. Lessons and the playground are files created in a folder of their own and
/// opened through <see cref="MainViewModel"/>, which calls <see cref="FileChecked"/> each time Lean finishes one.
/// </remarks>
public sealed partial class LearnViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    /// <summary>Build the lists, marking the lessons the settings say are done.</summary>
    /// <param name="main">The window's view model, to open files, insert text and save progress through.</param>
    public LearnViewModel(MainViewModel main)
    {
        _main = main;
        Lessons.Reset(Tutorial.Lessons.Select((l, i) => new LessonView(l, i + 1) { Done = main.Settings.CompletedLessons.Contains(l.FileName) }));
        Theorems.Reset(TheoremGallery.All);
        UpdateProgress();
    }

    /// <summary>The tutorial's lessons, in order.</summary>
    public ObservableList<LessonView> Lessons { get; } = new();
    /// <summary>Famous theorems to look at, and try in the playground or the Library.</summary>
    public ObservableList<FamousTheorem> Theorems { get; } = new();

    /// <summary>The symbol palette, by group, with how to type each symbol.</summary>
    public IReadOnlyList<SymbolGroup> SymbolGroups { get; } = BuildSymbols();

    /// <summary>The theorem selected in the list, or null.</summary>
    [ObservableProperty]
    private FamousTheorem? _selectedTheorem;

    /// <summary>A theorem is selected.</summary>
    [ObservableProperty]
    private bool _hasSelectedTheorem;

    /// <summary>How far through the tutorial the person is, in words.</summary>
    [ObservableProperty]
    private string _progress = "";

    /// <summary>What just happened, or what to do next, below the lists.</summary>
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

    /// <summary>Open the first lesson not yet done (or the first, when all are).</summary>
    [RelayCommand]
    private async Task StartTutorialAsync()
    {
        LessonView next = Lessons.FirstOrDefault(l => !l.Done) ?? Lessons[0];
        await OpenLessonAsync(next);
    }

    /// <summary>
    /// Open a lesson's file, writing out any lesson files that are missing first (existing ones keep the person's
    /// work).
    /// </summary>
    /// <param name="lesson">The lesson; null does nothing.</param>
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

    /// <summary>Open the playground file (creating it if needed) with the caret at its end.</summary>
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

    /// <summary>
    /// Try the selected theorem. One from Mathlib is shown in the Library in a project that uses Mathlib (and otherwise
    /// explained); any other is added to the end of the playground, which is saved.
    /// </summary>
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

    /// <summary>Insert a palette symbol at the editor's caret.</summary>
    /// <param name="s">The symbol; null does nothing.</param>
    [RelayCommand]
    private void InsertSymbol(SymbolButton? s)
    {
        if (s is not null)
        {
            _main.RequestInsert(s.Symbol);
        }
    }

    /// <summary>Called when Lean finishes a file: a tutorial lesson with no errors and no sorry is done.</summary>
    /// <remarks>
    /// Marks it done and saves the settings the first time. Files outside the tutorial's folder are ignored.
    /// </remarks>
    /// <param name="doc">The file Lean finished checking.</param>
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

    /// <summary>Put a lesson's file back as it started (overwriting the person's work) and mark it not done.</summary>
    /// <param name="lesson">The lesson; null does nothing.</param>
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
