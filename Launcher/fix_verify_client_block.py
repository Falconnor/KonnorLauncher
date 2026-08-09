from pathlib import Path
path = Path('verify_client.ps1')
text = path.read_text('utf-8')
start = text.index('# Verificar y reparar archivos')
end = text.index('# Eliminar archivos no autorizados', start)
new_block = '''# Verificar y reparar archivos
$repairFiles = @()
foreach ($file in $manifest.files.PSObject.Properties) {
    $relativePath = $file.Name
    $expectedHash = $file.Value.sha256
    $downloadUrl = "$baseUrl/$($relativePath.Replace('\\','/'))"
    $localPath = Join-Path $game $relativePath
    $needsRepair = $false

    if (!(Test-Path $localPath)) {
        Write-Host "[FALTA] $relativePath"
        $needsRepair = $true
    } else {
        $currentHash = (Get-FileHash $localPath -Algorithm SHA256).Hash
        if ($currentHash -ne $expectedHash) {
            Write-Host "[CORRUPTO] $relativePath"
            $needsRepair = $true
        }
    }

    if ($needsRepair) {
        $repairFiles += [pscustomobject]@{
            RelativePath = $relativePath
            ExpectedHash = $expectedHash
            DownloadUrl = $downloadUrl
            LocalPath = $localPath
        }
    }
}

$totalRepairs = $repairFiles.Count
$repairIndex = 0

foreach ($fileInfo in $repairFiles) {
    $repairIndex++
    $percentage = 0
    if ($totalRepairs -gt 0) {
        $percentage = [math]::Round(($repairIndex / $totalRepairs) * 100)
    }
    Write-Host "Descargando archivos $percentage%"
    Write-Host "Descargando $($fileInfo.RelativePath)"

    $tempFile = Join-Path $tempFolder ([System.IO.Path]::GetFileName($fileInfo.RelativePath))

    Invoke-WebRequest `
        -Uri $fileInfo.DownloadUrl `
        -OutFile $tempFile

    $downloadHash = (Get-FileHash $tempFile -Algorithm SHA256).Hash

    if ($downloadHash -eq $fileInfo.ExpectedHash) {
        $directory = Split-Path $fileInfo.LocalPath
        if (!(Test-Path $directory)) {
            New-Item $directory -ItemType Directory -Force | Out-Null
        }
        Move-Item $tempFile $fileInfo.LocalPath -Force
        Write-Host "[REPARADO] $($fileInfo.RelativePath)"
    } else {
        Write-Host "[ERROR HASH] Descarga inválida: $($fileInfo.RelativePath)"
        Remove-Item $tempFile -Force
    }
}
'''
path.write_text(text[:start] + new_block + text[end:], 'utf-8')
print('done')
