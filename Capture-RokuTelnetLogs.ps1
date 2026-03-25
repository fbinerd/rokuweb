param(
    [string]$RokuIp = "10.1.0.22",
    [int]$Port = 8085,
    [int]$DurationSeconds = 0,
    [string]$OutputPath = "",
    [int]$ReconnectDelaySeconds = 2
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Write-Status {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $timestamp = Get-Date -Format "HH:mm:ss"
    Write-Host ("[{0}] {1}" -f $timestamp, $Message)
}

function New-OutputPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [Parameter(Mandatory = $true)]
        [string]$RokuIp,
        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    $logRoot = Join-Path $RepoRoot "tmp\roku-telnet"
    New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    return Join-Path $logRoot ("roku-{0}-{1}-{2}.log" -f ($RokuIp -replace '[^0-9A-Za-z.-]', '_'), $Port, $timestamp)
}

function Write-LogLine {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory = $true)]
        [string]$Line
    )

    $Writer.WriteLine($Line)
    $Writer.Flush()
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = New-OutputPath -RepoRoot $repoRoot -RokuIp $RokuIp -Port $Port
}
else {
    $parent = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
}

$endAtUtc = if ($DurationSeconds -gt 0) { [DateTime]::UtcNow.AddSeconds($DurationSeconds) } else { [DateTime]::MaxValue }
$writer = [System.IO.StreamWriter]::new($OutputPath, $true, [System.Text.Encoding]::UTF8)
$writer.AutoFlush = $true

try {
    Write-Status ("Capturando logs Roku via telnet em {0}:{1}" -f $RokuIp, $Port)
    Write-Status ("Arquivo: {0}" -f $OutputPath)
    if ($DurationSeconds -le 0) {
        Write-Status "Duracao infinita. Use Ctrl+C para encerrar."
    }
    else {
        Write-Status ("Duracao: {0}s" -f $DurationSeconds)
    }

    Write-LogLine -Writer $writer -Line ("===== captura iniciada em {0:O} =====" -f [DateTime]::UtcNow)

    while ([DateTime]::UtcNow -lt $endAtUtc) {
        $client = $null
        $stream = $null
        $reader = $null
        try {
            Write-Status ("Conectando em {0}:{1}..." -f $RokuIp, $Port)
            $client = [System.Net.Sockets.TcpClient]::new()
            $connectTask = $client.ConnectAsync($RokuIp, $Port)
            if (-not $connectTask.Wait(5000)) {
                throw "timeout ao conectar"
            }

            $stream = $client.GetStream()
            $stream.ReadTimeout = 1000
            $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::ASCII, $false, 4096, $true)
            Write-Status "Conectado."
            Write-LogLine -Writer $writer -Line ("===== conectado em {0:O} =====" -f [DateTime]::UtcNow)

            while ($client.Connected -and [DateTime]::UtcNow -lt $endAtUtc) {
                try {
                    $line = $reader.ReadLine()
                }
                catch [System.IO.IOException] {
                    continue
                }

                if ($null -eq $line) {
                    throw "conexao encerrada pela Roku"
                }

                Write-Host $line
                Write-LogLine -Writer $writer -Line $line
            }
        }
        catch {
            $message = $_.Exception.Message
            Write-Status ("Desconectado: {0}" -f $message)
            Write-LogLine -Writer $writer -Line ("===== desconectado em {0:O}: {1} =====" -f [DateTime]::UtcNow, $message)
        }
        finally {
            if ($reader) { $reader.Dispose() }
            if ($stream) { $stream.Dispose() }
            if ($client) { $client.Dispose() }
        }

        if ([DateTime]::UtcNow -ge $endAtUtc) {
            break
        }

        Start-Sleep -Seconds ([Math]::Max(1, $ReconnectDelaySeconds))
    }
}
finally {
    Write-LogLine -Writer $writer -Line ("===== captura encerrada em {0:O} =====" -f [DateTime]::UtcNow)
    $writer.Dispose()
    Write-Status ("Captura encerrada. Log salvo em: {0}" -f $OutputPath)
}
