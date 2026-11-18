#Requires -Version 5.1
# Exercise only the installer helper with fake credentials and a mocked task writer.
# Does not prompt, register, update, start, or stop any scheduled task.
$ErrorActionPreference = 'Stop'
$errors = $null
$tokens = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-Widget.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$helper = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Save-MountTask' }, $true)
if ($null -eq $helper) { throw 'Credential helper is missing.' }
$parameterSet = (Get-Command ScheduledTasks\Set-ScheduledTask).ParameterSets | Where-Object { $_.Parameters.Name -contains 'InputObject' }
if ($parameterSet.Parameters.Name -notcontains 'User' -or $parameterSet.Parameters.Name -notcontains 'Password') { throw 'Installed task API does not support the required credential parameters.' }
. ([scriptblock]::Create($helper.Extent.Text))
foreach ($name in @('Resolve-AccountSid', 'Assert-MountAction')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if ($null -eq $function) { throw ('Installer helper is missing: ' + $name) }
    . ([scriptblock]::Create($function.Extent.Text))
}
$taskName = 'Test mount task'
$expectedSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$expectedAccount = ([Security.Principal.SecurityIdentifier]::new($expectedSid)).Translate([Security.Principal.NTAccount]).Value
$wrongAccount = ([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')).Translate([Security.Principal.NTAccount]).Value
function Get-ScheduledTask($TaskName, $TaskPath) {
    [pscustomobject]@{ State = if ($script:mode -eq 'running') { 'Running' } else { 'Ready' } }
}
function Get-Credential($UserName, $Message) {
    $script:prompts++
    if ($script:mode -eq 'cancel') { return $null }
    $account = if ($script:mode -eq 'wrong') { $wrongAccount } else { $expectedAccount }
    $fakeSecret = ConvertTo-SecureString 'fake-test-value-not-a-password' -AsPlainText -Force
    return [Management.Automation.PSCredential]::new($account, $fakeSecret)
}
function Set-ScheduledTask($InputObject, $User, $Password) {
    if ($User -ne $expectedAccount -or $Password -ne 'fake-test-value-not-a-password') { throw 'Credential forwarding failed.' }
    $script:writes++
}
function Reset-Test([string]$mode = 'valid') {
    if ($null -ne $script:taskCredential) { $script:taskCredential.Password.Dispose() }
    $script:taskCredential = $null
    $script:prompts = 0
    $script:writes = 0
    $script:mode = $mode
}
function Expect-NoWrite($definition, [string]$reason) {
    $failed = $false
    try { Save-MountTask $definition } catch { $failed = $true }
    if (-not $failed -or $script:writes -ne 0) { throw ('Unsafe task-write path: ' + $reason) }
    Write-Output ('PASS: ' + $reason + ' prevents task updates.')
}
$task = [pscustomobject]@{ Principal = [pscustomobject]@{ UserId = $expectedAccount; LogonType = 'Password' } }
try {
    Reset-Test
    Save-MountTask $task
    Save-MountTask $task
    if ($script:prompts -ne 1 -or $script:writes -ne 2) { throw 'Expected one credential prompt for multiple updates.' }
    Write-Output 'PASS: credentials are prompted once and forwarded on each update.'
    Reset-Test 'cancel'
    Expect-NoWrite $task 'Cancellation'
    Reset-Test 'wrong'
    Expect-NoWrite $task 'Credentials for a different account'
    Reset-Test 'running'
    Expect-NoWrite $task 'Task started during setup'
    Reset-Test
    $task.Principal.UserId = $wrongAccount
    Expect-NoWrite $task 'Unexpected task account'
    if ($script:prompts -ne 0) { throw 'Prompted before checking the task account.' }
    $task.Principal.UserId = $expectedAccount
    $task.Principal.LogonType = 'Interactive'
    Expect-NoWrite $task 'Unexpected task logon type'
    $writers = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Set-ScheduledTask' }, $true)
    $calls = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Save-MountTask' }, $true)
    if ($writers.Count -ne 1 -or $calls.Count -ne 2) { throw 'Not all install/restore updates use the credential helper.' }
    Write-Output 'PASS: install and restore use the checked credential helper.'

    $settings = [pscustomobject]@{ RclonePath = 'C:\Program Files\rclone\rclone.exe'; ExpectedRemote = 'my drive:'; MountPath = 'C:\My Drive' }
    Assert-MountAction $settings.RclonePath 'mount "my drive:" "C:\My Drive" --vfs-cache-mode full'
    foreach ($arguments in @(
        'mount "my drive:" "C:\My Drive Backup" --vfs-cache-mode full',
        'mount "my drive:" C:\My Drive --vfs-cache-mode full'
    )) {
        $rejected = $false
        try { Assert-MountAction $settings.RclonePath $arguments } catch { $rejected = $true }
        if (-not $rejected) { throw ('Unexpected task action accepted: ' + $arguments) }
    }
    Write-Output 'PASS: task action validation accepts quoted paths and rejects incorrect arguments.'
} finally { Reset-Test }
