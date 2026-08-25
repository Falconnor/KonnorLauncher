$launcher = $PSScriptRoot
. "$PSScriptRoot\common.ps1"

$propertiesFile = Join-Path $launcher "launcher.properties"


$gamePath = Get-Property `
    $propertiesFile `
    "game.path"


$game = Join-Path `
    $launcher `
    $gamePath


$game = (Resolve-Path $game).Path


$versionsPath = "$game\versions"


$availableVersions = Get-ChildItem $versionsPath -Directory | ForEach-Object {

    $jsonFile = "$($_.FullName)\$($_.Name).json"

    if (Test-Path $jsonFile) {

        $json = Get-Content $jsonFile | ConvertFrom-Json

        [PSCustomObject]@{
            Name = $json.id
            MainClass = $json.mainClass
            Path = $_.FullName
        }
    }
}


$availableVersions