#Requires -Version 5.1
# Run as dev after the administrator task update succeeded.
$ErrorActionPreference = 'Stop'
if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-21-3637571768-966278147-2091930766-1002') { throw 'Run this as dev.' }
$install = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installation-status.json') -Raw | ConvertFrom-Json
if ($install.State -ne 'Installed') { throw 'Complete the task installation first.' }
$shell = New-Object -ComObject WScript.Shell
foreach ($directory in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Startup'))) {
    $shortcut = $shell.CreateShortcut((Join-Path $directory 'Google Drive Widget.lnk'))
    $shortcut.TargetPath = Join-Path $PSScriptRoot 'GoogleDriveWidget.exe'
    $shortcut.WorkingDirectory = $PSScriptRoot
    $shortcut.Description = 'Show the Google Drive on/off widget. Opening this widget does not mount Drive.'
    $shortcut.Save()
}
Write-Output 'Desktop and sign-in shortcuts created. They show the widget without mounting Drive.'
