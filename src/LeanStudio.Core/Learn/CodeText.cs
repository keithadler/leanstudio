using LeanStudio.Core.Editing;

namespace LeanStudio.Core.Learn;

/// <summary>A file's lines with the comments and strings blanked out, so a pattern looks only at code.</summary>
internal static class CodeText
{
    /// <summary>The lines of <paramref name="text"/> with every character that is not code replaced by a space.</summary>
    public static string[] Lines(string text)
    {
        string unix = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        bool[] code = LeanText.CodeMask(unix);
        char[] chars = unix.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] != '\n' && i < code.Length && !code[i])
            {
                chars[i] = ' ';
            }
        }
        return new string(chars).Split('\n');
    }
}
