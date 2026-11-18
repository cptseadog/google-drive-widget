#Requires -Version 5.1
# Run as the mount task owner after installation succeeds.
$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'settings.json') -Raw | ConvertFrom-Json
$accountSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($settings.AccountSid -and $settings.AccountSid -ne $accountSid) { throw 'Run this as the account that prepared the widget.' }
$install = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installation-status.json') -Raw | ConvertFrom-Json
if ($install.State -ne 'Installed') { throw 'Complete the task installation first.' }
Add-Type -AssemblyName System.Security
# Prove that the current account can read the credential, including older settings without an AccountSid.
$bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($settings.CredentialPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
[Array]::Clear($bytes, 0, $bytes.Length)
$shell = New-Object -ComObject WScript.Shell
foreach ($directory in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Startup'))) {
    $shortcut = $shell.CreateShortcut((Join-Path $directory 'Google Drive Widget.lnk'))
    $shortcut.TargetPath = Join-Path $PSScriptRoot 'GoogleDriveWidget.exe'
    $shortcut.IconLocation = (Join-Path $PSScriptRoot 'GoogleDriveWidget.ico') + ',0'
    $shortcut.WorkingDirectory = $PSScriptRoot
    $shortcut.Description = 'Show the Google Drive on/off widget. Opening this widget does not mount Drive.'
    $shortcut.Save()
}
Write-Output 'Desktop and sign-in shortcuts created. They show the widget without mounting Drive.'
