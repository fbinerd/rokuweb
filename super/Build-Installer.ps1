param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseRoot,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseId,
    [string]$Channel = "develop"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path $ReleaseRoot)) {
    throw "Diretorio de release nao encontrado: $ReleaseRoot"
}

$exePath = Join-Path $ReleaseRoot "SuperPainel.exe"
if (-not (Test-Path $exePath)) {
    throw "Executavel nao encontrado em $ReleaseRoot"
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("superpainel-installer-" + [guid]::NewGuid().ToString("N"))
$bundleRoot = Join-Path $stageRoot "SuperPainel"
$payloadRoot = Join-Path $bundleRoot "app"

function New-ShortcutFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$TargetPath,
        [string]$Arguments = "",
        [string]$WorkingDirectory = "",
        [string]$IconLocation = ""
    )

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $TargetPath
    if (-not [string]::IsNullOrWhiteSpace($Arguments)) {
        $shortcut.Arguments = $Arguments
    }
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $shortcut.WorkingDirectory = $WorkingDirectory
    }
    if (-not [string]::IsNullOrWhiteSpace($IconLocation)) {
        $shortcut.IconLocation = $IconLocation
    }
    $shortcut.Save()
}

function Get-InstallerScriptContent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [string]$ReleaseId,
        [Parameter(Mandatory = $true)]
        [string]$Channel
    )

    $templatePath = Join-Path $PSScriptRoot "installer\Install-SuperPainel.template.ps1"
    if (-not (Test-Path $templatePath)) {
        throw "Template do instalador nao encontrado: $templatePath"
    }

    $template = Get-Content -Path $templatePath -Raw
    return $template.Replace('__VERSION__', $Version).Replace('__CHANNEL__', $Channel).Replace('__RELEASE_ID__', $ReleaseId)
}

try {
    if (Test-Path $stageRoot) {
        Remove-Item $stageRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $payloadRoot | Out-Null
    Get-ChildItem -Path $ReleaseRoot -Recurse -File | Where-Object {
        $_.Name -notin @("cef.log", "startup.log")
    } | ForEach-Object {
        $relativePath = $_.FullName.Substring($ReleaseRoot.Length).TrimStart('\')
        $destinationPath = Join-Path $payloadRoot $relativePath
        $destinationDir = Split-Path -Parent $destinationPath
        if (-not (Test-Path $destinationDir)) {
            New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null
        }

        Copy-Item -Path $_.FullName -Destination $destinationPath -Force
    }

    $installPs1Path = Join-Path $bundleRoot "Install-SuperPainel.ps1"
    $installCmdPath = Join-Path $bundleRoot "Install-SuperPainel.cmd"

    Set-Content -Path $installPs1Path -Value (Get-InstallerScriptContent -Version $Version -ReleaseId $ReleaseId -Channel $Channel) -Encoding UTF8
    Set-Content -Path $installCmdPath -Value "@echo off`r`nPowerShell -NoLogo -NoProfile -ExecutionPolicy Bypass -File ""%~dp0Install-SuperPainel.ps1"" -Launch" -Encoding ASCII

    if (Test-Path $OutputPath) {
        Remove-Item $OutputPath -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory($bundleRoot, $OutputPath)
    Write-Host "Instalador criado em: $OutputPath"
}
finally {
    if (Test-Path $stageRoot) {
        Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
