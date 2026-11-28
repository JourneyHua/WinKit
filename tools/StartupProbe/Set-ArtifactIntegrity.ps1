#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $ExecutablePath)

$ErrorActionPreference = 'Stop'

function Assert-ArtifactUnchanged {
    param(
        [string] $BeforeHash,
        [string] $AfterHash,
        [Security.AccessControl.RawSecurityDescriptor] $BeforeAccess,
        [Security.AccessControl.RawSecurityDescriptor] $AfterAccess
    )

    if ($BeforeHash -cne $AfterHash) {
        throw ('Normalizing the output label changed its bytes. Before SHA256: {0}; after SHA256: {1}' -f $BeforeHash, $AfterHash)
    }

    # SetLabel can set SE_DACL_AUTO_INHERITED without changing any ACE. Compare
    # the original DACL bytes, owner/group and every other access-related flag.
    # SACL flags describe the integrity label being deliberately changed here.
    $accessFlags = [Security.AccessControl.ControlFlags]::OwnerDefaulted -bor
        [Security.AccessControl.ControlFlags]::GroupDefaulted -bor
        [Security.AccessControl.ControlFlags]::DiscretionaryAclPresent -bor
        [Security.AccessControl.ControlFlags]::DiscretionaryAclDefaulted -bor
        [Security.AccessControl.ControlFlags]::DiscretionaryAclUntrusted -bor
        [Security.AccessControl.ControlFlags]::ServerSecurity -bor
        [Security.AccessControl.ControlFlags]::DiscretionaryAclAutoInheritRequired -bor
        [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected
    $states = foreach ($descriptor in @($BeforeAccess, $AfterAccess)) {
        $dacl = $null
        if ($null -ne $descriptor.DiscretionaryAcl) {
            $bytes = New-Object byte[] $descriptor.DiscretionaryAcl.BinaryLength
            $descriptor.DiscretionaryAcl.GetBinaryForm($bytes, 0)
            $dacl = [Convert]::ToBase64String($bytes)
        }
        [pscustomobject]@{
            Owner = $descriptor.Owner
            Group = $descriptor.Group
            Dacl = $dacl
            Flags = $descriptor.ControlFlags -band $accessFlags
        }
    }
    if ($states[0].Owner -ne $states[1].Owner -or
        $states[0].Group -ne $states[1].Group -or
        $states[0].Dacl -cne $states[1].Dacl -or
        $states[0].Flags -ne $states[1].Flags) {
        $sections = [Security.AccessControl.AccessControlSections]::Access -bor
            [Security.AccessControl.AccessControlSections]::Owner -bor
            [Security.AccessControl.AccessControlSections]::Group
        throw ('Normalizing the output label changed its access permissions on PowerShell {0}. Before flags: {1}; after flags: {2}; before: {3}; after: {4}' -f
            $PSVersionTable.PSVersion, $BeforeAccess.ControlFlags, $AfterAccess.ControlFlags,
            $BeforeAccess.GetSddlForm($sections), $AfterAccess.GetSddlForm($sections))
    }
}

$artifact = Get-Item -LiteralPath $ExecutablePath -Force
if ($artifact.PSIsContainer -or $artifact.Extension -ine '.exe' -or
    ($artifact.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Expected an existing, regular output EXE.'
}

# Only normalize our explicit output file. Never relabel its directory or TEMP.
# A Low-labelled image would lower the child token and prevent bundle extraction
# into the ordinary Medium-integrity TEMP, even with FullControl in its DACL.
$beforeAccess = [Security.AccessControl.RawSecurityDescriptor]::new(
    (Get-Acl -LiteralPath $artifact.FullName).GetSecurityDescriptorBinaryForm(), 0)
$beforeHash = (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash
$icacls = Join-Path $env:SystemRoot 'System32/icacls.exe'
& $icacls $artifact.FullName /setintegritylevel medium /Q
if ($LASTEXITCODE -ne 0) {
    throw 'Could not set the output EXE to ordinary Medium integrity. Use an ordinary Medium-integrity build session; no elevation is requested.'
}
$afterAccess = [Security.AccessControl.RawSecurityDescriptor]::new(
    (Get-Acl -LiteralPath $artifact.FullName).GetSecurityDescriptorBinaryForm(), 0)
$afterHash = (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash
Assert-ArtifactUnchanged -BeforeHash $beforeHash -AfterHash $afterHash -BeforeAccess $beforeAccess -AfterAccess $afterAccess
Write-Host ('Output EXE integrity normalized: ' + $artifact.FullName)
