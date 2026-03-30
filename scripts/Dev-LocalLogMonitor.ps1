param(
    [Parameter(Mandatory = $true)]
    [string]$SessionRoot,
    [Parameter(Mandatory = $true)]
    [string]$SuperLogPath,
    [string]$RokuIp = "",
    [int]$DurationMinutes = 30
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

New-Item -ItemType Directory -Force -Path $SessionRoot | Out-Null

$superRawPath = Join-Path $SessionRoot "super-raw.log"
$telnetRawPath = Join-Path $SessionRoot "telnet-raw.log"
$focusPath = Join-Path $SessionRoot "focus.log"
$statusPath = Join-Path $SessionRoot "monitor-status.log"

$includePatterns = @(
    'DirectOverlay',
    'BrowserVideoBlock',
    'BrowserControl',
    'StreamingMode',
    'BridgeDiag',
    'RokuDeploy',
    'RokuInput',
    '\[OVERLAY\]',
    '\[MODE\]',
    '\[HLS\]'
)

$includeRegex = [string]::Join('|', $includePatterns)
$startedAt = Get-Date
$deadline = $startedAt.AddMinutes($DurationMinutes)

function Write-Status {
    param([string]$Message)
    $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Add-Content -Path $statusPath -Value $line -Encoding UTF8
}

function Append-Focus {
    param(
        [string]$Source,
        [string]$Line
    )

    if ([string]::IsNullOrWhiteSpace($Line)) {
        return
    }

    if ($Line -match $includeRegex) {
        $entry = "[{0}] {1}" -f $Source, $Line
        Add-Content -Path $focusPath -Value $entry -Encoding UTF8
    }
}

$superJob = Start-Job -ScriptBlock {
    param($path, $deadlineArg, $superRaw, $focus, $regex)
    $stream = $null
    try {
        if (-not (Test-Path $path)) { New-Item -ItemType File -Force -Path $path | Out-Null }
        $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $stream.Seek(0, [System.IO.SeekOrigin]::End) | Out-Null
        $reader = New-Object System.IO.StreamReader($stream)
        while ((Get-Date) -lt $deadlineArg) {
            $line = $reader.ReadLine()
            if ($null -ne $line) {
                Add-Content -Path $superRaw -Value $line -Encoding UTF8
                if ($line -match $regex) {
                    Add-Content -Path $focus -Value ("[Super] {0}" -f $line) -Encoding UTF8
                }
            }
            else {
                Start-Sleep -Milliseconds 250
            }
        }
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
    }
} -ArgumentList $SuperLogPath, $deadline, $superRawPath, $focusPath, $includeRegex

$telnetJob = Start-Job -ScriptBlock {
    param($rokuHost, $deadlineArg, $telnetRaw, $focus, $status, $regex)
    function Write-StatusInner {
        param([string]$Message)
        Add-Content -Path $status -Value ("[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message) -Encoding UTF8
    }
    if ([string]::IsNullOrWhiteSpace($rokuHost)) {
        Write-StatusInner "Telnet desabilitado: RokuIp vazio."
        return
    }
    while ((Get-Date) -lt $deadlineArg) {
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $async = $client.BeginConnect($rokuHost, 8085, $null, $null)
            if (-not $async.AsyncWaitHandle.WaitOne(3000)) {
                Write-StatusInner "Telnet timeout ao conectar em $rokuHost`:8085"
                $client.Close()
                Start-Sleep -Seconds 2
                continue
            }
            $client.EndConnect($async)
            Write-StatusInner "Telnet conectado em $rokuHost`:8085"
            $stream = $client.GetStream()
            $reader = New-Object System.IO.StreamReader($stream)
            while ($client.Connected -and (Get-Date) -lt $deadlineArg) {
                $line = $reader.ReadLine()
                if ($null -ne $line) {
                    Add-Content -Path $telnetRaw -Value $line -Encoding UTF8
                    if ($line -match $regex) {
                        Add-Content -Path $focus -Value ("[Telnet] {0}" -f $line) -Encoding UTF8
                    }
                }
                else {
                    Start-Sleep -Milliseconds 250
                }
            }
        }
        catch {
            Write-StatusInner ("Telnet erro: " + $_.Exception.Message)
            Start-Sleep -Seconds 2
        }
        finally {
            try { $client.Close() } catch {}
        }
    }
} -ArgumentList $RokuIp, $deadline, $telnetRawPath, $focusPath, $statusPath, $includeRegex

Write-Status ("Monitor iniciado. SessionRoot=" + $SessionRoot)
Wait-Job -Job $superJob, $telnetJob | Out-Null
Receive-Job -Job $superJob, $telnetJob -Keep | Out-Null
Remove-Job -Job $superJob, $telnetJob -Force | Out-Null
Write-Status "Monitor finalizado."
