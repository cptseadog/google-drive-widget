#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The x64 .NET Framework C# compiler is required.' }
$output = Join-Path $PSScriptRoot 'bin\tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null

foreach ($test in @('Test-MountController', 'Test-ControlEndpoint')) {
    $executable = Join-Path $output ($test + '.exe')
    & $compiler /nologo /target:exe /platform:x64 /warn:4 "/out:$executable" /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:Microsoft.CSharp.dll "$PSScriptRoot\MountController.cs" "$PSScriptRoot\$test.cs"
    if ($LASTEXITCODE -ne 0) { throw "Could not compile $test." }
    & $executable
    if ($LASTEXITCODE -ne 0) { throw "$test failed." }
}

& (Join-Path $PSScriptRoot 'Test-InstallerCredentials.ps1')
