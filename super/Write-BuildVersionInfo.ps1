param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDir,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"

$projectPath = (Resolve-Path $ProjectDir).Path
$superRoot = (Resolve-Path $PSScriptRoot).Path
$repoRoot = Split-Path -Parent $superRoot
$manifestPath = Join-Path $repoRoot "manifest"
$resolvedOutputPath = $OutputPath

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

$major = Get-ManifestValue -Path $manifestPath -Key "major_version"
$minor = Get-ManifestValue -Path $manifestPath -Key "minor_version"
$build = Get-ManifestValue -Path $manifestPath -Key "build_version"
$version = "$major.$minor.$build"
$shortSha = "local"
$fullSha = "local"
$currentChannel = "stable"
$channelOverride = $env:SUPER_BUILD_CHANNEL
$localStamp = ""
$isDirtyWorkingTree = $false

try {
    $resolvedShortSha = (& git -C $repoRoot rev-parse --short HEAD 2>$null).Trim()
    if (-not [string]::IsNullOrWhiteSpace($resolvedShortSha)) {
        $shortSha = $resolvedShortSha
    }

    $resolvedFullSha = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim()
    if (-not [string]::IsNullOrWhiteSpace($resolvedFullSha)) {
        $fullSha = $resolvedFullSha
    }

    if (-not [string]::IsNullOrWhiteSpace($channelOverride)) {
        $currentChannel = $channelOverride.Trim().ToLowerInvariant()
    }
    else {
        $resolvedBranch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD 2>$null).Trim()
        if (-not [string]::IsNullOrWhiteSpace($resolvedBranch)) {
            if ($resolvedBranch -eq "develop") {
                $currentChannel = "develop"
            }
            elseif ($resolvedBranch -eq "stable") {
                $currentChannel = "stable"
            }
        }
    }

    $statusOutput = (& git -C $repoRoot status --porcelain 2>$null)
    $isDirtyWorkingTree = -not [string]::IsNullOrWhiteSpace(($statusOutput | Out-String).Trim())
}
catch {
}

if ([string]::Equals($currentChannel, "local", [System.StringComparison]::OrdinalIgnoreCase) -or $isDirtyWorkingTree) {
    $currentChannel = "local"
    $localStamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
}

$releaseId = if ([string]::IsNullOrWhiteSpace($localStamp)) {
    "$version-$shortSha"
}
else {
    "$version-$shortSha-local$localStamp"
}
$manifestUrl = "https://fbinerd.github.io/rokuweb/updates/latest-super.json"

$content = @"
namespace WindowManager.App.Runtime;

internal static class BuildVersionInfo
{
    public const string Version = "$version";
    public const string ReleaseId = "$releaseId";
    public const string Commit = "$fullSha";
    public const string CurrentBuildChannel = "$currentChannel";
    public const string LatestManifestUrl = "$manifestUrl";
}
"@

$directory = Split-Path -Parent $resolvedOutputPath
if (-not (Test-Path $directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

Set-Content -Path $resolvedOutputPath -Value $content -Encoding UTF8
