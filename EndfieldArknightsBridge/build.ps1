$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Requires Windows x64 with .NET Framework 4.8.' }
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$common = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output','/codepage:65001', '/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Web.Extensions.dll','/reference:System.Management.dll', "/resource:$PSScriptRoot\assets\background.png,EndfieldSteamBridge.Background", "/resource:$PSScriptRoot\LICENSE,EndfieldSteamBridge.License", "/win32icon:$PSScriptRoot\assets\EndfieldSteamBridge.ico")
$common += @("/resource:$PSScriptRoot\assets\endfield.png,EndfieldSteamBridge.EndfieldArt", "/resource:$PSScriptRoot\assets\arknights.png,EndfieldSteamBridge.ArknightsArt")
# Steam starts this small asInvoker entry point; only the scheduled helper elevates.
& $compiler @common /define:BRIDGE_FRONTEND,DUAL_GAME "/win32manifest:$PSScriptRoot\bridge.manifest" "/out:$output\EndfieldBridge.exe" @sources
if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
# Ship one manager EXE; extract its embedded frontend when enabling the bridge.
& $compiler @common /define:DUAL_GAME "/win32manifest:$PSScriptRoot\app.manifest" "/resource:$output\EndfieldBridge.exe,EndfieldSteamBridge.Frontend" "/out:$output\EndfieldArknightsBridge.exe" @sources
if ($LASTEXITCODE -ne 0) { throw 'Manager build failed.' }
Write-Output "Built $output\EndfieldArknightsBridge.exe"