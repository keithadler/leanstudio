using System.Text;
using System.Text.RegularExpressions;

namespace LeanStudio.Core.Editing;

/// <summary>
/// The math in docstrings (<c>$\sum_{i &lt; n} x_i^2 \le C$</c>, Mathlib writes a lot of it) turned into text a hover
/// can show: <c>∑_(i &lt; n) xᵢ² ≤ C</c>. Commands become their symbols (the same table as Lean's Unicode input,
/// plus the usual LaTeX spellings), <c>\mathbb{N}</c> becomes ℕ, <c>\frac{a}{b}</c> becomes <c>a/b</c>, and
/// single-character sub- and superscripts become Unicode ones where Unicode has them. Only what is between <c>$</c>
/// signs changes; text outside, and code in backticks, is left alone.
/// </summary>
public static partial class LatexText
{
    private static readonly Dictionary<string, string> Extra = new(StringComparer.Ordinal)
    {
        ["le"] = "≤", ["leq"] = "≤", ["ge"] = "≥", ["geq"] = "≥", ["ne"] = "≠", ["neq"] = "≠", ["cdot"] = "·", ["cdots"] = "⋯",
        ["ldots"] = "…", ["dots"] = "…", ["times"] = "×", ["infty"] = "∞", ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫",
        ["partial"] = "∂", ["nabla"] = "∇", ["pm"] = "±", ["mp"] = "∓", ["approx"] = "≈", ["equiv"] = "≡", ["sim"] = "∼",
        ["cong"] = "≅", ["propto"] = "∝", ["in"] = "∈", ["notin"] = "∉", ["subset"] = "⊂", ["subseteq"] = "⊆",
        ["supset"] = "⊃", ["supseteq"] = "⊇", ["cup"] = "∪", ["cap"] = "∩", ["setminus"] = "∖", ["emptyset"] = "∅",
        ["forall"] = "∀", ["exists"] = "∃", ["neg"] = "¬", ["lnot"] = "¬", ["land"] = "∧", ["lor"] = "∨", ["wedge"] = "∧", ["vee"] = "∨",
        ["to"] = "→", ["rightarrow"] = "→", ["leftarrow"] = "←", ["Rightarrow"] = "⇒", ["iff"] = "↔", ["Leftrightarrow"] = "⇔",
        ["mapsto"] = "↦", ["circ"] = "∘", ["langle"] = "⟨", ["rangle"] = "⟩", ["lfloor"] = "⌊", ["rfloor"] = "⌋", ["lceil"] = "⌈",
        ["rceil"] = "⌉", ["mid"] = "∣", ["nmid"] = "∤", ["perp"] = "⊥", ["top"] = "⊤", ["bot"] = "⊥", ["ell"] = "ℓ", ["hbar"] = "ℏ",
        ["degree"] = "°", ["quad"] = " ", ["qquad"] = "  ", [","] = " ", [";"] = " ", ["!"] = "", [" "] = " ", ["{"] = "{", ["}"] = "}",
        ["|"] = "‖", ["Vert"] = "‖", ["vert"] = "|", ["lvert"] = "|", ["rvert"] = "|", ["lVert"] = "‖", ["rVert"] = "‖",
        ["log"] = "log", ["ln"] = "ln", ["exp"] = "exp", ["sin"] = "sin", ["cos"] = "cos", ["tan"] = "tan", ["lim"] = "lim",
        ["max"] = "max", ["min"] = "min", ["sup"] = "sup", ["inf"] = "inf", ["det"] = "det", ["gcd"] = "gcd", ["dim"] = "dim",
        ["ker"] = "ker", ["deg"] = "deg", ["Pr"] = "Pr", ["mod"] = "mod", ["bmod"] = "mod", ["pmod"] = "mod",
        ["left"] = "", ["right"] = "", ["big"] = "", ["Big"] = "", ["bigg"] = "", ["Bigg"] = "", ["displaystyle"] = "",
    };

    private const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ⁿⁱᵃᵇᶜᵈᵉᶠᵍʰʲᵏˡᵐᵒᵖʳˢᵗᵘᵛʷˣʸᶻ";
    private const string SuperscriptKeys = "0123456789+-=()niabcdefghjklmoprstuvwxyz";
    private const string Subscripts = "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎ₐₑₕᵢⱼₖₗₘₙₒₚᵣₛₜᵤᵥₓ";
    private const string SubscriptKeys = "0123456789+-=()aehijklmnoprstuvx";

    private static readonly Dictionary<char, string> Blackboard = new()
    {
        ['N'] = "ℕ", ['Z'] = "ℤ", ['Q'] = "ℚ", ['R'] = "ℝ", ['C'] = "ℂ", ['P'] = "ℙ", ['H'] = "ℍ", ['F'] = "𝔽", ['K'] = "𝕜", ['E'] = "𝔼",
    };

    /// <summary>
    /// <paramref name="text"/> (a docstring, Markdown) with the math between <c>$…$</c> and <c>$$…$$</c> turned
    /// into Unicode, and the dollar signs gone.
    /// </summary>
    public static string ToUnicode(string text)
    {
        if (!text.Contains('$', StringComparison.Ordinal))
        {
            return text;
        }
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                end = end < 0 ? text.Length : end + 1;
                sb.Append(text, i, end - i); // code stays as it is
                i = end;
                continue;
            }
            if (c == '$' && (i == 0 || text[i - 1] != '\\'))
            {
                bool display = i + 1 < text.Length && text[i + 1] == '$';
                int open = i + (display ? 2 : 1);
                int close = text.IndexOf(display ? "$$" : "$", open, StringComparison.Ordinal);
                if (close > open)
                {
                    sb.Append(Math(text[open..close]));
                    i = close + (display ? 2 : 1);
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>One piece of math, without its dollar signs, as Unicode text.</summary>
    public static string Math(string latex)
    {
        string s = latex.Trim();
        s = FracRegex().Replace(s, m => Group(m.Groups["a"].Value) + "/" + Group(m.Groups["b"].Value));
        s = SqrtRegex().Replace(s, m => "√" + Group(m.Groups["x"].Value));
        s = BbRegex().Replace(s, m => string.Concat(m.Groups["x"].Value.Select(ch => Blackboard.TryGetValue(ch, out string? b) ? b : ch.ToString())));
        s = FontRegex().Replace(s, m => m.Groups["x"].Value); // \mathrm{x}, \text{x}, \operatorname{x}…
        s = CommandRegex().Replace(s, m =>
        {
            string name = m.Groups["name"].Value;
            return Extra.TryGetValue(name, out string? e) ? e
                : Abbreviations.Lookup(name) is string sym ? sym
                : m.Value;
        });
        s = ScriptRegex().Replace(s, m =>
        {
            bool sup = m.Groups["op"].Value == "^";
            string body = m.Groups["braced"].Success ? m.Groups["braced"].Value : m.Groups["one"].Value;
            string keys = sup ? SuperscriptKeys : SubscriptKeys, glyphs = sup ? Superscripts : Subscripts;
            // Unicode has sub- and superscripts for only some characters: when any is missing, keep ^{…} readable.
            if (body.All(ch => keys.Contains(ch, StringComparison.Ordinal)))
            {
                return string.Concat(body.Select(ch => glyphs[keys.IndexOf(ch, StringComparison.Ordinal)]));
            }
            return m.Groups["op"].Value + (body.Length == 1 ? body : "(" + body + ")");
        });
        return s.Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal).Replace("  ", " ", StringComparison.Ordinal);
    }

    private static string Group(string s) => s.Length <= 1 || s.All(char.IsLetterOrDigit) ? s : "(" + s + ")";

    [GeneratedRegex(@"\\[dt]?frac\s*\{(?<a>[^{}]*)\}\s*\{(?<b>[^{}]*)\}")]
    private static partial Regex FracRegex();

    [GeneratedRegex(@"\\sqrt\s*\{(?<x>[^{}]*)\}")]
    private static partial Regex SqrtRegex();

    [GeneratedRegex(@"\\mathbb\s*\{(?<x>[^{}]*)\}|\\mathbb\s*(?<x>[A-Z])")]
    private static partial Regex BbRegex();

    [GeneratedRegex(@"\\(?:mathrm|mathit|mathbf|mathsf|mathcal|mathfrak|text|textrm|textit|operatorname|mathop)\s*\{(?<x>[^{}]*)\}")]
    private static partial Regex FontRegex();

    [GeneratedRegex(@"\\(?<name>[A-Za-z]+|[,;! {}|])")]
    private static partial Regex CommandRegex();

    [GeneratedRegex(@"(?<op>[\^_])(?:\{(?<braced>[^{}]*)\}|(?<one>[^\s{}\\]))")]
    private static partial Regex ScriptRegex();
}
