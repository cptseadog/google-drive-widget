#Requires -Version 5.1
#Requires -RunAsAdministrator
# One-time task change. Does not alter cloud files, Google authorization, or cache contents.
[CmdletBinding()]
param([switch]$Restore)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$taskName = 'Rclone Google Drive - shared mount'
$statusPath = Join-Path $root 'installation-status.json'
$backupPath = Join-Path $root 'original-task.xml'
$securityPath = Join-Path $root 'original-task-security.txt'
$script:taskCredential = $null
function Set-InstallStatus([string]$state, [string]$message) {
    [ordered]@{ State = $state; Message = $message; UpdatedUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
}
function Save-MountTask($Definition) {
    $expectedSid = 'S-1-5-21-3637571768-966278147-2091930766-1002'
    $expectedAccount = ([Security.Principal.SecurityIdentifier]::new($expectedSid)).Translate([Security.Principal.NTAccount]).Value
    $principalSid = ([Security.Principal.NTAccount]::new([string]$Definition.Principal.UserId)).Translate([Security.Principal.SecurityIdentifier]).Value
    if ($principalSid -ne $expectedSid -or [string]$Definition.Principal.LogonType -ne 'Password') {
        throw 'Unexpected task account or logon type. No task update was attempted.'
    }
    if ($null -eq $script:taskCredential) {
        Set-InstallStatus 'AwaitingCredentials' ('Enter the Windows password for ' + $expectedAccount + ' in the credential prompt. This is separate from the administrator approval.')
        $script:taskCredential = Get-Credential -UserName $expectedAccount -Message 'Google Drive widget setup: enter the dev Windows account password (not a Windows Hello PIN). Task Scheduler requires this to update its existing password-logon task.'
        if ($null -eq $script:taskCredential) { throw 'Credential prompt canceled. No task update was attempted.' }
    }
    $credentialSid = ([Security.Principal.NTAccount]::new($script:taskCredential.UserName)).Translate([Security.Principal.SecurityIdentifier]).Value
    if ($credentialSid -ne $expectedSid) { throw 'Use the dev account password, not the administrator account. No task update was attempted.' }
    # The password goes directly to the Windows task API in this process. Never
    # write it to a file, status message, or child-process command line.
    Set-ScheduledTask -InputObject $Definition -User $expectedAccount -Password $script:taskCredential.GetNetworkCredential().Password | Out-Null
}
function Tail-Log {
    $stream = [IO.File]::Open('D:\dev\cache\rclone\logs\google-drive.log', [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $null = $stream.Seek([Math]::Max(0, $stream.Length - 32768), [IO.SeekOrigin]::Begin)
        $reader = New-Object IO.StreamReader($stream)
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}
try {
    Set-InstallStatus 'Preparing' 'Inspecting the existing task.'
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    $registered = $service.GetFolder('\').GetTask($taskName)
    if ((Test-Path -LiteralPath $backupPath) -xor (Test-Path -LiteralPath $securityPath)) { throw 'The original-task backup pair is incomplete. No task settings were changed; repair the backup before continuing.' }
    if ($Restore) {
        if (-not (Test-Path -LiteralPath $backupPath)) { throw 'Original task backup is missing.' }
        # Restores the original task settings using the same dev account.
        $originalXml = [xml](Get-Content -LiteralPath $backupPath -Raw)
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
        $registered.SetSecurityDescriptor((Get-Content -LiteralPath $securityPath -Raw), 0)
        Set-InstallStatus 'Restored' 'Original automatic startup has been restored. The mount was not started by this script.'
        exit 0
    }
    if ($task.Actions.Count -ne 1 -or $task.Actions[0].Execute -ne 'D:\dev\Program Files\rclone\rclone.exe' -or $task.Actions[0].Arguments -notlike 'mount gdrive:*') { throw 'Unexpected task action; no changes were made.' }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'GoogleDriveWidget.exe'))) { throw 'Build the widget before installation.' }
    $authPath = Join-Path $root 'private\control.htpasswd'
    if (-not (Test-Path -LiteralPath $authPath)) { throw 'Prepare protected credentials as dev first.' }
    if (-not (Test-Path -LiteralPath $backupPath)) {
        Export-ScheduledTask -TaskName $taskName | Set-Content -LiteralPath $backupPath -Encoding Unicode
        $registered.GetSecurityDescriptor(7) | Set-Content -LiteralPath $securityPath -Encoding UTF8
    }
    $original = [xml](Get-Content -LiteralPath $backupPath -Raw)
    $null = [Security.AccessControl.RawSecurityDescriptor]::new((Get-Content -LiteralPath $securityPath -Raw))
    $originalArgs = [string]$original.Task.Actions.Exec.Arguments
    if ($originalArgs -match '--rc(?:\s|=|$)') { throw 'The original task already has a control API; investigate before changing it.' }
    # Block startup and automatic retry first, while preserving the existing mount until idle.
    foreach ($trigger in $task.Triggers) { $trigger.Enabled = $false }
    # Clear both values: leaving an interval with zero retries generates an
    # invalid RestartOnFailure element with Interval but no required Count.
    $task.Settings.RestartCount = 0
    $task.Settings.RestartInterval = $null
    Save-MountTask $task
    if ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running') {
        Set-InstallStatus 'WaitingForIdle' 'Waiting for a new cache report with no active files or pending uploads. Save and close Drive files.'
        $waitStarted = Get-Date
        $deadline = $waitStarted.AddSeconds(100)
        $idle = $false
        do {
            $matches = [regex]::Matches((Tail-Log), '(?m)^(?<time>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}) INFO\s*: .*vfs cache: cleaned: objects \d+ \(was \d+\) in use (?<use>\d+), to upload (?<queued>\d+), uploading (?<uploading>\d+),')
            if ($matches.Count -gt 0) {
                $last = $matches[$matches.Count - 1]
                $stamp = [DateTime]::ParseExact($last.Groups['time'].Value, 'yyyy/MM/dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
                if ($stamp -gt $waitStarted -and [int]$last.Groups['use'].Value -eq 0 -and [int]$last.Groups['queued'].Value -eq 0 -and [int]$last.Groups['uploading'].Value -eq 0) { $idle = $true; break }
            }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $deadline)
        if (-not $idle) { throw 'Drive still has file activity or a fresh idle report was unavailable. Startup is disabled; the current mount was left running. Close Drive files, then retry installation.' }
        Stop-ScheduledTask -TaskName $taskName
        $stopDeadline = (Get-Date).AddSeconds(15)
        do { Start-Sleep -Milliseconds 500; $stillRunning = (Get-ScheduledTask -TaskName $taskName).State -eq 'Running' } while ($stillRunning -and (Get-Date) -lt $stopDeadline)
        if ($stillRunning) { throw 'The old mount did not stop. No replacement was launched.' }
    }
    # Do not create, remove, enumerate, or clear the mount directory.
    $task = Get-ScheduledTask -TaskName $taskName
    $task.Actions[0].Arguments = $originalArgs + ' --rc --rc-addr 127.0.0.1:5579 --rc-htpasswd "' + $authPath + '" --vfs-cache-poll-interval 10s'
    $task.Description = 'On-demand Google Drive mount controlled by the desktop widget. Startup triggers and failure retries are disabled. Uses the existing dev account, authorization, shared permissions and cache.'
    Save-MountTask $task
    # Give dev read/run rights on this one task. No permission to rewrite its definition.
    $registered = $service.GetFolder('\').GetTask($taskName)
    $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($registered.GetSecurityDescriptor(7))
    $sid = [Security.Principal.SecurityIdentifier]::new('S-1-5-21-3637571768-966278147-2091930766-1002')
    $executeAce = [Security.AccessControl.CommonAce]::new([Security.AccessControl.AceFlags]::None, [Security.AccessControl.AceQualifier]::AccessAllowed, 0x1200A9, $sid, $false, $null)
    $hasExecute = $false
    foreach ($ace in $descriptor.DiscretionaryAcl) { if ($ace.SecurityIdentifier -eq $sid -and $ace.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessAllowed -and ($ace.AccessMask -band 0x1200A9) -eq 0x1200A9) { $hasExecute = $true } }
    if (-not $hasExecute) { $descriptor.DiscretionaryAcl.InsertAce(0, $executeAce); $registered.SetSecurityDescriptor($descriptor.GetSddlForm([Security.AccessControl.AccessControlSections]::All), 0) }
    $verify = Get-ScheduledTask -TaskName $taskName
    if (@($verify.Triggers | Where-Object Enabled).Count -ne 0 -or $verify.Settings.RestartCount -ne 0 -or $verify.State -eq 'Running') { throw 'Final task-state verification failed.' }
    Set-InstallStatus 'Installed' 'On-demand task installed. Google Drive is OFF. Widget startup can now be enabled.'
} catch {
    Set-InstallStatus 'Failed' $_.Exception.Message
    exit 1
} finally {
    if ($null -ne $script:taskCredential) {
        $script:taskCredential.Password.Dispose()
        $script:taskCredential = $null
    }
}
