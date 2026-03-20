$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
try {
    $branchName = (& git -C $scriptRoot rev-parse --abbrev-ref HEAD 2>$null).Trim()
}
catch {
    $branchName = "stable"
}

if ($branchName -ne "develop") {
    $branchName = "stable"
}

$packagePath = Join-Path $scriptRoot "$branchName-roku.zip"
if (-not (Test-Path $packagePath)) {
    $packagePath = Join-Path $scriptRoot "stable-roku.zip"
}
if (-not (Test-Path $packagePath)) {
    $packagePath = Join-Path $scriptRoot "develop-roku.zip"
}
if (-not (Test-Path $packagePath)) {
    $packagePath = Join-Path $scriptRoot "hello-roku.zip"
}

function Build-Package {
    if (Test-Path $packagePath) {
        Remove-Item $packagePath -Force
    }

    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptRoot "package.ps1")

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $packagePath)) {
        throw "Falha ao gerar o pacote Roku."
    }
}

Build-Package
Write-Host "Pacote criado em: $packagePath"
