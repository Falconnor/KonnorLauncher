$launcher = $PSScriptRoot
. "$PSScriptRoot\common.ps1"

# Configuración de rutas del launcher
$propertiesFile = Join-Path $launcher "launcher.properties"


$gamePath = Get-Property `
    $propertiesFile `
    "game.path"


$game = Join-Path `
    $launcher `
    $gamePath


$game = (Resolve-Path $game).Path

# Configuración del cliente Minecraft
$gameConfig = Get-Content "$game\config.json" |
    ConvertFrom-Json

$javaPath = Get-Property `
    $propertiesFile `
    "java.path"

$java = Join-Path `
    $game `
    $javaPath

$version = Get-Property `
    $propertiesFile `
    "minecraft.version"

$json = Get-Content `
    "$game\versions\$version\$version.json" |
    ConvertFrom-Json

    if ($version -like "fabric-loader-*") {

    $minecraftVersion =
        $version -replace '^fabric-loader-[^-]+-',''

}
elseif ($version -like "forge-*") {

    # l�gica Forge

}
else {

    # Vanilla

    $minecraftVersion = $version

}


$natives = "$game\versions\$version\natives"

# Crear classpath
$classpath = $json.libraries | ForEach-Object {

    if ($_.name -and $_.name -notmatch ":natives-") {

        $parts = $_.name.Split(":")

        if ($parts.Count -ge 3) {

            $group = $parts[0].Replace(".", "\")
            $artifact = $parts[1]
            $versionLib = $parts[2]

            "$game\libraries\$group\$artifact\$versionLib\$artifact-$versionLib.jar"
        }
    }

} | Where-Object { Test-Path $_ }


$classpath = ($classpath -join ";")
$mainJar = "$game\versions\$version\$version.jar"

if (Test-Path $mainJar) {

    $classpath += ";$mainJar"

}

$baseJar = "$game\versions\$minecraftVersion\$minecraftVersion.jar"

if (
    $minecraftVersion -ne $version -and
    (Test-Path $baseJar)
) {

    $classpath += ";$baseJar"

}

# Ejecutar Minecraft
& $java `
"-Xmx$($gameConfig.java.memory)" ` `
"-Djava.library.path=$natives" `
-cp "$classpath" `
$json.mainClass `
--username $gameConfig.player.username `
--version $version `
--gameDir "$game" `
--assetsDir "$game\assets" `
--assetIndex 17 `
--uuid $gameConfig.player.uuid `
--accessToken $gameConfig.player.accessToken