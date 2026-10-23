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
$expectedAccount = ([Security.Principal.SecurityIdentifier]::new('S-1-5-21-3637571768-966278147-2091930766-1002')).Translate([Security.Principal.NTAccount]).Value
$wrongAccount = ([Security.Principal.SecurityIdentifier]::new('S-1-5-21-3637571768-966278147-2091930766-1000')).Translate([Security.Principal.NTAccount]).Value
function Set-InstallStatus($state, $message) { }
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
    Expect-NoWrite $task 'Administrator credentials instead of dev credentials'
    Reset-Test
    $task.Principal.UserId = $wrongAccount
    Expect-NoWrite $task 'Unexpected task account'
    if ($script:prompts -ne 0) { throw 'Prompted before checking the task account.' }
    $task.Principal.UserId = $expectedAccount
    $task.Principal.LogonType = 'Interactive'
    Expect-NoWrite $task 'Unexpected task logon type'
    $writers = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Set-ScheduledTask' }, $true)
    $calls = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Save-MountTask' }, $true)
    if ($writers.Count -ne 1 -or $calls.Count -ne 3) { throw 'Not all install/restore updates use the credential helper.' }
    Write-Output 'PASS: all three install/restore update sites use the checked credential helper.'
} finally { Reset-Test }