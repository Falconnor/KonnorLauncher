using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.App.Services;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Players;
using Microsoft.Extensions.Logging;

namespace KonnorLauncher.App.ViewModels;

/// <summary>Pestañas de la sección Skins: la galería de MineSkin (recientes o populares) o las descargadas.</summary>
public enum SkinsTab
{
    Latest,
    Popular,
    Downloaded,
}

/// <summary>Skin de la cuadrícula: nombre y vista de frente (se carga al aparecer).</summary>
public sealed partial class SkinCard(SkinSummary skin, bool? knownSlim = null) : ObservableObject
{
    public SkinSummary Skin { get; } = skin;
    public string Name => Skin.Name ?? "Sin nombre";
    public bool HasName => Skin.Name is not null;

    /// <summary>Modelo ya conocido (las descargadas lo guardan): no hace falta preguntarlo a MineSkin.</summary>
    internal bool? KnownSlim { get; } = knownSlim;

    [ObservableProperty] private ImageSource? _preview;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isInUse;

    /// <summary>La textura, ya descargada y validada (para el visor 3D y para aplicarla).</summary>
    internal SkinComposer.Pixels? Pixels { get; set; }
}

/// <summary>
/// Sección Skins: galería pública de MineSkin con búsqueda por nombre, las skins descargadas, vista 3D
/// de la elegida y botones para usarla como skin del jugador o descargarla sin aplicarla. Las texturas
/// de la galería se guardan en la caché del launcher; las descargadas, en <see cref="SkinLibrary"/>.
/// </summary>
public sealed partial class SkinsViewModel : ObservableObject
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Texturas descargadas a la vez para las vistas de la cuadrícula.</summary>
    private static readonly SemaphoreSlim Downloads = new(6, 6);

    private readonly MineSkinClient _client;
    private readonly SkinLibrary _library;
    private readonly AccountViewModel _account;
    private readonly ILogger<SkinsViewModel> _logger;
    private readonly HashSet<string> _seenTextures = [];
    private CancellationTokenSource? _searchCts;
    private string? _nextAfter;
    private bool _loaded;

    public SkinsViewModel(MineSkinClient client, SkinLibrary library, AccountViewModel account, ILogger<SkinsViewModel> logger)
    {
        _client = client;
        _library = library;
        _account = account;
        _logger = logger;
    }

    public ObservableCollection<SkinCard> Skins { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>Recientes, populares o descargadas (al buscar en la galería se busca entre todas, por recientes).</summary>
    [ObservableProperty] private SkinsTab _tab = SkinsTab.Latest;

    /// <summary>Zoom del visor 3D elegido con el deslizador (1 = el del visor; se empieza un 15 % más cerca).</summary>
    [ObservableProperty] private double _viewerZoom = DefaultViewerZoom;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasMore;

    // Skin elegida (panel de la derecha)
    [ObservableProperty] private SkinCard? _selected;
    [ObservableProperty] private SkinComposer.Pixels? _selectedPixels;
    [ObservableProperty] private bool _selectedSlim;
    [ObservableProperty] private bool _isSelectedLoading;
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isSelectedDownloaded;
    [ObservableProperty] private string? _selectedError;
    [ObservableProperty] private string? _appliedText;

    /// <summary>Zoom inicial: un 15 % y otro 15 % más cerca que el del visor (1,15 × 1,15). Es el centro del deslizador.</summary>
    public const double DefaultViewerZoom = 1.32;

    /// <summary>Límites del deslizador de zoom (su centro es <see cref="DefaultViewerZoom"/>).</summary>
    public const double MinViewerZoom = 0.75;
    public const double MaxViewerZoom = 1.89;

    /// <summary>En Descargadas, el segundo botón borra la skin en vez de descargarla.</summary>
    public bool CanDeleteSelected => IsDownloaded && IsSelectedDownloaded;

    /// <summary>Páginas que se piden seguidas si una no trae ninguna skin nueva (populares repite muchas).</summary>
    private const int MaxEmptyPages = 3;

    public bool IsLatest => Tab == SkinsTab.Latest;
    public bool IsPopular => Tab == SkinsTab.Popular;
    public bool IsDownloaded => Tab == SkinsTab.Downloaded;

    /// <summary>Populares solo se puede elegir sin búsqueda (la API no busca bien entre las populares).</summary>
    public bool CanChoosePopular => string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Orden que se pide a la API.</summary>
    private SkinListOrder ApiOrder => Tab == SkinsTab.Popular && CanChoosePopular ? SkinListOrder.Popular : SkinListOrder.Latest;

    public bool HasSelected => Selected is not null;
    public bool IsEmpty => !IsLoading && Error is null && Skins.Count == 0;

    public string EmptyText => IsDownloaded
        ? string.IsNullOrWhiteSpace(SearchText) ? "Todavía no has descargado ninguna skin." : "No hay skins descargadas con ese nombre."
        : "No hay skins con ese nombre.";

    public string SelectedModelText => SelectedSlim ? "Brazos finos (Alex)" : "Brazos clásicos (Steve)";

    private static string CacheTexturesDir => Path.Combine(LauncherPaths.CacheDir, "skins");

    private static string CachePath(string texture) => Path.Combine(CacheTexturesDir, texture + ".png");

    /// <summary>Archivo de la textura: el de las descargadas si está, si no el de la caché.</summary>
    private string TexturePath(string texture)
    {
        var downloaded = _library.TexturePath(texture);
        return File.Exists(downloaded) ? downloaded : CachePath(texture);
    }

    /// <summary>Al abrir la sección: la primera vez carga la galería.</summary>
    public async Task OpenAsync()
    {
        if (_loaded)
            return;
        _loaded = true;
        await SearchAsync(TimeSpan.Zero);
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanChoosePopular));
        _ = SearchAsync(SearchDelay);
    }

    partial void OnTabChanged(SkinsTab value)
    {
        OnPropertyChanged(nameof(IsLatest));
        OnPropertyChanged(nameof(IsPopular));
        OnPropertyChanged(nameof(IsDownloaded));
        OnPropertyChanged(nameof(CanDeleteSelected));
        _ = SearchAsync(TimeSpan.Zero);
    }

    partial void OnIsSelectedDownloadedChanged(bool value) => OnPropertyChanged(nameof(CanDeleteSelected));

    [RelayCommand]
    private void SetTab(string tab) => Tab = tab switch
    {
        "popular" => SkinsTab.Popular,
        "downloaded" => SkinsTab.Downloaded,
        _ => SkinsTab.Latest,
    };

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnSelectedChanged(SkinCard? value) => OnPropertyChanged(nameof(HasSelected));
    partial void OnSelectedSlimChanged(bool value) => OnPropertyChanged(nameof(SelectedModelText));

    [RelayCommand]
    private Task RetryAsync() => SearchAsync(TimeSpan.Zero);

    /// <summary>Primera página de la búsqueda (tras una pausa al escribir) o las descargadas.</summary>
    private async Task SearchAsync(TimeSpan delay)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cts.Token);
            IsLoading = true;
            Error = null;
            Skins.Clear();
            _seenTextures.Clear();
            HasMore = false;
            _nextAfter = null;
            if (IsDownloaded)
                await ShowDownloadedAsync(cts.Token);
            else
                await LoadPagesAsync(null, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (MineSkinException ex)
        {
            if (!cts.IsCancellationRequested)
                Error = ex.Message;
        }
        finally
        {
            if (_searchCts == cts)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(EmptyText));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>Las descargadas (de la más reciente a la más antigua), filtradas por nombre si se busca.</summary>
    private async Task ShowDownloadedAsync(CancellationToken ct)
    {
        var all = await Task.Run(_library.LoadAll, ct);
        if (ct.IsCancellationRequested)
            return;
        var filter = SearchText.Trim();
        foreach (var skin in all)
        {
            if (filter.Length > 0 && skin.Name?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) != true)
                continue;
            var summary = new SkinSummary(skin.Uuid ?? string.Empty, skin.Name, skin.Texture, skin.DownloadedAt.ToUnixTimeMilliseconds());
            AddCard(new SkinCard(summary, skin.Slim));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoadingMore || _nextAfter is null || _searchCts is not { } cts || IsDownloaded)
            return;
        IsLoadingMore = true;
        try
        {
            await LoadPagesAsync(_nextAfter, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (MineSkinException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    /// <summary>
    /// Pide la página que sigue a <paramref name="after"/> y, si no trae ninguna skin nueva (todas
    /// repetidas), unas pocas más: así la lista avanza y «Cargar más» no se queda sin efecto.
    /// </summary>
    private async Task LoadPagesAsync(string? after, CancellationToken ct)
    {
        for (var i = 0; i < MaxEmptyPages; i++)
        {
            var page = await _client.ListAsync(SearchText, after, order: ApiOrder, ct: ct);
            if (ct.IsCancellationRequested)
                return;
            // Un cursor que no avanza volvería a traer la misma página: se acaba ahí.
            var next = page.NextAfter is { } n && n != after ? n : null;
            var added = AddPage(page, next);
            if (added > 0 || next is null)
                return;
            after = next;
        }
    }

    /// <summary>Añade las skins nuevas (la galería repite muchas texturas: solo se muestra la primera).</summary>
    private int AddPage(SkinPage page, string? next)
    {
        _nextAfter = next;
        HasMore = next is not null && page.Skins.Count > 0;
        var added = 0;
        foreach (var skin in page.Skins)
        {
            if (AddCard(new SkinCard(skin)))
                added++;
        }

        OnPropertyChanged(nameof(IsEmpty));
        return added;
    }

    private bool AddCard(SkinCard card)
    {
        if (!_seenTextures.Add(card.Skin.Texture))
            return false;
        // La que se está viendo sigue marcada al volver a encontrarla en otra pestaña
        if (Selected?.Skin.Texture == card.Skin.Texture)
            card.IsSelected = true;
        Skins.Add(card);
        _ = LoadPreviewAsync(card);
        return true;
    }

    /// <summary>Descarga la textura (o la toma de la caché o de las descargadas) y prepara la vista de frente.</summary>
    private async Task LoadPreviewAsync(SkinCard card)
    {
        var pixels = await LoadTextureAsync(card.Skin.Texture);
        if (pixels is null)
            return;
        card.Pixels = pixels;
        card.Preview = await Task.Run(() => PlayerImages.ToBitmap(SkinComposer.ComposeFront(pixels)));
    }

    /// <summary>Textura validada y normalizada; null si no se pudo descargar o no es una skin.</summary>
    private async Task<SkinComposer.Pixels?> LoadTextureAsync(string texture)
    {
        var path = TexturePath(texture);
        await Downloads.WaitAsync();
        try
        {
            if (!File.Exists(path))
                await _client.DownloadTextureAsync(texture, path);
            var (skin, _, error) = await Task.Run(() => PlayerImages.LoadSkin(path));
            if (skin is null)
            {
                _logger.LogDebug("Textura {Texture} descartada: {Error}", texture, error);
                if (path == CachePath(texture))
                    TryDelete(path); // una descargada no se borra sola
            }

            return skin;
        }
        catch (MineSkinException)
        {
            return null;
        }
        finally
        {
            Downloads.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ── Skin elegida ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SelectAsync(SkinCard? card)
    {
        if (card is null)
            return;
        if (Selected is { } previous)
            previous.IsSelected = false;
        card.IsSelected = true;
        Selected = card;
        SelectedError = null;
        AppliedText = card.IsInUse ? "Es tu skin actual." : null;
        IsSelectedDownloaded = _library.Contains(card.Skin.Texture);
        SelectedPixels = card.Pixels;
        SelectedSlim = card.KnownSlim ?? (card.Pixels is { } p && SkinTexture.LooksSlim(p));
        IsSelectedLoading = true;
        try
        {
            // El modelo (brazos finos o clásicos) lo dice la ficha; la textura puede no estar aún.
            var details = card.KnownSlim is null && card.Skin.Uuid.Length > 0 ? _client.GetAsync(card.Skin.Uuid) : null;
            card.Pixels ??= await LoadTextureAsync(card.Skin.Texture);
            if (Selected != card)
                return;
            SelectedPixels = card.Pixels;
            if (card.Pixels is null)
                SelectedError = "No se pudo descargar esta skin.";
            if (details is null)
                return;
            try
            {
                var info = await details;
                if (Selected == card)
                    SelectedSlim = info.Slim;
            }
            catch (MineSkinException)
            {
                // Sin ficha se queda el modelo que parece por la textura.
            }
        }
        finally
        {
            if (Selected == card)
                IsSelectedLoading = false;
        }
    }

    [RelayCommand]
    private void SetModel(string model) => SelectedSlim = model == "slim";

    /// <summary>Usa la skin elegida como la del jugador (se guarda al momento).</summary>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (Selected is not { Pixels: not null } card || IsApplying)
            return;
        IsApplying = true;
        try
        {
            var error = await Task.Run(() => _account.ApplySkin(TexturePath(card.Skin.Texture), SelectedSlim));
            SelectedError = error;
            if (error is not null)
                return;
            foreach (var other in Skins)
                other.IsInUse = false;
            card.IsInUse = true;
            AppliedText = "Listo: es tu skin (la puedes ver en tu cuenta).";
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>Guarda la skin elegida en Descargadas, sin aplicarla.</summary>
    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (Selected is not { Pixels: not null } card || IsDownloading)
            return;
        IsDownloading = true;
        try
        {
            var slim = SelectedSlim;
            await Task.Run(() => _library.Add(TexturePath(card.Skin.Texture), card.Skin.Texture, card.Skin.Name, slim,
                card.Skin.Uuid.Length > 0 ? card.Skin.Uuid : null));
            if (Selected == card)
            {
                IsSelectedDownloaded = true;
                SelectedError = null;
                AppliedText = "Descargada: la tienes en Descargadas.";
            }

            if (IsDownloaded)
                await SearchAsync(TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "No se pudo guardar la skin {Texture}", card.Skin.Texture);
            if (Selected == card)
                SelectedError = "No se pudo guardar la skin: " + ex.Message;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>Borra la skin elegida de Descargadas (si es la del jugador, la sigue usando: es una copia aparte).</summary>
    [RelayCommand]
    private async Task DeleteDownloadedAsync()
    {
        if (Selected is not { } card || !IsSelectedDownloaded)
            return;
        try
        {
            await Task.Run(() => _library.Remove(card.Skin.Texture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo borrar la skin {Texture}", card.Skin.Texture);
            SelectedError = "No se pudo borrar la skin: " + ex.Message;
            return;
        }

        IsSelectedDownloaded = false;
        if (IsDownloaded)
        {
            // Fuera de la lista y se cierra su ficha
            // (la elegida puede ser la tarjeta de otra pestaña con la misma textura)
            if (Skins.FirstOrDefault(c => c.Skin.Texture == card.Skin.Texture) is { } shown)
                Skins.Remove(shown);
            _seenTextures.Remove(card.Skin.Texture);
            CloseSelected();
            OnPropertyChanged(nameof(EmptyText));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private void CloseSelected()
    {
        if (Selected is { } card)
            card.IsSelected = false;
        Selected = null;
        SelectedPixels = null;
    }

    /// <summary>Página de la skin en MineSkin (de dónde sale).</summary>
    [RelayCommand]
    private void OpenOnMineSkin()
    {
        if (Selected is not { } card || card.Skin.Uuid.Length == 0)
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MineSkinClient.PageUrl(card.Skin.Uuid)) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    /// <summary>Esc: cierra la ficha de la skin. Devuelve false si no había nada abierto.</summary>
    public bool CloseTopmost()
    {
        if (Selected is null)
            return false;
        CloseSelected();
        return true;
    }
}
