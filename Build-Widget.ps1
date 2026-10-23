#Requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$root = $PSScriptRoot
# Windows PowerShell 5.1 does not populate PSScriptRoot in parameter defaults.
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'bin'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warn:4 "/out:$outputRoot\GoogleDriveWidget.exe" "/win32manifest:$root\widget.manifest" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:Microsoft.CSharp.dll "$root\MountController.cs" "$root\Widget.cs"
if ($LASTEXITCODE -ne 0) { throw 'Widget build failed.' }
Write-Output "Widget build succeeded: $outputRoot\GoogleDriveWidget.exe"
