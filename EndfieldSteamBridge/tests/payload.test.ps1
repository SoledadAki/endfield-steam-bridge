param([Parameter(Mandatory=$true)][string]$Manager, [Parameter(Mandatory=$true)][string]$Frontend)
$ErrorActionPreference = 'Stop'
if (-not ('BridgeManifestTest' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class BridgeManifestTest {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    public static string Read(string path) {
        IntPtr module = LoadLibraryEx(path, IntPtr.Zero, 2);
        if(module==IntPtr.Zero) throw new Win32Exception();
        try {
            IntPtr resource=FindResource(module,new IntPtr(1),new IntPtr(24));
            if(resource==IntPtr.Zero) throw new Win32Exception();
            byte[] bytes=new byte[SizeofResource(module,resource)];
            Marshal.Copy(LockResource(LoadResource(module,resource)),bytes,0,bytes.Length);
            return Encoding.UTF8.GetString(bytes).Trim('\0','\uFEFF');
        } finally { FreeLibrary(module); }
    }
}
'@
}
foreach($pair in @(@{Path=$Manager;Level='requireAdministrator'},@{Path=$Frontend;Level='asInvoker'})) {
    [xml]$manifest=[BridgeManifestTest]::Read([IO.Path]::GetFullPath($pair.Path))
    if($manifest.SelectSingleNode('//*[local-name()="requestedExecutionLevel"]').level -ne $pair.Level) { throw 'Incorrect permission manifest: '+$pair.Path }
}
$assembly=[Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($Manager))
$resource=$assembly.GetManifestResourceStream('EndfieldSteamBridge.Frontend')
if($null -eq $resource) {throw 'Missing embedded frontend'}
try {
    $buffer=New-Object IO.MemoryStream
    $resource.CopyTo($buffer)
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $hash=[BitConverter]::ToString($sha.ComputeHash($buffer.ToArray())).Replace('-','') }
    finally { $sha.Dispose(); $buffer.Dispose() }
} finally { $resource.Dispose() }
if($hash -ne (Get-FileHash -LiteralPath $Frontend -Algorithm SHA256).Hash) {throw 'Embedded frontend differs from compiled frontend'}
if($assembly.GetManifestResourceNames() -notcontains 'EndfieldSteamBridge.License') {throw 'Missing embedded MIT license'}
Write-Output 'PASS packaged frontend matches; Steam entry is asInvoker; manager/helper requireAdministrator; license embedded'
