#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $ExecutablePath)

$ErrorActionPreference = 'Stop'
$artifact = Get-Item -LiteralPath $ExecutablePath -Force
if ($artifact.PSIsContainer -or $artifact.Extension -ine '.exe' -or
    ($artifact.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Expected an existing, regular output EXE.'
}

# Only normalize our explicit output file. Never relabel its directory or TEMP.
# A Low-labelled image would lower the child token and prevent bundle extraction
# into the ordinary Medium-integrity TEMP, even with FullControl in its DACL.
$sections = [Security.AccessControl.AccessControlSections]::Access -bor
    [Security.AccessControl.AccessControlSections]::Owner -bor
    [Security.AccessControl.AccessControlSections]::Group
$beforeAccess = (Get-Acl -LiteralPath $artifact.FullName).GetSecurityDescriptorSddlForm($sections)
$beforeHash = (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash
$icacls = Join-Path $env:SystemRoot 'System32/icacls.exe'
& $icacls $artifact.FullName /setintegritylevel medium /Q
if ($LASTEXITCODE -ne 0) {
    throw 'Could not set the output EXE to ordinary Medium integrity. Use an ordinary Medium-integrity build session; no elevation is requested.'
}
$afterAccess = (Get-Acl -LiteralPath $artifact.FullName).GetSecurityDescriptorSddlForm($sections)
$afterHash = (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash
if ($beforeHash -cne $afterHash) {
    throw ('Normalizing the output label changed its bytes. Before SHA256: {0}; after SHA256: {1}' -f $beforeHash, $afterHash)
}
if ($beforeAccess -cne $afterAccess) {
    throw ('Normalizing the output label changed its security descriptor on PowerShell {0}. Before: {1}; after: {2}' -f $PSVersionTable.PSVersion, $beforeAccess, $afterAccess)
}
Write-Host ('Output EXE integrity normalized: ' + $artifact.FullName)
