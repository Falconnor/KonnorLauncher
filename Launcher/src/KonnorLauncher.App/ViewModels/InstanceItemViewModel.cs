using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using KonnorLauncher.Core.Instances;

namespace KonnorLauncher.App.ViewModels;

/// <summary>Una instancia tal como se ve en la barra lateral y en el panel principal.</summary>
public sealed partial class InstanceItemViewModel : ObservableObject
{
    public InstanceItemViewModel(GameInstance model, string directory)
    {
        Model = model;
        Directory = directory;
        Update(model);
    }

    /// <summary>Carpeta de la instancia (mods, saves, config...).</summary>
    public string Directory { get; }

    public GameInstance Model { get; private set; }

    public string Id => Model.Id;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _initials = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _minecraftVersion = string.Empty;
    [ObservableProperty] private string _loaderText = string.Empty;
    [ObservableProperty] private bool _isModded;
    /// <summary>Cargador tal como se muestra (da el color del icono).</summary>
    [ObservableProperty] private ModLoader _loader;
    [ObservableProperty] private bool _isSynced;
    [ObservableProperty] private string _syncText = string.Empty;
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private string _updateText = string.Empty;
    [ObservableProperty] private string _readyText = string.Empty;
    [ObservableProperty] private string _lastPlayedText = string.Empty;
    [ObservableProperty] private string _memoryText = string.Empty;
    [ObservableProperty] private int _modCount;
    [ObservableProperty] private int _worldCount;
    [ObservableProperty] private int _resourcePackCount;
    [ObservableProperty] private int _shaderPackCount;

    /// <summary>Icono propio de la instancia (el del modpack con que se creó); null: se ven las iniciales.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private ImageSource? _icon;

    public bool HasIcon => Icon is not null;

    /// <summary>Archivo del icono dentro de la carpeta de la instancia.</summary>
    public const string IconFileName = "icon.png";

    /// <summary>Mods de la carpeta mods, activos y desactivados (.jar.disabled), cada uno con su interruptor.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ModFileItem> ModFiles { get; } = [];

    /// <summary>Shaders (shaderpacks) y paquetes de recursos (resourcepacks): .zip, desactivados y carpetas.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ModFileItem> ShaderFiles { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<ModFileItem> ResourcePackFiles { get; } = [];

    /// <summary>Pestaña del panel (Mods, Shaders, Paquetes de recursos): la elige la ventana.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentFiles))]
    private InstanceContentKind _contentTab;

    /// <summary>Lo que se ve en la pestaña elegida.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ModFileItem> CurrentFiles => ContentTab switch
    {
        InstanceContentKind.Shaders => ShaderFiles,
        InstanceContentKind.ResourcePacks => ResourcePackFiles,
        _ => ModFiles,
    };

    /// <summary>No se pudo activar o desactivar un mod (la ventana lo registra).</summary>
    public event EventHandler<Exception>? ModToggleFailed;

    public void Update(GameInstance model)
    {
        Model = model;
        Name = model.Name;
        Initials = MakeInitials(model.Name);
        Summary = model.Summary;
        var (minecraft, loader, loaderVersion) = model.Display; // interpreta el id que publica el servidor
        MinecraftVersion = string.IsNullOrWhiteSpace(minecraft) ? "Sin versión" : minecraft;
        IsModded = loader != ModLoader.Vanilla;
        Loader = loader;
        LoaderText = IsModded
            ? $"{GameInstance.LoaderDisplayName(loader)} {loaderVersion}".Trim()
            : "Vanilla";
        IsSynced = model.SyncWithServer;
        SyncText = (model.ServerClientVersion is { Length: > 0 } v ? $"Servidor · modpack {v}" : "Sincronizada con el servidor")
                   + (model.ServerReleased ? " · liberada" : string.Empty);
        // El servidor publicó una versión del cliente posterior a la instalada: se descarga al jugar.
        HasUpdate = model.HasUpdate;
        UpdateText = HasUpdate ? $"Actualización disponible · modpack {model.AvailableClientVersion}" : string.Empty;
        // Una instancia enlazada aún debe pasar la verificación del servidor antes de estar lista.
        ReadyText = IsSynced ? $"Jugar a {model.Name}" : $"Listo para jugar a {model.Name}";
        LastPlayedText = FormatLastPlayed(model.LastPlayed);
        MemoryText = model.MemoryMb is { } mb ? $"{InstanceEditorViewModel.FormatMemory(mb)} de RAM"
            : model.SyncWithServer && model.ServerMemoryMb is not null ? $"{InstanceEditorViewModel.FormatMemory(model.ResolveMemoryMb(0))} de RAM (del servidor)"
            : string.Empty;
        ModCount = Count("mods", "*.jar", files: true);
        LoadContent(InstanceContentKind.Mods, ModFiles);
        LoadContent(InstanceContentKind.Shaders, ShaderFiles);
        LoadContent(InstanceContentKind.ResourcePacks, ResourcePackFiles);
        WorldCount = Count("saves", "*", files: false);
        RefreshCounts();
        Icon = LoadIcon();
    }

    private ImageSource? LoadIcon()
    {
        var file = Path.Combine(Directory, IconFileName);
        try
        {
            if (!File.Exists(file))
                return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // no deja el archivo abierto
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 128;
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null; // dañado: se ven las iniciales
        }
    }

    private void LoadContent(InstanceContentKind kind, System.Collections.ObjectModel.ObservableCollection<ModFileItem> target)
    {
        IReadOnlyList<ModFile> files;
        var dir = Path.Combine(Directory, InstanceMods.FolderName(kind));
        try
        {
            files = InstanceMods.List(dir, kind);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files = [];
        }

        target.Clear();
        // El contenido de una instancia sincronizada lo decide su servidor: no se puede apagar.
        var canToggle = !Model.IsSyncLocked;
        foreach (var file in files)
        {
            var item = new ModFileItem(dir, file, canToggle);
            item.Toggled += (_, _) => RefreshCounts();
            item.Failed += (_, ex) => ModToggleFailed?.Invoke(this, ex);
            target.Add(item);
        }
    }

    /// <summary>Cifras del panel (los desactivados no cuentan).</summary>
    private void RefreshCounts()
    {
        ModCount = Count("mods", "*.jar", files: true);
        ResourcePackCount = Count("resourcepacks", "*", files: null) - ResourcePackFiles.Count(f => !f.IsEnabled);
        ShaderPackCount = Count("shaderpacks", "*", files: null) - ShaderFiles.Count(f => !f.IsEnabled);
    }

    /// <summary>Elementos de una subcarpeta: archivos (true), carpetas (false) o ambos (null).</summary>
    private int Count(string folder, string pattern, bool? files)
    {
        var path = Path.Combine(Directory, folder);
        try
        {
            if (!System.IO.Directory.Exists(path))
                return 0;
            return files switch
            {
                true => System.IO.Directory.EnumerateFiles(path, pattern).Count(),
                false => System.IO.Directory.EnumerateDirectories(path, pattern).Count(),
                null => System.IO.Directory.EnumerateFileSystemEntries(path, pattern).Count(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>"Servidor Konnor" → "SK"; "Pruebas" → "PR".</summary>
    public static string MakeInitials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var text = words.Length > 1 ? $"{words[0][0]}{words[1][0]}" : (words.FirstOrDefault() ?? "?")[..Math.Min(2, (words.FirstOrDefault() ?? "?").Length)];
        return text.ToUpperInvariant();
    }

    public static string FormatLastPlayed(DateTimeOffset? lastPlayed)
    {
        if (lastPlayed is not { } when)
            return "Nunca jugada";
        var span = DateTimeOffset.Now - when;
        if (span.TotalMinutes < 1)
            return "Jugada ahora mismo";
        if (span.TotalHours < 1)
            return $"Jugada hace {(int)span.TotalMinutes} min";
        if (span.TotalDays < 1)
            return $"Jugada hace {(int)span.TotalHours} h";
        if (span.TotalDays < 2)
            return "Jugada ayer";
        if (span.TotalDays < 30)
            return $"Jugada hace {(int)span.TotalDays} días";
        return $"Jugada el {when.LocalDateTime:d MMM yyyy}";
    }
}
