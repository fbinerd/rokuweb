# Baixar yt-dlp.exe
Invoke-WebRequest -Uri "https://github.com/yt-dlp/yt-dlp/releases/download/2026.03.17/yt-dlp.exe" -OutFile "$PSScriptRoot\..\..\tools\yt-dlp\yt-dlp.exe"
Write-Host "yt-dlp.exe baixado em $PSScriptRoot\..\..\tools\yt-dlp\yt-dlp.exe"