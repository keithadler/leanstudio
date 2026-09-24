using Avalonia.Platform;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace LeanStudio.App.Services;

/// <summary>TextMate's themes, plus Lean Studio's own grammar for Lean 4.</summary>
/// <param name="theme">The TextMate theme to use for highlighting.</param>
public sealed class LeanRegistryOptions(ThemeName theme) : IRegistryOptions
{
    /// <summary>The scope name of the bundled Lean 4 grammar (<c>Assets/lean4.tmLanguage.json</c>).</summary>
    public const string LeanScope = "source.lean4";

    private readonly RegistryOptions _inner = new(theme);
    private static readonly Lazy<IRawGrammar> Grammar = new(Load);

    private static IRawGrammar Load()
    {
        using Stream s = AssetLoader.Open(new Uri("avares://LeanStudio/Assets/lean4.tmLanguage.json"));
        using var reader = new StreamReader(s);
        return GrammarReader.ReadGrammarSync(reader);
    }

    /// <inheritdoc/>
    public IRawTheme GetTheme(string scopeName) => _inner.GetTheme(scopeName);

    /// <inheritdoc/>
    public IRawGrammar GetGrammar(string scopeName) => scopeName == LeanScope ? Grammar.Value : _inner.GetGrammar(scopeName);

    /// <inheritdoc/>
    public ICollection<string> GetInjections(string scopeName) => _inner.GetInjections(scopeName);

    /// <inheritdoc/>
    public IRawTheme GetDefaultTheme() => _inner.GetDefaultTheme();

    /// <inheritdoc/>
    public IRawTheme LoadTheme(ThemeName name) => _inner.LoadTheme(name);

    /// <summary>The bundled grammar for a file extension (Markdown, TOML, JSON…), or null for plain text.</summary>
    public string? ScopeForExtension(string extension) =>
        extension.Length == 0 ? null : _inner.GetScopeByExtension(extension);
}
