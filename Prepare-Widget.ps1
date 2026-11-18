#Requires -Version 5.1
# Run as the Windows account that owns the mount task.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$TaskName,
    [Parameter(Mandatory)][ValidatePattern('^[\w. -]+:$')][string]$Remote,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z]:\\')][string]$MountPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z]:\\')][string]$LogPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z]:\\')][string]$RclonePath
)
$ErrorActionPreference = 'Stop'
$accountSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($TaskName -match '[\\/]' -or [string]::IsNullOrWhiteSpace($TaskName)) { throw 'Select a task in the root Task Scheduler Library.' }
if ([IO.Path]::GetFileName($RclonePath) -ne 'rclone.exe' -or -not (Test-Path -LiteralPath $RclonePath -PathType Leaf)) {
    throw 'RclonePath must point to an existing rclone.exe.'
}

$settingsPath = Join-Path $PSScriptRoot 'settings.json'
$settings = [ordered]@{
    TaskName = $TaskName
    AccountSid = $accountSid
    RclonePath = $RclonePath
    Endpoint = 'http://127.0.0.1:5579/'
    MountPath = $MountPath
    LogPath = $LogPath
    CredentialPath = Join-Path $PSScriptRoot 'private\control-password.bin'
    ExpectedRemote = $Remote
    Username = 'drive-widget'
}
if (Test-Path -LiteralPath $settingsPath) {
    $existing = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    foreach ($key in $settings.Keys) {
        if ($existing.PSObject.Properties[$key] -and $existing.$key -cne $settings[$key]) {
            throw 'This folder already has different widget settings. Use a new runtime folder for another mount.'
        }
    }
}

Add-Type -AssemblyName System.Security
$privateDirectory = Join-Path $PSScriptRoot 'private'
$credentialPath = $settings.CredentialPath
$authPath = Join-Path $privateDirectory 'control.htpasswd'
$hasCredential = Test-Path -LiteralPath $credentialPath
if ($hasCredential -xor (Test-Path -LiteralPath $authPath)) { throw 'Incomplete existing credential files. Investigate before replacing them.' }
if ($hasCredential) {
    # Check ownership before updating the directory ACL, including when upgrading older settings.
    $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($credentialPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [Array]::Clear($bytes, 0, $bytes.Length)
}
New-Item -ItemType Directory -Path $privateDirectory -Force | Out-Null
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @($accountSid, 'S-1-5-18', 'S-1-5-32-544')) {
    $rule = New-Object Security.AccessControl.FileSystemAccessRule([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $privateDirectory -AclObject $acl
if (-not $hasCredential) {
    $random = New-Object byte[] 48
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($random) } finally { $rng.Dispose() }
    $password = [Convert]::ToBase64String($random)
    $passwordBytes = [Text.Encoding]::UTF8.GetBytes($password)
    try {
        $encrypted = [Security.Cryptography.ProtectedData]::Protect($passwordBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $sha = [Security.Cryptography.SHA1]::Create()
        try { $hash = [Convert]::ToBase64String($sha.ComputeHash($passwordBytes)) } finally { $sha.Dispose() }
        [IO.File]::WriteAllText($authPath, ($settings.Username + ':{SHA}' + $hash + "`n"), [Text.Encoding]::ASCII)
        try { [IO.File]::WriteAllBytes($credentialPath, $encrypted) }
        catch { [IO.File]::Delete($authPath); throw }
    } finally {
        [Array]::Clear($passwordBytes, 0, $passwordBytes.Length)
        [Array]::Clear($random, 0, $random.Length)
        $password = $null
    }
}
$settings | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8
Write-Output 'Widget settings and protected local-control credentials are ready.'
