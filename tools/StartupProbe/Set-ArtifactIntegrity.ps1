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
# Submit LABEL_SECURITY_INFORMATION only. The command-line label tool can also
# reapply inherited DACL entries on some Windows builds; this API does not submit
# owner, group, DACL, or audit-SACL changes. Keep the independent checks below.
if (-not ('WinKit.ArtifactIntegrity.NativeLabel' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace WinKit.ArtifactIntegrity {
    public static class NativeLabel {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
            string value, uint revision, out IntPtr descriptor, out uint size);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSecurityDescriptorSacl(
            IntPtr descriptor, out bool present, out IntPtr sacl, out bool defaulted);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint SetNamedSecurityInfo(
            string name, int objectType, uint securityInfo,
            IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
        public static void SetMedium(string path) {
            IntPtr descriptor;
            uint size;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                "S:(ML;;NW;;;ME)", 1, out descriptor, out size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                bool present, defaulted;
                IntPtr sacl;
                if (!GetSecurityDescriptorSacl(descriptor, out present, out sacl, out defaulted))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!present || sacl == IntPtr.Zero)
                    throw new InvalidOperationException("Missing output integrity label.");
                uint error = SetNamedSecurityInfo(path, 1, 0x10,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
                if (error != 0) throw new Win32Exception((int)error);
            } finally { LocalFree(descriptor); }
        }
    }
}
'@
}
[WinKit.ArtifactIntegrity.NativeLabel]::SetMedium($artifact.FullName)
$afterAccess = [Security.AccessControl.RawSecurityDescriptor]::new(
    (Get-Acl -LiteralPath $artifact.FullName).GetSecurityDescriptorBinaryForm(), 0)
$afterHash = (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash
Assert-ArtifactUnchanged -BeforeHash $beforeHash -AfterHash $afterHash -BeforeAccess $beforeAccess -AfterAccess $afterAccess
Write-Host ('Output EXE integrity normalized: ' + $artifact.FullName)
