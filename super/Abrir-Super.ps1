$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptRoot "Abrir-App.ps1")

if ($LASTEXITCODE -ne 0) {
    throw "Falha ao compilar e abrir o super."
}
