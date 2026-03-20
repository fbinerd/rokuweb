$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptRoot "Compilar-Tudo.ps1")

if ($LASTEXITCODE -ne 0) {
    throw "Falha ao compilar o rokuweb e o super."
}
