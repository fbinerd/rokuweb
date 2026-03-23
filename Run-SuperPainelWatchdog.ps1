param(
    [string]$ExePath = "",
    [int]$RestartDelaySeconds = 2,
    [int]$MaxRestartCount = 50
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $PSScriptRoot "super\src\WindowManager.App\bin\Release\net481\SuperPainel.exe"
}

if (-not (Test-Path $ExePath)) {
    throw "Executavel nao encontrado: $ExePath"
}

$watchdogRoot = Join-Path $env:LOCALAPPDATA "WindowManagerBroadcast\watchdog"
New-Item -ItemType Directory -Force -Path $watchdogRoot | Out-Null

$restartCount = 0

while ($true) {
    $token = [Guid]::NewGuid().ToString("N")
    $exitMarkerPath = Join-Path $watchdogRoot ("exit-{0}.ok" -f $token)
    if (Test-Path $exitMarkerPath) {
        Remove-Item $exitMarkerPath -Force -ErrorAction SilentlyContinue
    }

    $arguments = @("--watchdog-token", $token)
    $process = Start-Process -FilePath $ExePath -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $ExePath) -PassThru
    Wait-Process -Id $process.Id
    $process.Refresh()

    $gracefulExit = Test-Path $exitMarkerPath
    if ($gracefulExit) {
        Remove-Item $exitMarkerPath -Force -ErrorAction SilentlyContinue
        break
    }

    $restartCount++
    if ($restartCount -ge $MaxRestartCount) {
        throw "O SuperPainel excedeu o limite de reinicios automaticos."
    }

    Start-Sleep -Seconds $RestartDelaySeconds
}
