#Requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot 'assets' }
Add-Type -AssemblyName System.Drawing
Add-Type -Path (Join-Path $PSScriptRoot 'WidgetLogo.cs') -ReferencedAssemblies System.Drawing
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = [WidgetLogo]::Render($size)
    $stream = New-Object IO.MemoryStream
    try {
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $frames += ,$stream.ToArray()
        if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'GoogleDriveWidget.png'), $stream.ToArray()) }
    } finally { $stream.Dispose(); $bitmap.Dispose() }
}
# ICO directory followed by PNG-compressed, full-alpha frames (Windows Vista+).
$file = [IO.File]::Create((Join-Path $OutputDirectory 'GoogleDriveWidget.ico'))
$writer = New-Object IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output 'Generated application icon (16-256 px) and PNG preview.'