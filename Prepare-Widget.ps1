#Requires -Version 5.1
# Run as dev. Creates only this widget's settings and a DPAPI-protected local-control password.
$ErrorActionPreference = 'Stop'
$expectedSid = 'S-1-5-21-3637571768-966278147-2091930766-1002'
if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $expectedSid) { throw 'Prepare the widget as dev, not the administrator account.' }
Add-Type -AssemblyName System.Security
$privateDirectory = Join-Path $PSScriptRoot 'private'
New-Item -ItemType Directory -Path $privateDirectory -Force | Out-Null
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @($expectedSid, 'S-1-5-18', 'S-1-5-32-544')) {
    $rule = New-Object Security.AccessControl.FileSystemAccessRule([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $privateDirectory -AclObject $acl
$credentialPath = Join-Path $privateDirectory 'control-password.bin'
$authPath = Join-Path $privateDirectory 'control.htpasswd'
if ((Test-Path -LiteralPath $credentialPath) -xor (Test-Path -LiteralPath $authPath)) { throw 'Incomplete existing credential files. Investigate before replacing them.' }
if (-not (Test-Path -LiteralPath $credentialPath)) {
    $random = New-Object byte[] 48
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($random) } finally { $rng.Dispose() }
    $password = [Convert]::ToBase64String($random)
    $passwordBytes = [Text.Encoding]::UTF8.GetBytes($password)
    try {
        $encrypted = [Security.Cryptography.ProtectedData]::Protect($passwordBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        [IO.File]::WriteAllBytes($credentialPath, $encrypted)
        $sha = [Security.Cryptography.SHA1]::Create()
        try { $hash = [Convert]::ToBase64String($sha.ComputeHash($passwordBytes)) } finally { $sha.Dispose() }
        [IO.File]::WriteAllText($authPath, ('drive-widget:{SHA}' + $hash + "`n"), [Text.Encoding]::ASCII)
    } finally {
        [Array]::Clear($passwordBytes, 0, $passwordBytes.Length)
        [Array]::Clear($random, 0, $random.Length)
        $password = $null
    }
}
$settings = [ordered]@{
    TaskName = 'Rclone Google Drive - shared mount'
    Endpoint = 'http://127.0.0.1:5579/'
    MountPath = 'D:\dev\GoogleDrive'
    LogPath = 'D:\dev\cache\rclone\logs\google-drive.log'
    CredentialPath = $credentialPath
    ExpectedRemote = 'gdrive:'
    Username = 'drive-widget'
}
$settings | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'settings.json') -Encoding UTF8
Write-Output 'Widget settings and protected local-control credentials are ready.'
