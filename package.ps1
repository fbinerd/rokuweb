param(
    [string]$Output = "hello-roku.zip"
)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$zipPath = Join-Path $root $Output
$zipDirectory = Split-Path -Parent $zipPath

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
    if ($relativePath.StartsWith("source/")) { return $true }

    return $false
}

$files = Get-ChildItem -Path $root -Recurse -File | Where-Object {
    Test-IncludedRokuFile -FullName $_.FullName
}

$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)

try {
    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($root.Length + 1).Replace("\", "/")
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relativePath) | Out-Null
    }
}
finally {
    $zip.Dispose()
}

Write-Host "Pacote criado em: $zipPath"
