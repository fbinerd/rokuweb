param(
    [string]$RokuIp = "",
    [string]$RokuUser = "rokudev",
    [string]$RokuPassword = "1234",
    [switch]$LaunchSuper,
    [switch]$LaunchRokuApp,
    [switch]$ResetLocalData,
    [switch]$SkipSideload,
    [switch]$UseSyntheticPanelAudio
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

Add-Type -AssemblyName System.Net.Http

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$superRoot = Join-Path $repoRoot "super"
$superExePath = Join-Path $superRoot "src\WindowManager.App\bin\Release\net481\SuperPainel.exe"
$localRokuZip = Join-Path $repoRoot "local-roku.zip"
$sideloadLogRoot = Join-Path $repoRoot "tmp\sideload"
$diagnosticsRoot = Join-Path $repoRoot "tmp\diagnostics"
$superDataRoot = Join-Path $env:LOCALAPPDATA "WindowManagerBroadcast"
$superLogPath = Join-Path $superDataRoot "logs\super.log"
$desktopShortcutPath = Join-Path $env:USERPROFILE "Desktop\SuperPainel Local.lnk"
$launchSuperBeforeSideload = [bool]$LaunchSuper
$superReadyForSideload = $false
$activeDiagnosticsSession = ""

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    Write-Host "==> $Label"
    & $Action
}

function New-MultipartBody {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,
        [Parameter(Mandatory = $true)]
        [string]$Password
    )

    $boundary = "---------------------------" + [DateTime]::UtcNow.Ticks.ToString()
    $newLine = "`r`n"
    $headerBuilder = New-Object System.Text.StringBuilder
    [void]$headerBuilder.Append("--").Append($boundary).Append($newLine)
    [void]$headerBuilder.Append('Content-Disposition: form-data; name="mysubmit"').Append($newLine).Append($newLine)
    [void]$headerBuilder.Append("Install").Append($newLine)
    [void]$headerBuilder.Append("--").Append($boundary).Append($newLine)
    [void]$headerBuilder.Append('Content-Disposition: form-data; name="passwd"').Append($newLine).Append($newLine)
    [void]$headerBuilder.Append($Password).Append($newLine)
    [void]$headerBuilder.Append("--").Append($boundary).Append($newLine)
    [void]$headerBuilder.Append('Content-Disposition: form-data; name="archive"; filename="').Append([System.IO.Path]::GetFileName($PackagePath)).Append('"').Append($newLine)
    [void]$headerBuilder.Append("Content-Type: application/octet-stream").Append($newLine).Append($newLine)

    $headerBytes = [System.Text.Encoding]::UTF8.GetBytes($headerBuilder.ToString())
    $fileBytes = [System.IO.File]::ReadAllBytes($PackagePath)
    $footerBytes = [System.Text.Encoding]::UTF8.GetBytes($newLine + "--" + $boundary + "--" + $newLine)

    $buffer = New-Object byte[] ($headerBytes.Length + $fileBytes.Length + $footerBytes.Length)
    [System.Buffer]::BlockCopy($headerBytes, 0, $buffer, 0, $headerBytes.Length)
    [System.Buffer]::BlockCopy($fileBytes, 0, $buffer, $headerBytes.Length, $fileBytes.Length)
    [System.Buffer]::BlockCopy($footerBytes, 0, $buffer, $headerBytes.Length + $fileBytes.Length, $footerBytes.Length)

    return @{
        Boundary = $boundary
        Body = $buffer
    }
}

function Invoke-RokuSideload {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RokuHost,
        [Parameter(Mandatory = $true)]
        [string]$Username,
        [Parameter(Mandatory = $true)]
        [string]$Password,
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,
        [Parameter(Mandatory = $false)]
        [bool]$LaunchChannel = $false
    )

    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.Credentials = New-Object System.Net.NetworkCredential($Username, $Password)
    $handler.PreAuthenticate = $true

    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(12)
    try {
        New-Item -ItemType Directory -Force -Path $sideloadLogRoot | Out-Null
        $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"

        $basic = [Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes("$Username`:$Password"))
        $client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Basic", $basic)

        $payload = New-MultipartBody -PackagePath $PackagePath -Password $Password
        $content = [System.Net.Http.ByteArrayContent]::new($payload.Body)
        $content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("multipart/form-data; boundary=" + $payload.Boundary)

        $installUri = "http://$RokuHost/plugin_install"
        $installResponse = $client.PostAsync($installUri, $content).GetAwaiter().GetResult()
        $installBody = $installResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $installDumpPath = Join-Path $sideloadLogRoot ("plugin_install-{0}-{1}.html" -f ($RokuHost -replace '[^0-9A-Za-z.-]', '_'), $timestamp)
        Set-Content -Path $installDumpPath -Value $installBody -Encoding UTF8
        Write-Host ("plugin_install => status={0}" -f [int]$installResponse.StatusCode)
        if (-not [string]::IsNullOrWhiteSpace($installBody)) {
            $compactInstallBody = ($installBody -replace '\s+', ' ').Trim()
            if ($compactInstallBody.Length -gt 220) {
                $compactInstallBody = $compactInstallBody.Substring(0, 220) + "..."
            }
            Write-Host ("plugin_install body => {0}" -f $compactInstallBody)
        }
        Write-Host ("plugin_install dump => {0}" -f $installDumpPath)
        if (-not $installResponse.IsSuccessStatusCode) {
            throw "Falha no plugin_install: $([int]$installResponse.StatusCode) $installBody"
        }

        if ($LaunchChannel) {
            $devAlreadyActive = $false
            for ($attempt = 0; $attempt -lt 8; $attempt++) {
                Start-Sleep -Milliseconds 900
                try {
                    $activeAppResponse = $client.GetAsync("http://${RokuHost}:8060/query/active-app").GetAwaiter().GetResult()
                    $activeAppBody = $activeAppResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    if ($activeAppResponse.IsSuccessStatusCode -and $activeAppBody -match 'id=\"dev\"') {
                        $devAlreadyActive = $true
                        Write-Host ("active-app => canal dev ja ativo apos plugin_install (tentativa {0})" -f ($attempt + 1))
                        break
                    }
                }
                catch {
                }
            }

            if (-not $devAlreadyActive) {
                Start-Sleep -Milliseconds 1200
                try {
                    $homeResponse = $client.PostAsync("http://${RokuHost}:8060/keypress/Home", [System.Net.Http.StringContent]::new("")).GetAwaiter().GetResult()
                    Write-Host ("keypress/Home => status={0}" -f [int]$homeResponse.StatusCode)
                }
                catch {
                    Write-Host "keypress/Home => falhou"
                }

                Start-Sleep -Milliseconds 1200
                $launchResponse = $client.PostAsync("http://${RokuHost}:8060/launch/dev", [System.Net.Http.StringContent]::new("")).GetAwaiter().GetResult()
                $launchBody = $launchResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                $launchDumpPath = Join-Path $sideloadLogRoot ("launch_dev-{0}-{1}.txt" -f ($RokuHost -replace '[^0-9A-Za-z.-]', '_'), $timestamp)
                Set-Content -Path $launchDumpPath -Value $launchBody -Encoding UTF8
                Write-Host ("launch/dev => status={0}" -f [int]$launchResponse.StatusCode)
                Write-Host ("launch/dev dump => {0}" -f $launchDumpPath)
            }
            else {
                Write-Host "launch/dev => ignorado porque o canal dev ja ficou ativo apos plugin_install"
            }
        }
        else {
            Write-Host "launch/dev => ignorado (use -LaunchRokuApp para relancar o canal na TV)"
        }
    }
    catch {
        $message = $_.Exception.Message
        $inner = $_.Exception.InnerException
        while ($inner -ne $null) {
            $message = $message + " | " + $inner.Message
            $inner = $inner.InnerException
        }

        throw "Falha ao comunicar com a Roku em $RokuHost. Detalhes: $message"
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

function Test-TcpEndpoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetHost,
        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    $tcp = New-Object System.Net.Sockets.TcpClient
    try {
        $connectTask = $tcp.ConnectAsync($TargetHost, $Port)
        if (-not $connectTask.Wait(3000)) {
            return $false
        }

        return $tcp.Connected
    }
    catch {
        return $false
    }
    finally {
        $tcp.Dispose()
    }
}

function Reset-SuperLocalData {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DataRoot
    )

    if (-not (Test-Path $DataRoot)) {
        Write-Host "Base local inexistente; nada para resetar."
        return
    }

    Get-Process -Name "SuperPainel" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    Get-ChildItem -Path $DataRoot -Force -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.PSIsContainer -and $_.Name -ieq "cef") {
            return
        }

        Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host ("Base local resetada em: {0}" -f $DataRoot)
}

function Update-SuperDesktopShortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetPath,
        [Parameter(Mandatory = $true)]
        [string]$ShortcutPath,
        [Parameter(Mandatory = $false)]
        [string]$Arguments = "",
        [Parameter(Mandatory = $false)]
        [string]$WorkingDirectory = ""
    )

    if (-not (Test-Path $TargetPath)) {
        throw "Destino nao encontrado para criar atalho: $TargetPath"
    }

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $TargetPath
        $shortcut.Arguments = $Arguments
        $shortcut.WorkingDirectory = $(if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) { Split-Path -Parent $TargetPath } else { $WorkingDirectory })
        $shortcut.IconLocation = "$superExePath,0"
        $shortcut.Description = "SuperPainel local com watchdog automatico"
        $shortcut.Save()
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    }

    Write-Host ("Atalho atualizado em: {0}" -f $ShortcutPath)
}

function Send-RokuHome {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RokuHost
    )

    try {
        Invoke-WebRequest -Uri "http://${RokuHost}:8060/keypress/Home" -Method Post -UseBasicParsing -TimeoutSec 5 | Out-Null
        Write-Host "keypress/Home => enviado antes do sideload"
    }
    catch {
        Write-Host "keypress/Home => falhou antes do sideload"
    }

    Start-Sleep -Milliseconds 1200
}

function Stop-ExistingSuperLaunchers {
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.Name -match 'powershell|wscript') -and
            (
                ($_.CommandLine -match 'Run-SuperPainelWatchdog') -or
                ($_.CommandLine -match 'Run-SuperPainelWatchdogHidden')
            )
        } |
        ForEach-Object {
            try {
                Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            }
            catch {
            }
        }
}

function Stop-ExistingLocalLogMonitors {
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -match 'powershell' -and
            $_.CommandLine -match 'Dev-LocalLogMonitor\.ps1'
        } |
        ForEach-Object {
            try {
                Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            }
            catch {
            }
        }
}

function Start-LocalDiagnosticsMonitor {
    param(
        [string]$RokuHost
    )

    Stop-ExistingLocalLogMonitors
    New-Item -ItemType Directory -Force -Path $diagnosticsRoot | Out-Null

    $sessionName = Get-Date -Format "yyyyMMdd-HHmmss"
    $sessionRoot = Join-Path $diagnosticsRoot $sessionName
    New-Item -ItemType Directory -Force -Path $sessionRoot | Out-Null

    $monitorScriptPath = Join-Path $repoRoot "scripts\Dev-LocalLogMonitor.ps1"
    if (-not (Test-Path $monitorScriptPath)) {
        throw "Script de monitor nao encontrado: $monitorScriptPath"
    }

    $argumentList = @(
        "-NoLogo",
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", ('"{0}"' -f $monitorScriptPath),
        "-SessionRoot", ('"{0}"' -f $sessionRoot),
        "-SuperLogPath", ('"{0}"' -f $superLogPath),
        "-DurationMinutes", "30"
    )

    if (-not [string]::IsNullOrWhiteSpace($RokuHost)) {
        $argumentList += @("-RokuIp", ('"{0}"' -f $RokuHost))
    }

    Start-Process -FilePath "powershell.exe" -ArgumentList $argumentList -WorkingDirectory $repoRoot -WindowStyle Hidden | Out-Null
    return $sessionRoot
}

function Start-SuperLocal {
    Stop-ExistingSuperLaunchers
    Get-Process -Name "SuperPainel" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600

    if (-not (Test-Path $superExePath)) {
        throw "Executavel do SuperPainel nao encontrado: $superExePath"
    }

    $process = Start-Process -FilePath $superExePath -WorkingDirectory (Split-Path -Parent $superExePath) -WindowStyle Normal -PassThru
    Start-Sleep -Milliseconds 1200

    try {
        $process.Refresh()
        if ($process.HasExited) {
            throw "O SuperPainel encerrou logo apos a abertura. ExitCode=$($process.ExitCode)"
        }
    }
    catch {
        throw
    }

    return $process
}

function Wait-SuperWindowReady {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,
        [int]$TimeoutSeconds = 20
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $Process.Refresh()
            if ($Process.HasExited) {
                throw "O SuperPainel encerrou antes de criar a janela principal. ExitCode=$($Process.ExitCode)"
            }

            if ($Process.MainWindowHandle -ne 0) {
                return $true
            }
        }
        catch {
            throw
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Wait-SuperBridgeReady {
    param(
        [string]$BaseUrl = "http://127.0.0.1:8090",
        [int]$TimeoutSeconds = 25
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri ($BaseUrl.TrimEnd('/') + "/api/windows") -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                return $true
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 700
    }

    return $false
}

if ($ResetLocalData) {
    Invoke-Step -Label "Resetar base local do SuperPainel" -Action {
        Reset-SuperLocalData -DataRoot $superDataRoot
    }
}

Invoke-Step -Label "Iniciar monitor de diagnostico local" -Action {
    $script:activeDiagnosticsSession = Start-LocalDiagnosticsMonitor -RokuHost $RokuIp.Trim()
    Write-Host ("Diagnostico: {0}" -f $script:activeDiagnosticsSession)
    Write-Host ("Super bruto: {0}" -f (Join-Path $script:activeDiagnosticsSession "super-raw.log"))
    Write-Host ("Telnet bruto: {0}" -f (Join-Path $script:activeDiagnosticsSession "telnet-raw.log"))
    Write-Host ("Foco filtrado: {0}" -f (Join-Path $script:activeDiagnosticsSession "focus.log"))
}

Invoke-Step -Label "Compilar super em modo local" -Action {
    $env:SUPER_BUILD_CHANNEL = "local"
    if ($UseSyntheticPanelAudio) {
        $env:SUPERPAINEL_SYNTH_AUDIO = "1"
    }
    try {
        Get-Process -Name "SuperPainel" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
        Push-Location $superRoot
        & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ".\build.ps1" -Restore -Build
        if ($LASTEXITCODE -ne 0) {
            throw "Falha ao compilar o super."
        }
    }
    finally {
        Pop-Location
        Remove-Item Env:SUPER_BUILD_CHANNEL -ErrorAction SilentlyContinue
        if (-not $LaunchSuper) {
            Remove-Item Env:SUPERPAINEL_SYNTH_AUDIO -ErrorAction SilentlyContinue
        }
    }
}

Invoke-Step -Label "Empacotar canal Roku local" -Action {
    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot "package.ps1") -Channel "local" -Output "local-roku.zip"
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao empacotar local-roku.zip."
    }
}

Invoke-Step -Label "Atualizar atalho local do SuperPainel" -Action {
    Update-SuperDesktopShortcut `
        -TargetPath $superExePath `
        -ShortcutPath $desktopShortcutPath `
        -Arguments "" `
        -WorkingDirectory (Split-Path -Parent $superExePath)
}

if ($launchSuperBeforeSideload) {
    Invoke-Step -Label "Abrir SuperPainel local" -Action {
        $superProcess = Start-SuperLocal
        Write-Host ("SuperPainel iniciado => pid={0}" -f $superProcess.Id)
        if (-not (Wait-SuperWindowReady -Process $superProcess)) {
            throw "O SuperPainel nao criou a janela principal a tempo."
        }
        Write-Host "SuperPainel com janela principal ativa."
        if (-not (Wait-SuperBridgeReady)) {
            throw "O SuperPainel nao respondeu em /api/windows a tempo."
        }
        $script:superReadyForSideload = $true
        Write-Host "SuperPainel pronto para seguir com o sideload."
    }
}

if (-not $LaunchSuper) {
    Remove-Item Env:SUPERPAINEL_SYNTH_AUDIO -ErrorAction SilentlyContinue
}

if (-not $SkipSideload) {
    if ($LaunchSuper -and -not $superReadyForSideload) {
        throw "O sideload foi bloqueado porque o SuperPainel nao ficou pronto antes da etapa da Roku."
    }

    if ([string]::IsNullOrWhiteSpace($RokuIp)) {
        throw "Informe -RokuIp para fazer sideload local, ou use -SkipSideload."
    }

    Invoke-Step -Label "Verificar conectividade da Roku em $RokuIp" -Action {
        $rokuHost = $RokuIp.Trim()
        $devPortOk = Test-TcpEndpoint -TargetHost $rokuHost -Port 80
        $ecpPortOk = Test-TcpEndpoint -TargetHost $rokuHost -Port 8060

        Write-Host ("porta 80 => {0}" -f $(if ($devPortOk) { "ok" } else { "falhou" }))
        Write-Host ("porta 8060 => {0}" -f $(if ($ecpPortOk) { "ok" } else { "falhou" }))

        if (-not $devPortOk -and -not $ecpPortOk) {
            throw "A Roku em $rokuHost nao respondeu nas portas 80 e 8060. Verifique IP, modo developer e conectividade de rede."
        }
    }

    Invoke-Step -Label "Enviar sideload local para $RokuIp" -Action {
        if ($LaunchRokuApp) {
            Send-RokuHome -RokuHost $RokuIp.Trim()
        }
        Invoke-RokuSideload -RokuHost $RokuIp.Trim() -Username $RokuUser -Password $RokuPassword -PackagePath $localRokuZip -LaunchChannel:$LaunchRokuApp
    }
}

Write-Host ""
Write-Host "Fluxo local concluido."
Write-Host "Super: $superExePath"
Write-Host "Roku zip: $localRokuZip"
if (-not [string]::IsNullOrWhiteSpace($activeDiagnosticsSession)) {
    Write-Host "Diagnostico:"
    Write-Host ("  Pasta: {0}" -f $activeDiagnosticsSession)
    Write-Host ("  Super: {0}" -f (Join-Path $activeDiagnosticsSession "super-raw.log"))
    Write-Host ("  Telnet: {0}" -f (Join-Path $activeDiagnosticsSession "telnet-raw.log"))
    Write-Host ("  Foco: {0}" -f (Join-Path $activeDiagnosticsSession "focus.log"))
}
