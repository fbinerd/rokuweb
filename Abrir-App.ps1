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

#region Update/Channel Selection Logic

function Select-UpdateChannel {
    $channels = @("stable", "develop", "local")
    Write-Host "Selecione o canal de atualização:"
    for ($i = 0; $i -lt $channels.Count; $i++) {
        Write-Host ("[{0}] {1}" -f ($i+1), $channels[$i])
    }
    $default = 1
    $input = Read-Host "Digite o número do canal desejado (default: $($channels[$default-1]))"
    if ([string]::IsNullOrWhiteSpace($input)) { return $channels[$default-1] }
    $idx = [int]$input - 1
    if ($idx -ge 0 -and $idx -lt $channels.Count) { return $channels[$idx] }
    return $channels[$default-1]
}

function Check-ForUpdate {
    param(
        [string]$Channel
    )
    $manifestUrl = "https://fbinerd.github.io/rokuweb/updates/$Channel/latest-super.json"
    Write-Host "Consultando atualizações em: $manifestUrl"
    try {
        $resp = Invoke-WebRequest -Uri $manifestUrl -UseBasicParsing -TimeoutSec 10
        $json = $resp.Content | ConvertFrom-Json
        return $json
    } catch {
        Write-Host "Falha ao consultar atualizações: $_"
        return $null
    }
}

$updateChannel = Select-UpdateChannel
$manifest = Check-ForUpdate -Channel $updateChannel
if ($manifest -eq $null) {
    Write-Host "Não foi possível obter informações de atualização. Prosseguindo com build normal."
    # ...existing code...
    Build-Package
    Write-Host "Pacote criado em: $packagePath"
    exit 0
}

$currentVersion = "1.0.35" # TODO: obter dinamicamente do manifest/local
$latestVersion = $manifest.currentVersion
if ($currentVersion -eq $latestVersion) {
    Write-Host "Aplicativo já está na versão mais recente ($currentVersion)."
    # ...existing code...
    Build-Package
    Write-Host "Pacote criado em: $packagePath"
    exit 0
}

Write-Host "Atualização disponível: $latestVersion (atual: $currentVersion)"
# Continuação: backup e update...
#endregion

#region Autobackup antes do update
function Backup-BeforeUpdate {
    param(
        [string]$ReleaseId
    )
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    $backupRoot = Join-Path $localAppData 'WindowManagerBroadcast\\backups'
    if (-not (Test-Path $backupRoot)) { New-Item -ItemType Directory -Path $backupRoot | Out-Null }
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupName = "pre-update-$timestamp-$ReleaseId.zip"
    $backupPath = Join-Path $backupRoot $backupName
    $dataRoot = Join-Path $localAppData 'WindowManagerBroadcast'
    if (Test-Path $dataRoot) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($dataRoot, $backupPath)
        Write-Host "Backup criado em: $backupPath"
        return $backupPath
    } else {
        Write-Host "Nenhum dado encontrado para backup."
        return $null
    }
}
#endregion

#region Restaurar backup após update
function Restore-Backup {
    param(
        [string]$BackupZipPath
    )
    if (-not (Test-Path $BackupZipPath)) {
        Write-Host "Backup não encontrado para restaurar: $BackupZipPath"
        return $false
    }
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    $dataRoot = Join-Path $localAppData 'WindowManagerBroadcast'
    if (Test-Path $dataRoot) {
        Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($BackupZipPath, $dataRoot)
    Write-Host "Backup restaurado em: $dataRoot"
    return $true
}
#endregion

#region Aplicar update (simples)
function Apply-Update {
    param(
        [object]$Manifest
    )
    $latestUrl = $Manifest.releases | Where-Object { $_.releaseId -eq $Manifest.currentRelease } | Select-Object -ExpandProperty fullPackageUrl
    if (-not $latestUrl) {
        Write-Host "URL do pacote de update não encontrada."
        return $false
    }
    $tempDir = Join-Path $env:TEMP ("super-update-" + [guid]::NewGuid().ToString())
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    $zipPath = Join-Path $tempDir "update.zip"
    Write-Host "Baixando update: $latestUrl"
    Invoke-WebRequest -Uri $latestUrl -OutFile $zipPath -UseBasicParsing
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    $dataRoot = Join-Path $localAppData 'WindowManagerBroadcast'
    if (Test-Path $dataRoot) {
        Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $dataRoot)
    Write-Host "Update aplicado em: $dataRoot"
    return $true
}
#endregion

if ($manifest -ne $null -and $currentVersion -ne $latestVersion) {
    $backupPath = Backup-BeforeUpdate -ReleaseId $latestVersion
    $updateOk = Apply-Update -Manifest $manifest
    if (-not $updateOk) {
        Write-Host "Update falhou. Restaurando backup..."
        Restore-Backup -BackupZipPath $backupPath
        throw "Update falhou e backup foi restaurado."
    }
    Write-Host "Update concluído com sucesso."
}

#region Integração final: manter build normal se não houver update
if ($manifest -eq $null -or $currentVersion -eq $latestVersion) {
    Build-Package
    Write-Host "Pacote criado em: $packagePath"
    exit 0
}
#endregion

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
