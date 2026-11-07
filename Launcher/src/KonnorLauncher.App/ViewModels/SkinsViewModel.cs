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

/// <summary>Skin de la cuadrícula: nombre y vista de frente (se carga al aparecer).</summary>
public sealed partial class SkinCard(SkinSummary skin) : ObservableObject
{
    public SkinSummary Skin { get; } = skin;
    public string Name => Skin.Name ?? "Sin nombre";
    public bool HasName => Skin.Name is not null;

    [ObservableProperty] private ImageSource? _preview;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isInUse;

    /// <summary>La textura, ya descargada y validada (para el visor 3D y para aplicarla).</summary>
    internal SkinComposer.Pixels? Pixels { get; set; }
}

/// <summary>
/// Sección Skins: galería pública de MineSkin con búsqueda por nombre, vista 3D de la elegida y botón
/// para usarla como skin del jugador. Las texturas se guardan en la caché del launcher.
/// </summary>
public sealed partial class SkinsViewModel : ObservableObject
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Texturas descargadas a la vez para las vistas de la cuadrícula.</summary>
    private static readonly SemaphoreSlim Downloads = new(6, 6);

    private readonly MineSkinClient _client;
    private readonly AccountViewModel _account;
    private readonly ILogger<SkinsViewModel> _logger;
    private readonly HashSet<string> _seenTextures = [];
    private CancellationTokenSource? _searchCts;
    private string? _nextAfter;
    private bool _loaded;

    public SkinsViewModel(MineSkinClient client, AccountViewModel account, ILogger<SkinsViewModel> logger)
    {
        _client = client;
        _account = account;
        _logger = logger;
    }

    public ObservableCollection<SkinCard> Skins { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
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
    [ObservableProperty] private string? _selectedError;
    [ObservableProperty] private string? _appliedText;

    public bool HasSelected => Selected is not null;
    public bool IsEmpty => !IsLoading && Error is null && Skins.Count == 0;
    public string SelectedModelText => SelectedSlim ? "Brazos finos (Alex)" : "Brazos clásicos (Steve)";

    private static string TexturesDir => Path.Combine(LauncherPaths.CacheDir, "skins");

    private static string TexturePath(string texture) => Path.Combine(TexturesDir, texture + ".png");

    /// <summary>Al abrir la sección: la primera vez carga la galería.</summary>
    public async Task OpenAsync()
    {
        if (_loaded)
            return;
        _loaded = true;
        await SearchAsync(TimeSpan.Zero);
    }

    partial void OnSearchTextChanged(string value) => _ = SearchAsync(SearchDelay);

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnSelectedChanged(SkinCard? value) => OnPropertyChanged(nameof(HasSelected));
    partial void OnSelectedSlimChanged(bool value) => OnPropertyChanged(nameof(SelectedModelText));

    [RelayCommand]
    private Task RetryAsync() => SearchAsync(TimeSpan.Zero);

    /// <summary>Primera página de la búsqueda (tras una pausa al escribir).</summary>
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
            var page = await _client.ListAsync(SearchText, ct: cts.Token);
            if (cts.IsCancellationRequested)
                return;
            AddPage(page);
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
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoadingMore || _nextAfter is null || _searchCts is not { } cts)
            return;
        IsLoadingMore = true;
        try
        {
            var page = await _client.ListAsync(SearchText, _nextAfter, ct: cts.Token);
            if (!cts.IsCancellationRequested)
                AddPage(page);
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

    /// <summary>Añade las skins nuevas (la galería repite muchas texturas: solo se muestra la primera).</summary>
    private void AddPage(SkinPage page)
    {
        _nextAfter = page.NextAfter;
        HasMore = page.NextAfter is not null && page.Skins.Count > 0;
        foreach (var skin in page.Skins)
        {
            if (!_seenTextures.Add(skin.Texture))
                continue;
            var card = new SkinCard(skin);
            Skins.Add(card);
            _ = LoadPreviewAsync(card);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Descarga la textura (o la toma de la caché) y prepara la vista de frente.</summary>
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
                TryDelete(path);
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
        SelectedPixels = card.Pixels;
        SelectedSlim = card.Pixels is { } p && SkinTexture.LooksSlim(p);
        IsSelectedLoading = true;
        try
        {
            // El modelo (brazos finos o clásicos) lo dice la ficha; la textura puede no estar aún.
            var details = _client.GetAsync(card.Skin.Uuid);
            card.Pixels ??= await LoadTextureAsync(card.Skin.Texture);
            if (Selected != card)
                return;
            SelectedPixels = card.Pixels;
            if (card.Pixels is null)
                SelectedError = "No se pudo descargar esta skin.";
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
        if (Selected is not { } card)
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
