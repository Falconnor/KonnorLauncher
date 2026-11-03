using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
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
/// <para>
/// La barra de desplazamiento es la del launcher (la de la página va oculta): el control de composición
/// no hace llegar a la página el arrastre de su propia barra. Se mantienen sincronizadas leyendo la
/// posición de la página y moviéndola con scripts del propio launcher (los de la página siguen
/// desactivados).
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
    // El texto sin formato (sin WebView2) va en su propio ScrollViewer. Está oculto salvo en ese caso:
    // visible, quedaría encima de la página y de la barra y se quedaría todos los clics.
    private readonly ScrollViewer _fallbackScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Visibility = Visibility.Collapsed,
    };
    private readonly ScrollBar _bar = new() { Orientation = Orientation.Vertical, Margin = new Thickness(6, 2, 0, 2), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _sync = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _ready;
    private bool _failed;
    private bool _reading;   // hay una lectura de la posición en curso
    private bool _updating;  // la barra se está moviendo desde la página: no hay que volver a mover la página

    public HtmlView()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _fallback.SetResourceReference(TextBlock.ForegroundProperty, "Ink2");
        _bar.SetResourceReference(StyleProperty, "GlassScrollBar");
        SetColumn(_bar, 1);
        Children.Add(_web);
        Children.Add(_bar);
        _fallbackScroll.Content = _fallback;
        SetColumnSpan(_fallbackScroll, 2);
        Children.Add(_fallbackScroll);

        _bar.ValueChanged += OnBarValueChanged;
        _sync.Tick += async (_, _) => await ReadScrollAsync();
        // Solo se sincroniza mientras se ve (la galería y las versiones la ocultan).
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && _ready)
                _sync.Start();
            else
                _sync.Stop();
        };
        Unloaded += (_, _) => _sync.Stop();
        Loaded += async (_, _) => await InitializeAsync();
        // El control de composición ya pasó el clic a la página (barra de desplazamiento, enlaces, texto),
        // pero no lo marca como atendido: sin esto, la ventana empezaría a arrastrarse y se quedaría el ratón.
        MouseLeftButtonDown += (_, e) => e.Handled = true;
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
            if (IsVisible)
                _sync.Start();
            Show();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException)
        {
            _failed = true;
            Show();
        }
    }

    /// <summary>Posición de la página → barra del launcher.</summary>
    private async Task ReadScrollAsync()
    {
        if (!_ready || _reading || _web.CoreWebView2 is null)
            return;
        _reading = true;
        try
        {
            // Script del launcher (no de la página): funciona aunque los de la página estén desactivados.
            var json = await _web.CoreWebView2.ExecuteScriptAsync(
                "[window.scrollY, document.documentElement.scrollHeight, window.innerHeight]");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() != 3)
                return;
            var top = doc.RootElement[0].GetDouble();
            var height = doc.RootElement[1].GetDouble();
            var viewport = doc.RootElement[2].GetDouble();
            var max = Math.Max(0, height - viewport);
            _updating = true;
            _bar.Maximum = max;
            _bar.ViewportSize = viewport;
            _bar.LargeChange = Math.Max(1, viewport * 0.9);
            _bar.SmallChange = 48;
            if (!_bar.IsMouseCaptureWithin)
                _bar.Value = Math.Clamp(top, 0, max);
            _bar.Visibility = max > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or JsonException or ObjectDisposedException)
        {
            // La página aún no está lista (o se está cambiando): se lee en el siguiente intento.
        }
        finally
        {
            _updating = false;
            _reading = false;
        }
    }

    /// <summary>Barra del launcher (arrastre, clic o accesibilidad) → posición de la página.</summary>
    private void OnBarValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !_ready || _web.CoreWebView2 is null)
            return;
        var y = e.NewValue.ToString("0", CultureInfo.InvariantCulture);
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.scrollTo(0, {y})");
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
            _bar.Visibility = Visibility.Collapsed;
            _fallbackScroll.Visibility = Visibility.Visible;
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
