using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using LeanStudio.App.ViewModels;

namespace LeanStudio.App.Views;

/// <summary>
/// Lean's own infoview inside the window, widgets and all: the page the infoview bridge serves, in the platform's
/// web view (WebKit on macOS, WebView2 on Windows, WebKitGTK on Linux). The page is loaded the first time the pane
/// is shown and kept after that, so switching tabs doesn't restart it. Where there is no web view (a Linux without
/// WebKitGTK, or a headless run), the pane says so and offers the browser instead.
/// </summary>
public sealed class InfoviewPane : UserControl
{
    private NativeWebView? _web;
    private Uri? _page;

    /// <summary>
    /// Whether to use the platform's web view at all. The headless snapshot run turns it off, since there is no
    /// native window to put one in.
    /// </summary>
    public static bool NativeWebViewAllowed { get; set; } = true;

    /// <summary>Whether the page is shown in a web view (false: the fallback is shown instead).</summary>
    public bool IsEmbedded => _web is not null;

    /// <summary>Why the web view isn't used, when it isn't; null before the pane is first shown.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>The web view, once the page is loaded in one (for checks that read the page).</summary>
    public NativeWebView? WebView => _web;

    /// <summary>Load the infoview the first time the pane is shown: in a web view if there is one, or the fallback.</summary>
    public void EnsureLoaded()
    {
        if (_web is not null || UnavailableReason is not null || DataContext is not MainViewModel vm)
        {
            return;
        }
        UnavailableReason = Unavailable();
        if (UnavailableReason is not null)
        {
            Content = Fallback(vm, UnavailableReason);
            return;
        }
        _page = vm.StartEmbeddedInfoview();
        _web = new NativeWebView { Source = _page };
        _web.Bind(NativeWebView.BackgroundProperty, this.GetResourceObservable("PanelBackground"));
        _web.EnvironmentRequested += (_, e) => e.EnableDevTools = false;
        _web.NavigationStarted += (_, e) =>
        {
            // The infoview stays the infoview: a link in it (to documentation, say) opens in the browser.
            if (e.Request is Uri to && !SameOrigin(to))
            {
                e.Cancel = true;
                _ = vm.OpenLinkAsync(to);
            }
        };
        _web.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.Request is Uri to)
            {
                _ = vm.OpenLinkAsync(to);
            }
        };
        Content = _web;
    }

    private bool SameOrigin(Uri to) => _page is not null && (to.Scheme is "about" or "data" or "blob"
        || (to.Scheme == _page.Scheme && to.Host == _page.Host && to.Port == _page.Port));

    /// <summary>Why this platform has no web view for the pane, or null if it has one.</summary>
    private static string? Unavailable()
    {
        if (!NativeWebViewAllowed)
        {
            return "This run has no window to show a web view in.";
        }
        WebViewAdapterType type = OperatingSystem.IsMacOS() ? WebViewAdapterType.WkWebView
            : OperatingSystem.IsWindows() ? WebViewAdapterType.WebView2
            : OperatingSystem.IsLinux() ? WebViewAdapterType.WebKitGtk
            : WebViewAdapterType.Unknown;
        if (type == WebViewAdapterType.Unknown)
        {
            return "There is no web view on this system.";
        }
        try
        {
            DetailedWebViewAdapterInfo info = WebViewAdapterInfo.GetAdapterInfo(type);
            if (info.IsSupported && info.IsInstalled)
            {
                return null;
            }
            string why = string.IsNullOrWhiteSpace(info.UnavailableReason) ? "The system's web view isn't available." : info.UnavailableReason;
            return OperatingSystem.IsLinux() ? why + " Installing WebKitGTK (libwebkit2gtk-4.1) lets Lean Studio show it here." : why;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or InvalidOperationException or TypeInitializationException)
        {
            return "The system's web view isn't available: " + e.Message;
        }
    }

    private static Control Fallback(MainViewModel vm, string reason)
    {
        var open = new Button { Content = "Open in Browser", Command = vm.OpenInfoviewCommand, HorizontalAlignment = HorizontalAlignment.Center };
        open.Classes.Add("accent");
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 340 };
        panel.Children.Add(new PathIcon { Data = (Geometry?)Application.Current?.FindResource("IconWidget"), Width = 28, Height = 28, Opacity = 0.6 });
        panel.Children.Add(new TextBlock { Text = "Lean's infoview", FontWeight = FontWeight.SemiBold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = "The infoview VS Code uses, with ProofWidgets and every other widget. It can't be shown in this window here. " + reason,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Opacity = 0.75,
        });
        panel.Children.Add(open);
        return panel;
    }
}
