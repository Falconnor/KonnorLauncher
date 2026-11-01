namespace KonnorLauncher.Core.Verification;

/// <summary>Error que bloquea el inicio del juego.</summary>
public sealed class VerificationException : Exception
{
    /// <summary>Mensaje especial que la UI distingue del resto de fallos.</summary>
    public const string ServerNotResponding = "servidor no responde";

    public VerificationException(string message) : base(message) { }
    public VerificationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Entrada ya validada del manifest.
/// <paramref name="UrlPath"/> usa <c>/</c> y es a la vez la clave del manifest, la ruta de la URL
/// y (dentro de un bloque) el nombre de la entrada ZIP. <paramref name="RelativePath"/> usa el
/// separador del sistema. <paramref name="Sha256"/> está en mayúsculas.
/// </summary>
public sealed record ManifestFile(string RelativePath, string UrlPath, string Sha256, long Size);

/// <summary>Bloque ZIP de assets con sus archivos internos.</summary>
public sealed record AssetBlock(string Name, string Sha256, long Size, IReadOnlyList<ManifestFile> Files);

/// <summary>
/// Manifest completo: <c>client_version</c> (opcional), <c>files</c> y <c>asset_blocks</c>.
/// Política del servidor (opcional; null si un servidor anterior no la publica):
/// <c>delete_unauthorized</c> (limpieza), <c>always_online</c> (sin respuesta del servidor no se juega)
/// y <c>released</c> (instancias liberadas: el jugador puede desactivar la sincronización).
/// </summary>
public sealed record ClientManifest(string? ClientVersion, IReadOnlyList<ManifestFile> Files, IReadOnlyList<AssetBlock> AssetBlocks,
    bool? DeleteUnauthorized = null, bool? AlwaysOnline = null, bool? Released = null);

/// <summary>Resultado de una verificación.</summary>
/// <param name="Success">true = el juego puede iniciarse.</param>
/// <param name="Status">Último estado (<c>verificacion exitosa</c>, <c>servidor no responde</c> o <c>verificacion fallida</c>).</param>
/// <param name="BlocksToRepair">Bloques que necesitaban reparación.</param>
/// <param name="FilesToRepair">Archivos sueltos que necesitaban reparación.</param>
/// <param name="RemovedFiles">Archivos eliminados por la limpieza.</param>
public sealed record VerificationResult(bool Success, string Status, int BlocksToRepair = 0, int FilesToRepair = 0, int RemovedFiles = 0)
{
    public const string SuccessStatus = "verificacion exitosa";
    public const string FailedStatus = "verificacion fallida";
}
