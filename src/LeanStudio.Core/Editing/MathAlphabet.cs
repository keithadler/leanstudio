using System.Text;

namespace LeanStudio.Core.Editing;

/// <summary>
/// The letters of mathcal, mathfrak and mathbf as Unicode's mathematical alphabets (𝒜, 𝔄, 𝐀), which math fonts
/// draw upright, so no styled text run is needed. A character without a counterpart stays as it is.
/// </summary>
public static class MathAlphabet
{
    /// <summary>Script capitals and lower case: 𝒜 ℬ 𝒞 …</summary>
    public const int Script = 0x1D49C;

    /// <summary>Fraktur letters: 𝔄 𝔅 ℭ …</summary>
    public const int Fraktur = 0x1D504;

    /// <summary>Bold letters and digits: 𝐀 𝐁 … 𝟎</summary>
    public const int Bold = 0x1D400;

    // Letters the script and fraktur alphabets had as separate code points before the block existed; their slots in it are empty.
    private static readonly Dictionary<char, string> ScriptHoles = new() { ['B'] = "ℬ", ['E'] = "ℰ", ['F'] = "ℱ", ['H'] = "ℋ", ['I'] = "ℐ", ['L'] = "ℒ", ['M'] = "ℳ", ['R'] = "ℛ", ['e'] = "ℯ", ['g'] = "ℊ", ['o'] = "ℴ" };
    private static readonly Dictionary<char, string> FrakturHoles = new() { ['C'] = "ℭ", ['H'] = "ℌ", ['I'] = "ℑ", ['R'] = "ℜ", ['Z'] = "ℨ" };

    /// <summary><paramref name="text"/> in the alphabet that starts at <paramref name="alphabet"/> (one of the constants).</summary>
    public static string Map(string text, int alphabet)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (char c in text)
        {
            if (alphabet == Script && ScriptHoles.TryGetValue(c, out string? s))
            {
                sb.Append(s);
            }
            else if (alphabet == Fraktur && FrakturHoles.TryGetValue(c, out string? f))
            {
                sb.Append(f);
            }
            else if (c is >= 'A' and <= 'Z')
            {
                sb.Append(char.ConvertFromUtf32(alphabet + (c - 'A')));
            }
            else if (c is >= 'a' and <= 'z')
            {
                sb.Append(char.ConvertFromUtf32(alphabet + 26 + (c - 'a')));
            }
            else if (alphabet == Bold && c is >= '0' and <= '9')
            {
                sb.Append(char.ConvertFromUtf32(0x1D7CE + (c - '0')));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
