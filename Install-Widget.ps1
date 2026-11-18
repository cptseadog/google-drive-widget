#Requires -Version 5.1
#Requires -RunAsAdministrator
# One-time task change. Does not alter cloud files, Google authorization, or cache contents.
[CmdletBinding()]
param([switch]$Restore)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$statusPath = Join-Path $root 'installation-status.json'
$backupPath = Join-Path $root 'original-task.xml'
$securityPath = Join-Path $root 'original-task-security.txt'
$script:taskCredential = $null
$setupLock = $null
$updating = $false
function Set-InstallStatus([string]$state, [string]$message) {
    [ordered]@{ State = $state; Message = $message; UpdatedUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
}
function Save-MountTask($Definition) {
    $expectedAccount = ([Security.Principal.SecurityIdentifier]::new($expectedSid)).Translate([Security.Principal.NTAccount]).Value
    $principalSid = Resolve-AccountSid $Definition.Principal.UserId
    if ($principalSid -ne $expectedSid -or [string]$Definition.Principal.LogonType -ne 'Password') {
        throw 'Unexpected task account or logon type. No task update was attempted.'
    }
    if ($null -eq $script:taskCredential) {
        $script:taskCredential = Get-Credential -UserName $expectedAccount -Message 'Enter the Windows password for the mount task account. Task Scheduler requires the account password to update this task.'
        if ($null -eq $script:taskCredential) { throw 'Credential prompt canceled. No task update was attempted.' }
    }
    $credentialSid = Resolve-AccountSid $script:taskCredential.UserName
    if ($credentialSid -ne $expectedSid) { throw 'Use the Windows account that owns the mount task. No task update was attempted.' }
    if ((Get-ScheduledTask -TaskName $taskName -TaskPath '\').State -ne 'Ready') {
        throw 'The mount task started during setup. Stop the mount normally and retry.'
    }
    # The password goes directly to the Windows task API in this process. Never
    # write it to a file, status message, or child-process command line.
    Set-ScheduledTask -InputObject $Definition -User $expectedAccount -Password $script:taskCredential.GetNetworkCredential().Password | Out-Null
}
function Resolve-AccountSid([string]$Identity) {
    if ($Identity -match '^S-1-') { return ([Security.Principal.SecurityIdentifier]::new($Identity)).Value }
    return ([Security.Principal.NTAccount]::new($Identity)).Translate([Security.Principal.SecurityIdentifier]).Value
}
function Assert-MountAction([string]$Execute, [string]$Arguments) {
    $mount = [regex]::Match($Arguments, '^\s*mount\s+(?:"(?<remote>[^"]+)"|(?<remote>[^\s"]+))\s+(?:"(?<path>[^"]+)"|(?<path>[^\s"]+))(?=\s|$)')
    if ([IO.Path]::GetFileName($Execute) -ne 'rclone.exe' -or
        ($settings.RclonePath -and $Execute -ne $settings.RclonePath) -or
        -not $mount.Success -or $mount.Groups['remote'].Value -cne $settings.ExpectedRemote -or
        $mount.Groups['path'].Value -ne $settings.MountPath) {
        throw 'The task action must match the configured rclone executable, remote, and mount path.'
    }
}
try {
    $settings = Get-Content -LiteralPath (Join-Path $root 'settings.json') -Raw | ConvertFrom-Json
    $taskName = $settings.TaskName
    if ([string]::IsNullOrWhiteSpace($taskName) -or $taskName -match '[\\/]' -or
        $settings.Endpoint -ne 'http://127.0.0.1:5579/' -or
        [string]::IsNullOrWhiteSpace($settings.ExpectedRemote) -or [string]::IsNullOrWhiteSpace($settings.MountPath)) {
        throw 'Invalid widget settings. Run Prepare-Widget.ps1 first.'
    }
    $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop
    if (@($task).Count -ne 1 -or $task.TaskName -ne $taskName) { throw 'Select one task by its exact name.' }
    # Older settings used the mount task principal without recording its SID in settings.json.
    $principalSid = Resolve-AccountSid $task.Principal.UserId
    $expectedSid = if ($settings.AccountSid) { $settings.AccountSid } else { $principalSid }
    if ($principalSid -ne $expectedSid -or [string]$task.Principal.LogonType -ne 'Password') {
        throw 'The task must use the preparing Windows account and the Password logon type.'
    }
    if ($task.Actions.Count -ne 1 -or $task.State -eq 'Disabled') {
        throw 'Select an enabled task with one rclone action.'
    }
    Assert-MountAction $task.Actions[0].Execute $task.Actions[0].Arguments
    if ($task.State -ne 'Ready') { throw 'Save and close Drive files and stop the mount normally before installing or restoring the widget.' }

    $setupLock = [IO.File]::Open((Join-Path $root '.setup.lock'), 'OpenOrCreate', 'Write', 'None')
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    $registered = $service.GetFolder('\').GetTask($taskName)
    if ((Test-Path -LiteralPath $backupPath) -xor (Test-Path -LiteralPath $securityPath)) { throw 'The original-task backup pair is incomplete. No task settings were changed; repair the backup before continuing.' }
    if ($Restore) {
        if (-not (Test-Path -LiteralPath $backupPath)) { throw 'Original task backup is missing.' }
        # Restore only the settings changed by this installer.
        $originalXml = [xml](Get-Content -LiteralPath $backupPath -Raw)
        Assert-MountAction $originalXml.Task.Actions.Exec.Command $originalXml.Task.Actions.Exec.Arguments
        if (($originalXml.Task.RegistrationInfo.URI -and $originalXml.Task.RegistrationInfo.URI -ne ('\' + $taskName)) -or
            (Resolve-AccountSid $originalXml.Task.Principals.Principal.UserId) -ne $expectedSid) {
            throw 'The saved backup belongs to a different task or account.'
        }
        $originalSecurity = Get-Content -LiteralPath $securityPath -Raw
        $null = [Security.AccessControl.RawSecurityDescriptor]::new($originalSecurity)
        $task.Actions[0].Execute = $originalXml.Task.Actions.Exec.Command
        $task.Actions[0].Arguments = $originalXml.Task.Actions.Exec.Arguments
        $task.Actions[0].WorkingDirectory = $originalXml.Task.Actions.Exec.WorkingDirectory
        $task.Description = [string]$originalXml.Task.RegistrationInfo.Description
        $originalTriggers = @($originalXml.Task.Triggers.ChildNodes | Where-Object { $_.NodeType -eq 'Element' })
        if ($originalTriggers.Count -ne $task.Triggers.Count) { throw 'Trigger layout changed since backup; restore requires review.' }
        for ($i = 0; $i -lt $task.Triggers.Count; $i++) { $task.Triggers[$i].Enabled = ([string]$originalTriggers[$i].Enabled -ne 'false') }
        $task.Settings.RestartCount = [int]$originalXml.Task.Settings.RestartOnFailure.Count
        $task.Settings.RestartInterval = [string]$originalXml.Task.Settings.RestartOnFailure.Interval
        Save-MountTask $task
        $updating = $true
        $service.GetFolder('\').GetTask($taskName).SetSecurityDescriptor($originalSecurity, 0)
        Set-InstallStatus 'Restored' 'Original automatic startup has been restored. The mount was not started by this script.'
        exit 0
    }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'GoogleDriveWidget.exe'))) { throw 'Build the widget before installation.' }
    $authPath = Join-Path $root 'private\control.htpasswd'
    if (-not (Test-Path -LiteralPath $authPath) -or -not (Test-Path -LiteralPath $settings.CredentialPath)) {
        throw 'Run Prepare-Widget.ps1 as the mount task owner first.'
    }
    $hasBackup = Test-Path -LiteralPath $backupPath
    $originalTask = if ($hasBackup) { Get-Content -LiteralPath $backupPath -Raw } else { Export-ScheduledTask -TaskName $taskName -TaskPath '\' }
    $originalSecurity = if ($hasBackup) { Get-Content -LiteralPath $securityPath -Raw } else { $registered.GetSecurityDescriptor(7) }
    $original = [xml]$originalTask
    Assert-MountAction $original.Task.Actions.Exec.Command $original.Task.Actions.Exec.Arguments
    if (($original.Task.RegistrationInfo.URI -and $original.Task.RegistrationInfo.URI -ne ('\' + $taskName)) -or
        (Resolve-AccountSid $original.Task.Principals.Principal.UserId) -ne $expectedSid) {
        throw 'The saved task definition belongs to a different task or account.'
    }
    $null = [Security.AccessControl.RawSecurityDescriptor]::new($originalSecurity)
    $originalArgs = [string]$original.Task.Actions.Exec.Arguments
    if ($originalArgs -match '(?:^|\s)--rc(?:[\s=\-]|$)') { throw 'The original task already has remote-control flags.' }
    $widgetArgs = $originalArgs + ' --rc --rc-addr 127.0.0.1:5579 --rc-htpasswd "' + $authPath + '" --vfs-cache-poll-interval 10s'
    if ($task.Actions[0].Arguments -cne $originalArgs -and $task.Actions[0].Arguments -cne $widgetArgs) {
        throw 'The task arguments changed since backup. Review the saved and current settings before installing again.'
    }
    if (-not $hasBackup) {
        [IO.File]::WriteAllText($backupPath, $originalTask, [Text.Encoding]::Unicode)
        try { [IO.File]::WriteAllText($securityPath, $originalSecurity, [Text.Encoding]::UTF8) }
        catch { [IO.File]::Delete($backupPath); throw }
    }

    foreach ($trigger in $task.Triggers) { $trigger.Enabled = $false }
    # Clear both values: leaving an interval with zero retries generates an
    # invalid RestartOnFailure element with Interval but no required Count.
    $task.Settings.RestartCount = 0
    $task.Settings.RestartInterval = $null
    $task.Actions[0].Arguments = $widgetArgs
    $task.Description = 'On-demand Google Drive mount controlled by the desktop widget.'
    Save-MountTask $task
    $updating = $true
    # Give the widget account read/run rights on this task.
    $registered = $service.GetFolder('\').GetTask($taskName)
    $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($registered.GetSecurityDescriptor(7))
    $sid = [Security.Principal.SecurityIdentifier]::new($expectedSid)
    $executeAce = [Security.AccessControl.CommonAce]::new([Security.AccessControl.AceFlags]::None, [Security.AccessControl.AceQualifier]::AccessAllowed, 0x1200A9, $sid, $false, $null)
    $hasExecute = $false
    foreach ($ace in $descriptor.DiscretionaryAcl) { if ($ace.SecurityIdentifier -eq $sid -and $ace.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessAllowed -and ($ace.AccessMask -band 0x1200A9) -eq 0x1200A9) { $hasExecute = $true } }
    if (-not $hasExecute) { $descriptor.DiscretionaryAcl.InsertAce(0, $executeAce); $registered.SetSecurityDescriptor($descriptor.GetSddlForm([Security.AccessControl.AccessControlSections]::All), 0) }
    $verify = Get-ScheduledTask -TaskName $taskName -TaskPath '\'
    if (@($verify.Triggers | Where-Object Enabled).Count -ne 0 -or $verify.Settings.RestartCount -ne 0 -or $verify.State -ne 'Ready' -or
        $verify.Actions[0].Arguments -cne $task.Actions[0].Arguments) { throw 'Final task-state verification failed.' }
    Set-InstallStatus 'Installed' 'On-demand task installed. Google Drive is OFF. Widget startup can now be enabled.'
} catch {
    if ($updating) { Set-InstallStatus 'Failed' $_.Exception.Message }
    Write-Error $_ -ErrorAction Continue
    exit 1
} finally {
    if ($null -ne $setupLock) { $setupLock.Dispose() }
    if ($null -ne $script:taskCredential) {
        $script:taskCredential.Password.Dispose()
        $script:taskCredential = $null
    }
}
