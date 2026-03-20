$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$internalSuperRoot = Join-Path $scriptRoot "superWebRTCStream"
$externalSuperRoot = Join-Path $scriptRoot "..\superWebRTCStream"
$superRoot = $null

if (Test-Path $internalSuperRoot) {
    $superRoot = Resolve-Path $internalSuperRoot
} elseif (Test-Path $externalSuperRoot) {
    $superRoot = Resolve-Path $externalSuperRoot
} else {
    throw "Nao encontrei a pasta do superWebRTCStream nem dentro nem ao lado do rokuweb."
}

$superBuildScript = Join-Path $superRoot "Abrir-App.ps1"
$rokuBuildScript = Join-Path $scriptRoot "Abrir-App.ps1"

if (-not (Test-Path $superBuildScript)) {
    throw "Script do superWebRTCStream nao encontrado em '$superBuildScript'."
}

if (-not (Test-Path $rokuBuildScript)) {
    throw "Script do rokuweb nao encontrado em '$rokuBuildScript'."
}

Write-Host "Compilando superWebRTCStream..."
& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $superBuildScript

if ($LASTEXITCODE -ne 0) {
    throw "Falha ao compilar o superWebRTCStream."
}

Write-Host "Empacotando rokuweb..."
& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $rokuBuildScript

if ($LASTEXITCODE -ne 0) {
    throw "Falha ao empacotar o rokuweb."
}

Write-Host "Os dois projetos foram processados com sucesso."
