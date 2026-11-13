using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using KonnorLauncher.App.Services;
using KonnorLauncher.Core.Instances;

namespace KonnorLauncher.App.ViewModels;

/// <summary>
/// Un mod en el panel de la instancia: nombre, icono (leído del jar en segundo plano) y el interruptor que lo activa o
/// lo desactiva (renombrándolo a <c>.jar.disabled</c>).
/// </summary>
public sealed partial class ModFileItem : ObservableObject
{
    // Iconos ya leídos (por ruta y fecha del archivo): al recargar la instancia no se vuelven a abrir los jar.
    private static readonly ConcurrentDictionary<(string Path, DateTime Written), ImageSource?> IconCache = new();
    private static readonly SemaphoreSlim IconSlots = new(2);

    private readonly string _modsDir;
    private ModFile _file;
    private bool _toggling;

    public ModFileItem(string modsDir, ModFile file, bool canToggle)
    {
        _modsDir = modsDir;
        _file = file;
        CanToggle = canToggle && !file.IsFolder; // una carpeta no se desactiva renombrándola
        _isEnabled = file.Enabled;
    }

    /// <summary>Nombre sin la extensión (<c>sodium-fabric-0.6.jar</c> → <c>sodium-fabric-0.6</c>); una carpeta, tal cual.</summary>
    public string Name => _file.IsFolder ? _file.Name : Path.GetFileNameWithoutExtension(_file.Name);

    /// <summary>Las instancias sincronizadas con un servidor no dejan tocar sus mods (los gestiona el servidor).</summary>
    public bool CanToggle { get; }

    private ImageSource? _icon;
    private bool _iconRequested;

    /// <summary>Icono del mod: se lee la primera vez que se muestra (no al cargar todas las instancias).</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested)
            {
                _iconRequested = true;
                _ = LoadIconAsync();
            }

            return _icon;
        }
        private set
        {
            if (SetProperty(ref _icon, value))
                OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => Icon is not null;

    /// <summary>Interruptor: renombra el archivo; si no se puede (abierto por el juego, ya existe el otro), vuelve atrás.</summary>
    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_toggling || value == _file.Enabled)
            return;
        try
        {
            _file = InstanceMods.SetEnabled(_modsDir, _file, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _toggling = true;
            IsEnabled = _file.Enabled;
            _toggling = false;
            Failed?.Invoke(this, ex);
            return;
        }

        Toggled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Se activó o desactivó (la instancia actualiza su contador de mods).</summary>
    public event EventHandler? Toggled;

    /// <summary>No se pudo cambiar (la ventana lo registra).</summary>
    public event EventHandler<Exception>? Failed;

    private async Task LoadIconAsync()
    {
        var path = Path.Combine(_modsDir, _file.FileName);
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (IconCache.TryGetValue((path, written), out var cached))
        {
            Icon = cached;
            if (cached is not null || _file.IsFolder)
                return;
        }
        else
        {
            await IconSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                var image = await Task.Run(() => Decode(InstanceMods.ReadIcon(path))).ConfigureAwait(false);
                IconCache[(path, written)] = image;
                Icon = image;
                if (image is not null || _file.IsFolder)
                    return;
            }
            finally
            {
                IconSlots.Release();
            }
        }

        // Sin icono dentro (casi todos los shaders): el de su proyecto en Modrinth, si está allí.
        if (ContentIconResolver.Current is { } remote && await remote.ResolveAsync(path).ConfigureAwait(false) is { } found)
        {
            IconCache[(path, written)] = found;
            Icon = found;
        }
    }

    /// <summary>PNG reducido a 96 px y congelado (se puede crear fuera del hilo de la interfaz).</summary>
    private static ImageSource? Decode(byte[]? data)
    {
        if (data is null)
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = 96;
            image.StreamSource = new MemoryStream(data);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException
                                       or IOException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
