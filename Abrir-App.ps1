$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$packagePath = Join-Path $scriptRoot "hello-roku.zip"
$configPath = Join-Path $scriptRoot "roku-dev.config.clixml"

function Get-DeveloperConfig {
    if (Test-Path $configPath) {
        return Import-Clixml -Path $configPath
    }

    Write-Host "Configuracao inicial do Roku."
    $deviceIp = Read-Host "IP do Roku na rede local"
    if ([string]::IsNullOrWhiteSpace($deviceIp)) {
        throw "O IP do Roku e obrigatorio."
    }

    $userName = Read-Host "Usuario de desenvolvedor (Enter para usar rokudev)"
    if ([string]::IsNullOrWhiteSpace($userName)) {
        $userName = "rokudev"
    }

    $credential = Get-Credential -UserName $userName -Message "Informe a senha do modo desenvolvedor do Roku"

    $config = [pscustomobject]@{
        DeviceIp   = $deviceIp.Trim()
        Credential = $credential
    }

    $config | Export-Clixml -Path $configPath
    return $config
}

function Invoke-Curl {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    & curl.exe @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao executar curl.exe $($Arguments -join ' ')"
    }
}

function Build-Package {
    if (Test-Path $packagePath) {
        Remove-Item $packagePath -Force
    }

    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptRoot "package.ps1")

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $packagePath)) {
        throw "Falha ao gerar o pacote Roku."
    }
}

function Install-And-LaunchRokuApp {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DeviceIp,
        [Parameter(Mandatory = $true)]
        [pscredential]$Credential
    )

    $userName = $Credential.UserName
    $password = $Credential.GetNetworkCredential().Password

    Invoke-Curl `
        --silent `
        --show-error `
        --fail `
        --digest `
        --user "$userName`:$password" `
        --form "mysubmit=Replace" `
        --form "archive=@$packagePath;type=application/octet-stream" `
        --form "passwd=" `
        "http://$DeviceIp/plugin_install"

    Invoke-Curl `
        --silent `
        --show-error `
        --fail `
        --request POST `
        "http://$DeviceIp:8060/launch/dev"
}

Build-Package
$config = Get-DeveloperConfig
Install-And-LaunchRokuApp -DeviceIp $config.DeviceIp -Credential $config.Credential

Write-Host "Pacote enviado e canal de desenvolvedor iniciado no Roku."
