param(
    [string]$Output = "",
    [string]$Channel = ""
)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$resolvedChannel = if ([string]::IsNullOrWhiteSpace($Channel)) {
    try {
        $branch = (& git -C $root rev-parse --abbrev-ref HEAD 2>$null).Trim()
        if ($branch -eq "develop") { "develop" } else { "stable" }
    }
    catch {
        "stable"
    }
}
else {
    $Channel.Trim().ToLowerInvariant()
}

if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = "$resolvedChannel-roku.zip"
}

$zipPath = Join-Path $root $Output
$zipDirectory = Split-Path -Parent $zipPath
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rokuweb-package-" + [System.Guid]::NewGuid().ToString("N"))
$stagingRoot = Join-Path $tempRoot "staging"

if (-not [string]::IsNullOrWhiteSpace($zipDirectory) -and -not (Test-Path $zipDirectory)) {
    New-Item -ItemType Directory -Path $zipDirectory -Force | Out-Null
}

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

function Test-IncludedRokuFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FullName
    )

    $relativePath = $FullName.Substring($root.Length + 1).Replace("\", "/")

    if ($relativePath -eq $Output) { return $false }
    if ($relativePath -eq "manifest") { return $true }
    if ($relativePath.StartsWith("components/")) { return $true }
    if ($relativePath.StartsWith("images/")) { return $true }
    if ($relativePath.StartsWith("source/")) { return $true }

    return $false
}

function Get-ManifestValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Key
    )

    $line = Get-Content $Path | Where-Object { $_ -like "$Key=*" } | Select-Object -First 1
    if (-not $line) {
        return ""
    }

    return $line.Substring($Key.Length + 1).Trim()
}

function Get-RokuReleaseId {
    $manifestPath = Join-Path $root "manifest"
    $major = Get-ManifestValue -Path $manifestPath -Key "major_version"
    $minor = Get-ManifestValue -Path $manifestPath -Key "minor_version"
    $build = Get-ManifestValue -Path $manifestPath -Key "build_version"
    $version = "$major.$minor.$build"
    $shortSha = "local"

    try {
        $resolvedShortSha = (& git -C $root rev-parse --short HEAD 2>$null).Trim()
        if (-not [string]::IsNullOrWhiteSpace($resolvedShortSha)) {
            $shortSha = $resolvedShortSha
        }
    }
    catch {
    }

    return "$version-$shortSha"
}

function Get-RokuBuildVersion {
    $fallback = Get-ManifestValue -Path (Join-Path $root "manifest") -Key "build_version"

    try {
        $commitCount = (& git -C $root rev-list --count HEAD 2>$null).Trim()
        if (-not [string]::IsNullOrWhiteSpace($commitCount)) {
            return $commitCount
        }
    }
    catch {
    }

    return $fallback
}

function Write-StagedManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot
    )

    $sourceManifestPath = Join-Path $root "manifest"
    $destinationManifestPath = Join-Path $DestinationRoot "manifest"
    $buildVersion = Get-RokuBuildVersion
    $content = Get-Content -Path $sourceManifestPath
    $rewritten = foreach ($line in $content) {
        if ($line -like "build_version=*") {
            "build_version=$buildVersion"
        }
        else {
            $line
        }
    }

    Set-Content -Path $destinationManifestPath -Value $rewritten -Encoding ASCII
}

function Write-BuildInfoFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot
    )

    $sourceDirectory = Join-Path $DestinationRoot "source"
    if (-not (Test-Path $sourceDirectory)) {
        New-Item -ItemType Directory -Path $sourceDirectory -Force | Out-Null
    }

    $releaseId = Get-RokuReleaseId
    $content = @"
function GetRokuChannelReleaseId() as string
    return "$releaseId"
end function
"@

    Set-Content -Path (Join-Path $sourceDirectory "BuildInfo.brs") -Value $content -Encoding ASCII
}

$files = Get-ChildItem -Path $root -Recurse -File | Where-Object {
    Test-IncludedRokuFile -FullName $_.FullName
}

New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

$copiedFiles = @()
foreach ($file in $files) {
    $relativePath = $file.FullName.Substring($root.Length + 1).Replace("\", "/")
    $destinationPath = Join-Path $stagingRoot ($relativePath -replace "/", "\")
    $destinationDirectory = Split-Path -Parent $destinationPath
    if (-not (Test-Path $destinationDirectory)) {
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    }

    Copy-Item -Path $file.FullName -Destination $destinationPath -Force
    $copiedFiles += [pscustomobject]@{
        Source = $file.FullName
        Relative = $relativePath
        Destination = $destinationPath
    }
}

Write-BuildInfoFile -DestinationRoot $stagingRoot
Write-StagedManifest -DestinationRoot $stagingRoot

$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)

try {
    foreach ($file in $copiedFiles) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.Destination, $file.Relative) | Out-Null
    }

    $buildInfoPath = Join-Path $stagingRoot "source\BuildInfo.brs"
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $buildInfoPath, "source/BuildInfo.brs") | Out-Null
}
finally {
    $zip.Dispose()
    if (Test-Path $tempRoot) {
        Remove-Item -Path $tempRoot -Recurse -Force
    }
}

Write-Host "Pacote criado em: $zipPath"
