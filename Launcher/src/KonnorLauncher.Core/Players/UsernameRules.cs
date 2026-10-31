using System.Text.RegularExpressions;

namespace KonnorLauncher.Core.Players;

/// <summary>
/// Reglas del nombre de jugador offline: <c>^[A-Za-z0-9_]{4,16}$</c>.
/// El panel de Opciones y el aviso de JUGAR usan textos distintos; se conservan los dos tal cual.
/// </summary>
public static partial class UsernameRules
{
    public const int MinLength = 4;
    public const int MaxLength = 16;

    [GeneratedRegex("^[A-Za-z0-9_]{4,16}$")]
    private static partial Regex ValidRegex();

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex AllowedCharsRegex();

    /// <summary>True si el nombre (recortado) cumple las reglas.</summary>
    public static bool IsValid(string? name) => name is not null && ValidRegex().IsMatch(name.Trim());

    /// <summary>
    /// Mensaje de error del panel OPCIONES (validación en tiempo real, en minúsculas).
    /// Devuelve null si el nombre es válido.
    /// </summary>
    public static string? ValidateForOptions(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length == 0)
            return "el nombre no puede estar vacío";
        if (text.Length < MinLength)
            return "el nombre debe contener mínimo 4 caracteres";
        if (text.Length > MaxLength)
            return "el nombre no puede superar 16 caracteres";
        if (!AllowedCharsRegex().IsMatch(text))
            return "solo letras, números y guion bajo ( _ )";
        return null;
    }

    /// <summary>
    /// Mensaje de error del diálogo ESCRIBE TU NOMBRE. Devuelve null si es válido.
    /// (El campo ya limita a 16 caracteres, pero se comprueba igualmente por seguridad.)
    /// </summary>
    public static string? ValidateForPrompt(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length == 0)
            return "El nombre no puede estar vacío";
        if (text.Length < MinLength)
            return "Mínimo 4 caracteres";
        if (!AllowedCharsRegex().IsMatch(text))
            return "Solo letras, números y guiones bajos (_)";
        if (text.Length > MaxLength)
            return "Máximo 16 caracteres";
        return null;
    }
}
