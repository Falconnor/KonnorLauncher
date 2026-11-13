using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.App.Services;
using KonnorLauncher.App.Theming;
using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Launching;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Theming;
using Microsoft.Extensions.Logging;

namespace KonnorLauncher.App.ViewModels;

/// <summary>Opción del combo de RAM: se muestra "4 GB" y se guarda "4G".</summary>
public sealed record RamOption(string Display, string Value)
{
    /// <summary>El cuadro del combo muestra este texto ("4 GB").</summary>
    public override string ToString() => Display;
}

/// <summary>Secciones de la barra lateral de Opciones.</summary>
public enum OptionsSection
{
    Game,
    Window,
    Theme,
    Launcher,
    Storage,
    About,
}

/// <summary>Entrada de la barra lateral de Opciones.</summary>
public sealed record OptionsSectionItem(OptionsSection Section, string Label, string Icon);

/// <summary>Acreditación de un tercero (Acerca de): qué aporta, su licencia (vacía en servicios) y su página.</summary>
public sealed record Credit(string Name, string Role, string License, string Url);

/// <summary>Tarjeta de un tema de serie: nombre y sus colores (fondo, cristal, acento y JUGAR) para la vista previa.</summary>
public sealed class ThemePresetItem
{
    public ThemePresetItem(ThemePreset preset)
    {
        Preset = preset;
        var palette = ThemeEngine.Build(new ThemeSettings(preset.Id));
        Window = Brush(palette["WindowBase"]);
        Glass = Brush(palette["WindowTint"]);
        Accent = Brush(palette["Accent"]);
        Brand = Brush(palette["Brand"]);
    }

    public ThemePreset Preset { get; }
    public string Id => Preset.Id;
    public string Name => Preset.Name;
    public Brush Window { get; }
    public Brush Glass { get; }
    public Brush Accent { get; }
    public Brush Brand { get; }

    internal static Brush Brush(Argb color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Tamaño de ventana sugerido ("1280 × 720"). Ancho 0 = el de Minecraft.</summary>
public sealed record ResolutionPreset(string Display, int Width, int Height);

/// <summary>
/// ViewModel del overlay "OPCIONES" (ajustes del jugador, comunes a todas las instancias), con secciones en una
/// barra lateral. Carga los valores de <c>launcher.properties</c> al abrirlo y los guarda todos juntos al pulsar
/// GUARDAR. La versión, el servidor y la carpeta del juego son de cada instancia.
/// </summary>
public sealed partial class OptionsViewModel : ObservableObject
{
    private static readonly string[] RamValues = ["1G", "2G", "3G", "4G", "6G", "8G", "10G", "12G", "16G"];

    private readonly LauncherSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogger<OptionsViewModel> _logger;

    public OptionsViewModel(LauncherSettings settings, IDialogService dialogs, LauncherUpdater updater, ILogger<OptionsViewModel> logger)
    {
        Updater = updater;
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;
        SelectedSection = Sections[0];
    }

    /// <summary>Actualizaciones del launcher (Acerca de): estado y «Buscar».</summary>
    public LauncherUpdater Updater { get; }

    [RelayCommand]
    private Task CheckUpdates() => Updater.CheckAsync();

    /// <summary>Se dispara después de guardar (la ventana principal actualiza la bienvenida).</summary>
    public event EventHandler? Saved;

    public IReadOnlyList<OptionsSectionItem> Sections { get; } =
    [
        new(OptionsSection.Game, "Juego", ""),
        new(OptionsSection.Window, "Ventana", ""),
        new(OptionsSection.Theme, "Tema", ""),
        new(OptionsSection.Launcher, "Launcher", ""),
        new(OptionsSection.Storage, "Almacenamiento", ""),
        new(OptionsSection.About, "Acerca de", ""),
    ];

    public ObservableCollection<RamOption> RamOptions { get; } = [];

    public IReadOnlyList<ResolutionPreset> ResolutionPresets { get; } =
    [
        new("Predeterminado", 0, 0),
        new("1280 × 720", 1280, 720),
        new("1600 × 900", 1600, 900),
        new("1920 × 1080", 1920, 1080),
        new("2560 × 1440", 2560, 1440),
    ];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private OptionsSectionItem _selectedSection;
    [ObservableProperty] private RamOption? _selectedRam;
    [ObservableProperty] private string _javaPath = string.Empty;
    [ObservableProperty] private string _javaArgs = string.Empty;
    [ObservableProperty] private string _gameWidth = string.Empty;
    [ObservableProperty] private string _gameHeight = string.Empty;
    [ObservableProperty] private bool _fullscreen;
    [ObservableProperty] private LauncherOnPlay _onPlay;
    [ObservableProperty] private bool _openLogOnCrash;

    // ── Tema ──
    [ObservableProperty] private ThemePresetItem? _selectedThemePreset;
    [ObservableProperty] private string _customAccent = string.Empty;
    [ObservableProperty] private int _glassPercent = 100;
    [ObservableProperty] private string? _backgroundPath;

    /// <summary>Tipo de fondo: sólido (opaco, como VS Code o Modrinth) o translúcido (cristal sobre la imagen).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranslucentBackground))]
    private bool _solidBackground;

    /// <summary>Segmento "Translúcido" (el contrario de <see cref="SolidBackground"/>).</summary>
    public bool IsTranslucentBackground
    {
        get => !SolidBackground;
        set => SolidBackground = !value;
    }

    /// <summary>Tema guardado al abrir: CANCELAR vuelve a él.</summary>
    private ThemeSettings _savedTheme = ThemeSettings.Default;

    /// <summary>Al cargar los valores no se repinta en cada campo, solo al final.</summary>
    private bool _loadingTheme;

    public IReadOnlyList<ThemePresetItem> ThemePresets { get; } = ThemeEngine.Presets.Select(p => new ThemePresetItem(p)).ToList();

    public int MinGlassPercent => ThemeEngine.MinGlassPercent;
    public int MaxGlassPercent => ThemeEngine.MaxGlassPercent;

    /// <summary>Muestra del acento efectivo (el propio si es válido; si no, el del tema).</summary>
    public Brush AccentPreview => ThemePresetItem.Brush(ThemeEngine.AccentOf(DraftTheme));

    /// <summary>Hay un tono propio elegido en la rueda (si no, se usa el color del tema).</summary>
    public bool HasCustomAccent => Argb.TryParse(CustomAccent, out _);

    // Rueda de color: tono, saturación y brillo del acento. Se guardan aparte del color (#RRGGBB) para que el selector
    // no salte al redondear (con saturación 0 el tono no se puede recuperar del color).
    private double _accentHue;
    private double _accentSaturation = 1;
    private double _accentBrightness = 1;
    private bool _settingFromWheel;

    public double MinAccentBrightness => ThemeEngine.MinCustomBrightness;

    public double AccentHue
    {
        get => _accentHue;
        set => SetWheel(value, _accentSaturation, _accentBrightness);
    }

    public double AccentSaturation
    {
        get => _accentSaturation;
        set => SetWheel(_accentHue, value, _accentBrightness);
    }

    public double AccentBrightness
    {
        get => _accentBrightness;
        set => SetWheel(_accentHue, _accentSaturation, value);
    }

    /// <summary>Mover la rueda o el brillo elige un color propio.</summary>
    private void SetWheel(double hue, double saturation, double brightness)
    {
        _accentHue = hue;
        _accentSaturation = Math.Clamp(saturation, 0, 1);
        _accentBrightness = Math.Clamp(brightness, ThemeEngine.MinCustomBrightness, 1);
        _settingFromWheel = true;
        try
        {
            CustomAccent = Argb.FromHsv(_accentHue, _accentSaturation, _accentBrightness).ToString();
        }
        finally
        {
            _settingFromWheel = false;
        }

        NotifyWheel();
    }

    /// <summary>La rueda se coloca en el acento efectivo (al abrir, al elegir un tema o al quitar el color propio).</summary>
    private void SyncWheel()
    {
        var (h, s, v) = ThemeEngine.AccentOf(DraftTheme).ToHsv();
        if (s > 0.001)
            _accentHue = h;
        _accentSaturation = s;
        _accentBrightness = Math.Max(v, ThemeEngine.MinCustomBrightness);
        NotifyWheel();
    }

    private void NotifyWheel()
    {
        OnPropertyChanged(nameof(AccentHue));
        OnPropertyChanged(nameof(AccentSaturation));
        OnPropertyChanged(nameof(AccentBrightness));
    }

    public bool HasCustomBackground => BackgroundPath is not null;

    public string BackgroundName => BackgroundPath is { } path ? Path.GetFileName(path) : "La del launcher";

    /// <summary>El tema tal como está en el formulario.</summary>
    private ThemeSettings DraftTheme => new(
        SelectedThemePreset?.Id ?? ThemeEngine.DefaultPresetId,
        Argb.TryParse(CustomAccent, out var accent) ? accent.WithAlpha(0xFF).ToString() : null,
        GlassPercent,
        BackgroundPath,
        SolidBackground);

    partial void OnSelectedThemePresetChanged(ThemePresetItem? value)
    {
        // Elegir un tema de serie quita el tono propio: si no, el tema no se notaría.
        if (!_loadingTheme && value is not null)
            CustomAccent = string.Empty;
        if (!_loadingTheme)
            SyncWheel();
        PreviewTheme();
    }

    partial void OnCustomAccentChanged(string value)
    {
        OnPropertyChanged(nameof(HasCustomAccent));
        if (!_settingFromWheel && !_loadingTheme)
            SyncWheel();
        PreviewTheme();
    }
    partial void OnGlassPercentChanged(int value) => PreviewTheme();
    partial void OnSolidBackgroundChanged(bool value) => PreviewTheme();

    partial void OnBackgroundPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCustomBackground));
        OnPropertyChanged(nameof(BackgroundName));
        PreviewTheme();
    }

    /// <summary>Repinta el launcher con el tema del formulario (sin guardarlo).</summary>
    private void PreviewTheme()
    {
        if (_loadingTheme)
            return;
        OnPropertyChanged(nameof(AccentPreview));
        if (Error.Length > 0 && SelectedSection.Section == OptionsSection.Theme)
            Error = string.Empty;
        ThemePalette.Current.Apply(DraftTheme);
    }

    /// <summary>Error de validación (se muestra junto a GUARDAR); vacío = ninguno.</summary>
    [ObservableProperty] private string _error = string.Empty;

    /// <summary>Carpeta con las instancias y los archivos compartidos.</summary>
    public string DataDir => LauncherPaths.DataDir;

    public string LogsDir => LauncherPaths.LogsDir;

    public string LauncherVersion =>
        typeof(OptionsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>Servicios en línea que usa el launcher.</summary>
    public IReadOnlyList<Credit> ServiceCredits { get; } =
    [
        new("Minecraft (Mojang Studios)", "El juego: versiones, librerías, recursos, Java y texturas de las skins", "", "https://www.minecraft.net"),
        new("Modrinth", "Mods, modpacks, paquetes de recursos, shaders y lista de servidores", "", "https://modrinth.com"),
        new("MineSkin", "Firma de las skins para que se vean en los servidores", "", "https://mineskin.org"),
    ];


    public string RuntimeText => $".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";

    // Visibilidad de cada sección (la vista muestra la seleccionada).
    public bool IsGameSection => SelectedSection.Section == OptionsSection.Game;
    public bool IsWindowSection => SelectedSection.Section == OptionsSection.Window;
    public bool IsThemeSection => SelectedSection.Section == OptionsSection.Theme;
    public bool IsLauncherSection => SelectedSection.Section == OptionsSection.Launcher;
    public bool IsStorageSection => SelectedSection.Section == OptionsSection.Storage;
    public bool IsAboutSection => SelectedSection.Section == OptionsSection.About;

    // Segmentos de "Mientras juegas".
    public bool OnPlayKeep { get => OnPlay == LauncherOnPlay.Keep; set { if (value) OnPlay = LauncherOnPlay.Keep; } }
    public bool OnPlayMinimize { get => OnPlay == LauncherOnPlay.Minimize; set { if (value) OnPlay = LauncherOnPlay.Minimize; } }
    public bool OnPlayHide { get => OnPlay == LauncherOnPlay.Hide; set { if (value) OnPlay = LauncherOnPlay.Hide; } }

    /// <summary>Tamaño sugerido que coincide con los campos (null = tamaño personalizado).</summary>
    public ResolutionPreset? SelectedPreset
    {
        get => ResolutionPresets.FirstOrDefault(p => p.Width == 0
            ? GameWidth.Trim().Length == 0 && GameHeight.Trim().Length == 0
            : GameWidth.Trim() == p.Width.ToString() && GameHeight.Trim() == p.Height.ToString());
        set
        {
            if (value is null)
                return;
            GameWidth = value.Width == 0 ? string.Empty : value.Width.ToString();
            GameHeight = value.Height == 0 ? string.Empty : value.Height.ToString();
        }
    }

    partial void OnSelectedSectionChanged(OptionsSectionItem value)
    {
        OnPropertyChanged(nameof(IsGameSection));
        OnPropertyChanged(nameof(IsWindowSection));
        OnPropertyChanged(nameof(IsThemeSection));
        OnPropertyChanged(nameof(IsLauncherSection));
        OnPropertyChanged(nameof(IsStorageSection));
        OnPropertyChanged(nameof(IsAboutSection));
    }

    partial void OnOnPlayChanged(LauncherOnPlay value)
    {
        OnPropertyChanged(nameof(OnPlayKeep));
        OnPropertyChanged(nameof(OnPlayMinimize));
        OnPropertyChanged(nameof(OnPlayHide));
    }

    partial void OnGameWidthChanged(string value) => OnResolutionChanged();
    partial void OnGameHeightChanged(string value) => OnResolutionChanged();

    private void OnResolutionChanged()
    {
        Error = string.Empty;
        OnPropertyChanged(nameof(SelectedPreset));
    }

    /// <summary>Abre el panel con los valores actuales del archivo, en la primera sección.</summary>
    public void Open()
    {
        _settings.Properties.Reload();

        // RAM: si el valor guardado no está en la lista (p. ej. "5G"), se añade para no perderlo al guardar.
        RamOptions.Clear();
        foreach (var value in RamValues)
            RamOptions.Add(new RamOption(value.Replace("G", " GB"), value));
        var currentRam = _settings.JavaMemory.Trim();
        var selected = RamOptions.FirstOrDefault(o => string.Equals(o.Value, currentRam, StringComparison.OrdinalIgnoreCase));
        if (selected is null && LauncherSettings.ParseMemoryMb(currentRam) is not null)
        {
            selected = new RamOption(currentRam, currentRam);
            RamOptions.Add(selected);
        }

        SelectedRam = selected ?? RamOptions.First(o => o.Value == LauncherSettings.DefaultMemory);

        // Java vacío = automático (el campo muestra "Automático (recomendado)").
        JavaPath = JavaSelector.IsAutomatic(_settings.JavaPath) ? string.Empty : _settings.JavaPath;
        JavaArgs = _settings.JavaArgs;

        // Solo se muestra un tamaño completo y válido; si no, el de Minecraft.
        var (width, height) = (_settings.GameWidth, _settings.GameHeight);
        GameWidth = width is not null && height is not null ? width.Value.ToString() : string.Empty;
        GameHeight = width is not null && height is not null ? height.Value.ToString() : string.Empty;
        Fullscreen = _settings.GameFullscreen;

        OnPlay = _settings.OnPlay;
        OpenLogOnCrash = _settings.OpenLogOnCrash;

        _savedTheme = _settings.Theme;
        LoadTheme(_savedTheme);

        Error = string.Empty;
        SelectedSection = Sections[0];
        IsOpen = true;
    }

    /// <summary>GUARDAR: valida y guarda todas las claves de una vez (el nombre y la skin están en la cuenta).</summary>
    [RelayCommand]
    private void Save()
    {
        var width = GameWidth.Trim();
        var height = GameHeight.Trim();
        if (width.Length > 0 || height.Length > 0)
        {
            if (LauncherSettings.ParseDimension(width) is null || LauncherSettings.ParseDimension(height) is null)
            {
                Error = $"El tamaño de la ventana debe tener ancho y alto entre {LauncherSettings.MinDimension} y {LauncherSettings.MaxDimension}.";
                SelectedSection = Sections.First(s => s.Section == OptionsSection.Window);
                return;
            }
        }

        var accent = CustomAccent.Trim();
        if (accent.Length > 0 && !Argb.TryParse(accent, out _))
        {
            Error = "El color de acento debe ser un color hexadecimal, como #3478F6.";
            SelectedSection = Sections.First(s => s.Section == OptionsSection.Theme);
            return;
        }

        var theme = DraftTheme;
        _settings.Properties.SetMany(
        [
            new(LauncherSettings.KeyJavaMemory, SelectedRam?.Value ?? LauncherSettings.DefaultMemory),
            new(LauncherSettings.KeyJavaPath, JavaPath.Trim()),
            new(LauncherSettings.KeyJavaArgs, JavaArgs.Trim()),
            new(LauncherSettings.KeyGameWidth, width),
            new(LauncherSettings.KeyGameHeight, height),
            new(LauncherSettings.KeyGameFullscreen, Fullscreen ? "true" : "false"),
            new(LauncherSettings.KeyLauncherOnPlay, LauncherSettings.FormatOnPlay(OnPlay)),
            new(LauncherSettings.KeyOpenLogOnCrash, OpenLogOnCrash ? "true" : "false"),
            new(LauncherSettings.KeyThemePreset, theme.PresetId),
            new(LauncherSettings.KeyThemeAccent, theme.CustomAccent ?? string.Empty),
            new(LauncherSettings.KeyThemeGlass, theme.GlassPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(LauncherSettings.KeyThemeBackground, theme.BackgroundPath ?? string.Empty),
            new(LauncherSettings.KeyThemeMode, theme.Solid ? "solid" : "translucent"),
        ]);
        _savedTheme = theme;
        ThemePalette.Current.Apply(theme);
        CleanBackgrounds(keep: theme.BackgroundPath);
        _logger.LogInformation("Opciones guardadas");
        Error = string.Empty;
        IsOpen = false;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>CANCELAR, ✕ o Esc: cierra sin guardar (y deshace la vista previa del tema).</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsOpen)
        {
            ThemePalette.Current.Apply(_savedTheme);
            CleanBackgrounds(keep: _savedTheme.BackgroundPath);
        }

        IsOpen = false;
    }

    /// <summary>Pone los valores de un tema en el formulario y lo muestra.</summary>
    private void LoadTheme(ThemeSettings theme)
    {
        _loadingTheme = true;
        SelectedThemePreset = ThemePresets.FirstOrDefault(p => p.Id == theme.PresetId) ?? ThemePresets[0];
        CustomAccent = theme.CustomAccent ?? string.Empty;
        GlassPercent = theme.GlassPercent;
        BackgroundPath = theme.BackgroundPath;
        SolidBackground = theme.Solid;
        _loadingTheme = false;
        SyncWheel();
        PreviewTheme();
    }

    /// <summary>"Usar el del tema": quita el tono propio.</summary>
    [RelayCommand]
    private void ClearCustomAccent() => CustomAccent = string.Empty;

    /// <summary>Vuelve al tema del diseño (sin guardar todavía).</summary>
    [RelayCommand]
    private void ResetTheme() => LoadTheme(ThemeSettings.Default);

    /// <summary>Carpeta donde se copian las imágenes de fondo elegidas (así no dependen del archivo original).</summary>
    private static string ThemeDir => Path.Combine(LauncherPaths.DataDir, "theme");

    private const long MaxBackgroundBytes = 40 * 1024 * 1024;

    /// <summary>"Elegir…": copia la imagen a la carpeta de datos y la muestra.</summary>
    [RelayCommand]
    private void PickBackground()
    {
        var path = _dialogs.PickFile("Imagen de fondo", "Imágenes (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp");
        if (path is null)
            return;
        try
        {
            if (new FileInfo(path).Length > MaxBackgroundBytes)
            {
                _dialogs.ShowWarning("Imagen de fondo", "La imagen ocupa demasiado (máximo 40 MB).");
                return;
            }

            if (ThemePalette.LoadImage(path) is null)
            {
                _dialogs.ShowWarning("Imagen de fondo", "No se pudo abrir la imagen. Prueba con un PNG o un JPG.");
                return;
            }

            Directory.CreateDirectory(ThemeDir);
            var copy = Path.Combine(ThemeDir, $"fondo-{Guid.NewGuid():N}"[..14] + Path.GetExtension(path).ToLowerInvariant());
            File.Copy(path, copy);
            BackgroundPath = copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo copiar la imagen de fondo {Path}", path);
            _dialogs.ShowWarning("Imagen de fondo", "No se pudo copiar la imagen.");
        }
    }

    /// <summary>"Quitar": vuelve a la imagen del launcher.</summary>
    [RelayCommand]
    private void ClearBackground() => BackgroundPath = null;

    /// <summary>Borra las imágenes copiadas que ya no se usan (las de vistas previas descartadas).</summary>
    private void CleanBackgrounds(string? keep)
    {
        try
        {
            if (!Directory.Exists(ThemeDir))
                return;
            foreach (var file in Directory.EnumerateFiles(ThemeDir, "fondo-*"))
            {
                if (!string.Equals(Path.GetFullPath(file), keep is null ? null : Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudieron limpiar las imágenes de fondo antiguas");
        }
    }

    /// <summary>Vuelve a los valores recomendados de la sección Juego (sin guardar todavía).</summary>
    [RelayCommand]
    private void ResetGame()
    {
        SelectedRam = RamOptions.First(o => o.Value == LauncherSettings.DefaultMemory);
        JavaPath = string.Empty;
        JavaArgs = string.Empty;
    }

    /// <summary>"Examinar" de Java: selector de ejecutable.</summary>
    [RelayCommand]
    private void BrowseJava()
    {
        var path = _dialogs.PickFile("Seleccionar Java", "Ejecutables (*.exe)|*.exe|Todos (*)|*.*");
        if (path is not null)
            JavaPath = path;
    }

    /// <summary>Abre en el Explorador la carpeta de datos (instancias y archivos compartidos).</summary>
    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(DataDir);

    /// <summary>Abre en el Explorador la carpeta de registros del launcher y del juego.</summary>
    [RelayCommand]
    private void OpenLogsFolder() => OpenFolder(LogsDir);

    /// <summary>Abre el registro de la última partida con el programa asociado a .log.</summary>
    [RelayCommand]
    private void OpenGameLog()
    {
        var path = Path.Combine(LogsDir, "game-latest.log");
        if (!File.Exists(path))
        {
            _dialogs.ShowWarning("Registro del juego", "Todavía no hay registro: aparece después de jugar una partida.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "No se pudo abrir {Path}", path);
        }
    }

    /// <summary>Abre en el navegador la página de un tercero (solo las de las listas, y solo https).</summary>
    [RelayCommand]
    private void OpenCredit(Credit? credit)
    {
        if (credit is null || !ServiceCredits.Contains(credit)
            || !Uri.TryCreate(credit.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "No se pudo abrir {Url}", uri);
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            _dialogs.OpenInExplorer(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "No se pudo abrir la carpeta {Path}", path);
        }
    }
}
