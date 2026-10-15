$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'test.ps1')
$versionSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\Models.cs') -Raw
if ($versionSource -notmatch 'const string Version = "([0-9.]+)"') { throw 'Version missing.' }
$version = $Matches[1]
$dist = Join-Path $PSScriptRoot 'dist'
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ('EndfieldBridge-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $binary = Join-Path $stage 'binary'
    $source = Join-Path $stage 'source'
    New-Item -ItemType Directory -Path $binary,$source | Out-Null
    Copy-Item -LiteralPath (Join-Path $dist 'EndfieldSteamBridge.exe') -Destination $binary
    foreach ($name in @('README.md','LICENSE')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $binary }
    foreach ($name in @('src','tests','assets','app.manifest','build.ps1','test.ps1','package.ps1','README.md','LICENSE','.gitignore','EndfieldSteamBridge.csproj')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $source -Recurse
    }
    $binaryZip = Join-Path $dist "EndfieldSteamBridge-$version-win-x64.zip"
    $sourceZip = Join-Path $dist "EndfieldSteamBridge-$version-source.zip"
    Compress-Archive -Path (Join-Path $binary '*') -DestinationPath $binaryZip -Force
    # Explicit hidden-file inclusion preserves .gitignore on Windows PowerShell.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $sourceZip) { Remove-Item -LiteralPath $sourceZip }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($source, $sourceZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $lines = foreach ($file in @($binaryZip,$sourceZip)) {
        $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
        $hash.Hash.ToLowerInvariant() + '  ' + [System.IO.Path]::GetFileName($file)
    }
    $lines | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
    Get-Item -LiteralPath $binaryZip,$sourceZip | Select-Object Name,Length
}
finally {
    $resolved = [System.IO.Path]::GetFullPath($stage)
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolved.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and [System.IO.Path]::GetFileName($resolved).StartsWith('EndfieldBridge-package-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
