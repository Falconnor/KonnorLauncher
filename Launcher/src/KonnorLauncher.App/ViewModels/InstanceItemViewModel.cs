using System.IO;
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
    [ObservableProperty] private bool _isSynced;
    [ObservableProperty] private string _syncText = string.Empty;
    [ObservableProperty] private string _lastPlayedText = string.Empty;
    [ObservableProperty] private string _memoryText = string.Empty;
    [ObservableProperty] private int _modCount;
    [ObservableProperty] private int _worldCount;
    [ObservableProperty] private int _resourcePackCount;
    [ObservableProperty] private int _shaderPackCount;

    /// <summary>Archivos .jar de la carpeta mods (los desactivados .jar.disabled no cuentan).</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> ModFiles { get; } = [];

    public void Update(GameInstance model)
    {
        Model = model;
        Name = model.Name;
        Initials = MakeInitials(model.Name);
        Summary = model.Summary;
        var (minecraft, loader, loaderVersion) = model.Display; // interpreta el id que publica el servidor
        MinecraftVersion = string.IsNullOrWhiteSpace(minecraft) ? "Sin versión" : minecraft;
        IsModded = loader != ModLoader.Vanilla;
        LoaderText = IsModded
            ? $"{GameInstance.LoaderDisplayName(loader)} {loaderVersion}".Trim()
            : "Vanilla";
        IsSynced = model.SyncWithServer;
        SyncText = (model.ServerClientVersion is { Length: > 0 } v ? $"Servidor · modpack {v}" : "Sincronizada con el servidor")
                   + (model.ServerReleased ? " · liberada" : string.Empty);
        LastPlayedText = FormatLastPlayed(model.LastPlayed);
        MemoryText = model.MemoryMb is { } mb ? $"{InstanceEditorViewModel.FormatMemory(mb)} de RAM" : string.Empty;
        ModCount = Count("mods", "*.jar", files: true);
        ModFiles.Clear();
        foreach (var file in ListMods())
            ModFiles.Add(file);
        WorldCount = Count("saves", "*", files: false);
        ResourcePackCount = Count("resourcepacks", "*", files: null);
        ShaderPackCount = Count("shaderpacks", "*", files: null);
    }

    private IEnumerable<string> ListMods()
    {
        try
        {
            var mods = Path.Combine(Directory, "mods");
            return System.IO.Directory.Exists(mods)
                ? System.IO.Directory.EnumerateFiles(mods, "*.jar").Select(Path.GetFileName).OfType<string>()
                    .Order(StringComparer.OrdinalIgnoreCase).Take(500).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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
