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
$superDataRoot = Join-Path $env:LOCALAPPDATA "WindowManagerBroadcast"
$desktopShortcutPath = Join-Path $env:USERPROFILE "Desktop\SuperPainel Local.lnk"

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
        [string]$ExePath,
        [Parameter(Mandatory = $true)]
        [string]$ShortcutPath
    )

    if (-not (Test-Path $ExePath)) {
        throw "Executavel nao encontrado para criar atalho: $ExePath"
    }

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $ExePath
        $shortcut.WorkingDirectory = Split-Path -Parent $ExePath
        $shortcut.IconLocation = "$ExePath,0"
        $shortcut.Description = "SuperPainel local do repositorio rokuweb"
        $shortcut.Save()
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    }

    Write-Host ("Atalho atualizado em: {0}" -f $ShortcutPath)
}

if ($ResetLocalData) {
    Invoke-Step -Label "Resetar base local do SuperPainel" -Action {
        Reset-SuperLocalData -DataRoot $superDataRoot
    }
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
    Update-SuperDesktopShortcut -ExePath $superExePath -ShortcutPath $desktopShortcutPath
}

if ($LaunchSuper) {
    Invoke-Step -Label "Abrir SuperPainel local" -Action {
        if (-not (Test-Path $superExePath)) {
            throw "Executavel nao encontrado: $superExePath"
        }

        Start-Process -FilePath $superExePath -WorkingDirectory (Split-Path -Parent $superExePath)
    }
}

if (-not $LaunchSuper) {
    Remove-Item Env:SUPERPAINEL_SYNTH_AUDIO -ErrorAction SilentlyContinue
}

if (-not $SkipSideload) {
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
        Invoke-RokuSideload -RokuHost $RokuIp.Trim() -Username $RokuUser -Password $RokuPassword -PackagePath $localRokuZip -LaunchChannel:$LaunchRokuApp
    }
}

Write-Host ""
Write-Host "Fluxo local concluido."
Write-Host "Super: $superExePath"
Write-Host "Roku zip: $localRokuZip"
