using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KonnorLauncher.App.Controls;

/// <summary>
/// Muestra una página HTML generada por el launcher (la descripción de un proyecto de Modrinth) con
/// WebView2. Se usa el control de composición: se dibuja como el resto de la ventana, así que los
/// paneles y diálogos del launcher pueden ir por encima.
/// <para>
/// Sin scripts, sin menú contextual ni herramientas de desarrollo. Los enlaces no navegan dentro: se
/// abren en el navegador del sistema (solo http y https). Si no está instalado el runtime de WebView2,
/// se muestra el texto sin formato.
/// </para>
/// </summary>
public sealed partial class HtmlView : Grid
{
    public static readonly DependencyProperty HtmlProperty = DependencyProperty.Register(
        nameof(Html), typeof(string), typeof(HtmlView), new PropertyMetadata(null, (d, _) => ((HtmlView)d).Show()));

    public string? Html
    {
        get => (string?)GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    private static Task<CoreWebView2Environment>? _environment;

    private readonly WebView2CompositionControl _web = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    private readonly TextBlock _fallback = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14.5,
        Visibility = Visibility.Collapsed,
    };
    private bool _ready;
    private bool _failed;

    public HtmlView()
    {
        _fallback.SetResourceReference(TextBlock.ForegroundProperty, "Ink2");
        Children.Add(_web);
        Children.Add(new ScrollViewer { Content = _fallback, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (_ready || _failed)
            return;
        try
        {
            // Un solo entorno para todo el launcher, con su carpeta de datos en %LOCALAPPDATA%.
            _environment ??= CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KonnorLauncher", "webview"));
            await _web.EnsureCoreWebView2Async(await _environment);
            var core = _web.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            _ready = true;
            Show();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException)
        {
            _failed = true;
            Show();
        }
    }

    /// <summary>Solo se carga la propia página; cualquier enlace se abre fuera.</summary>
    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || e.Uri == "about:blank")
            return;
        e.Cancel = true;
        if (e.IsUserInitiated)
            OpenExternal(e.Uri);
    }

    private static void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private void Show()
    {
        var html = Html ?? string.Empty;
        if (_failed)
        {
            _web.Visibility = Visibility.Collapsed;
            _fallback.Visibility = Visibility.Visible;
            _fallback.Text = PlainText(html);
            return;
        }

        if (_ready)
            _web.NavigateToString(html.Length > 0 ? html : "<html><body></body></html>");
    }

    /// <summary>Texto aproximado de la página, sin etiquetas (si no hay WebView2).</summary>
    private static string PlainText(string html)
    {
        var body = BodyRegex().Match(html) is { Success: true } match ? match.Groups[1].Value : html;
        var text = BreakRegex().Replace(body, "\n");
        text = TagRegex().Replace(text, string.Empty);
        return SpacesRegex().Replace(WebUtility.HtmlDecode(text), "\n\n").Trim();
    }

    [GeneratedRegex("<body>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BodyRegex();

    [GeneratedRegex("<(br|/p|/h[1-6]|/li|/tr|/div)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex SpacesRegex();
}
