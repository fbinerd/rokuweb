param(
    [string]$BaseRef = "HEAD~1",
    [string]$OutputDirectory = "dist"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repoRoot $OutputDirectory
$superRoot = Join-Path $repoRoot "super"
$superReleaseRoot = Join-Path $superRoot "src\WindowManager.App\bin\Release\net481"
$tempWorktree = Join-Path ([System.IO.Path]::GetTempPath()) ("rokuweb-release-base-" + [Guid]::NewGuid().ToString("N"))

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
        foreach ($file in $Files) {
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

    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot "package.ps1") -Output (Join-Path $OutputDirectory ("rokuweb-{0}-full.zip" -f $releaseId))
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao gerar pacote completo do rokuweb."
    }

    $superFullZip = Join-Path $outputRoot ("super-{0}-full.zip" -f $releaseId)
    New-ZipFromDirectory -DirectoryPath $superReleaseRoot -ZipPath $superFullZip

    $rokuFiles = Get-RokuPackageFiles -Root $repoRoot
    $rokuCurrentMap = Get-HashDictionary -Root $repoRoot -Files $rokuFiles
    $superCurrentFiles = Get-SuperPackageFiles -Root $superReleaseRoot
    $superCurrentMap = Get-HashDictionary -Root $superReleaseRoot -Files $superCurrentFiles

    $previousVersion = ""
    $previousReleaseId = ""
    $rokuDeltaZip = $null
    $superDeltaZip = $null
    $rokuChanged = @()
    $rokuDeleted = @()
    $superChanged = @()
    $superDeleted = @()

    $canCreateDelta = $true
    try {
        & git -C $repoRoot rev-parse --verify $BaseRef *> $null
        if ($LASTEXITCODE -ne 0) {
            $canCreateDelta = $false
        }
    }
    catch {
        $canCreateDelta = $false
    }

    if ($canCreateDelta) {
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

        $rokuDeltaFiles = $rokuChanged | ForEach-Object { Join-Path $repoRoot $_ }
        $rokuDeltaZip = Join-Path $outputRoot ("rokuweb-{0}-delta-from-{1}.zip" -f $releaseId, $previousReleaseId)
        New-ZipFromFiles -Root $repoRoot -Files $rokuDeltaFiles -ZipPath $rokuDeltaZip

        $superDeltaFiles = $superChanged | ForEach-Object { Join-Path $superReleaseRoot $_ }
        $superDeltaZip = Join-Path $outputRoot ("super-{0}-delta-from-{1}.zip" -f $releaseId, $previousReleaseId)
        New-ZipFromFiles -Root $superReleaseRoot -Files $superDeltaFiles -ZipPath $superDeltaZip
    }

    Write-JsonFile -Path (Join-Path $outputRoot ("rokuweb-{0}-changes.json" -f $releaseId)) -Data ([pscustomobject]@{
        app = "rokuweb"
        version = $rokuVersion
        releaseId = $releaseId
        previousVersion = $previousVersion
        previousReleaseId = $previousReleaseId
        baseRef = $BaseRef
        generatedAtUtc = $generatedAtUtc
        fullPackage = "rokuweb-$releaseId-full.zip"
        deltaPackage = if ($rokuDeltaZip) { Split-Path -Leaf $rokuDeltaZip } else { $null }
        deltaSupportedFromVersions = if ($previousVersion) { [object[]]@($previousVersion) } else { [object[]]@() }
        deltaSupportedFromReleases = if ($previousReleaseId) { [object[]]@($previousReleaseId) } else { [object[]]@() }
        fullPackageRequiredIfCurrentVersionOlderThan = $previousVersion
        fullPackageRequiredIfCurrentReleaseOlderThan = $previousReleaseId
        changedFiles = @($rokuChanged)
        deletedFiles = @($rokuDeleted)
    })

    Write-JsonFile -Path (Join-Path $outputRoot ("super-{0}-changes.json" -f $releaseId)) -Data ([pscustomobject]@{
        app = "super"
        version = $rokuVersion
        releaseId = $releaseId
        previousVersion = $previousVersion
        previousReleaseId = $previousReleaseId
        baseRef = $BaseRef
        generatedAtUtc = $generatedAtUtc
        fullPackage = "super-$releaseId-full.zip"
        deltaPackage = if ($superDeltaZip) { Split-Path -Leaf $superDeltaZip } else { $null }
        deltaSupportedFromVersions = if ($previousVersion) { [object[]]@($previousVersion) } else { [object[]]@() }
        deltaSupportedFromReleases = if ($previousReleaseId) { [object[]]@($previousReleaseId) } else { [object[]]@() }
        fullPackageRequiredIfCurrentVersionOlderThan = $previousVersion
        fullPackageRequiredIfCurrentReleaseOlderThan = $previousReleaseId
        changedFiles = @($superChanged)
        deletedFiles = @($superDeleted)
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
