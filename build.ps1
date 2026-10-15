$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Requires Windows x64 with .NET Framework 4.8.' }
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output /codepage:65001 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Management.dll "/resource:$PSScriptRoot\assets\background.png,EndfieldSteamBridge.Background" "/win32manifest:$PSScriptRoot\app.manifest" "/out:$output\EndfieldSteamBridge.exe" @sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built $output\EndfieldSteamBridge.exe"

