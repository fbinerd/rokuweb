$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptRoot "build.ps1") -Restore -Build

if ($LASTEXITCODE -ne 0) {
    throw "Falha ao compilar o super."
}
