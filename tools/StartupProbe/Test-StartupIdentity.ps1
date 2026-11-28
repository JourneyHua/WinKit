#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$probe = Join-Path $PSScriptRoot 'Invoke-StartupProbe.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($probe, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Startup probe has parser errors.' }
# Load only the actual pure policy functions, never the launcher or native code.
foreach ($name in @('Get-ExpectedProcessIntegrityRid', 'Test-ExpectedProcessIdentity')) {
    $functions = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $true))
    if ($functions.Count -ne 1) { throw ('Expected one policy function: ' + $name) }
    . ([scriptblock]::Create($functions[0].Extent.Text))
}
$script:checks = 0
function Assert-Policy {
    param([bool] $Condition, [string] $Description)
    $script:checks++
    if (-not $Condition) { throw ('Identity policy assertion failed: ' + $Description) }
}
foreach ($case in @(@(8192, 'Observer', 8192), @(12288, 'Observer', 12288),
    @(8192, 'Medium', 8192), @(12288, 'Medium', 8192))) {
    $actual = Get-ExpectedProcessIntegrityRid -ObserverIntegrityRid $case[0] -Mode $case[1]
    Assert-Policy ($actual -eq $case[2]) ('expected RID for observer ' + $case[0] + ', mode ' + $case[1])
}
foreach ($rid in @(0, 4096, 16384)) {
    $rejected = $false
    try { [void](Get-ExpectedProcessIntegrityRid -ObserverIntegrityRid $rid -Mode Medium) }
    catch { $rejected = $true }
    Assert-Policy $rejected ('Medium policy rejects observer RID ' + $rid)
}
$invalidModeRejected = $false
try { [void](Get-ExpectedProcessIntegrityRid -ObserverIntegrityRid 8192 -Mode Unknown) }
catch { $invalidModeRejected = $true }
Assert-Policy $invalidModeRejected 'unknown integrity policy rejected'
$sid = 'S-1-5-21-1-2-3-1001'
foreach ($case in @(@($sid, 8192, 8192, $true), @($sid, 12288, 12288, $true),
    @($sid, 4096, 8192, $false), @($sid, 12288, 8192, $false),
    @($sid, 16384, 8192, $false), @('S-1-5-21-1-2-3-1002', 8192, 8192, $false))) {
    $identity = [pscustomobject]@{ Sid = $case[0]; IntegrityRid = $case[1] }
    $actual = Test-ExpectedProcessIdentity -Identity $identity -Sid $sid -IntegrityRid $case[2]
    Assert-Policy ($actual -eq $case[3]) ('exact SID/RID match: ' + $case[0] + '/' + $case[1] + ', expected ' + $case[2])
}
Assert-Policy (-not (Test-ExpectedProcessIdentity -Identity $null -Sid $sid -IntegrityRid 8192)) 'missing identity rejected'
$ordinary = [pscustomobject]@{ Sid=$sid; IntegrityRid=8192; IsAppContainer=$false; IsRestricted=$false; RestrictedSids=@() }
Assert-Policy (Test-ExpectedProcessIdentity -Identity $ordinary -Sid $sid -IntegrityRid 8192 -RequireUnrestricted) 'known unrestricted identity accepted'
foreach ($kind in @('appcontainer', 'restricted', 'restricted-sid', 'missing-flags', 'unknown-flag')) {
    $identity = [pscustomobject]@{ Sid=$sid; IntegrityRid=8192; IsAppContainer=$false; IsRestricted=$false; RestrictedSids=@() }
    switch ($kind) {
        'appcontainer' { $identity.IsAppContainer = $true }
        'restricted' { $identity.IsRestricted = $true }
        'restricted-sid' { $identity.RestrictedSids = @('S-1-5-12') }
        'missing-flags' { $identity = [pscustomobject]@{ Sid=$sid; IntegrityRid=8192 } }
        'unknown-flag' { $identity.IsRestricted = 'false' }
    }
    Assert-Policy (-not (Test-ExpectedProcessIdentity -Identity $identity -Sid $sid -IntegrityRid 8192 -RequireUnrestricted)) ('explicit Medium rejects ' + $kind)
}
Write-Output ('Startup identity policy: ' + $script:checks + ' checks passed; no process or token changes.')
