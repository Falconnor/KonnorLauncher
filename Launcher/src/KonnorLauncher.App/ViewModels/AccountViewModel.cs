using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.App.Services;
using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Players;
using Microsoft.Extensions.Logging;

namespace KonnorLauncher.App.ViewModels;

/// <summary>
/// Overlay de la cuenta sin conexión: nombre, foto de perfil y skin (con su vista 3D). Los cambios se
/// guardan todos juntos al pulsar Guardar. La foto y la skin se copian a la carpeta de datos, así
/// siguen funcionando aunque se borre o se mueva el archivo original.
/// </summary>
public sealed partial class AccountViewModel : ObservableObject
{
    private readonly LauncherSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogger<AccountViewModel> _logger;

    // Cambios pendientes de guardar. Skin: null = Steve (la de por defecto).
    private bool _avatarChanged;
    private BitmapSource? _pendingAvatar;
    private bool _skinChanged;
    private string? _pendingSkinPath;

    public AccountViewModel(LauncherSettings settings, IDialogService dialogs, ILogger<AccountViewModel> logger)
    {
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>Se dispara después de guardar (la barra lateral actualiza nombre y foto).</summary>
    public event EventHandler? Saved;

    /// <summary>La vista enfoca el campo de nombre cuando Guardar falla por un nombre inválido.</summary>
    public event EventHandler? FocusUsernameRequested;

    public static string AvatarPath => Path.Combine(LauncherPaths.PlayerDir, "avatar.png");
    public static string SkinPath => Path.Combine(LauncherPaths.PlayerDir, "skin.png");

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string? _usernameError;
    [ObservableProperty] private ImageSource? _avatar;
    [ObservableProperty] private string? _avatarError;
    [ObservableProperty] private SkinComposer.Pixels? _skin;
    [ObservableProperty] private bool _isSlim;
    [ObservableProperty] private string _skinName = string.Empty;
    [ObservableProperty] private string? _skinError;
    [ObservableProperty] private bool _isCustomSkin;
    [ObservableProperty] private string? _error;

    public bool HasAvatar => Avatar is not null;

    public string Initials
    {
        get
        {
            var name = Username.Trim();
            return name.Length == 0 ? "?" : InstanceItemViewModel.MakeInitials(name);
        }
    }

    partial void OnUsernameChanged(string value)
    {
        UsernameError = UsernameRules.ValidateForOptions(value);
        OnPropertyChanged(nameof(Initials));
    }

    partial void OnAvatarChanged(ImageSource? value) => OnPropertyChanged(nameof(HasAvatar));

    /// <summary>Abre el panel con los valores guardados.</summary>
    public void Open()
    {
        _settings.Properties.Reload();
        Username = _settings.PlayerUsername;
        UsernameError = null; // el error solo aparece al editar o al guardar
        Error = null;

        _avatarChanged = false;
        _pendingAvatar = null;
        AvatarError = null;
        Avatar = PlayerImages.OpenAvatar(_settings.PlayerAvatar);

        _skinChanged = false;
        _pendingSkinPath = null;
        SkinError = null;
        IsSlim = _settings.PlayerSkinSlim;
        var saved = _settings.PlayerSkin.Trim();
        if (saved.Length > 0)
        {
            var (skin, _, error) = PlayerImages.LoadSkin(saved);
            if (skin is not null)
            {
                ShowSkin(skin, Path.GetFileName(saved) == "skin.png" ? "Skin personalizada" : Path.GetFileName(saved), custom: true);
                IsOpen = true;
                return;
            }

            SkinError = $"No se pudo cargar la skin guardada: {error} Se muestra la de por defecto.";
        }

        ShowSteve();
        IsOpen = true;
    }

    // ── Foto de perfil ──────────────────────────────────────────────────────

    [RelayCommand]
    private void ChooseAvatar()
    {
        var path = _dialogs.PickFile("Elegir foto de perfil", "Imágenes (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif");
        if (path is null)
            return;
        var (image, error) = PlayerImages.LoadAvatar(path);
        AvatarError = error;
        if (image is null)
            return;
        _pendingAvatar = image;
        _avatarChanged = true;
        Avatar = image;
    }

    [RelayCommand]
    private void RemoveAvatar()
    {
        _pendingAvatar = null;
        _avatarChanged = true;
        AvatarError = null;
        Avatar = null;
    }

    // ── Skin ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ChooseSkin()
    {
        var path = _dialogs.PickFile("Elegir skin", "Skins de Minecraft (*.png)|*.png");
        if (path is null)
            return;
        var (skin, looksSlim, error) = PlayerImages.LoadSkin(path);
        if (skin is null)
        {
            SkinError = error; // la skin anterior se queda en la vista
            return;
        }

        SkinError = null;
        _pendingSkinPath = path;
        _skinChanged = true;
        IsSlim = looksSlim; // se propone el modelo que parece; se puede cambiar
        ShowSkin(skin, Path.GetFileName(path), custom: true);
    }

    [RelayCommand]
    private void ResetSkin()
    {
        SkinError = null;
        _pendingSkinPath = null;
        _skinChanged = true;
        IsSlim = false;
        ShowSteve();
    }

    [RelayCommand]
    private void SetModel(string model) => IsSlim = model == "slim";

    private void ShowSteve()
    {
        try
        {
            Skin = PlayerImages.Steve();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "No se pudo cargar la skin de Steve");
            Skin = null;
        }

        SkinName = "Steve (por defecto)";
        IsCustomSkin = false;
    }

    private void ShowSkin(SkinComposer.Pixels skin, string name, bool custom)
    {
        Skin = skin;
        SkinName = name;
        IsCustomSkin = custom;
    }

    // ── Guardar ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Save()
    {
        var username = Username.Trim();
        if (!UsernameRules.IsValid(username))
        {
            UsernameError = UsernameRules.ValidateForOptions(username);
            FocusUsernameRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var avatarSetting = _settings.PlayerAvatar;
        var skinSetting = _settings.PlayerSkin;
        try
        {
            if (_avatarChanged)
            {
                if (_pendingAvatar is null)
                {
                    File.Delete(AvatarPath);
                    avatarSetting = string.Empty;
                }
                else
                {
                    PlayerImages.SaveAvatar(_pendingAvatar, AvatarPath);
                    avatarSetting = AvatarPath;
                }
            }

            if (_skinChanged)
            {
                if (_pendingSkinPath is null)
                {
                    skinSetting = string.Empty;
                }
                else
                {
                    Directory.CreateDirectory(LauncherPaths.PlayerDir);
                    if (!string.Equals(Path.GetFullPath(_pendingSkinPath), Path.GetFullPath(SkinPath), StringComparison.OrdinalIgnoreCase))
                        File.Copy(_pendingSkinPath, SkinPath, overwrite: true);
                    skinSetting = SkinPath;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError(ex, "No se pudo guardar la cuenta");
            Error = $"No se pudo guardar: {ex.Message}";
            return;
        }

        _settings.Properties.SetMany(
        [
            new(LauncherSettings.KeyPlayerUsername, username),
            new(LauncherSettings.KeyPlayerAvatar, avatarSetting),
            new(LauncherSettings.KeyPlayerSkin, skinSetting),
            new(LauncherSettings.KeyPlayerSkinModel, IsSlim ? "slim" : "classic"),
        ]);
        _logger.LogInformation("Cuenta guardada");
        IsOpen = false;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancelar, ✕ o Esc: cierra sin guardar.</summary>
    [RelayCommand]
    private void Cancel() => IsOpen = false;
}
