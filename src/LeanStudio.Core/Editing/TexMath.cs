using System.Text;

namespace LeanStudio.Core.Editing;

/// <summary>What a piece of a formula is, which decides its spacing and whether limits stack above and below it.</summary>
public enum TexKind
{
    /// <summary>An ordinary symbol or number.</summary>
    Ord,
    /// <summary>A binary operator, <c>+</c> or <c>×</c>, with space either side.</summary>
    Bin,
    /// <summary>A relation, <c>=</c> or <c>≤</c>, with more space either side.</summary>
    Rel,
    /// <summary>A large operator, <c>∑ ∏ ∫</c>, drawn larger, its limits above and below in display style.</summary>
    BigOp,
    /// <summary>A named function such as <c>log</c>, upright.</summary>
    Func,
    /// <summary>An opening or closing bracket.</summary>
    Fence,
    /// <summary>A variable, drawn in italics.</summary>
    Var,
    /// <summary>Upright text, from <c>\text{…}</c> or <c>\mathrm{…}</c>.</summary>
    Text,
    /// <summary>Space of the given width in ems (<c>\quad</c>).</summary>
    Space,
}

/// <summary>A node of a parsed formula.</summary>
public abstract record TexNode;

/// <summary>A run of symbols drawn side by side.</summary>
public sealed record TexRow(IReadOnlyList<TexNode> Items) : TexNode;

/// <summary>One symbol or word.</summary>
public sealed record TexAtom(string Text, TexKind Kind, double Em = 0) : TexNode;

/// <summary>A base with a subscript, a superscript, or both.</summary>
public sealed record TexScript(TexNode Base, TexNode? Sub, TexNode? Sup) : TexNode;

/// <summary>A fraction, or with <paramref name="Rule"/> off the stacked pair of a binomial coefficient.</summary>
public sealed record TexFrac(TexNode Numerator, TexNode Denominator, bool Rule = true) : TexNode;

/// <summary>A square root, or an nth root when <paramref name="Index"/> is set.</summary>
public sealed record TexSqrt(TexNode Body, TexNode? Index) : TexNode;

/// <summary>How the columns of a <see cref="TexMatrix"/> line up.</summary>
public enum TexColumns
{
    /// <summary>Each cell centred, as in a matrix.</summary>
    Center,
    /// <summary>Each cell left-aligned, as in cases.</summary>
    Left,
    /// <summary>Columns alternately right- and left-aligned with no space inside a pair, as in aligned.</summary>
    Aligned,
}

/// <summary>A grid of formulas: a matrix, cases or aligned lines, with the brackets round it, if any.</summary>
public sealed record TexMatrix(IReadOnlyList<IReadOnlyList<TexNode>> Rows, TexColumns Columns, string Open, string Close) : TexNode;

/// <summary>A formula with a label above or below it, optionally under a brace: overset, underbrace and the like.</summary>
public sealed record TexStack(TexNode Body, TexNode? Over, TexNode? Under, bool Brace) : TexNode;

/// <summary>A formula wrapped in brackets that grow with it, from <c>\left( … \right)</c>.</summary>
public sealed record TexFenced(string Open, TexNode Body, string Close) : TexNode;

/// <summary>A formula with a mark over it: <c>\overline</c>, <c>\hat</c>, <c>\bar</c>, <c>\vec</c>.</summary>
public sealed record TexAccent(TexNode Body, string Mark) : TexNode;

/// <summary>
/// The LaTeX that Lean docstrings use (<c>\sum_{k=1}^n k^3</c>, <c>\frac{n(n+1)}{2}</c>, <c>\mathbb{N}</c>,
/// <c>\cdots</c>, <c>\left( … \right)</c>) read into a tree an editor can lay out and draw. It is not TeX: macros,
/// environments such as <c>align</c>, and anything unknown are kept as the plain text of the command, so a formula
/// always parses and nothing is ever lost, only drawn plainly. <see cref="Parse"/> never throws.
/// </summary>
public static class TexMath
{
    private static readonly HashSet<string> Relations = new(StringComparer.Ordinal)
    {
        "=", "<", ">", "≤", "≥", "≠", "≈", "≡", "∼", "≅", "∝", "∈", "∉", "⊂", "⊆", "⊃", "⊇", "→", "←", "⇒", "⇔", "↔", "↦", "∣", "∤", "⊥", ":",
    };

    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal) { "+", "-", "−", "×", "·", "±", "∓", "∪", "∩", "∖", "∧", "∨", "∘", "*" };

    private static readonly HashSet<string> Big = new(StringComparer.Ordinal) { "∑", "∏", "∫", "∮", "⋃", "⋂", "⨁", "⨂" };

    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "log", "ln", "exp", "sin", "cos", "tan", "cot", "sec", "csc", "sinh", "cosh", "tanh", "arcsin", "arccos", "arctan", "lim", "limsup", "liminf", "max", "min", "sup", "inf", "det", "gcd", "dim", "ker", "deg", "Pr", "mod", "arg", "hom", "Re", "Im", "id", "rank", "tr",
    };

    /// <summary><c>\bigcup</c> and its relatives, which Unicode input does not spell the way LaTeX does.</summary>
    private static readonly Dictionary<string, string> BigNames = new(StringComparer.Ordinal)
    {
        ["bigcup"] = "⋃", ["bigcap"] = "⋂", ["oint"] = "∮", ["bigoplus"] = "⨁", ["bigotimes"] = "⨂", ["coprod"] = "∐",
    };

    private static readonly Dictionary<char, string> Blackboard = new()
    {
        ['N'] = "ℕ", ['Z'] = "ℤ", ['Q'] = "ℚ", ['R'] = "ℝ", ['C'] = "ℂ", ['P'] = "ℙ", ['H'] = "ℍ", ['F'] = "𝔽", ['K'] = "𝕜", ['E'] = "𝔼",
    };

    /// <summary>Read <paramref name="latex"/> (without its dollar signs).</summary>
    public static TexNode Parse(string latex)
    {
        var p = new Parser(latex);
        return p.ParseRow(stopAtClose: false);
    }

    /// <summary>Whether <paramref name="node"/> draws nothing.</summary>
    public static bool IsEmpty(TexNode node) => node is TexRow { Items.Count: 0 };

    private sealed class Parser(string s)
    {
        private int _i;
        private int _depth;
        private int _cells;

        public TexNode ParseRow(bool stopAtClose)
        {
            var items = new List<TexNode>();
            while (_i < s.Length)
            {
                char c = s[_i];
                if (c == '}' )
                {
                    if (stopAtClose)
                    {
                        break;
                    }
                    _i++; // a stray close brace is dropped
                    continue;
                }
                if (c == '\\' && Peek("\\right"))
                {
                    break; // belongs to the \left that is reading this row
                }
                if (_cells > 0 && (c == '&' || (c == '\\' && (Peek("\\end") || (_i + 1 < s.Length && s[_i + 1] == '\\')))))
                {
                    break; // the end of a cell or row of an environment
                }
                if (c == '&')
                {
                    _i++;
                    items.Add(new TexAtom("", TexKind.Space, 0.5));
                    continue;
                }
                TexNode? atom = ParseAtom();
                if (atom is null)
                {
                    continue;
                }
                atom = ParseScripts(atom);
                items.Add(atom);
            }
            return items.Count == 1 ? items[0] : new TexRow(items);
        }

        private bool Peek(string text) => string.CompareOrdinal(s, _i, text, 0, text.Length) == 0
            && (_i + text.Length >= s.Length || !char.IsLetter(s[_i + text.Length]));

        /// <summary>Any <c>_</c> and <c>^</c> after <paramref name="b"/>, in either order.</summary>
        private TexNode ParseScripts(TexNode b)
        {
            TexNode? sub = null, sup = null;
            while (true)
            {
                SkipSpaces();
                if (_i >= s.Length || (s[_i] != '_' && s[_i] != '^') || _depth > 40)
                {
                    break;
                }
                bool isSup = s[_i] == '^';
                _i++;
                SkipSpaces();
                _depth++;
                TexNode arg = ParseArgument();
                _depth--;
                if (isSup)
                {
                    sup = sup is null ? arg : new TexRow([sup, arg]);
                }
                else
                {
                    sub = sub is null ? arg : new TexRow([sub, arg]);
                }
            }
            return sub is null && sup is null ? b : new TexScript(b, sub, sup);
        }

        private void SkipSpaces()
        {
            while (_i < s.Length && char.IsWhiteSpace(s[_i]))
            {
                _i++;
            }
        }

        /// <summary>A <c>{group}</c>, or the one character or command after it.</summary>
        private TexNode ParseArgument()
        {
            if (_i >= s.Length)
            {
                return new TexRow([]);
            }
            if (s[_i] == '{')
            {
                _i++;
                _depth++;
                TexNode row = _depth > 40 ? new TexRow([]) : ParseRow(stopAtClose: true);
                _depth--;
                if (_i < s.Length && s[_i] == '}')
                {
                    _i++;
                }
                return row;
            }
            TexNode? one = ParseAtom();
            return one ?? new TexRow([]);
        }

        private TexNode? ParseAtom()
        {
            char c = s[_i];
            switch (c)
            {
                case '{':
                    return ParseArgument();
                case '\\':
                    return ParseCommand();
                case ' ':
                case '\t':
                case '\r':
                case '\n':
                    _i++;
                    return null;
                case '~':
                    _i++;
                    return new TexAtom("", TexKind.Space, 0.28);
                case '_':
                case '^':
                    return new TexAtom("", TexKind.Ord); // a script with nothing before it: scripts attach to an empty base
                case '-':
                    _i++;
                    return new TexAtom("−", TexKind.Bin);
                case '\'':
                    _i++;
                    return new TexAtom("′", TexKind.Ord);
            }
            if (char.IsDigit(c))
            {
                int start = _i;
                while (_i < s.Length && (char.IsDigit(s[_i]) || (s[_i] == '.' && _i + 1 < s.Length && char.IsDigit(s[_i + 1]))))
                {
                    _i++;
                }
                return new TexAtom(s[start.._i], TexKind.Ord);
            }
            int len = char.IsHighSurrogate(c) && _i + 1 < s.Length ? 2 : 1;
            string text = s.Substring(_i, len);
            _i += len;
            return Classify(text, letterIsVar: true);
        }

        private static TexAtom Classify(string text, bool letterIsVar)
        {
            if (Relations.Contains(text))
            {
                return new TexAtom(text, TexKind.Rel);
            }
            if (Operators.Contains(text))
            {
                return new TexAtom(text, TexKind.Bin);
            }
            if (Big.Contains(text))
            {
                return new TexAtom(text, TexKind.BigOp);
            }
            if (text is "(" or "[" or ")" or "]" or "⟨" or "⟩" or "⌊" or "⌋" or "⌈" or "⌉" or "|" or "‖")
            {
                return new TexAtom(text, TexKind.Fence);
            }
            if (Functions.Contains(text))
            {
                return new TexAtom(text, TexKind.Func);
            }
            return new TexAtom(text, letterIsVar && text.Length > 0 && char.IsLetter(text[0]) && !IsUpright(text) ? TexKind.Var : TexKind.Ord);
        }

        // Blackboard capitals, and most Greek capitals, are drawn upright as TeX does.
        private static bool IsUpright(string text) => text.Length > 0 && (char.GetUnicodeCategory(text[0]) == System.Globalization.UnicodeCategory.UppercaseLetter && text[0] > 0x370 || "ℕℤℚℝℂℙℍ".Contains(text[0], StringComparison.Ordinal));

        private TexNode? ParseCommand()
        {
            _i++; // the backslash
            if (_i >= s.Length)
            {
                return null;
            }
            int start = _i;
            if (char.IsLetter(s[_i]))
            {
                while (_i < s.Length && char.IsLetter(s[_i]))
                {
                    _i++;
                }
            }
            else
            {
                _i++; // \, \; \{ \| and the like
            }
            string name = s[start.._i];
            switch (name)
            {
                case "frac":
                case "dfrac":
                case "tfrac":
                {
                    SkipSpaces();
                    TexNode a = ParseArgument();
                    SkipSpaces();
                    TexNode b = ParseArgument();
                    return new TexFrac(a, b);
                }
                case "binom":
                {
                    SkipSpaces();
                    TexNode a = ParseArgument();
                    SkipSpaces();
                    TexNode b = ParseArgument();
                    return new TexFenced("(", new TexFrac(a, b, Rule: false), ")");
                }
                case "sqrt":
                {
                    SkipSpaces();
                    TexNode? index = null;
                    if (_i < s.Length && s[_i] == '[')
                    {
                        int close = s.IndexOf(']', _i);
                        if (close > _i)
                        {
                            index = Parse(s[(_i + 1)..close]);
                            _i = close + 1;
                        }
                    }
                    SkipSpaces();
                    return new TexSqrt(ParseArgument(), index);
                }
                case "mathbb":
                {
                    SkipSpaces();
                    string raw = ReadRaw();
                    return new TexAtom(string.Concat(raw.Select(ch => Blackboard.TryGetValue(ch, out string? b) ? b : ch.ToString())), TexKind.Ord);
                }
                case "mathrm":
                case "text":
                case "textrm":
                case "operatorname":
                case "mathsf":
                case "textit":
                case "textbf":
                case "mathop":
                {
                    SkipSpaces();
                    return new TexAtom(ReadRaw(), name is "text" or "textrm" or "textit" or "textbf" ? TexKind.Text : TexKind.Func);
                }
                case "overline":
                case "bar":
                    SkipSpaces();
                    return new TexAccent(ParseArgument(), "‾");
                case "hat":
                case "widehat":
                    SkipSpaces();
                    return new TexAccent(ParseArgument(), "^");
                case "vec":
                    SkipSpaces();
                    return new TexAccent(ParseArgument(), "→");
                case "tilde":
                case "widetilde":
                    SkipSpaces();
                    return new TexAccent(ParseArgument(), "~");
                case "dot":
                    SkipSpaces();
                    return new TexAccent(ParseArgument(), "˙");
                case "begin":
                    return ParseEnvironment();
                case "end":
                    ReadRaw();
                    return null;
                case "overset":
                case "stackrel":
                {
                    SkipSpaces();
                    TexNode top = ParseArgument();
                    SkipSpaces();
                    return new TexStack(ParseArgument(), top, null, false);
                }
                case "underset":
                {
                    SkipSpaces();
                    TexNode bottom = ParseArgument();
                    SkipSpaces();
                    return new TexStack(ParseArgument(), null, bottom, false);
                }
                case "overbrace":
                case "underbrace":
                {
                    SkipSpaces();
                    TexNode body = ParseArgument();
                    TexNode? label = null;
                    // The label is the script that follows: ^{...} over a brace, _{...} under one.
                    SkipSpaces();
                    char mark = name == "overbrace" ? '^' : '_';
                    if (_i < s.Length && s[_i] == mark)
                    {
                        _i++;
                        SkipSpaces();
                        label = ParseArgument();
                    }
                    return name == "overbrace" ? new TexStack(body, label, null, true) : new TexStack(body, null, label, true);
                }
                case "underline":
                    SkipSpaces();
                    return new TexAccent(ParseArgument(), "_");
                case "cfrac":
                {
                    SkipSpaces();
                    TexNode a = ParseArgument();
                    SkipSpaces();
                    return new TexFrac(a, ParseArgument());
                }
                case "pmod":
                {
                    SkipSpaces();
                    TexNode m = ParseArgument();
                    return new TexRow([new TexAtom("", TexKind.Space, 0.5), new TexAtom("(", TexKind.Fence), new TexAtom("mod", TexKind.Func), new TexAtom("", TexKind.Space, 0.3), m, new TexAtom(")", TexKind.Fence)]);
                }
                case "bmod":
                case "mod":
                    return new TexAtom("mod", TexKind.Func);
                case "mathcal":
                case "mathscr":
                case "mathfrak":
                case "mathbf":
                case "boldsymbol":
                case "bm":
                {
                    SkipSpaces();
                    string raw = ReadRaw();
                    return new TexAtom(MathAlphabet.Map(raw, name is "mathcal" or "mathscr" ? MathAlphabet.Script : name == "mathfrak" ? MathAlphabet.Fraktur : MathAlphabet.Bold), TexKind.Ord);
                }
                case "left":
                {
                    SkipSpaces();
                    string open = ReadDelimiter();
                    _depth++;
                    TexNode body = _depth > 40 ? new TexRow([]) : ParseRow(stopAtClose: false);
                    _depth--;
                    string close = "";
                    if (Peek("\\right"))
                    {
                        _i += "\\right".Length;
                        SkipSpaces();
                        close = ReadDelimiter();
                    }
                    return new TexFenced(open, body, close);
                }
                case "right":
                    SkipSpaces();
                    ReadDelimiter();
                    return null;
                case "quad":
                    return new TexAtom("", TexKind.Space, 1);
                case "qquad":
                    return new TexAtom("", TexKind.Space, 2);
                case ",":
                    return new TexAtom("", TexKind.Space, 0.17);
                case ":":
                case ";":
                    return new TexAtom("", TexKind.Space, 0.25);
                case "!":
                case "displaystyle":
                case "textstyle":
                case "big":
                case "Big":
                case "bigg":
                case "Bigg":
                case "limits":
                case "nolimits":
                    return null;
                case "{":
                case "}":
                    return new TexAtom(name, TexKind.Fence);
                case "|":
                case "Vert":
                    return new TexAtom("‖", TexKind.Fence);
                case "vert":
                case "lvert":
                case "rvert":
                    return new TexAtom("|", TexKind.Fence);
                case "lVert":
                case "rVert":
                    return new TexAtom("‖", TexKind.Fence);
                case "\\":
                    return new TexAtom("", TexKind.Space, 1);
            }
            if (BigNames.TryGetValue(name, out string? big))
            {
                return new TexAtom(big, TexKind.BigOp);
            }
            if (Functions.Contains(name))
            {
                return new TexAtom(name, TexKind.Func);
            }
            string? symbol = LatexText.Command(name);
            if (symbol is not null && symbol.Length > 0)
            {
                return Classify(symbol, letterIsVar: true);
            }
            return new TexAtom("\\" + name, TexKind.Text); // unknown: shown as typed
        }

        /// <summary>An environment such as pmatrix, cases or aligned, after its begin command: cells split by &amp;, rows by a double backslash.</summary>
        private TexNode ParseEnvironment()
        {
            SkipSpaces();
            string env = ReadRaw().Trim().TrimEnd('*');
            if (env == "array" || env == "alignat")
            {
                SkipSpaces();
                ReadRaw(); // the column spec, which this layout does not need
            }
            (string open, string close, TexColumns cols) = env switch
            {
                "pmatrix" => ("(", ")", TexColumns.Center),
                "bmatrix" => ("[", "]", TexColumns.Center),
                "Bmatrix" => ("{", "}", TexColumns.Center),
                "vmatrix" => ("|", "|", TexColumns.Center),
                "Vmatrix" => ("‖", "‖", TexColumns.Center),
                "cases" => ("{", "", TexColumns.Left),
                "aligned" or "align" or "alignat" or "split" or "eqnarray" => ("", "", TexColumns.Aligned),
                _ => ("", "", TexColumns.Center), // matrix, array, gather, and anything unknown
            };
            var rows = new List<IReadOnlyList<TexNode>>();
            var row = new List<TexNode>();
            _cells++;
            _depth++;
            while (_i < s.Length && _depth <= 40)
            {
                row.Add(ParseRow(stopAtClose: false));
                if (_i >= s.Length)
                {
                    break;
                }
                if (s[_i] == '&')
                {
                    _i++;
                }
                else if (Peek("\\end"))
                {
                    _i += "\\end".Length;
                    ReadRaw();
                    break;
                }
                else if (s[_i] == '\\' && _i + 1 < s.Length && s[_i + 1] == '\\')
                {
                    _i += 2;
                    rows.Add(row);
                    row = [];
                }
                else
                {
                    break; // a right with no left: leave it for whoever is waiting
                }
            }
            _depth--;
            _cells--;
            if (row.Count > 1 || (row.Count == 1 && !IsEmpty(row[0])))
            {
                rows.Add(row);
            }
            return new TexMatrix(rows, cols, open, close);
        }

        /// <summary>The text of <c>{…}</c> as written, or one character; used where braces hold plain text.</summary>
        private string ReadRaw()
        {
            if (_i < s.Length && s[_i] == '{')
            {
                int depth = 0;
                int start = _i + 1;
                for (int j = _i; j < s.Length; j++)
                {
                    if (s[j] == '{')
                    {
                        depth++;
                    }
                    else if (s[j] == '}' && --depth == 0)
                    {
                        string raw = s[start..j];
                        _i = j + 1;
                        return raw;
                    }
                }
                string rest = s[start..];
                _i = s.Length;
                return rest;
            }
            return _i < s.Length ? s[_i++].ToString() : "";
        }

        private string ReadDelimiter()
        {
            if (_i >= s.Length)
            {
                return "";
            }
            if (s[_i] == '\\')
            {
                _i++;
                int start = _i;
                if (_i < s.Length && char.IsLetter(s[_i]))
                {
                    while (_i < s.Length && char.IsLetter(s[_i]))
                    {
                        _i++;
                    }
                }
                else if (_i < s.Length)
                {
                    _i++;
                }
                string name = s[start.._i];
                return name switch
                {
                    "{" => "{", "}" => "}", "|" or "Vert" or "lVert" or "rVert" => "‖", "vert" or "lvert" or "rvert" => "|",
                    _ => LatexText.Command(name) ?? "",
                };
            }
            string d = s[_i].ToString();
            _i++;
            return d == "." ? "" : d;
        }
    }

    /// <summary>The formula as plain text, for tooltips and tests: <c>∑_(k=1)^n k</c>.</summary>
    public static string Flatten(TexNode node)
    {
        var sb = new StringBuilder();
        Append(sb, node);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, TexNode node)
    {
        switch (node)
        {
            case TexRow r:
                foreach (TexNode n in r.Items)
                {
                    Append(sb, n);
                }
                break;
            case TexAtom a:
                sb.Append(a.Kind == TexKind.Space ? " " : a.Text);
                break;
            case TexScript sc:
                Append(sb, sc.Base);
                if (sc.Sub is not null)
                {
                    sb.Append("_(");
                    Append(sb, sc.Sub);
                    sb.Append(')');
                }
                if (sc.Sup is not null)
                {
                    sb.Append("^(");
                    Append(sb, sc.Sup);
                    sb.Append(')');
                }
                break;
            case TexFrac f:
                sb.Append('(');
                Append(sb, f.Numerator);
                sb.Append(")/(");
                Append(sb, f.Denominator);
                sb.Append(')');
                break;
            case TexSqrt q:
                sb.Append('√');
                Append(sb, q.Body);
                break;
            case TexFenced fe:
                sb.Append(fe.Open);
                Append(sb, fe.Body);
                sb.Append(fe.Close);
                break;
            case TexAccent ac:
                Append(sb, ac.Body);
                break;
            case TexStack st:
                Append(sb, st.Body);
                break;
            case TexMatrix mx:
                sb.Append(mx.Open);
                sb.Append(string.Join("; ", mx.Rows.Select(r => string.Join(", ", r.Select(Flatten)))));
                sb.Append(mx.Close);
                break;
        }
    }
}
