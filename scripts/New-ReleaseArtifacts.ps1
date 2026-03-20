param(
    [string]$BaseRef = "HEAD~1",
    [string]$OutputDirectory = "dist",
    [string]$BaseUrl = "",
    [string]$Channel = ""
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repoRoot $OutputDirectory
$superRoot = Join-Path $repoRoot "super"
$superReleaseRoot = Join-Path $superRoot "src\WindowManager.App\bin\Release\net481"
$tempWorktree = Join-Path ([System.IO.Path]::GetTempPath()) ("rokuweb-release-base-" + [Guid]::NewGuid().ToString("N"))
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

    foreach ($folderName in @("components", "source")) {
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
        [string]$CurrentDeltaPackage,
        [string[]]$CurrentChangedFiles,
        [string[]]$CurrentDeletedFiles,
        [psobject[]]$CurrentFiles,
        [psobject[]]$CurrentDeltaFiles,
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl
    )

    $packagePrefix = if ($AppName -eq "super") { "$Channel-super" } else { "$Channel-rokuweb" }

    $pathspecs = if ($AppName -eq "super") {
        @("--", "super")
    }
    else {
        @("--", "manifest", "components", "source", "package.ps1")
    }

    $historyCommits = @()
    $logOutput = & git -C $RepositoryRoot log --format=%H @pathspecs
    if ($LASTEXITCODE -eq 0 -and $logOutput) {
        foreach ($line in $logOutput) {
            $commit = $line.Trim()
            if (-not [string]::IsNullOrWhiteSpace($commit)) {
                $historyCommits += $commit
            }
        }
    }

    $seenReleaseIds = @{}
    $releases = @()

    $currentEntry = [pscustomobject]@{
        version = $CurrentVersion
        releaseId = $CurrentReleaseId
        commit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
        publishedAtUtc = $CurrentGeneratedAtUtc
        fullPackage = $CurrentFullPackage
        fullPackageUrl = if ($CurrentFullPackage) { $BaseUrl + $CurrentFullPackage } else { $null }
        deltaPackage = $CurrentDeltaPackage
        deltaPackageUrl = if ($CurrentDeltaPackage) { $BaseUrl + $CurrentDeltaPackage } else { $null }
        deltaSupportedFromVersions = if ($CurrentPreviousVersion) { [object[]]@($CurrentPreviousVersion) } else { [object[]]@() }
        deltaSupportedFromReleases = if ($CurrentPreviousReleaseId) { [object[]]@($CurrentPreviousReleaseId) } else { [object[]]@() }
        fullPackageRequiredIfCurrentVersionOlderThan = $CurrentPreviousVersion
        fullPackageRequiredIfCurrentReleaseOlderThan = $CurrentPreviousReleaseId
        changedFiles = [object[]]@($CurrentChangedFiles)
        deletedFiles = [object[]]@($CurrentDeletedFiles)
        files = [object[]]@($CurrentFiles)
        deltaFiles = [object[]]@($CurrentDeltaFiles)
    }
    $releases += $currentEntry
    $seenReleaseIds[$CurrentReleaseId] = $true

    for ($i = 0; $i -lt $historyCommits.Count; $i++) {
        $commit = $historyCommits[$i]
        $shortSha = Get-GitShortSha -RepositoryRoot $RepositoryRoot -Revision $commit
        $version = Get-RokuVersionAtRevision -RepositoryRoot $RepositoryRoot -Revision $commit
        if ([string]::IsNullOrWhiteSpace($version)) {
            continue
        }

        $releaseId = "$version-$shortSha"
        if ($seenReleaseIds.ContainsKey($releaseId)) {
            continue
        }

        $parentVersion = ""
        $parentReleaseId = ""
        $parentRef = "$commit^"
        if (Test-GitRevisionExists -RepositoryRoot $RepositoryRoot -Revision $parentRef) {
            $parentVersion = Get-RokuVersionAtRevision -RepositoryRoot $RepositoryRoot -Revision $parentRef
            if (-not [string]::IsNullOrWhiteSpace($parentVersion)) {
                $parentShortSha = Get-GitShortSha -RepositoryRoot $RepositoryRoot -Revision $parentRef
                $parentReleaseId = "$parentVersion-$parentShortSha"
            }
        }

        $publishedAtUtc = Get-GitCommitTimestampUtc -RepositoryRoot $RepositoryRoot -Revision $commit
        $deltaPackage = if ($parentReleaseId) {
            "$packagePrefix-$releaseId-delta-from-$parentReleaseId.zip"
        }
        else {
            $null
        }

        $releases += [pscustomobject]@{
            version = $version
            releaseId = $releaseId
            commit = $commit
            publishedAtUtc = $publishedAtUtc
            fullPackage = "$packagePrefix-$releaseId-full.zip"
            fullPackageUrl = $BaseUrl + "$packagePrefix-$releaseId-full.zip"
            deltaPackage = $deltaPackage
            deltaPackageUrl = if ($deltaPackage) { $BaseUrl + $deltaPackage } else { $null }
            deltaSupportedFromVersions = if ($parentVersion) { [object[]]@($parentVersion) } else { [object[]]@() }
            deltaSupportedFromReleases = if ($parentReleaseId) { [object[]]@($parentReleaseId) } else { [object[]]@() }
            fullPackageRequiredIfCurrentVersionOlderThan = $parentVersion
            fullPackageRequiredIfCurrentReleaseOlderThan = $parentReleaseId
            changedFiles = [object[]]@()
            deletedFiles = [object[]]@()
            files = [object[]]@()
            deltaFiles = [object[]]@()
        }
        $seenReleaseIds[$releaseId] = $true
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

    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot "package.ps1") -Channel $Channel -Output (Join-Path $OutputDirectory ("{0}-rokuweb-{1}-full.zip" -f $Channel, $releaseId))
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao gerar pacote completo do rokuweb."
    }

    $superFullZip = Join-Path $outputRoot ("{0}-super-{1}-full.zip" -f $Channel, $releaseId)
    New-ZipFromDirectory -DirectoryPath $superReleaseRoot -ZipPath $superFullZip

    $rokuFiles = Get-RokuPackageFiles -Root $repoRoot
    $rokuCurrentMap = Get-HashDictionary -Root $repoRoot -Files $rokuFiles
    $rokuCurrentFileEntries = Get-FileEntryList -Root $repoRoot -Files $rokuFiles
    $superCurrentFiles = Get-SuperPackageFiles -Root $superReleaseRoot
    $superCurrentMap = Get-HashDictionary -Root $superReleaseRoot -Files $superCurrentFiles
    $superCurrentFileEntries = Get-FileEntryList -Root $superReleaseRoot -Files $superCurrentFiles

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

    $canCreateDelta = $true
    try {
        if (-not (Test-GitRevisionExists -RepositoryRoot $repoRoot -Revision $BaseRef)) {
            $canCreateDelta = $false
            $deltaStatus = "skipped"
            $deltaMessage = "Base ref '$BaseRef' nao esta disponivel neste checkout."
        }
    }
    catch {
        $canCreateDelta = $false
        $deltaStatus = "skipped"
        $deltaMessage = "Falha ao verificar base ref '$BaseRef': $($_.Exception.Message)"
    }

    if ($canCreateDelta) {
        try {
            & git -C $repoRoot worktree add --detach $tempWorktree $BaseRef | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Falha ao criar worktree temporario para delta."
            }

            $previousVersion = Get-RokuVersion -ManifestPath (Join-Path $tempWorktree "manifest")
            $previousShortSha = Get-GitShortSha -RepositoryRoot $repoRoot -Revision $BaseRef
            $previousReleaseId = "$previousVersion-$previousShortSha"

            $previousRokuFiles = Get-RokuPackageFiles -Root $tempWorktree
            $rokuPreviousMap = Get-HashDictionary -Root $tempWorktree -Files $previousRokuFiles

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

            $rokuDeltaFiles = @($rokuChanged | ForEach-Object { Join-Path $repoRoot $_ })
            if ($rokuDeltaFiles.Count -gt 0) {
                $rokuDeltaZip = Join-Path $outputRoot ("{0}-rokuweb-{1}-delta-from-{2}.zip" -f $Channel, $releaseId, $previousReleaseId)
                New-ZipFromFiles -Root $repoRoot -Files $rokuDeltaFiles -ZipPath $rokuDeltaZip
                $rokuDeltaFileEntries = Get-FileEntryList -Root $repoRoot -Files $rokuDeltaFiles
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
        baseRef = $BaseRef
        generatedAtUtc = $generatedAtUtc
        fullPackage = "$Channel-rokuweb-$releaseId-full.zip"
        fullPackageUrl = "$BaseUrl" + "$Channel-rokuweb-$releaseId-full.zip"
        deltaPackage = if ($rokuDeltaZip) { Split-Path -Leaf $rokuDeltaZip } else { $null }
        deltaPackageUrl = if ($rokuDeltaZip) { "$BaseUrl" + (Split-Path -Leaf $rokuDeltaZip) } else { $null }
        deltaSupportedFromVersions = if ($previousVersion) { [object[]]@($previousVersion) } else { [object[]]@() }
        deltaSupportedFromReleases = if ($previousReleaseId) { [object[]]@($previousReleaseId) } else { [object[]]@() }
        deltaStatus = $deltaStatus
        deltaMessage = $deltaMessage
        fullPackageRequiredIfCurrentVersionOlderThan = $previousVersion
        fullPackageRequiredIfCurrentReleaseOlderThan = $previousReleaseId
        changedFiles = @($rokuChanged)
        deletedFiles = @($rokuDeleted)
        files = @($rokuCurrentFileEntries)
        deltaFiles = @($rokuDeltaFileEntries)
    }
    Write-JsonFile -Path (Join-Path $outputRoot ("{0}-rokuweb-{1}-changes.json" -f $Channel, $releaseId)) -Data $rokuChangesData

    $superChangesData = [pscustomobject]@{
        app = "super"
        version = $rokuVersion
        releaseId = $releaseId
        previousVersion = $previousVersion
        previousReleaseId = $previousReleaseId
        baseRef = $BaseRef
        generatedAtUtc = $generatedAtUtc
        fullPackage = "$Channel-super-$releaseId-full.zip"
        fullPackageUrl = "$BaseUrl" + "$Channel-super-$releaseId-full.zip"
        deltaPackage = if ($superDeltaZip) { Split-Path -Leaf $superDeltaZip } else { $null }
        deltaPackageUrl = if ($superDeltaZip) { "$BaseUrl" + (Split-Path -Leaf $superDeltaZip) } else { $null }
        deltaSupportedFromVersions = if ($previousVersion) { [object[]]@($previousVersion) } else { [object[]]@() }
        deltaSupportedFromReleases = if ($previousReleaseId) { [object[]]@($previousReleaseId) } else { [object[]]@() }
        deltaStatus = $deltaStatus
        deltaMessage = $deltaMessage
        fullPackageRequiredIfCurrentVersionOlderThan = $previousVersion
        fullPackageRequiredIfCurrentReleaseOlderThan = $previousReleaseId
        changedFiles = @($superChanged)
        deletedFiles = @($superDeleted)
        files = @($superCurrentFileEntries)
        deltaFiles = @($superDeltaFileEntries)
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
        releases = [object[]]$rokuHistory
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
        releases = [object[]]$superHistory
    })
}
finally {
    if (Test-Path $tempWorktree) {
        try {
            & git -C $repoRoot worktree remove $tempWorktree --force | Out-Null
        }
        catch {
        }
    }
}
