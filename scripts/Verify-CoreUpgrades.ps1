param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$upgradeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $upgradeRoot
$previousPreview = $env:ISLAND_PREVIEW
$previousData = $env:ISLAND_PREVIEW_DATA
try {
    & $Dotnet test DynamicIsland.Windows.Tests -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
    & $Dotnet build DynamicIsland.Windows -c Release -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    $env:ISLAND_PREVIEW = '1'
    $env:ISLAND_PREVIEW_DATA = Join-Path $upgradeRoot ('artifacts/upgrade-verification/' + [Guid]::NewGuid().ToString('N'))
    $upgradeExe = Join-Path $upgradeRoot 'DynamicIsland.Windows/bin/Release/net10.0-windows10.0.19041.0/win-x64/DynamicIsland.Windows.exe'
    $verification = Start-Process -FilePath $upgradeExe -ArgumentList '--verify-upgrades' -WindowStyle Hidden -Wait -PassThru
    if ($verification.ExitCode -ne 0) {
        Get-Content -LiteralPath (Join-Path $env:ISLAND_PREVIEW_DATA 'verification-error.txt') -ErrorAction SilentlyContinue
        throw 'Native fixture verification failed.'
    }
    Get-Content -LiteralPath (Join-Path $env:ISLAND_PREVIEW_DATA 'captures/checks.txt')
    Write-Output "Native captures: $env:ISLAND_PREVIEW_DATA\captures"
}
finally {
    $env:ISLAND_PREVIEW = $previousPreview
    $env:ISLAND_PREVIEW_DATA = $previousData
    Pop-Location
}
