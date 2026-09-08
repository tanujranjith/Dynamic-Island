param()
$ErrorActionPreference = 'Stop'
$upgradeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$upgradeExe = Join-Path $upgradeRoot 'artifacts/core-upgrades/DynamicIsland.Windows.exe'
if (!(Test-Path -LiteralPath $upgradeExe)) {
    throw 'Build the preview first: dotnet publish DynamicIsland.Windows -c Release -o artifacts/core-upgrades'
}
$previewData = Join-Path $upgradeRoot 'artifacts/upgrade-preview-data'
New-Item -ItemType Directory -Path $previewData -Force | Out-Null
$previewSettings = Join-Path $previewData 'settings.json'
if (!(Test-Path -LiteralPath $previewSettings)) {
    @{ SchemaVersion = 11; TopOffset = 120; HasOnboarded = $false; QEnabled = $false; LaunchOnStartup = $false } |
        ConvertTo-Json | Set-Content -LiteralPath $previewSettings
}
$previousPreview = $env:ISLAND_PREVIEW
$previousData = $env:ISLAND_PREVIEW_DATA
try {
    $env:ISLAND_PREVIEW = '1'
    $env:ISLAND_PREVIEW_DATA = $previewData
    Start-Process -FilePath $upgradeExe -WorkingDirectory $upgradeRoot -WindowStyle Hidden | Out-Null
}
finally {
    $env:ISLAND_PREVIEW = $previousPreview
    $env:ISLAND_PREVIEW_DATA = $previousData
}
