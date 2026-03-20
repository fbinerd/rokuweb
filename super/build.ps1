param(
    [switch]$Restore,
    [switch]$Build
)

$ErrorActionPreference = "Stop"

$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_HOME = (Resolve-Path ".").Path
$env:NUGET_PACKAGES = Join-Path $env:DOTNET_CLI_HOME ".nuget\packages"
$script:NuGetConfigPath = Join-Path $env:DOTNET_CLI_HOME "NuGet.Config"
$script:AppProjectPath = Join-Path $env:DOTNET_CLI_HOME "src\WindowManager.App\WindowManager.App.csproj"

function Get-SolutionPath {
    $slnPath = Join-Path $env:DOTNET_CLI_HOME "WindowManager.sln"
    $slnxPath = Join-Path $env:DOTNET_CLI_HOME "WindowManager.slnx"

    if (Test-Path $slnPath) {
        return $slnPath
    }

    if (Test-Path $slnxPath) {
        return $slnxPath
    }

    return $slnPath
}

function Invoke-DotNet {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    & dotnet @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao executar: dotnet $($Arguments -join ' ')"
    }
}

function Assert-DotNetSdk {
    $sdkList = & dotnet --list-sdks 2>$null
    if (-not $sdkList) {
        throw "Nenhum .NET SDK foi encontrado. Instale o .NET 8 SDK para compilar este projeto."
    }
}

function Ensure-Solution {
    $solutionPath = Get-SolutionPath

    if (-not (Test-Path $solutionPath)) {
        $null = Invoke-DotNet new sln --name WindowManager --format sln --output .
    }

    $solutionPath = Get-SolutionPath
    if (-not (Test-Path $solutionPath)) {
        throw "Nao foi possivel criar ou localizar o arquivo de solucao em '$solutionPath'."
    }

    return $solutionPath
}

function Ensure-ProjectInSolution {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    $solutionPath = Get-SolutionPath
    $solutionContent = ""
    if (Test-Path $solutionPath) {
        $solutionContent = Get-Content $solutionPath -Raw
    }

    if ($solutionContent -notmatch [regex]::Escape($ProjectPath)) {
        Invoke-DotNet sln $solutionPath add $ProjectPath
    }
}

Assert-DotNetSdk
$solutionPath = Ensure-Solution
Ensure-ProjectInSolution -ProjectPath "src\WindowManager.Core\WindowManager.Core.csproj"
Ensure-ProjectInSolution -ProjectPath "src\WindowManager.App\WindowManager.App.csproj"

if ($Restore) {
    Invoke-DotNet restore $script:AppProjectPath --configfile $script:NuGetConfigPath
}

if ($Build) {
    Invoke-DotNet build $script:AppProjectPath -c Release --no-restore --configfile $script:NuGetConfigPath
}

Write-Host "Bootstrap concluido."
Write-Host "Use:"
Write-Host "  .\build.ps1 -Restore -Build"
