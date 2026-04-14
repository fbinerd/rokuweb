param(
    [string]$BaseRef = "HEAD~1",
    [string]$OutputDirectory = "dist",
    [string]$BaseUrl = "",
    [string]$Channel = "",
    [string]$SuperCurrentFullPackageUrl = ""
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repoRoot $OutputDirectory
$superRoot = Join-Path $repoRoot "super"
$superReleaseRoot = Join-Path $superRoot "src\WindowManager.App\bin\Release\net481"
$tempWorktree = Join-Path ([System.IO.Path]::GetTempPath()) ("rokuweb-release-base-" + [Guid]::NewGuid().ToString("N"))
$tempPackageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rokuweb-release-package-" + [Guid]::NewGuid().ToString("N"))
$deltaStatus = "not_attempted"
$deltaMessage = ""

if ([string]::IsNullOrWhiteSpace($Channel)) {
    try {
        $branch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD 2>$null).Trim()
        if ($branch -eq "develop") {
            $Channel = "develop"
        }
        else {
            $Channel = "stable"
        }
    }
    catch {
        $Channel = "stable"
    }
}

$Channel = $Channel.Trim().ToLowerInvariant()

if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    $BaseUrl = "https://fbinerd.github.io/rokuweb/updates/$Channel/"
}

function Get-GitShortSha {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string]$Revision
    )

    $sha = (& git -C $RepositoryRoot rev-parse --short $Revision).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) {
        throw "Falha ao obter hash curto para '$Revision'."
    }

    return $sha
}

function Get-GitCommitTimestampUtc {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string]$Revision
    )

    $timestamp = (& git -C $RepositoryRoot show -s --format=%cI $Revision).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($timestamp)) {
        throw "Falha ao obter timestamp para '$Revision'."
    }

    return $timestamp
}

function Test-GitRevisionExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string]$Revision
    )

    $escapedRoot = $RepositoryRoot.Replace('"', '\"')
    $escapedRevision = $Revision.Replace('"', '\"')
    cmd /c "git -C ""$escapedRoot"" rev-parse --verify ""$escapedRevision"" >nul 2>nul"
    return ($LASTEXITCODE -eq 0)
}

function Get-ManifestValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath,
        [Parameter(Mandatory = $true)]
        [string]$Key
    )

    $line = Get-Content $ManifestPath | Where-Object { $_ -like "$Key=*" } | Select-Object -First 1
    if (-not $line) {
        return ""
    }

    return $line.Substring($Key.Length + 1).Trim()
}

function Get-ManifestValueFromContent {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Content,
        [Parameter(Mandatory = $true)]
        [string]$Key
    )

    $line = $Content | Where-Object { $_ -like "$Key=*" } | Select-Object -First 1
    if (-not $line) {
        return ""
    }

    return $line.Substring($Key.Length + 1).Trim()
}

function Get-RokuVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath
    )

    $major = Get-ManifestValue -ManifestPath $ManifestPath -Key "major_version"
    $minor = Get-ManifestValue -ManifestPath $ManifestPath -Key "minor_version"
    $build = Get-ManifestValue -ManifestPath $ManifestPath -Key "build_version"
    return "$major.$minor.$build"
}

function Get-RokuVersionAtRevision {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string]$Revision
    )

    $content = & git -C $RepositoryRoot show "$Revision`:manifest" 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $content) {
        return ""
    }

    $major = Get-ManifestValueFromContent -Content $content -Key "major_version"
    $minor = Get-ManifestValueFromContent -Content $content -Key "minor_version"
    $build = Get-ManifestValueFromContent -Content $content -Key "build_version"

    if ([string]::IsNullOrWhiteSpace($major) -or [string]::IsNullOrWhiteSpace($minor) -or [string]::IsNullOrWhiteSpace($build)) {
        return ""
    }

    return "$major.$minor.$build"
}

function Get-RokuPackageFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $files = New-Object System.Collections.Generic.List[string]
    $manifestPath = Join-Path $Root "manifest"
    if (Test-Path $manifestPath) {
        $files.Add($manifestPath)
    }

    foreach ($folderName in @("components", "images", "source")) {
        $folderPath = Join-Path $Root $folderName
        if (Test-Path $folderPath) {
            Get-ChildItem -Path $folderPath -Recurse -File | ForEach-Object {
                $files.Add($_.FullName)
            }
        }
    }

    return $files
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return $Path.Substring($Root.Length + 1).Replace("\", "/")
}

function Get-HashDictionary {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [Parameter(Mandatory = $true)]
        [string[]]$Files
    )

    $map = @{}
    foreach ($file in $Files) {
        if (-not (Test-Path $file)) {
            continue
        }

        $relative = Get-RelativePath -Root $Root -Path $file
        $map[$relative] = (Get-FileHash -Algorithm SHA256 -Path $file).Hash
    }

    return $map
}

function Get-FileEntryList {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [Parameter(Mandatory = $true)]
        [string[]]$Files
    )

    $entries = @()
    foreach ($file in ($Files | Sort-Object -Unique)) {
        if (-not (Test-Path $file)) {
            continue
        }

        $relative = Get-RelativePath -Root $Root -Path $file
        $fileInfo = Get-Item -Path $file
        $entries += [pscustomobject]@{
            path = $relative
            sha256 = (Get-FileHash -Algorithm SHA256 -Path $file).Hash
            size = [int64]$fileInfo.Length
        }
    }

    return [object[]]$entries
}

function Get-DirectoryHashDictionary {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $map = @{}
    if (-not (Test-Path $Root)) {
        return $map
    }

    Get-ChildItem -Path $Root -Recurse -File | ForEach-Object {
        $relative = Get-RelativePath -Root $Root -Path $_.FullName
        $map[$relative] = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash
    }

    return $map
}

function Get-SuperPackageFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    if (-not (Test-Path $Root)) {
        return @()
    }

    $excludedExtensions = @(".log")
    $excludedNames = @(
        "startup.log",
        "cef.log"
    )

    return Get-ChildItem -Path $Root -Recurse -File | Where-Object {
        $excludedNames -notcontains $_.Name -and $excludedExtensions -notcontains $_.Extension.ToLowerInvariant()
    } | Select-Object -ExpandProperty FullName
}

function Get-ChangedRelativeFiles {
    param(
        [hashtable]$Current,
        [hashtable]$Previous
    )

    $changed = New-Object System.Collections.Generic.List[string]
    foreach ($key in $Current.Keys) {
        if (-not $Previous.ContainsKey($key) -or $Previous[$key] -ne $Current[$key]) {
            $changed.Add($key)
        }
    }

    return $changed | Sort-Object -Unique
}

function Get-DeletedRelativeFiles {
    param(
        [hashtable]$Current,
        [hashtable]$Previous
    )

    $deleted = New-Object System.Collections.Generic.List[string]
    foreach ($key in $Previous.Keys) {
        if (-not $Current.ContainsKey($key)) {
            $deleted.Add($key)
        }
    }

    return $deleted | Sort-Object -Unique
}

function New-ZipFromFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [Parameter(Mandatory = $true)]
        [string[]]$Files,
        [Parameter(Mandatory = $true)]
        [string]$ZipPath
    )

    $zipDirectory = Split-Path -Parent $ZipPath
    if (-not (Test-Path $zipDirectory)) {
        New-Item -ItemType Directory -Path $zipDirectory -Force | Out-Null
    }

    if (Test-Path $ZipPath) {
        Remove-Item $ZipPath -Force
    }

    $zip = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in @($Files)) {
            if (-not (Test-Path $file)) {
                continue
            }

            $relative = Get-RelativePath -Root $Root -Path $file
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file, $relative) | Out-Null
        }
    }
    finally {
        $zip.Dispose()
    }
}

function New-ZipFromDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DirectoryPath,
        [Parameter(Mandatory = $true)]
        [string]$ZipPath
    )

    $files = @()
    if (Test-Path $DirectoryPath) {
        $files = Get-SuperPackageFiles -Root $DirectoryPath
    }

    New-ZipFromFiles -Root $DirectoryPath -Files $files -ZipPath $ZipPath
}

function Expand-ZipToDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ZipPath,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    if (Test-Path $DestinationPath) {
        Remove-Item $DestinationPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $DestinationPath)
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [psobject]$Data
    )

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $Data | ConvertTo-Json -Depth 8 | Set-Content -Path $Path -Encoding UTF8
}

function New-JsonArray {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [object[]]$Items
    )

    $list = New-Object System.Collections.ArrayList
    foreach ($item in @($Items)) {
        if ($null -ne $item) {
            [void]$list.Add($item)
        }
    }

    return $list
}

function Get-PublishedManifestDocument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl,
        [Parameter(Mandatory = $true)]
        [string]$AppName
    )

    $manifestName = if ($AppName -eq "super") { "latest-super.json" } else { "latest-rokuweb.json" }
    $manifestUrl = "$BaseUrl$manifestName"

    try {
        $response = Invoke-WebRequest -Uri $manifestUrl -UseBasicParsing -TimeoutSec 15
        if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300 -or [string]::IsNullOrWhiteSpace($response.Content)) {
            return $null
        }

        return $response.Content | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Get-ShortShaFromReleaseId {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ReleaseId,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    if ([string]::IsNullOrWhiteSpace($ReleaseId) -or [string]::IsNullOrWhiteSpace($Version)) {
        return ""
    }

    $prefix = "$Version-"
    if (-not $ReleaseId.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return ""
    }

    return $ReleaseId.Substring($prefix.Length).Trim()
}

function Get-ReleaseHistory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string]$AppName,
        [Parameter(Mandatory = $true)]
        [string]$CurrentVersion,
        [Parameter(Mandatory = $true)]
        [string]$CurrentReleaseId,
        [Parameter(Mandatory = $true)]
        [string]$CurrentGeneratedAtUtc,
        [string]$CurrentPreviousVersion,
        [string]$CurrentPreviousReleaseId,
        [string]$CurrentFullPackage,
        [string]$CurrentFullPackageUrl,
        [string]$CurrentDeltaPackage,
        [string[]]$CurrentChangedFiles,
        [string[]]$CurrentDeletedFiles,
        [psobject[]]$CurrentFiles,
        [psobject[]]$CurrentDeltaFiles,
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl
    )

    $seenReleaseIds = @{}
    $releases = @()

    $currentEntry = [pscustomobject]@{
        version = $CurrentVersion
        releaseId = $CurrentReleaseId
        commit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
        publishedAtUtc = $CurrentGeneratedAtUtc
        fullPackage = $CurrentFullPackage
        fullPackageUrl = if (-not [string]::IsNullOrWhiteSpace($CurrentFullPackageUrl)) { $CurrentFullPackageUrl } elseif ($CurrentFullPackage) { $BaseUrl + $CurrentFullPackage } else { $null }
        deltaPackage = $CurrentDeltaPackage
        deltaPackageUrl = if ($CurrentDeltaPackage) { $BaseUrl + $CurrentDeltaPackage } else { $null }
        deltaSupportedFromVersions = if ($CurrentPreviousVersion) { (New-JsonArray $CurrentPreviousVersion) } else { (New-JsonArray) }
        deltaSupportedFromReleases = if ($CurrentPreviousReleaseId) { (New-JsonArray $CurrentPreviousReleaseId) } else { (New-JsonArray) }
        fullPackageRequiredIfCurrentVersionOlderThan = $CurrentPreviousVersion
        fullPackageRequiredIfCurrentReleaseOlderThan = $CurrentPreviousReleaseId
        changedFiles = (New-JsonArray $CurrentChangedFiles)
        deletedFiles = (New-JsonArray $CurrentDeletedFiles)
        files = (New-JsonArray $CurrentFiles)
        deltaFiles = (New-JsonArray $CurrentDeltaFiles)
    }
    $releases += $currentEntry
    $seenReleaseIds[$CurrentReleaseId] = $true

    if (-not [string]::IsNullOrWhiteSpace($CurrentPreviousReleaseId) -and -not $seenReleaseIds.ContainsKey($CurrentPreviousReleaseId)) {
        $previousCommit = Get-ShortShaFromReleaseId -ReleaseId $CurrentPreviousReleaseId -Version $CurrentPreviousVersion
        $releases += [pscustomobject]@{
            version = $CurrentPreviousVersion
            releaseId = $CurrentPreviousReleaseId
            commit = $previousCommit
            publishedAtUtc = $null
            fullPackage = $null
            fullPackageUrl = $null
            deltaPackage = $null
            deltaPackageUrl = $null
            deltaSupportedFromVersions = (New-JsonArray)
            deltaSupportedFromReleases = (New-JsonArray)
            fullPackageRequiredIfCurrentVersionOlderThan = $null
            fullPackageRequiredIfCurrentReleaseOlderThan = $null
            changedFiles = (New-JsonArray)
            deletedFiles = (New-JsonArray)
            files = (New-JsonArray)
            deltaFiles = (New-JsonArray)
        }
        $seenReleaseIds[$CurrentPreviousReleaseId] = $true
    }

    $publishedManifest = Get-PublishedManifestDocument -BaseUrl $BaseUrl -AppName $AppName
    if ($publishedManifest -and $publishedManifest.releases) {
        foreach ($release in @($publishedManifest.releases)) {
            if ($null -eq $release -or [string]::IsNullOrWhiteSpace($release.releaseId)) {
                continue
            }

            $existingReleaseId = [string]$release.releaseId
            if ($seenReleaseIds.ContainsKey($existingReleaseId)) {
                continue
            }

            $releases += $release
            $seenReleaseIds[$existingReleaseId] = $true
        }
    }

    return [object[]]$releases
}

try {
    if (Test-Path $outputRoot) {
        Remove-Item $outputRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

    $manifestPath = Join-Path $repoRoot "manifest"
    $rokuVersion = Get-RokuVersion -ManifestPath $manifestPath
    $currentShortSha = Get-GitShortSha -RepositoryRoot $repoRoot -Revision "HEAD"
    $releaseId = "$rokuVersion-$currentShortSha"
    $generatedAtUtc = [DateTime]::UtcNow.ToString("O")

    $rokuFullZipName = "{0}-rokuweb-{1}-full.zip" -f $Channel, $releaseId
    $rokuFullZipOutput = Join-Path $OutputDirectory $rokuFullZipName
    $rokuFullZip = Join-Path $outputRoot $rokuFullZipName
    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot "package.ps1") -Channel $Channel -Output $rokuFullZipOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao gerar pacote completo do rokuweb."
    }

    $superFullZip = Join-Path $outputRoot ("{0}-super-{1}-full.zip" -f $Channel, $releaseId)
    New-ZipFromDirectory -DirectoryPath $superReleaseRoot -ZipPath $superFullZip

    $currentRokuPackageRoot = Join-Path $tempPackageRoot "current"
    Expand-ZipToDirectory -ZipPath $rokuFullZip -DestinationPath $currentRokuPackageRoot

    $rokuFiles = Get-RokuPackageFiles -Root $currentRokuPackageRoot
    $rokuCurrentMap = Get-HashDictionary -Root $currentRokuPackageRoot -Files $rokuFiles
    $rokuCurrentFileEntries = Get-FileEntryList -Root $currentRokuPackageRoot -Files $rokuFiles
    $superCurrentFiles = Get-SuperPackageFiles -Root $superReleaseRoot
    $superCurrentMap = Get-HashDictionary -Root $superReleaseRoot -Files $superCurrentFiles
    $superCurrentFileEntries = Get-FileEntryList -Root $superReleaseRoot -Files $superCurrentFiles

    $previousVersion = ""
    $previousReleaseId = ""
    $deltaBaseRef = $BaseRef
    $rokuDeltaZip = $null
    $superDeltaZip = $null
    $rokuChanged = @()
    $rokuDeleted = @()
    $superChanged = @()
    $superDeleted = @()
    $rokuDeltaFileEntries = @()
    $superDeltaFileEntries = @()

    $canCreateDelta = $true
    $publishedSuperManifest = Get-PublishedManifestDocument -BaseUrl $BaseUrl -AppName "super"
    if ($publishedSuperManifest) {
        $publishedPreviousReleaseId = ""
        $publishedPreviousVersion = ""

        if (-not [string]::IsNullOrWhiteSpace($publishedSuperManifest.currentRelease) -and
            -not [string]::Equals([string]$publishedSuperManifest.currentRelease, $releaseId, [System.StringComparison]::OrdinalIgnoreCase)) {
            $publishedPreviousReleaseId = [string]$publishedSuperManifest.currentRelease
            $publishedPreviousVersion = [string]$publishedSuperManifest.currentVersion
        }
        elseif ($publishedSuperManifest.releases) {
            foreach ($release in @($publishedSuperManifest.releases)) {
                if ($null -eq $release -or [string]::IsNullOrWhiteSpace($release.releaseId)) {
                    continue
                }

                if ([string]::Equals([string]$release.releaseId, $releaseId, [System.StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }

                $publishedPreviousReleaseId = [string]$release.releaseId
                $publishedPreviousVersion = [string]$release.version
                break
            }
        }

        $publishedPreviousSha = Get-ShortShaFromReleaseId -ReleaseId $publishedPreviousReleaseId -Version $publishedPreviousVersion
        if (-not [string]::IsNullOrWhiteSpace($publishedPreviousSha) -and (Test-GitRevisionExists -RepositoryRoot $repoRoot -Revision $publishedPreviousSha)) {
            $deltaBaseRef = $publishedPreviousSha
        }
    }

    try {
        if (-not (Test-GitRevisionExists -RepositoryRoot $repoRoot -Revision $deltaBaseRef)) {
            $canCreateDelta = $false
            $deltaStatus = "skipped"
            $deltaMessage = "Base ref '$deltaBaseRef' nao esta disponivel neste checkout."
        }
    }
    catch {
        $canCreateDelta = $false
        $deltaStatus = "skipped"
        $deltaMessage = "Falha ao verificar base ref '$deltaBaseRef': $($_.Exception.Message)"
    }

    if ($canCreateDelta) {
        try {
            & git -C $repoRoot worktree add --detach $tempWorktree $deltaBaseRef | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Falha ao criar worktree temporario para delta."
            }

            $previousVersion = Get-RokuVersion -ManifestPath (Join-Path $tempWorktree "manifest")
            $previousShortSha = Get-GitShortSha -RepositoryRoot $repoRoot -Revision $deltaBaseRef
            $previousReleaseId = "$previousVersion-$previousShortSha"

            $previousRokuFullZipName = "previous-roku-full.zip"
            & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tempWorktree "package.ps1") -Channel $Channel -Output $previousRokuFullZipName
            if ($LASTEXITCODE -ne 0) {
                throw "Falha ao gerar pacote completo base do rokuweb para delta."
            }

            $previousRokuFullZip = Join-Path $tempWorktree $previousRokuFullZipName
            $previousRokuPackageRoot = Join-Path $tempPackageRoot "previous"
            Expand-ZipToDirectory -ZipPath $previousRokuFullZip -DestinationPath $previousRokuPackageRoot

            $previousRokuFiles = Get-RokuPackageFiles -Root $previousRokuPackageRoot
            $rokuPreviousMap = Get-HashDictionary -Root $previousRokuPackageRoot -Files $previousRokuFiles

            Push-Location (Join-Path $tempWorktree "super")
            try {
                & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ".\build.ps1" -Restore -Build
                if ($LASTEXITCODE -ne 0) {
                    throw "Falha ao compilar versao base do super para delta."
                }
            }
            finally {
                Pop-Location
            }

            $superPreviousRoot = Join-Path $tempWorktree "super\src\WindowManager.App\bin\Release\net481"
            $superPreviousFiles = Get-SuperPackageFiles -Root $superPreviousRoot
            $superPreviousMap = Get-HashDictionary -Root $superPreviousRoot -Files $superPreviousFiles

            $rokuChanged = Get-ChangedRelativeFiles -Current $rokuCurrentMap -Previous $rokuPreviousMap
            $rokuDeleted = Get-DeletedRelativeFiles -Current $rokuCurrentMap -Previous $rokuPreviousMap
            $superChanged = Get-ChangedRelativeFiles -Current $superCurrentMap -Previous $superPreviousMap
            $superDeleted = Get-DeletedRelativeFiles -Current $superCurrentMap -Previous $superPreviousMap

            $rokuDeltaFiles = @($rokuChanged | ForEach-Object { Join-Path $currentRokuPackageRoot $_ })
            if ($rokuDeltaFiles.Count -gt 0) {
                $rokuDeltaZip = Join-Path $outputRoot ("{0}-rokuweb-{1}-delta-from-{2}.zip" -f $Channel, $releaseId, $previousReleaseId)
                New-ZipFromFiles -Root $currentRokuPackageRoot -Files $rokuDeltaFiles -ZipPath $rokuDeltaZip
                $rokuDeltaFileEntries = Get-FileEntryList -Root $currentRokuPackageRoot -Files $rokuDeltaFiles
            }

            $superDeltaFiles = @($superChanged | ForEach-Object { Join-Path $superReleaseRoot $_ })
            if ($superDeltaFiles.Count -gt 0) {
                $superDeltaZip = Join-Path $outputRoot ("{0}-super-{1}-delta-from-{2}.zip" -f $Channel, $releaseId, $previousReleaseId)
                New-ZipFromFiles -Root $superReleaseRoot -Files $superDeltaFiles -ZipPath $superDeltaZip
                $superDeltaFileEntries = Get-FileEntryList -Root $superReleaseRoot -Files $superDeltaFiles
            }

            if ($rokuDeltaFiles.Count -eq 0 -and $superDeltaFiles.Count -eq 0) {
                $deltaStatus = "not_needed"
                $deltaMessage = "Nenhum arquivo do app mudou nesta release."
            }
            else {
                $deltaStatus = "created"
                $deltaMessage = ""
            }
        }
        catch {
            $deltaStatus = "skipped"
            $deltaMessage = $_.Exception.Message
            $previousVersion = ""
            $previousReleaseId = ""
            $rokuDeltaZip = $null
            $superDeltaZip = $null
            $rokuChanged = @()
            $rokuDeleted = @()
            $superChanged = @()
            $superDeleted = @()
            $rokuDeltaFileEntries = @()
            $superDeltaFileEntries = @()
        }
    }

    $rokuChangesData = [pscustomobject]@{
        app = "rokuweb"
        version = $rokuVersion
        releaseId = $releaseId
        previousVersion = $previousVersion
        previousReleaseId = $previousReleaseId
        baseRef = $deltaBaseRef
        generatedAtUtc = $generatedAtUtc
        fullPackage = "$Channel-rokuweb-$releaseId-full.zip"
        fullPackageUrl = "$BaseUrl" + "$Channel-rokuweb-$releaseId-full.zip"
        deltaPackage = if ($rokuDeltaZip) { Split-Path -Leaf $rokuDeltaZip } else { $null }
        deltaPackageUrl = if ($rokuDeltaZip) { "$BaseUrl" + (Split-Path -Leaf $rokuDeltaZip) } else { $null }
        deltaSupportedFromVersions = if ($previousVersion) { (New-JsonArray $previousVersion) } else { (New-JsonArray) }
        deltaSupportedFromReleases = if ($previousReleaseId) { (New-JsonArray $previousReleaseId) } else { (New-JsonArray) }
        deltaStatus = $deltaStatus
        deltaMessage = $deltaMessage
        fullPackageRequiredIfCurrentVersionOlderThan = $previousVersion
        fullPackageRequiredIfCurrentReleaseOlderThan = $previousReleaseId
        changedFiles = (New-JsonArray $rokuChanged)
        deletedFiles = (New-JsonArray $rokuDeleted)
        files = (New-JsonArray $rokuCurrentFileEntries)
        deltaFiles = (New-JsonArray $rokuDeltaFileEntries)
    }
    Write-JsonFile -Path (Join-Path $outputRoot ("{0}-rokuweb-{1}-changes.json" -f $Channel, $releaseId)) -Data $rokuChangesData

    $superChangesData = [pscustomobject]@{
        app = "super"
        version = $rokuVersion
        releaseId = $releaseId
        previousVersion = $previousVersion
        previousReleaseId = $previousReleaseId
        baseRef = $deltaBaseRef
        generatedAtUtc = $generatedAtUtc
        fullPackage = "$Channel-super-$releaseId-full.zip"
        fullPackageUrl = if ([string]::IsNullOrWhiteSpace($SuperCurrentFullPackageUrl)) { "$BaseUrl" + "$Channel-super-$releaseId-full.zip" } else { $SuperCurrentFullPackageUrl }
        deltaPackage = if ($superDeltaZip) { Split-Path -Leaf $superDeltaZip } else { $null }
        deltaPackageUrl = if ($superDeltaZip) { "$BaseUrl" + (Split-Path -Leaf $superDeltaZip) } else { $null }
        deltaSupportedFromVersions = if ($previousVersion) { (New-JsonArray $previousVersion) } else { (New-JsonArray) }
        deltaSupportedFromReleases = if ($previousReleaseId) { (New-JsonArray $previousReleaseId) } else { (New-JsonArray) }
        deltaStatus = $deltaStatus
        deltaMessage = $deltaMessage
        fullPackageRequiredIfCurrentVersionOlderThan = $previousVersion
        fullPackageRequiredIfCurrentReleaseOlderThan = $previousReleaseId
        changedFiles = (New-JsonArray $superChanged)
        deletedFiles = (New-JsonArray $superDeleted)
        files = (New-JsonArray $superCurrentFileEntries)
        deltaFiles = (New-JsonArray $superDeltaFileEntries)
    }
    Write-JsonFile -Path (Join-Path $outputRoot ("{0}-super-{1}-changes.json" -f $Channel, $releaseId)) -Data $superChangesData

    $rokuHistory = Get-ReleaseHistory `
        -RepositoryRoot $repoRoot `
        -AppName "rokuweb" `
        -CurrentVersion $rokuVersion `
        -CurrentReleaseId $releaseId `
        -CurrentGeneratedAtUtc $generatedAtUtc `
        -CurrentPreviousVersion $previousVersion `
        -CurrentPreviousReleaseId $previousReleaseId `
        -CurrentFullPackage $rokuChangesData.fullPackage `
        -CurrentFullPackageUrl $rokuChangesData.fullPackageUrl `
        -CurrentDeltaPackage $rokuChangesData.deltaPackage `
        -CurrentChangedFiles $rokuChanged `
        -CurrentDeletedFiles $rokuDeleted `
        -CurrentFiles $rokuCurrentFileEntries `
        -CurrentDeltaFiles $rokuDeltaFileEntries `
        -BaseUrl $BaseUrl

    $superHistory = Get-ReleaseHistory `
        -RepositoryRoot $repoRoot `
        -AppName "super" `
        -CurrentVersion $rokuVersion `
        -CurrentReleaseId $releaseId `
        -CurrentGeneratedAtUtc $generatedAtUtc `
        -CurrentPreviousVersion $previousVersion `
        -CurrentPreviousReleaseId $previousReleaseId `
        -CurrentFullPackage $superChangesData.fullPackage `
        -CurrentFullPackageUrl $superChangesData.fullPackageUrl `
        -CurrentDeltaPackage $superChangesData.deltaPackage `
        -CurrentChangedFiles $superChanged `
        -CurrentDeletedFiles $superDeleted `
        -CurrentFiles $superCurrentFileEntries `
        -CurrentDeltaFiles $superDeltaFileEntries `
        -BaseUrl $BaseUrl

    Write-JsonFile -Path (Join-Path $outputRoot "latest-rokuweb.json") -Data ([pscustomobject]@{
        app = "rokuweb"
        distributionChannel = "github-pages"
        manifestPath = "updates/$Channel/latest-rokuweb.json"
        assetBasePath = "updates/$Channel/"
        manifestUrl = "$BaseUrl" + "latest-rokuweb.json"
        currentRelease = $releaseId
        currentVersion = $rokuVersion
        publishedAtUtc = $generatedAtUtc
        minimumSupportedVersion = if ($previousVersion) { $previousVersion } else { $rokuVersion }
        minimumSupportedRelease = if ($previousReleaseId) { $previousReleaseId } else { $releaseId }
        releases = (New-JsonArray $rokuHistory)
    })

    Write-JsonFile -Path (Join-Path $outputRoot "latest-super.json") -Data ([pscustomobject]@{
        app = "super"
        distributionChannel = "github-pages"
        manifestPath = "updates/$Channel/latest-super.json"
        assetBasePath = "updates/$Channel/"
        manifestUrl = "$BaseUrl" + "latest-super.json"
        currentRelease = $releaseId
        currentVersion = $rokuVersion
        publishedAtUtc = $generatedAtUtc
        minimumSupportedVersion = if ($previousVersion) { $previousVersion } else { $rokuVersion }
        minimumSupportedRelease = if ($previousReleaseId) { $previousReleaseId } else { $releaseId }
        releases = (New-JsonArray $superHistory)
    })
}
finally {
    if (Test-Path $tempPackageRoot) {
        try {
            Remove-Item $tempPackageRoot -Recurse -Force
        }
        catch {
        }
    }

    if (Test-Path $tempWorktree) {
        try {
            & git -C $repoRoot worktree remove $tempWorktree --force | Out-Null
        }
        catch {
        }
    }
}
