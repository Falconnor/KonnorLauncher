using System.Security.Cryptography;
using System.Text;

namespace KonnorLauncher.App.Services;

/// <summary>
/// Cifra los tokens de los hostings de terceros con la cuenta de Windows del jugador (DPAPI): en disco nunca están en
/// claro y solo ese usuario, en ese equipo, puede descifrarlos.
/// </summary>
public static class TokenProtector
{
    // Sal propia del launcher: otro programa del mismo usuario no lo descifra con solo llamar a DPAPI.
    private static readonly byte[] Entropy = "KonnorLauncher.ThirdParty.v1"u8.ToArray();

    public static string Protect(string token) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>El token en claro; null si no se puede descifrar (otro usuario u otro equipo).</summary>
    public static string? Unprotect(string? protectedToken)
    {
        if (string.IsNullOrWhiteSpace(protectedToken))
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
