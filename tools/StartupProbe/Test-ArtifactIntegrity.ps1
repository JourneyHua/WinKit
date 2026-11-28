#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$script:checks = 0
$helper = Join-Path $PSScriptRoot 'Set-ArtifactIntegrity.ps1'
$icacls = Join-Path $env:SystemRoot 'System32/icacls.exe'

function Assert-Check([bool] $Condition, [string] $Name) {
    if (-not $Condition) { throw ('FAIL: ' + $Name) }
    $script:checks++
    Write-Host ('PASS: ' + $Name)
}

function Assert-Rejected([string] $Name, [scriptblock] $Action, [string] $MessagePattern) {
    $rejected = $false
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike $MessagePattern) { throw }
        $rejected = $true
    }
    Assert-Check $rejected $Name
}

function Read-FixtureAccess([string] $Path) {
    [Security.AccessControl.RawSecurityDescriptor]::new(
        (Get-Acl -LiteralPath $Path).GetSecurityDescriptorBinaryForm(), 0)
}

function Get-DaclBinary([Security.AccessControl.RawSecurityDescriptor] $Descriptor) {
    if ($null -eq $Descriptor.DiscretionaryAcl) { return $null }
    $bytes = New-Object byte[] $Descriptor.DiscretionaryAcl.BinaryLength
    $Descriptor.DiscretionaryAcl.GetBinaryForm($bytes, 0)
    [Convert]::ToBase64String($bytes)
}

# LABEL_SECURITY_INFORMATION can be read without SeSecurityPrivilege. This only
# reads the temporary fixture; it never requests the audit SACL or changes a token.
if (-not ('WinKit.StartupProbeTests.NativeIntegrity' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace WinKit.StartupProbeTests {
    public static class NativeIntegrity {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetNamedSecurityInfo(
            string name, int objectType, uint securityInfo, out IntPtr owner,
            out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")]
        private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
        public static byte[] ReadLabel(string path) {
            IntPtr owner, group, dacl, sacl, descriptor;
            uint error = GetNamedSecurityInfo(path, 1, 0x10,
                out owner, out group, out dacl, out sacl, out descriptor);
            if (error != 0) throw new Win32Exception((int)error);
            try {
                byte[] bytes = new byte[GetSecurityDescriptorLength(descriptor)];
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                return bytes;
            } finally { LocalFree(descriptor); }
        }
    }
}
'@
}

function Get-FixtureLabel([string] $Path) {
    $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new(
        [WinKit.StartupProbeTests.NativeIntegrity]::ReadLabel($Path), 0)
    # .NET Framework exposes SYSTEM_MANDATORY_LABEL_ACE (0x11) as CustomAce.
    # Its SID follows the four-byte ACE header and four-byte access mask.
    $labels = foreach ($ace in $descriptor.SystemAcl) {
        if ([int]$ace.AceType -eq 0x11) {
            $bytes = New-Object byte[] $ace.BinaryLength
            $ace.GetBinaryForm($bytes, 0)
            [Security.Principal.SecurityIdentifier]::new($bytes, 8).Value
        }
    }
    @($labels) -join ','
}

$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixtureDirectory = Join-Path $tempParent ('WinKit-ArtifactIntegrity-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureDirectory)
try {
    $fixture = Join-Path $fixtureDirectory 'fixture.exe'
    [IO.File]::WriteAllBytes($fixture, [byte[]](77, 90, 0, 1))
    $fixtureBeforeAccess = Read-FixtureAccess $fixture
    $fixtureBeforeHash = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash

    # Dot-source the real helper once so its actual assertion can also receive
    # synthetic negative cases below. Only our four-byte fixture is relabelled.
    . $helper -ExecutablePath $fixture
    $fixtureAfterAccess = Read-FixtureAccess $fixture
    Assert-Check ((Get-FixtureLabel $fixture) -ceq 'S-1-16-8192') 'fresh fixture has an explicit Medium label'
    Assert-Check ($fixtureBeforeHash -ceq (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash) 'fresh label leaves EXE bytes unchanged'
    Assert-Check ((Get-DaclBinary $fixtureBeforeAccess) -ceq (Get-DaclBinary $fixtureAfterAccess)) 'fresh label leaves original DACL bytes unchanged'
    Assert-Check ($fixtureBeforeAccess.Owner -eq $fixtureAfterAccess.Owner -and
        $fixtureBeforeAccess.Group -eq $fixtureAfterAccess.Group) 'fresh label leaves owner and group unchanged'
    Write-Host ('Fixture flags: {0} -> {1}' -f $fixtureBeforeAccess.ControlFlags, $fixtureAfterAccess.ControlFlags)

    & $helper -ExecutablePath $fixture
    Assert-Check ((Get-FixtureLabel $fixture) -ceq 'S-1-16-8192') 'normalization is idempotent'

    & $icacls $fixture /setintegritylevel low /Q
    if ($LASTEXITCODE -ne 0) { throw 'Could not label the temporary fixture Low.' }
    Assert-Check ((Get-FixtureLabel $fixture) -ceq 'S-1-16-4096') 'temporary fixture starts with a real Low label'
    & $helper -ExecutablePath $fixture
    Assert-Check ((Get-FixtureLabel $fixture) -ceq 'S-1-16-8192') 'real Low label is normalized to Medium'

    $baseSddl = 'O:SYG:BAD:(A;ID;FA;;;SY)(A;ID;FR;;;BU)'
    $baseDescriptor = [Security.AccessControl.RawSecurityDescriptor]::new($baseSddl)
    $aiDescriptor = [Security.AccessControl.RawSecurityDescriptor]::new(
        'O:SYG:BAD:AI(A;ID;FA;;;SY)(A;ID;FR;;;BU)')
    Assert-ArtifactUnchanged $fixtureBeforeHash $fixtureBeforeHash $baseDescriptor $aiDescriptor
    Assert-Check $true 'DACL auto-inherited bookkeeping may be added'
    Assert-ArtifactUnchanged $fixtureBeforeHash $fixtureBeforeHash $aiDescriptor $baseDescriptor
    Assert-Check $true 'DACL auto-inherited bookkeeping may be cleared'

    # No real filesystem ACL is changed for these cases. They exercise the same
    # assertion used by the real label operation with independent raw descriptors.
    $negativeSddl = [ordered]@{
        'changed ACE rights' = 'O:SYG:BAD:(A;ID;FA;;;SY)(A;ID;FA;;;BU)'
        'changed ACE order' = 'O:SYG:BAD:(A;ID;FR;;;BU)(A;ID;FA;;;SY)'
        'changed ACE inheritance flag' = 'O:SYG:BAD:(A;ID;FA;;;SY)(A;;FR;;;BU)'
        'changed owner' = 'O:BAG:BAD:(A;ID;FA;;;SY)(A;ID;FR;;;BU)'
        'changed group' = 'O:SYG:SYD:(A;ID;FA;;;SY)(A;ID;FR;;;BU)'
        'protected DACL' = 'O:SYG:BAD:P(A;ID;FA;;;SY)(A;ID;FR;;;BU)'
        'auto-inherit request' = 'O:SYG:BAD:AR(A;ID;FA;;;SY)(A;ID;FR;;;BU)'
    }
    foreach ($name in $negativeSddl.Keys) {
        $changedDescriptor = [Security.AccessControl.RawSecurityDescriptor]::new($negativeSddl[$name])
        Assert-Rejected $name {
            Assert-ArtifactUnchanged $fixtureBeforeHash $fixtureBeforeHash $baseDescriptor $changedDescriptor
        } '*changed its access permissions*'
    }

    foreach ($flag in @(
        [Security.AccessControl.ControlFlags]::OwnerDefaulted,
        [Security.AccessControl.ControlFlags]::GroupDefaulted,
        [Security.AccessControl.ControlFlags]::DiscretionaryAclDefaulted,
        [Security.AccessControl.ControlFlags]::DiscretionaryAclUntrusted,
        [Security.AccessControl.ControlFlags]::ServerSecurity
    )) {
        $changedDescriptor = [Security.AccessControl.RawSecurityDescriptor]::new($baseSddl)
        $changedDescriptor.SetFlags($changedDescriptor.ControlFlags -bor $flag)
        Assert-Rejected ('changed control flag ' + $flag) {
            Assert-ArtifactUnchanged $fixtureBeforeHash $fixtureBeforeHash $baseDescriptor $changedDescriptor
        } '*changed its access permissions*'
    }

    $emptyDacl = [Security.AccessControl.RawSecurityDescriptor]::new('O:SYG:BAD:')
    $nullDacl = [Security.AccessControl.RawSecurityDescriptor]::new('O:SYG:BAD:NO_ACCESS_CONTROL')
    Assert-Rejected 'null DACL differs from an empty DACL' {
        Assert-ArtifactUnchanged $fixtureBeforeHash $fixtureBeforeHash $nullDacl $emptyDacl
    } '*changed its access permissions*'
    $absentDacl = [Security.AccessControl.RawSecurityDescriptor]::new('O:SYG:BA')
    Assert-Rejected 'DACL presence remains significant' {
        Assert-ArtifactUnchanged $fixtureBeforeHash $fixtureBeforeHash $absentDacl $nullDacl
    } '*changed its access permissions*'

    [IO.File]::WriteAllBytes($fixture, [byte[]](77, 90, 0, 2))
    $tamperedHash = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
    Assert-Rejected 'real byte tampering is rejected' {
        Assert-ArtifactUnchanged $fixtureBeforeHash $tamperedHash $baseDescriptor $baseDescriptor
    } '*changed its bytes*'

    Write-Host ('Artifact integrity regression passed: {0} checks on PowerShell {1}.' -f $script:checks, $PSVersionTable.PSVersion)
}
finally {
    $resolvedDirectory = [IO.Path]::GetFullPath($fixtureDirectory)
    if ([IO.Path]::GetDirectoryName($resolvedDirectory) -cne $tempParent -or
        [IO.Path]::GetFileName($resolvedDirectory) -notmatch '^WinKit-ArtifactIntegrity-[0-9a-f]{32}$') {
        throw 'Refusing unexpected fixture cleanup target.'
    }
    Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
}
