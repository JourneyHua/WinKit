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
if ($beforeAccess -cne $afterAccess -or
    $beforeHash -cne (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash) {
    throw 'Normalizing the output label unexpectedly changed its bytes or discretionary access permissions.'
}
Write-Host ('Output EXE integrity normalized: ' + $artifact.FullName)
