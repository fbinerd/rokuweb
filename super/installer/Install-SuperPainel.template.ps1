param(
    [string]$InstallRoot = [System.IO.Path]::Combine($env:LOCALAPPDATA, 'Programs', 'SuperPainel'),
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

function New-ShortcutFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$TargetPath,
        [string]$Arguments = '',
        [string]$WorkingDirectory = '',
        [string]$IconLocation = ''
    )

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $TargetPath
    if (-not [string]::IsNullOrWhiteSpace($Arguments)) { $shortcut.Arguments = $Arguments }
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) { $shortcut.WorkingDirectory = $WorkingDirectory }
    if (-not [string]::IsNullOrWhiteSpace($IconLocation)) { $shortcut.IconLocation = $IconLocation }
    $shortcut.Save()
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$payloadRoot = Join-Path $scriptRoot 'app'
$installRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$exePath = Join-Path $installRoot 'SuperPainel.exe'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'SuperPainel.lnk'
$programsFolder = [Environment]::GetFolderPath('Programs')
$startMenuFolder = Join-Path $programsFolder 'SuperPainel'
$startMenuShortcut = Join-Path $startMenuFolder 'SuperPainel.lnk'
$uninstallShortcut = Join-Path $startMenuFolder 'Desinstalar SuperPainel.lnk'
$uninstallScriptPath = Join-Path $installRoot 'Uninstall-SuperPainel.ps1'
$uninstallCmdPath = Join-Path $installRoot 'Uninstall-SuperPainel.cmd'
$uninstallRegPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SuperPainel'

if (-not (Test-Path $payloadRoot)) {
    throw 'Pasta app do instalador nao encontrada.'
}

$running = Get-Process -Name 'SuperPainel' -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
Copy-Item -Path (Join-Path $payloadRoot '*') -Destination $installRoot -Recurse -Force

$uninstallContent = @'
param()
$ErrorActionPreference = 'Stop'
$installRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'SuperPainel.lnk'
$programsFolder = [Environment]::GetFolderPath('Programs')
$startMenuFolder = Join-Path $programsFolder 'SuperPainel'
$uninstallRegPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SuperPainel'

$running = Get-Process -Name 'SuperPainel' -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

Remove-Item $desktopShortcut -Force -ErrorAction SilentlyContinue
Remove-Item $startMenuFolder -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $uninstallRegPath -Recurse -Force -ErrorAction SilentlyContinue

$cleanupScript = Join-Path $env:TEMP ('SuperPainel-Cleanup-' + [guid]::NewGuid().ToString('N') + '.cmd')
$cleanupContent = @"
@echo off
timeout /t 2 /nobreak >nul
rmdir /s /q ""$installRoot""
del ""%~f0""
"@
Set-Content -Path $cleanupScript -Value $cleanupContent -Encoding ASCII
Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $cleanupScript -WindowStyle Hidden
'@
Set-Content -Path $uninstallScriptPath -Value $uninstallContent -Encoding UTF8

$uninstallCmd = "@echo off`r`nPowerShell -NoLogo -NoProfile -ExecutionPolicy Bypass -File ""%~dp0Uninstall-SuperPainel.ps1"""
Set-Content -Path $uninstallCmdPath -Value $uninstallCmd -Encoding ASCII

New-Item -ItemType Directory -Force -Path $startMenuFolder | Out-Null
New-ShortcutFile -Path $desktopShortcut -TargetPath $exePath -WorkingDirectory $installRoot -IconLocation $exePath
New-ShortcutFile -Path $startMenuShortcut -TargetPath $exePath -WorkingDirectory $installRoot -IconLocation $exePath
New-ShortcutFile -Path $uninstallShortcut -TargetPath $uninstallCmdPath -WorkingDirectory $installRoot

New-Item -Path $uninstallRegPath -Force | Out-Null
Set-ItemProperty -Path $uninstallRegPath -Name 'DisplayName' -Value 'SuperPainel'
Set-ItemProperty -Path $uninstallRegPath -Name 'DisplayVersion' -Value '__VERSION__'
Set-ItemProperty -Path $uninstallRegPath -Name 'Publisher' -Value 'fbinerd'
Set-ItemProperty -Path $uninstallRegPath -Name 'InstallLocation' -Value $installRoot
Set-ItemProperty -Path $uninstallRegPath -Name 'DisplayIcon' -Value $exePath
Set-ItemProperty -Path $uninstallRegPath -Name 'UninstallString' -Value $uninstallCmdPath
Set-ItemProperty -Path $uninstallRegPath -Name 'QuietUninstallString' -Value $uninstallCmdPath
Set-ItemProperty -Path $uninstallRegPath -Name 'NoModify' -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallRegPath -Name 'NoRepair' -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallRegPath -Name 'InstallDate' -Value (Get-Date -Format 'yyyyMMdd')

$estimatedSizeKb = [int][Math]::Ceiling(((Get-ChildItem -Path $installRoot -Recurse -File | Measure-Object -Property Length -Sum).Sum) / 1KB)
Set-ItemProperty -Path $uninstallRegPath -Name 'EstimatedSize' -Value $estimatedSizeKb -Type DWord

Write-Host 'SuperPainel instalado em' $installRoot
Write-Host 'Canal:' '__CHANNEL__'
Write-Host 'Release:' '__RELEASE_ID__'

if ($Launch) {
    Start-Process -FilePath $exePath -WorkingDirectory $installRoot
}
