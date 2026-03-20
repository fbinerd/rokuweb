param(
    [string]$RokuIp = "",
    [string]$RokuUser = "rokudev",
    [string]$RokuPassword = "1234",
    [switch]$LaunchSuper,
    [switch]$SkipSideload
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$superRoot = Join-Path $repoRoot "super"
$superExePath = Join-Path $superRoot "src\WindowManager.App\bin\Release\net481\SuperPainel.exe"
$localRokuZip = Join-Path $repoRoot "local-roku.zip"

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

    $boundary = "---------------------------" + [Guid]::NewGuid().ToString("N")
    $newLine = "`r`n"
    $fileName = [System.IO.Path]::GetFileName($PackagePath)
    $headerText = @(
        "--$boundary"
        'Content-Disposition: form-data; name="mysubmit"'
        ""
        "Install"
        "--$boundary"
        'Content-Disposition: form-data; name="passwd"'
        ""
        $Password
        "--$boundary"
        "Content-Disposition: form-data; name=`"archive`"; filename=`"$fileName`""
        "Content-Type: application/octet-stream"
        ""
    ) -join $newLine

    $footerText = $newLine + "--$boundary--" + $newLine
    $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($headerText)
    $fileBytes = [System.IO.File]::ReadAllBytes($PackagePath)
    $footerBytes = [System.Text.Encoding]::ASCII.GetBytes($footerText)

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
        [string]$Host,
        [Parameter(Mandatory = $true)]
        [string]$Username,
        [Parameter(Mandatory = $true)]
        [string]$Password,
        [Parameter(Mandatory = $true)]
        [string]$PackagePath
    )

    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.Credentials = New-Object System.Net.NetworkCredential($Username, $Password)
    $handler.PreAuthenticate = $true

    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        $basic = [Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes("$Username`:$Password"))
        $client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Basic", $basic)

        $payload = New-MultipartBody -PackagePath $PackagePath -Password $Password
        $content = [System.Net.Http.ByteArrayContent]::new($payload.Body)
        $content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("multipart/form-data; boundary=" + $payload.Boundary)

        $installUri = "http://$Host/plugin_install"
        $installResponse = $client.PostAsync($installUri, $content).GetAwaiter().GetResult()
        $installBody = $installResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        Write-Host ("plugin_install => status={0}" -f [int]$installResponse.StatusCode)
        if (-not $installResponse.IsSuccessStatusCode) {
            throw "Falha no plugin_install: $([int]$installResponse.StatusCode) $installBody"
        }

        Start-Sleep -Milliseconds 600
        $null = $client.PostAsync("http://$Host:8060/keypress/Home", [System.Net.Http.StringContent]::new("")).GetAwaiter().GetResult()
        Start-Sleep -Milliseconds 600
        $launchResponse = $client.PostAsync("http://$Host:8060/launch/dev", [System.Net.Http.StringContent]::new("")).GetAwaiter().GetResult()
        Write-Host ("launch/dev => status={0}" -f [int]$launchResponse.StatusCode)
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

Invoke-Step -Label "Compilar super em modo local" -Action {
    $env:SUPER_BUILD_CHANNEL = "local"
    try {
        Push-Location $superRoot
        & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ".\build.ps1" -Restore -Build
        if ($LASTEXITCODE -ne 0) {
            throw "Falha ao compilar o super."
        }
    }
    finally {
        Pop-Location
        Remove-Item Env:SUPER_BUILD_CHANNEL -ErrorAction SilentlyContinue
    }
}

Invoke-Step -Label "Empacotar canal Roku local" -Action {
    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot "package.ps1") -Channel "local" -Output "local-roku.zip"
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao empacotar local-roku.zip."
    }
}

if ($LaunchSuper) {
    Invoke-Step -Label "Abrir SuperPainel local" -Action {
        if (-not (Test-Path $superExePath)) {
            throw "Executavel nao encontrado: $superExePath"
        }

        Start-Process -FilePath $superExePath -WorkingDirectory (Split-Path -Parent $superExePath)
    }
}

if (-not $SkipSideload) {
    if ([string]::IsNullOrWhiteSpace($RokuIp)) {
        throw "Informe -RokuIp para fazer sideload local, ou use -SkipSideload."
    }

    Invoke-Step -Label "Enviar sideload local para $RokuIp" -Action {
        Invoke-RokuSideload -Host $RokuIp.Trim() -Username $RokuUser -Password $RokuPassword -PackagePath $localRokuZip
    }
}

Write-Host ""
Write-Host "Fluxo local concluido."
Write-Host "Super: $superExePath"
Write-Host "Roku zip: $localRokuZip"
