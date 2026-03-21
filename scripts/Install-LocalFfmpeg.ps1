[CmdletBinding()]
param(
    [string]$DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
    [string]$DestinationRoot = (Join-Path $PSScriptRoot "..\\tools\\ffmpeg")
)

$ErrorActionPreference = "Stop"
$resolvedDestination = [System.IO.Path]::GetFullPath($DestinationRoot)
$downloadRoot = Join-Path $resolvedDestination "_download"
$archivePath = Join-Path $downloadRoot "ffmpeg-release-essentials.zip"
$extractRoot = Join-Path $downloadRoot "extract"

function Clear-Directory {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return
    }

    Get-ChildItem -Path $Path -Force | Remove-Item -Recurse -Force
}

Write-Host "==> Preparar pasta local do ffmpeg"
New-Item -ItemType Directory -Path $resolvedDestination -Force | Out-Null
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
Clear-Directory -Path $extractRoot
New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null

Write-Host "==> Baixar ffmpeg"
Invoke-WebRequest -Uri $DownloadUrl -OutFile $archivePath

Write-Host "==> Extrair ffmpeg"
Expand-Archive -Path $archivePath -DestinationPath $extractRoot -Force

$ffmpegExe = Get-ChildItem -Path $extractRoot -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
$ffprobeExe = Get-ChildItem -Path $extractRoot -Recurse -Filter "ffprobe.exe" | Select-Object -First 1

if ($null -eq $ffmpegExe) {
    throw "Nao foi encontrado ffmpeg.exe no pacote baixado."
}

if ($null -eq $ffprobeExe) {
    throw "Nao foi encontrado ffprobe.exe no pacote baixado."
}

Write-Host "==> Instalar ffmpeg no projeto"
Get-ChildItem -Path $resolvedDestination -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne "_download" } |
    Remove-Item -Recurse -Force

Copy-Item -Path $ffmpegExe.FullName -Destination (Join-Path $resolvedDestination "ffmpeg.exe") -Force
Copy-Item -Path $ffprobeExe.FullName -Destination (Join-Path $resolvedDestination "ffprobe.exe") -Force

Write-Host "ffmpeg local instalado em: $resolvedDestination"
