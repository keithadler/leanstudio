namespace LeanStudio.Core.Editing;

/// <summary>
/// Lean's Unicode input: type <c>\alpha</c> and get <c>α</c>. An abbreviation is replaced as soon as it is complete
/// and no longer one could be meant (<c>\to</c> becomes → at once), or when a character that cannot continue it is
/// typed (<c>\a</c> then space becomes α).
/// </summary>
public static class Abbreviations
{
    /// <summary>The character that starts an abbreviation: a backslash.</summary>
    public const char Leader = '\\';

    private static readonly Dictionary<string, string> BuiltIn = Build();
    private static Dictionary<string, string> _table = BuiltIn;
    private static HashSet<string> Prefixes = PrefixesOf(_table);
    private static Dictionary<string, List<string>> BySymbol = BySymbolOf(_table);

    /// <summary>
    /// Every abbreviation (without the leading backslash) and the symbol it stands for, the person's own
    /// (<see cref="SetCustom"/>) included. Case-sensitive.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Table => _table;

    /// <summary>The abbreviations Lean Studio comes with.</summary>
    public static IReadOnlyDictionary<string, string> BuiltInTable => BuiltIn;

    /// <summary>
    /// Add the person's own abbreviations (abbreviations.json), which win over built-in ones with the same name.
    /// Pass an empty dictionary to go back to the built-in table.
    /// </summary>
    public static void SetCustom(IReadOnlyDictionary<string, string> custom)
    {
        var t = new Dictionary<string, string>(BuiltIn, StringComparer.Ordinal);
        foreach ((string k, string v) in custom)
        {
            t[k] = v;
        }
        (_table, Prefixes, BySymbol) = (t, PrefixesOf(t), BySymbolOf(t));
    }

    /// <summary>
    /// Read abbreviations.json: an object of abbreviation to symbol, <c>{ "foo": "☺" }</c>, as VS Code's
    /// <c>lean4.input.customTranslations</c> has them. A leading backslash on a name is dropped. Returns the
    /// abbreviations, and a line for anything that couldn't be read.
    /// </summary>
    public static (IReadOnlyDictionary<string, string> Custom, IReadOnlyList<string> Problems) ParseCustom(string json)
    {
        var custom = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return (custom, ["abbreviations.json should be an object: { \"foo\": \"☺\" }"]);
            }
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                string name = p.Name.TrimStart(Leader);
                if (name.Length == 0 || name.Any(char.IsWhiteSpace) || p.Value.ValueKind != System.Text.Json.JsonValueKind.String || p.Value.GetString() is not { Length: > 0 } symbol)
                {
                    problems.Add($"\"{p.Name}\" needs a name without spaces and a symbol");
                    continue;
                }
                custom[name] = symbol;
            }
        }
        catch (System.Text.Json.JsonException e)
        {
            problems.Add("abbreviations.json isn't valid JSON: " + e.Message);
        }
        return (custom, problems);
    }

    private static HashSet<string> PrefixesOf(Dictionary<string, string> t) =>
        t.Keys.SelectMany(k => Enumerable.Range(1, k.Length).Select(n => k[..n])).ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, List<string>> BySymbolOf(Dictionary<string, string> t) =>
        t.GroupBy(kv => kv.Value, StringComparer.Ordinal)
         .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

    /// <summary>How to type a symbol: its abbreviations, shortest first (⊢ → ["|-", "vdash", "entails"]).</summary>
    public static IReadOnlyList<string> NamesFor(string symbol) => BySymbol.TryGetValue(symbol, out List<string>? names) ? names : [];

    /// <summary>Whether <paramref name="s"/> is an abbreviation or the start of one (without the backslash).</summary>
    public static bool IsPrefix(string s) => Prefixes.Contains(s);

    /// <summary>The symbol <paramref name="s"/> abbreviates exactly, or null if it is not a complete abbreviation.</summary>
    public static string? Lookup(string s) => Table.TryGetValue(s, out string? v) ? v : null;

    /// <summary>Is there an abbreviation strictly longer than <paramref name="s"/> that starts with it?</summary>
    public static bool HasLongerMatch(string s) =>
        Table.Keys.Any(k => k.Length > s.Length && k.StartsWith(s, StringComparison.Ordinal));

    /// <summary>
    /// What typing <paramref name="next"/> after <c>\<paramref name="pending"/></c> should do.
    /// Returns the replacement for <c>\pending</c> when it should be replaced now, else null.
    /// </summary>
    public static string? OnType(string pending, char next)
    {
        string extended = pending + next;
        if (IsPrefix(extended))
        {
            return null;
        }
        return Lookup(pending);
    }

    /// <summary>
    /// The best candidates for a partial abbreviation, for a completion hint: every abbreviation starting with
    /// <paramref name="pending"/>, as (abbreviation, symbol) pairs, shortest first.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> Candidates(string pending) =>
        Table.Where(kv => kv.Key.StartsWith(pending, StringComparison.Ordinal))
             .OrderBy(kv => kv.Key.Length)
             .ThenBy(kv => kv.Key, StringComparer.Ordinal);

    private static Dictionary<string, string> Build()
    {
        var t = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string symbol, params string[] names)
        {
            foreach (string n in names)
            {
                t[n] = symbol;
            }
        }

        // Greek, lower and upper case.
        Add("α", "a", "alpha"); Add("β", "b", "beta"); Add("γ", "g", "gamma"); Add("δ", "delta");
        Add("ε", "e", "eps", "epsilon"); Add("ζ", "z", "zeta"); Add("η", "eta"); Add("θ", "th", "theta");
        Add("ι", "i", "iota"); Add("κ", "k", "kappa"); Add("λ", "la", "lam", "lambda", "fun"); Add("μ", "m", "mu");
        Add("ν", "nu"); Add("ξ", "xi"); Add("π", "p", "pi"); Add("ρ", "rho"); Add("σ", "s", "sigma");
        Add("τ", "tau"); Add("υ", "upsilon"); Add("φ", "ph", "phi"); Add("χ", "ch", "chi"); Add("ψ", "ps", "psi");
        Add("ω", "w", "omega"); Add("ϕ", "varphi"); Add("ϵ", "varepsilon");
        Add("Γ", "G", "Gamma"); Add("Δ", "D", "Delta"); Add("Θ", "Th", "Theta"); Add("Λ", "La", "Lambda");
        Add("Ξ", "Xi"); Add("Π", "P", "Pi"); Add("Σ", "S", "Sigma"); Add("Φ", "Ph", "Phi"); Add("Ψ", "Ps", "Psi");
        Add("Ω", "W", "Omega");

        // Logic.
        Add("→", "r", "to", "->", "imp", "rightarrow"); Add("←", "l", "<-", "gets", "leftarrow");
        Add("↔", "iff", "<->", "lr", "leftrightarrow"); Add("↑", "u", "uparrow", "up"); Add("↓", "d", "downarrow", "down");
        Add("⇒", "=>", "Rightarrow"); Add("⇐", "Leftarrow"); Add("⇔", "<=>", "Leftrightarrow"); Add("↦", "mapsto", "|->");
        Add("∀", "all", "forall", "A"); Add("∃", "ex", "exists", "E"); Add("∄", "nexists");
        Add("∧", "and", "wedge", "/\\"); Add("∨", "or", "vee", "\\/"); Add("¬", "not", "neg", "n");
        Add("⊢", "|-", "vdash", "entails"); Add("⊤", "top"); Add("⊥", "bot"); Add("∎", "qed");

        // Relations.
        Add("≤", "le", "<="); Add("≥", "ge", ">="); Add("≠", "ne", "neq", "!="); Add("≈", "approx", "~~");
        Add("≃", "simeq", "equiv", "~="); Add("≅", "cong", "iso"); Add("≡", "==", "eqv", "defeq"); Add("∼", "sim", "~");
        Add("≺", "prec"); Add("≻", "succ"); Add("⊆", "sub", "subseteq", "ss"); Add("⊂", "ssub", "subset");
        Add("⊇", "sup", "supseteq"); Add("⊃", "ssup", "supset"); Add("∈", "in", "mem"); Add("∉", "notin", "nin");
        Add("∋", "ni"); Add("∣", "mid", "|", "dvd"); Add("∤", "nmid"); Add("⊊", "subsetneq"); Add("≪", "<<"); Add("≫", ">>");
        Add("⊑", "sqsubseteq"); Add("⊒", "sqsupseteq"); Add("⋖", "lessdot"); Add("⋗", "gtrdot");

        // Operations.
        Add("∘", "o", "circ", "comp"); Add("×", "x", "times", "prod'"); Add("·", ".", "cdot", "centerdot");
        Add("•", "bu", "bullet", "smul"); Add("∩", "cap", "inter"); Add("∪", "cup", "union", "un");
        Add("⋂", "bigcap", "Inter", "iInter"); Add("⋃", "bigcup", "Union", "iUnion"); Add("⊓", "sqcap", "inf");
        Add("⊔", "sqcup", "sup'"); Add("⨅", "iInf", "biginf"); Add("⨆", "iSup", "bigsup"); Add("∑", "sum", "Sum");
        Add("∏", "prod", "Prod"); Add("∐", "coprod"); Add("⊕", "oplus", "o+"); Add("⊗", "otimes", "ox", "o*");
        Add("⊖", "ominus", "o-"); Add("⊙", "odot"); Add("∫", "int", "integral"); Add("∂", "partial"); Add("∇", "nabla");
        Add("√", "sqrt"); Add("∞", "inf'", "infty"); Add("∅", "empty", "emptyset"); Add("∖", "\\", "setminus", "sdiff");
        Add("ᶜ", "^c", "compl"); Add("⁻¹", "-1", "^-1", "inv"); Add("⬝", "dot"); Add("▸", "t", "tri", "subst");
        Add("⟨", "<", "langle", "("); Add("⟩", ">", "rangle", ")"); Add("⟪", "<<'", "llangle"); Add("⟫", ">>'", "rrangle");
        Add("⌊", "lfloor", "floor"); Add("⌋", "rfloor"); Add("⌈", "lceil", "ceil"); Add("⌉", "rceil");
        Add("‖", "||", "norm"); Add("†", "dagger"); Add("′", "'", "prime"); Add("∗", "ast", "*");
        Add("⋆", "star"); Add("◃", "lhd'"); Add("⊣", "-|", "dashv"); Add("⊸", "-o", "multimap");
        Add("⟶", "hom", "-->"); Add("⥤", "func", "functor"); Add("≌", "equivalence"); Add("⋙", ">>>", "ggg");
        Add("𝟙", "b1", "one", "bb1"); Add("⧸", "quot", "/"); Add("∠", "angle"); Add("∆", "triangle");
        Add("ℵ", "aleph"); Add("ℏ", "hbar"); Add("ℓ", "ell"); Add("℘", "wp");

        // Blackboard bold.
        Add("ℕ", "N", "nat", "Nat", "bN"); Add("ℤ", "Z", "int'", "Int", "bZ"); Add("ℚ", "Q", "rat", "Rat", "bQ");
        Add("ℝ", "R", "real", "Real", "bR"); Add("ℂ", "C", "complex", "Complex", "bC"); Add("𝔽", "bF"); Add("𝕜", "bk", "kk");
        Add("ℍ", "H", "bH"); Add("𝔼", "bE"); Add("ℙ", "bP"); Add("𝕂", "bK");

        // Subscripts and superscripts.
        string[] sub = ["₀", "₁", "₂", "₃", "₄", "₅", "₆", "₇", "₈", "₉"];
        string[] sup = ["⁰", "¹", "²", "³", "⁴", "⁵", "⁶", "⁷", "⁸", "⁹"];
        for (int d = 0; d < 10; d++)
        {
            Add(sub[d], "_" + d);
            Add(sup[d], "^" + d);
        }
        Add("ₐ", "_a"); Add("ₑ", "_e"); Add("ₕ", "_h"); Add("ᵢ", "_i"); Add("ⱼ", "_j"); Add("ₖ", "_k"); Add("ₗ", "_l");
        Add("ₘ", "_m"); Add("ₙ", "_n"); Add("ₒ", "_o"); Add("ₚ", "_p"); Add("ᵣ", "_r"); Add("ₛ", "_s"); Add("ₜ", "_t");
        Add("ᵤ", "_u"); Add("ᵥ", "_v"); Add("ₓ", "_x"); Add("₊", "_+"); Add("₋", "_-"); Add("₌", "_="); Add("₍", "_("); Add("₎", "_)");
        Add("ᵃ", "^a"); Add("ᵇ", "^b"); Add("ᵈ", "^d"); Add("ᵉ", "^e"); Add("ᶠ", "^f"); Add("ᵍ", "^g"); Add("ʰ", "^h");
        Add("ⁱ", "^i"); Add("ʲ", "^j"); Add("ᵏ", "^k"); Add("ˡ", "^l"); Add("ᵐ", "^m"); Add("ⁿ", "^n"); Add("ᵒ", "^o");
        Add("ᵖ", "^p"); Add("ʳ", "^r"); Add("ˢ", "^s"); Add("ᵗ", "^t"); Add("ᵘ", "^u"); Add("ᵛ", "^v"); Add("ʷ", "^w");
        Add("ˣ", "^x"); Add("ʸ", "^y"); Add("ᶻ", "^z"); Add("⁺", "^+"); Add("⁻", "^-"); Add("⁼", "^="); Add("ᵒᵈ", "^od", "od");
        Add("ᵐᵒᵖ", "^mop", "mop"); Add("ᵃᵒᵖ", "^aop", "aop"); Add("ᵒᵖ", "^op", "op");

        // Script, fraktur and a few calligraphic capitals used in Mathlib.
        Add("𝒜", "McA"); Add("ℬ", "McB"); Add("𝒞", "McC"); Add("𝒟", "McD"); Add("ℱ", "McF"); Add("𝒢", "McG");
        Add("ℋ", "McH"); Add("ℒ", "McL"); Add("𝒩", "McN"); Add("𝒪", "McO"); Add("𝒫", "McP", "powerset"); Add("𝒮", "McS");
        Add("𝒰", "McU"); Add("𝓝", "nhds", "MCN"); Add("𝓤", "uniformity", "MCU"); Add("𝔞", "fraka"); Add("𝔟", "frakb");
        Add("𝔠", "frakc"); Add("𝔪", "frakm"); Add("𝔭", "frakp"); Add("𝔮", "frakq");

        // Arrows and brackets used for specific Lean notations.
        Add("↪", "embed", "into", "hookrightarrow"); Add("↠", "onto", "twoheadrightarrow"); Add("⇑", "Uparrow", "coe'");
        Add("↥", "upr", "coeSort"); Add("⟹", "==>", "implies"); Add("⟶", "hom"); Add("⥲", "->~");
        Add("≃ₗ", "equivl"); Add("→ₗ", "->l", "linearmap"); Add("→+", "->+"); Add("→*", "->*"); Add("⦃", "{{", "f{");
        Add("⦄", "}}", "f}"); Add("⟦", "[[", "llbracket"); Add("⟧", "]]", "rrbracket"); Add("‹", "f<"); Add("›", "f>");
        Add("«", "f<<"); Add("»", "f>>"); Add("⌜", "ulc"); Add("⌝", "urc"); Add("⊤ₐ", "topa");
        Add("✝", "cross", "inaccessible");
        return t;
    }
}
