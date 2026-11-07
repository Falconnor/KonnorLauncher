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
/// El visor solo existe mientras se ve: con la página cerrada, en otra pestaña o con el launcher
/// minimizado se destruye. El control de composición captura la página continuamente y hace que WPF
/// dibuje fotogramas sin parar (un 10-15 % de CPU con el launcher en reposo, también minimizado). Al
/// volver a verse se crea de nuevo y recupera la posición del desplazamiento.
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
        nameof(Html), typeof(string), typeof(HtmlView), new PropertyMetadata(null, (d, _) => ((HtmlView)d).OnHtmlChanged()));

    public string? Html
    {
        get => (string?)GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>El visor, solo mientras se ve (ver la descripción de la clase).</summary>
    private WebView2CompositionControl? _web;
    private Window? _window;
    private int _generation;        // cada visor creado; uno que termina de iniciarse tarde ve que ya no es el actual
    private double _restoreScroll;  // posición a recuperar al volver a crear el visor
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
        Children.Add(_bar);
        _fallbackScroll.Content = _fallback;
        SetColumnSpan(_fallbackScroll, 2);
        Children.Add(_fallbackScroll);

        _bar.ValueChanged += OnBarValueChanged;
        _sync.Tick += async (_, _) => await ReadScrollAsync();
        IsVisibleChanged += (_, _) => UpdateWeb();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null)
                _window.StateChanged += OnWindowStateChanged;
            UpdateWeb();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null)
                _window.StateChanged -= OnWindowStateChanged;
            _window = null;
            DestroyWeb();
        };
        // El control de composición ya pasó el clic a la página (barra de desplazamiento, enlaces, texto),
        // pero no lo marca como atendido: sin esto, la ventana empezaría a arrastrarse y se quedaría el ratón.
        MouseLeftButtonDown += (_, e) => e.Handled = true;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateWeb();

    /// <summary>Otra página (otro proyecto): empieza arriba, no donde estaba la anterior.</summary>
    private void OnHtmlChanged()
    {
        _restoreScroll = 0;
        Show();
    }

    /// <summary>Se ve de verdad: visible, con una página que mostrar y el launcher sin minimizar.</summary>
    private bool ShouldShowWeb =>
        !_failed && IsVisible && !string.IsNullOrEmpty(Html) && _window is { WindowState: not WindowState.Minimized };

    /// <summary>Crea o destruye el visor según <see cref="ShouldShowWeb"/>.</summary>
    private void UpdateWeb()
    {
        if (ShouldShowWeb)
        {
            if (_web is null)
                _ = CreateWebAsync();
        }
        else if (_web is not null)
        {
            DestroyWeb();
        }
    }

    private async Task CreateWebAsync()
    {
        var generation = ++_generation;
        var web = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Transparent };
        _web = web;
        Children.Insert(0, web);
        try
        {
            // Un solo entorno para todo el launcher, con su carpeta de datos en %LOCALAPPDATA%.
            _environment ??= CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KonnorLauncher", "webview"));
            await web.EnsureCoreWebView2Async(await _environment);
            if (generation != _generation)
                return; // se destruyó mientras se iniciaba
            var core = web.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            _ready = true;
            _sync.Start();
            Show();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (generation != _generation)
                return;
            _failed = true;
            DestroyWeb();
            Show();
        }
    }

    /// <summary>Cierra el visor (y su captura). La posición del desplazamiento se guarda para recuperarla.</summary>
    private void DestroyWeb()
    {
        _generation++;
        _sync.Stop();
        _ready = false;
        _bar.Visibility = Visibility.Collapsed;
        if (_web is not { } web)
            return;
        _web = null;
        Children.Remove(web);
        web.Dispose();
    }

    /// <summary>Al volver a crear el visor, la página vuelve a donde estaba.</summary>
    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_restoreScroll <= 0 || _web?.CoreWebView2 is not { } core)
            return;
        var y = _restoreScroll.ToString("0", CultureInfo.InvariantCulture);
        _restoreScroll = 0;
        _ = core.ExecuteScriptAsync($"window.scrollTo(0, {y})");
    }

    /// <summary>Posición de la página → barra del launcher.</summary>
    private async Task ReadScrollAsync()
    {
        if (!_ready || _reading || _web?.CoreWebView2 is not { } core)
            return;
        _reading = true;
        try
        {
            // Script del launcher (no de la página): funciona aunque los de la página estén desactivados.
            var json = await core.ExecuteScriptAsync(
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
            if (_web is not null)
                _restoreScroll = top;
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
        if (_updating || !_ready || _web?.CoreWebView2 is not { } core)
            return;
        var y = e.NewValue.ToString("0", CultureInfo.InvariantCulture);
        _ = core.ExecuteScriptAsync($"window.scrollTo(0, {y})");
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
            _bar.Visibility = Visibility.Collapsed;
            _fallbackScroll.Visibility = Visibility.Visible;
            _fallback.Visibility = Visibility.Visible;
            _fallback.Text = PlainText(html);
            return;
        }

        if (_ready && _web is not null)
        {
            if (html.Length > 0)
                _web.NavigateToString(html);
            else
                DestroyWeb(); // sin página (se cerró el proyecto): fuera el visor
            return;
        }

        UpdateWeb();
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
