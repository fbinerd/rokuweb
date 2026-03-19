param(
    [string]$Output = "hello-roku.zip"
)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$zipPath = Join-Path $root $Output

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

$files = Get-ChildItem -Path $root -Recurse -File | Where-Object {
    $_.FullName -ne $zipPath
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
