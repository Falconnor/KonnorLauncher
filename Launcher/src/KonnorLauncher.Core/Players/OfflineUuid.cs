using System.Security.Cryptography;
using System.Text;

namespace KonnorLauncher.Core.Players;

/// <summary>
/// UUID offline: UUID v3 de <c>"OfflinePlayer:" + nombre</c>, el mismo que calcula
/// un servidor con <c>online-mode=false</c> (Java <c>UUID.nameUUIDFromBytes</c>). Así el inventario
/// y los permisos de cada jugador coinciden con los que tiene en el servidor.
/// Se calcula aquí porque <c>MSession.CreateOfflineSession</c> de CmlLib 4.1 genera un UUID aleatorio.
/// </summary>
public static class OfflineUuid
{
    /// <summary>Devuelve el UUID en formato de 32 hex sin guiones (el que usa el argumento <c>--uuid</c>).</summary>
    public static string FromUsername(string username)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // versión 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // variante IETF
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Formato con guiones (8-4-4-4-12), útil para mostrar o comparar con el servidor.</summary>
    public static string ToDashed(string undashed) =>
        $"{undashed[..8]}-{undashed[8..12]}-{undashed[12..16]}-{undashed[16..20]}-{undashed[20..]}";
}
