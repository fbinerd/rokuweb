param(
    [Parameter(Mandatory=$true)]
    [string]$YoutubeUrl
)

$ytDlpPath = Join-Path $PSScriptRoot '..\..\tools\yt-dlp\yt-dlp.exe'

if (!(Test-Path $ytDlpPath)) {
    Write-Error "yt-dlp.exe não encontrado em $ytDlpPath. Rode o script 'baixar-yt-dlp.ps1' primeiro."
    exit 1
}

# Obtém o link m3u8 do vídeo do YouTube
$link = & $ytDlpPath -g -f "best[ext=m3u8]" $YoutubeUrl

if ($LASTEXITCODE -ne 0 -or !$link) {
    Write-Error "Não foi possível obter o link m3u8."
    exit 2
}

Write-Host "Link m3u8 encontrado: $link"
$link
