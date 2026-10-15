$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$testExe = Join-Path $PSScriptRoot 'dist\CoreTests.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /utf8output /codepage:65001 /main:EndfieldSteamBridge.CoreTests /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Management.dll "/resource:$PSScriptRoot\assets\background.png,EndfieldSteamBridge.Background" "/out:$testExe" @sources (Join-Path $PSScriptRoot 'tests\CoreTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

