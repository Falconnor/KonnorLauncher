using KonnorLauncher.Core.Instances;

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
/// <paramref name="Sha512"/> (minúsculas) solo lo llevan los archivos que pueden estar en Modrinth:
/// sirve para buscarlos allí, pero la comprobación es siempre con el SHA-256.
/// </summary>
public sealed record ManifestFile(string RelativePath, string UrlPath, string Sha256, long Size, string? Sha512 = null);

/// <summary>
/// Sección <c>minecraft</c>: versión y cargador que el launcher instala por su cuenta (Mojang y la web
/// oficial del cargador). <paramref name="LoaderVersion"/> es null en vanilla.
/// </summary>
public sealed record ManifestGame(string MinecraftVersion, ModLoader Loader, string? LoaderVersion);

/// <summary>Bloque ZIP de assets con sus archivos internos.</summary>
public sealed record AssetBlock(string Name, string Sha256, long Size, IReadOnlyList<ManifestFile> Files);

/// <summary>
/// Manifest completo: <c>client_version</c> (opcional), <c>files</c> y <c>asset_blocks</c>.
/// Política del servidor (opcional; null si un servidor anterior no la publica):
/// <c>delete_unauthorized</c> (limpieza), <c>always_online</c> (sin respuesta del servidor no se juega)
/// <c>released</c> (instancias liberadas: el jugador puede desactivar la sincronización) y
/// <c>lock_server_url</c> (el jugador no puede cambiar la dirección del servidor; reversible) y
/// <c>protect_packs</c> (los paquetes de recursos y de shaders se instalan siempre y se protegen).
/// <c>auto_join</c> y <c>server_address</c>: al jugar, el juego entra directamente a ese servidor de Minecraft
/// (<see cref="AutoJoinAddress"/>; null si no se pide o la dirección no es válida).
/// <c>recommended_memory_mb</c>: RAM que el servidor propone para la instancia (<see cref="RecommendedMemoryMb"/>).
/// <c>verify_hashes</c> (seguridad avanzada): los archivos se comprueban también por su SHA-256, no solo por
/// nombre y tamaño.
/// <c>minecraft</c> (opcional): si está, <c>versions</c>, <c>libraries</c> y <c>assets</c> no vienen del
/// servidor, los instala el launcher.
/// </summary>
public sealed record ClientManifest(string? ClientVersion, IReadOnlyList<ManifestFile> Files, IReadOnlyList<AssetBlock> AssetBlocks,
    bool? DeleteUnauthorized = null, bool? AlwaysOnline = null, bool? Released = null, bool? LockServerUrl = null,
    bool? ProtectPacks = null, ManifestGame? Game = null, string? AutoJoinAddress = null, int? RecommendedMemoryMb = null,
    bool? VerifyHashes = null);

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
