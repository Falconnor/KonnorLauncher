using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KonnorLauncher.Core.Verification;

/// <summary>
/// Reglas que un servidor impone a las instancias vinculadas, tal como las firma (<see cref="ServerPolicyCrypto"/>).
/// <paramref name="ManifestSha256"/> es el SHA-256 del <c>client_manifest.json</c> que sirve en ese momento: el
/// launcher solo acepta un manifest con ese hash, así nadie puede cambiar en el camino la lista de archivos.
/// Vale hasta <paramref name="ExpiresAt"/>: sin conexión con el servidor, el launcher usa la última recibida mientras
/// no caduque; después aplica las reglas más restrictivas.
/// </summary>
public sealed record ServerPolicy(
    [property: JsonPropertyName("client_version")] string? ClientVersion,
    [property: JsonPropertyName("always_online")] bool AlwaysOnline,
    [property: JsonPropertyName("lock_server_url")] bool LockServerUrl,
    [property: JsonPropertyName("protect_packs")] bool ProtectPacks,
    [property: JsonPropertyName("released")] bool Released,
    [property: JsonPropertyName("server_address")] string? AutoJoinAddress,
    [property: JsonPropertyName("recommended_memory_mb")] int? RecommendedMemoryMb,
    [property: JsonPropertyName("manifest_sha256")] string ManifestSha256,
    [property: JsonPropertyName("issued_at")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    /// <summary>Las reglas de un manifest (las mismas claves), firmables tal cual.</summary>
    public static ServerPolicy FromManifest(ClientManifest manifest, byte[] manifestBytes, DateTimeOffset issuedAt, TimeSpan validity) => new(
        manifest.ClientVersion,
        manifest.AlwaysOnline ?? true,
        manifest.LockServerUrl ?? true,
        manifest.ProtectPacks == true,
        manifest.Released == true,
        manifest.AutoJoinAddress,
        manifest.RecommendedMemoryMb,
        Convert.ToHexString(SHA256.HashData(manifestBytes)),
        issuedAt,
        issuedAt + validity);
}

/// <summary>
/// Política firmada tal como viaja y como la guarda la instancia («última sesión»): el JSON de la política en Base64
/// (<see cref="Payload"/>), su firma ECDSA P-256 / SHA-256 (<see cref="Signature"/>) y la clave pública del servidor
/// (<see cref="Key"/>, SubjectPublicKeyInfo en Base64). Se puede leer, pero cambiar un solo carácter invalida la firma.
/// </summary>
public sealed record SignedServerPolicy(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("key")] string Key);

/// <summary>Resultado de comprobar una política firmada.</summary>
public enum PolicyStatus
{
    /// <summary>Firma correcta, de la clave fijada y sin caducar.</summary>
    Valid,

    /// <summary>Firma correcta pero caducada (sin conexión demasiado tiempo).</summary>
    Expired,

    /// <summary>Firmada con otra clave: no es el servidor con el que se vinculó la instancia.</summary>
    KeyMismatch,

    /// <summary>Falta, está dañada o se modificó (la firma no coincide).</summary>
    Invalid,
}

/// <summary>Política comprobada: <see cref="Policy"/> solo si la firma es correcta (válida o caducada).</summary>
public sealed record PolicyCheck(PolicyStatus Status, ServerPolicy? Policy)
{
    public static readonly PolicyCheck Invalid = new(PolicyStatus.Invalid, null);
}

/// <summary>
/// Firma (servidor) y comprobación (launcher) de <see cref="ServerPolicy"/> con ECDSA P-256, que trae .NET. La clave
/// privada nunca sale del equipo del servidor; el launcher fija la pública la primera vez que se vincula y desde
/// entonces solo acepta políticas firmadas con ella.
/// </summary>
public static class ServerPolicyCrypto
{
    /// <summary>Ruta del servicio en el servidor de publicación.</summary>
    public const string Endpoint = "/api/policy";

    /// <summary>Validez de una política: sin conexión, el launcher la respeta hasta entonces.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromDays(7);

    /// <summary>Margen para relojes un poco desajustados entre servidor y jugador.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// Clave del servidor: la guardada en <paramref name="path"/> (PKCS#8 en PEM) o una nueva que se guarda ahí. Debe
    /// conservarse: si se pierde, los launchers vinculados dejan de aceptar al servidor (otra identidad).
    /// </summary>
    public static ECDsa LoadOrCreateKey(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            key.ImportFromPem(File.ReadAllText(path));
            return key;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, key.ExportPkcs8PrivateKeyPem());
        File.Move(tmp, path, overwrite: true);
        return key;
    }

    /// <summary>Clave pública (SubjectPublicKeyInfo en Base64): la que fija el launcher.</summary>
    public static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    public static SignedServerPolicy Sign(ServerPolicy policy, ECDsa key)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(policy, JsonOptions);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256);
        return new SignedServerPolicy(Convert.ToBase64String(payload), Convert.ToBase64String(signature), PublicKey(key));
    }

    /// <summary>
    /// Comprueba la firma, que sea de la clave fijada (<paramref name="pinnedKey"/>; null = aún no hay ninguna) y que
    /// no haya caducado en <paramref name="now"/>.
    /// </summary>
    public static PolicyCheck Verify(SignedServerPolicy? signed, string? pinnedKey, DateTimeOffset now)
    {
        if (signed is null || string.IsNullOrEmpty(signed.Payload) || string.IsNullOrEmpty(signed.Signature) || string.IsNullOrEmpty(signed.Key))
            return PolicyCheck.Invalid;
        if (pinnedKey is not null && !string.Equals(pinnedKey, signed.Key, StringComparison.Ordinal))
            return new PolicyCheck(PolicyStatus.KeyMismatch, null);
        try
        {
            var payload = Convert.FromBase64String(signed.Payload);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(signed.Key), out _);
            if (key.KeySize != 256 || !key.VerifyData(payload, Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256))
                return PolicyCheck.Invalid;
            var policy = JsonSerializer.Deserialize<ServerPolicy>(payload, JsonOptions);
            if (policy is null || policy.ManifestSha256.Length != 64 || policy.ExpiresAt <= policy.IssuedAt)
                return PolicyCheck.Invalid;
            return now > policy.ExpiresAt + ClockSkew
                ? new PolicyCheck(PolicyStatus.Expired, policy)
                : new PolicyCheck(PolicyStatus.Valid, policy);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            return PolicyCheck.Invalid;
        }
    }

    /// <summary>La política tal como viaja (JSON con <c>payload</c>, <c>signature</c> y <c>key</c>).</summary>
    public static string Serialize(SignedServerPolicy signed) => JsonSerializer.Serialize(signed, JsonOptions);

    /// <summary>null si no es una política firmada.</summary>
    public static SignedServerPolicy? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SignedServerPolicy>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Hash del manifest tal como se recibió (lo que se compara con <see cref="ServerPolicy.ManifestSha256"/>).</summary>
    public static string ManifestHash(byte[] manifestBytes) => Convert.ToHexString(SHA256.HashData(manifestBytes));
}
