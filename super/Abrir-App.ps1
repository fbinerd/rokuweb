$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionPath = Join-Path $scriptRoot "WindowManager.sln"
$projectPath = Join-Path $scriptRoot "src\WindowManager.App\WindowManager.App.csproj"
$launcherProjectPath = Join-Path $scriptRoot "src\SuperLauncher\SuperLauncher.csproj"
$releaseRoot = Join-Path $scriptRoot "src\WindowManager.App\bin\Release"

$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_HOME = $scriptRoot
$env:NUGET_PACKAGES = Join-Path $scriptRoot ".nuget\packages"

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

function Remove-BuildArtifacts {
    Get-ChildItem -Path (Join-Path $scriptRoot "src") -Directory -Recurse |
        Where-Object { $_.Name -in @("bin", "obj") } |
        ForEach-Object {
            Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
}

function Stop-RunningAppInstances {
    $processes = Get-Process -Name "SuperPainel" -ErrorAction SilentlyContinue
    if (-not $processes) {
        return
    }

    Write-Host "Encerrando instancias abertas de SuperPainel..."
    $processes | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

Stop-RunningAppInstances
Remove-BuildArtifacts
Invoke-DotNet restore $solutionPath --configfile (Join-Path $scriptRoot "NuGet.Config")
Invoke-DotNet build $projectPath -c Release --no-restore
Invoke-DotNet build $launcherProjectPath -c Release --no-restore

$exeFile = Get-ChildItem -Path $releaseRoot -Recurse -Filter "SuperLauncher.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $exeFile) {
    throw "Launcher nao encontrado em '$releaseRoot'."
}

Start-Process -FilePath $exeFile.FullName -WorkingDirectory $exeFile.DirectoryName
Write-Host "Launcher compilado e aberto."
exit 0
