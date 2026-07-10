# get-openal.ps1
# Downloads OpenAL Soft native libraries and places them under
# src/P2000.UI/runtimes/<rid>/native/ so the build copies them to the output.
#
# Run once from the repo root:
#   powershell -ExecutionPolicy Bypass -File tools\get-openal.ps1
#
# Linux: ship libopenal.so.1 from your distro (apt install libopenal1)
#        or copy it to src/P2000.UI/runtimes/linux-x64/native/
# macOS: system OpenAL framework is used automatically; or copy
#        libopenal.dylib from 'brew install openal-soft' to
#        src/P2000.UI/runtimes/osx/native/

param([string]$Version = "1.23.1")

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path $PSScriptRoot -Parent
$NativeDir = Join-Path $RepoRoot "src\P2000.UI\runtimes"

# -- Windows x64 --
$winOut = Join-Path $NativeDir "win-x64\native\openal32.dll"
if (Test-Path $winOut) {
    Write-Host "win-x64: already present, skipping"
} else {
    Write-Host "win-x64: downloading OpenAL Soft $Version..."
    $zipUrl  = "https://github.com/kcat/openal-soft/releases/download/$Version/openal-soft-$Version-bin.zip"
    $zipPath = Join-Path $env:TEMP "openal-soft-win.zip"
    $extract = Join-Path $env:TEMP "openal-soft-win"
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
    Expand-Archive -Path $zipPath -DestinationPath $extract -Force
    $src = Get-ChildItem $extract -Recurse -Filter "soft_oal.dll" |
           Where-Object { $_.FullName -match "Win64" } |
           Select-Object -First 1
    if (-not $src) {
        $src = Get-ChildItem $extract -Recurse -Filter "soft_oal.dll" | Select-Object -First 1
    }
    New-Item -ItemType Directory -Force (Split-Path $winOut) | Out-Null
    Copy-Item $src.FullName $winOut
    Write-Host "win-x64: done -> $winOut"
    Remove-Item $zipPath, $extract -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Done. Run 'dotnet build src/P2000.UI' to verify the DLL is copied to the output."
