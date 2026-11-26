#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PublishedProbe,
    [Parameter(Mandatory = $true)] [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$tempRoot = $null
$reportPath = $null
$report = $null
$oldProcess = $null
$newProcess = $null

function Write-GitHubArtifactOutput([string] $directory, [string] $reportFile) {
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        $content = "artifact_directory=$directory
report_path=$reportFile
"
        [System.IO.File]::AppendAllText($env:GITHUB_OUTPUT, $content, [System.Text.UTF8Encoding]::new($false))
    }
    Write-Output "UPDATE_E2E_ARTIFACT_DIRECTORY=$directory"
    Write-Output "UPDATE_E2E_REPORT_PATH=$reportFile"
}

function Save-ObserverReport([string] $path, $value) {
    if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace($path)) {
        $json = ConvertTo-Json -InputObject $value -Depth 10
        [System.IO.File]::WriteAllText($path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    }
}

function Get-IntegrityLevel([int] $rid) {
    if ($rid -ge 0x5000) { return 'Protected' }
    if ($rid -ge 0x4000) { return 'System' }
    if ($rid -ge 0x3000) { return 'High' }
    if ($rid -ge 0x2100) { return 'MediumPlus' }
    if ($rid -ge 0x2000) { return 'Medium' }
    if ($rid -ge 0x1000) { return 'Low' }
    return 'Untrusted'
}

$tokenSource = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public sealed class WinKitProbeProcessIdentity
{
    public string Sid { get; set; }
    public int IntegrityRid { get; set; }

    public static WinKitProbeProcessIdentity Read(int processId)
    {
        IntPtr process = OpenProcess(0x1000, false, (uint)processId);
        if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr token;
            if (!OpenProcessToken(process, 0x0008, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                IntPtr user = ReadInfo(token, 1);
                try
                {
                    IntPtr sidPointer = Marshal.ReadIntPtr(user);
                    IntPtr stringSid;
                    if (!ConvertSidToStringSid(sidPointer, out stringSid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    string sid;
                    try { sid = Marshal.PtrToStringUni(stringSid); }
                    finally { LocalFree(stringSid); }

                    IntPtr integrity = ReadInfo(token, 25);
                    try
                    {
                        IntPtr integritySid = Marshal.ReadIntPtr(integrity);
                        byte count = Marshal.ReadByte(integritySid, 1);
                        if (count == 0) throw new InvalidOperationException("Process integrity SID is invalid.");
                        int rid = Marshal.ReadInt32(integritySid, 8 + (count - 1) * sizeof(int));
                        return new WinKitProbeProcessIdentity { Sid = sid, IntegrityRid = rid };
                    }
                    finally { Marshal.FreeHGlobal(integrity); }
                }
                finally { Marshal.FreeHGlobal(user); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    private static IntPtr ReadInfo(IntPtr token, int kind)
    {
        int length;
        GetTokenInformation(token, kind, IntPtr.Zero, 0, out length);
        if (length <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr buffer = Marshal.AllocHGlobal(length);
        if (!GetTokenInformation(token, kind, buffer, length, out length))
        {
            Marshal.FreeHGlobal(buffer);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return buffer;
    }

    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr information, int length, out int returned);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
'@

try {
    if (-not [Environment]::OSVersion.Platform.ToString().StartsWith('Win')) {
        throw 'The public-update end-to-end probe requires Windows.'
    }
    if ($ExpectedVersion -cnotmatch '\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z') {
        throw 'ExpectedVersion must be a canonical stable major.minor.patch value.'
    }
    $expectedVersion = [version]$ExpectedVersion
    if ($expectedVersion -le [version]'1.11.0') {
        throw 'ExpectedVersion must be newer than the isolated 1.11.0 probe.'
    }

    $source = [System.IO.Path]::GetFullPath($PublishedProbe)
    if ([System.IO.Path]::GetFileName($source) -cne 'WinKit.exe' -or -not [System.IO.File]::Exists($source)) {
        throw "Published single-file probe not found or has the wrong name: $source"
    }
    $probeVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($source).ProductVersion
    if ([string]::IsNullOrWhiteSpace($probeVersion) -or $probeVersion.Split('+')[0] -cne '1.11.0') {
        throw "Probe must be the isolated low-version 1.11.0 executable; got '$probeVersion'."
    }

    $guid = [guid]::NewGuid().ToString('N')
    $tempRoot = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "WinKit-UpdateEndToEndProbe-$guid")
    $appDirectory = [System.IO.Path]::Combine($tempRoot, 'app')
    [void][System.IO.Directory]::CreateDirectory($appDirectory)
    $target = [System.IO.Path]::Combine($appDirectory, 'WinKit.exe')
    [System.IO.File]::Copy($source, $target, $false)
    $reportPath = [System.IO.Path]::Combine($tempRoot, 'report.json')
    Write-GitHubArtifactOutput $tempRoot $reportPath

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $target
    $startInfo.WorkingDirectory = $appDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    [void]$startInfo.ArgumentList.Add('--run')
    [void]$startInfo.ArgumentList.Add($ExpectedVersion)
    [void]$startInfo.ArgumentList.Add($reportPath)
    $oldProcess = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $oldProcess) { throw 'Failed to start the isolated old-version probe.' }

    $oldDeadline = [DateTimeOffset]::UtcNow.AddMinutes(7.25)
    while (-not $oldProcess.HasExited -and [DateTimeOffset]::UtcNow -lt $oldDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not $oldProcess.HasExited) {
        throw 'The old-version probe did not complete within its bounded 7 minute 15 second wait. It was not terminated.'
    }
    $oldProcess.Refresh()
    if ($oldProcess.ExitCode -ne 0) {
        throw "The isolated probe exited with code $($oldProcess.ExitCode); inspect its stderr and report artifact."
    }
    if (-not [System.IO.File]::Exists($reportPath)) { throw 'The probe exited successfully without its required handshake report.' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.SchemaVersion -ne 1 -or $report.ExpectedVersion -cne $ExpectedVersion -or
        $report.ReleaseVersion -cne $ExpectedVersion -or -not $report.ReadyHandshake -or -not $report.ExitAllowed -or
        $report.ProcessId -ne $oldProcess.Id -or
        -not [string]::Equals([System.IO.Path]::GetFullPath($report.TargetPath), $target, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([System.IO.Path]::GetFullPath($report.RunRoot), $tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The probe report did not attest the expected release, isolated target and production Ready/commit handshake.'
    }

    Add-Type -TypeDefinition $tokenSource -Language CSharp
    try {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
    }
    catch {
        throw "Windows UI Automation assemblies are unavailable: $($_.Exception.Message)"
    }

    $newDeadline = [DateTimeOffset]::UtcNow.AddMinutes(1.5)
    $windowTitle = 'WinKit ' + (-join [char[]]@(0x7CFB, 0x7EDF, 0x7BA1, 0x5BB6))
    $successText = 'WinKit ' + (-join [char[]]@(0x5DF2, 0x66F4, 0x65B0, 0x5230, 0x65B0, 0x7248, 0x672C, 0x3002))
    $foundWindow = $false
    $foundSuccess = $false
    $identity = $null
    while ([DateTimeOffset]::UtcNow -lt $newDeadline -and -not $foundSuccess) {
        if (-not $foundWindow) {
            $candidates = [System.Diagnostics.Process]::GetProcessesByName('WinKit')
            foreach ($candidate in $candidates) {
                try {
                    if ($candidate.Id -eq $report.ProcessId -or $candidate.HasExited) { continue }
                    $candidate.Refresh()
                    $imagePath = $candidate.MainModule.FileName
                    if (-not [string]::Equals([System.IO.Path]::GetFullPath($imagePath), $target, [StringComparison]::OrdinalIgnoreCase)) { continue }
                    if ($candidate.StartTime.ToUniversalTime() -lt [DateTime]::Parse($report.ProcessStartedUtc).ToUniversalTime()) { continue }
                    if ($candidate.MainWindowTitle -cne $windowTitle -or $candidate.MainWindowHandle -eq [IntPtr]::Zero) { continue }

                    $candidateVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($imagePath).ProductVersion.Split('+')[0]
                    if ($candidateVersion -cne $ExpectedVersion) { throw "Same-path GUI process is running unexpected version '$candidateVersion'." }
                    $identity = [WinKitProbeProcessIdentity]::Read($candidate.Id)
                    if ($identity.Sid -cne $report.UserSid -or $identity.IntegrityRid -ne [int]$report.IntegrityRid) {
                        throw "Restarted GUI token differs from the old process (SID '$($identity.Sid)', integrity RID $($identity.IntegrityRid))."
                    }
                    $newProcess = $candidate
                    $foundWindow = $true
                }
                catch [System.ComponentModel.Win32Exception] { }
                finally { if ($null -ne $candidate -and $candidate -ne $newProcess) { $candidate.Dispose() } }
            }
        }

        if ($foundWindow -and -not $foundSuccess) {
            $processCondition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $newProcess.Id)
            $windowCondition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Window)
            $condition = [System.Windows.Automation.AndCondition]::new($processCondition, $windowCondition)
            $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Children, $condition)
            foreach ($window in $windows) {
                try {
                    if ($window.Current.Name -cne $windowTitle) { continue }
                    $textCondition = [System.Windows.Automation.PropertyCondition]::new(
                        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                        [System.Windows.Automation.ControlType]::Text)
                    $textElements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
                    foreach ($textElement in $textElements) {
                        try {
                            if ($textElement.Current.Name -ceq $successText -and -not $textElement.Current.IsOffscreen) { $foundSuccess = $true; break }
                        }
                        catch { }
                    }
                }
                catch { }
                if ($foundSuccess) { break }
            }
        }
        if (-not $foundSuccess) { Start-Sleep -Milliseconds 100 }
    }
    if (-not $foundWindow) { throw 'The restarted WinKit main window did not appear within 90 seconds.' }
    if (-not $foundSuccess) { throw "UI Automation did not find the visible success notice '$successText'." }

    $actualHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $report.AssetSha256.ToLowerInvariant() -or
        $actualHash -ceq $report.OldFileSha256.ToLowerInvariant()) {
        throw 'The same-path target hash does not prove replacement by the downloaded release.'
    }
    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($target)
    if ($versionInfo.ProductVersion.Split('+')[0] -cne $ExpectedVersion -or
        $versionInfo.FileMajorPart -ne $expectedVersion.Major -or
        $versionInfo.FileMinorPart -ne $expectedVersion.Minor -or
        $versionInfo.FileBuildPart -ne $expectedVersion.Build) {
        throw "Replaced executable version does not match $ExpectedVersion (product '$($versionInfo.ProductVersion)')."
    }

    $protectionDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    $protectionFiles = @()
    do {
        $protectionFiles = @(Get-ChildItem -LiteralPath $appDirectory -Force -ErrorAction Stop |
            Where-Object { $_.Name -match '^\.winkit-update-[0-9a-f]{32}\.(original|previous|new)$' } |
            ForEach-Object { $_.FullName })
        if ($protectionFiles.Count -eq 0) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $protectionDeadline)
    if ($protectionFiles.Count -gt 0) {
        throw "Updater transaction artifacts were not cleaned: $($protectionFiles -join ', ')"
    }

    $closeRequested = $newProcess.CloseMainWindow()
    if (-not $closeRequested) { throw 'CloseMainWindow did not accept a normal close request for the verified isolated GUI.' }
    $closeDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while (-not $newProcess.HasExited -and [DateTimeOffset]::UtcNow -lt $closeDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not $newProcess.HasExited) { throw 'The verified isolated GUI did not close normally within 20 seconds; it was not terminated.' }

    $report | Add-Member -NotePropertyName ObserverStatus -NotePropertyValue 'passed' -Force
    $report | Add-Member -NotePropertyName NewProcessId -NotePropertyValue $newProcess.Id -Force
    $report | Add-Member -NotePropertyName NewProcessPath -NotePropertyValue $target -Force
    $report | Add-Member -NotePropertyName NewProcessVersion -NotePropertyValue $versionInfo.ProductVersion -Force
    $report | Add-Member -NotePropertyName NewProcessSid -NotePropertyValue $identity.Sid -Force
    $report | Add-Member -NotePropertyName NewProcessIntegrityRid -NotePropertyValue $identity.IntegrityRid -Force
    $report | Add-Member -NotePropertyName NewProcessIntegrityLevel -NotePropertyValue (Get-IntegrityLevel $identity.IntegrityRid) -Force
    $report | Add-Member -NotePropertyName UpdatedFileSha256 -NotePropertyValue $actualHash -Force
    $report | Add-Member -NotePropertyName MainWindowTitle -NotePropertyValue $windowTitle -Force
    $report | Add-Member -NotePropertyName SuccessNotice -NotePropertyValue $successText -Force
    $report | Add-Member -NotePropertyName SuccessNoticeFoundByUIAutomation -NotePropertyValue $true -Force
    $report | Add-Member -NotePropertyName ProtectionFilesRemaining -NotePropertyValue @() -Force
    $report | Add-Member -NotePropertyName MainWindowClosedNormally -NotePropertyValue $true -Force
    $report | Add-Member -NotePropertyName CompletedUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) -Force
    Save-ObserverReport $reportPath $report
    Write-Output "PASS: same-path replacement, SHA256/version, SID/integrity, UI Automation success notice, and protection cleanup verified."
    Write-GitHubArtifactOutput $tempRoot $reportPath
}
catch {
    if ($null -ne $report) {
        $report | Add-Member -NotePropertyName ObserverStatus -NotePropertyValue 'failed' -Force
        $report | Add-Member -NotePropertyName ObserverError -NotePropertyValue $_.Exception.ToString() -Force
        try { Save-ObserverReport $reportPath $report } catch { Write-Warning "Could not update failure report: $($_.Exception.Message)" }
    }
    if ($null -ne $tempRoot) {
        Write-Output "UPDATE_E2E_ARTIFACT_DIRECTORY=$tempRoot"
        Write-Output "UPDATE_E2E_REPORT_PATH=$reportPath"
    }
    Write-Error $_
    exit 1
}
finally {
    if ($null -ne $oldProcess) { $oldProcess.Dispose() }
    if ($null -ne $newProcess) { $newProcess.Dispose() }
}
