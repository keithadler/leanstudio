using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using LeanStudio.Core.Verification;

namespace LeanStudio.App.ViewModels;

/// <summary>
/// Value converters the views use in bindings, as <c>{x:Static vm:StatusConverters.IsZero}</c> and the like.
/// </summary>
public static class StatusConverters
{
    /// <summary>True for <see cref="VerificationStatus.Verified"/>: Tenet accepted the declaration.</summary>
    public static readonly IValueConverter IsVerified = new FuncValueConverter<VerificationStatus, bool>(s => s == VerificationStatus.Verified);
    /// <summary>
    /// True for <see cref="VerificationStatus.RestsOnAssumption"/>: it rests on sorry or on an axiom the project
    /// introduces.
    /// </summary>
    public static readonly IValueConverter IsConditional = new FuncValueConverter<VerificationStatus, bool>(s => s == VerificationStatus.RestsOnAssumption);
    /// <summary>True for <see cref="VerificationStatus.Rejected"/>: Tenet's kernel rejected it.</summary>
    public static readonly IValueConverter IsRejected = new FuncValueConverter<VerificationStatus, bool>(s => s == VerificationStatus.Rejected);

    /// <summary>True for an empty list's count: shows a panel's "nothing here" message.</summary>
    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(n => n == 0);

    /// <summary>The last part of a path: a recent project's name.</summary>
    public static readonly IValueConverter LastPart = new FuncValueConverter<string, string>(p => Path.GetFileName((p ?? "").TrimEnd('/', '\\')));

    /// <summary>A path with the home folder shortened to ~, for display.</summary>
    public static readonly IValueConverter Friendly = new FuncValueConverter<string, string>(p =>
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return p is not null && home.Length > 0 && p.StartsWith(home, StringComparison.Ordinal) ? "~" + p[home.Length..] : p ?? "";
    });

    /// <summary>A file tree icon key (folder, lean, doc, file) to its shape.</summary>
    public static readonly IValueConverter Icon = new FuncValueConverter<string, Geometry?>(k => Resource<Geometry>(k switch
    {
        "folder" => "IconFolder",
        "lean" => "IconLean",
        "doc" => "IconBook",
        _ => "IconFile",
    }));

    private static T? Resource<T>(string key) where T : class =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out object? v) ? v as T : null;
}
