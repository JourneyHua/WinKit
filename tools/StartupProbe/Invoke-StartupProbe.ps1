#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position = 0)] [string] $ExecutablePath,
    [string] $ExpectedVersion,
    [string] $ReportPath,
    [ValidateRange(5, 90)] [int] $TimeoutSeconds = 90,
    [switch] $RequireMediumIntegrity,
    [ValidateSet('Observer', 'Medium')] [string] $ExpectedProcessIntegrity = 'Observer',
    [switch] $Help
)

if ($Help -or $ExecutablePath -eq '--help') {
    Write-Output @'
Observe a real WinKit EXE startup without application arguments or environment overrides.
Usage: powershell -NoProfile -File Invoke-StartupProbe.ps1 -ExecutablePath <exe>
       [-ExpectedVersion <major.minor.patch>] [-ReportPath <json>]
       [-TimeoutSeconds <5..90>] [-RequireMediumIntegrity]
       [-ExpectedProcessIntegrity <Observer|Medium>]
The observer invokes only the overview navigation button, then closes its own process.
Default reports: dist/startup-probe/<unique-id>/report.json beneath the repository.
Exit code: 0 = startup, UI interaction, stability and normal close passed; 1 = failed.
'@
    exit 0
}

$ErrorActionPreference = 'Stop'
$process = $null
$capture = $null
$launchedStartTicks = $null
$expectedPath = $null
$observerIdentity = $null
$expectedProcessIntegrityRid = $null
$windowWork = $null
$probeStarted = [DateTimeOffset]::UtcNow
$report = [ordered]@{
    SchemaVersion = 1
    Status = 'failed'
    StartedUtc = $probeStarted.ToString('o')
    FinishedUtc = $null
    PowerShellVersion = $PSVersionTable.PSVersion.ToString()
    ExecutablePath = $ExecutablePath
    Sha256 = $null
    ProductVersion = $null
    FileVersion = $null
    ExpectedVersion = $ExpectedVersion
    Arguments = @()
    UseShellExecute = $false
    EnvironmentOverrides = @{}
    InheritedEnvironment = [ordered]@{}
    RequireMediumIntegrity = [bool]$RequireMediumIntegrity
    ExpectedProcessIntegrity = $ExpectedProcessIntegrity
    ExpectedProcessIntegrityRid = $null
    ObserverIdentity = $null
    ProcessIdentity = $null
    ProcessId = $null
    ProcessStartedUtc = $null
    WindowTimeoutSeconds = $TimeoutSeconds
    StableSecondsRequired = 3
    StableSecondsObserved = 0
    OverviewGroupInvoked = $false
    OverviewInvoked = $false
    Window = $null
    LastWindowObservation = $null
    NormalCloseRequested = $false
    NormalCloseSucceeded = $false
    ExitCode = $null
    ExitCodeHex = $null
    Stdout = ''
    Stderr = ''
    StreamEncoding = 'UTF-8'
    StdoutTruncated = $false
    StderrTruncated = $false
    CaptureError = $null
    ForcedTermination = $false
    Cleanup = 'not-needed'
    Error = $null
    ReportPath = $null
}

# No child environment is changed: inherited values are evidence, not replacements.
foreach ($name in @('TEMP', 'TMP', 'DOTNET_BUNDLE_EXTRACT_BASE_DIR', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT_X86', 'DOTNET_DISABLE_GUI_ERRORS')) {
    $report.InheritedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

# Keep this script ASCII so Windows PowerShell 5.1 also reads it without a BOM.
$nativeSource = @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WinKit.StartupProbe {
    public sealed class Identity {
        public string Sid { get; set; }
        public int IntegrityRid { get; set; }
        public string IntegrityLevel { get; set; }
        public string[] RestrictedSids { get; set; }
        public bool IsRestricted { get; set; }
        public int ElevationType { get; set; }
        public bool IsAppContainer { get; set; }
    }
    public static class Native {
        public static Identity ReadIdentity(int processId) {
            IntPtr process = OpenProcess(0x1000, false, (uint)processId);
            if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                IntPtr token;
                if (!OpenProcessToken(process, 0x0008, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
                try {
                    string sid;
                    IntPtr user = ReadTokenInfo(token, 1);
                    try {
                        IntPtr text;
                        if (!ConvertSidToStringSid(Marshal.ReadIntPtr(user), out text)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        try { sid = Marshal.PtrToStringUni(text); }
                        finally { LocalFree(text); }
                    } finally { Marshal.FreeHGlobal(user); }
                    IntPtr integrity = ReadTokenInfo(token, 25);
                    try {
                        IntPtr integritySid = Marshal.ReadIntPtr(integrity);
                        byte count = Marshal.ReadByte(integritySid, 1);
                        if (count == 0) throw new InvalidOperationException("Invalid integrity SID.");
                        int rid = Marshal.ReadInt32(integritySid, 8 + (count - 1) * sizeof(int));
                        string level = rid >= 0x5000 ? "Protected" : rid >= 0x4000 ? "System" : rid >= 0x3000 ? "High" :
                            rid >= 0x2100 ? "MediumPlus" : rid >= 0x2000 ? "Medium" : rid >= 0x1000 ? "Low" : "Untrusted";
                        return new Identity { Sid = sid, IntegrityRid = rid, IntegrityLevel = level,
                            RestrictedSids = ReadRestrictedSids(token), IsRestricted = IsTokenRestricted(token),
                            ElevationType = ReadTokenInt(token, 18), IsAppContainer = ReadTokenInt(token, 29) != 0 };
                    } finally { Marshal.FreeHGlobal(integrity); }
                } finally { CloseHandle(token); }
            } finally { CloseHandle(process); }
        }
        public static string ImagePath(int processId) {
            IntPtr process = OpenProcess(0x1000, false, (uint)processId);
            if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                StringBuilder value = new StringBuilder(32768);
                int length = value.Capacity;
                if (!QueryFullProcessImageName(process, 0, value, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return value.ToString();
            } finally { CloseHandle(process); }
        }
        private static int ReadTokenInt(IntPtr token, int kind) {
            IntPtr buffer = ReadTokenInfo(token, kind);
            try { return Marshal.ReadInt32(buffer); } finally { Marshal.FreeHGlobal(buffer); }
        }
        private static string[] ReadRestrictedSids(IntPtr token) {
            IntPtr groups = ReadTokenInfo(token, 11);
            try {
                int count = Marshal.ReadInt32(groups);
                string[] values = new string[count];
                int offset = IntPtr.Size == 8 ? 8 : 4;
                int size = IntPtr.Size == 8 ? 16 : 8;
                for (int index = 0; index < count; index++) {
                    IntPtr text;
                    if (!ConvertSidToStringSid(Marshal.ReadIntPtr(groups, offset + index * size), out text))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    try { values[index] = Marshal.PtrToStringUni(text); } finally { LocalFree(text); }
                }
                return values;
            } finally { Marshal.FreeHGlobal(groups); }
        }
        private static IntPtr ReadTokenInfo(IntPtr token, int kind) {
            int length;
            GetTokenInformation(token, kind, IntPtr.Zero, 0, out length);
            if (length <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, kind, buffer, length, out length)) {
                Marshal.FreeHGlobal(buffer);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return buffer;
        }
        public static bool WindowBelongsTo(IntPtr handle, int processId) {
            uint owner;
            GetWindowThreadProcessId(handle, out owner);
            return owner == (uint)processId && IsWindowVisible(handle) && IsWindowEnabled(handle);
        }
        [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
        [DllImport("advapi32.dll", SetLastError=true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll")] private static extern bool IsTokenRestricted(IntPtr token);
        [DllImport("advapi32.dll", SetLastError=true)] private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr buffer, int length, out int returned);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr text);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr handle);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    }
    public sealed class StreamCapture {
        private readonly object gate = new object();
        private readonly StringBuilder stdout = new StringBuilder();
        private readonly StringBuilder stderr = new StringBuilder();
        private readonly Thread outThread;
        private readonly Thread errThread;
        public bool StdoutTruncated { get; private set; }
        public bool StderrTruncated { get; private set; }
        public string Error { get; private set; }
        public StreamCapture(Process process) {
            outThread = new Thread(delegate() { Drain(process.StandardOutput, stdout, false); });
            errThread = new Thread(delegate() { Drain(process.StandardError, stderr, true); });
            outThread.IsBackground = true;
            errThread.IsBackground = true;
            outThread.Start();
            errThread.Start();
        }
        private void Drain(StreamReader reader, StringBuilder buffer, bool isError) {
            try {
                char[] chunk = new char[4096];
                int count;
                while ((count = reader.Read(chunk, 0, chunk.Length)) > 0) {
                    lock (gate) {
                        int keep = Math.Min(count, Math.Max(0, 1048576 - buffer.Length));
                        buffer.Append(chunk, 0, keep);
                        if (keep < count) { if (isError) StderrTruncated = true; else StdoutTruncated = true; }
                    }
                }
            } catch (Exception error) { lock (gate) { Error = error.ToString(); } }
        }
        public string Stdout { get { lock (gate) { return stdout.ToString(); } } }
        public string Stderr { get { lock (gate) { return stderr.ToString(); } } }
        public bool Finish(int milliseconds) {
            Stopwatch timer = Stopwatch.StartNew();
            bool outDone = outThread.Join(milliseconds);
            int remaining = Math.Max(0, milliseconds - (int)timer.ElapsedMilliseconds);
            return errThread.Join(remaining) && outDone;
        }
    }
}
'@

$uiaSource = @'
using System;
using System.Threading;
using System.Windows.Automation;

namespace WinKit.StartupProbe {
    public sealed class WindowObservation {
        public bool Passed { get; set; }
        public string Reason { get; set; }
        public string Title { get; set; }
        public int ProcessId { get; set; }
        public long Handle { get; set; }
        public bool IsVisible { get; set; }
        public bool IsEnabled { get; set; }
        public string InteractionState { get; set; }
        public bool OverviewVisible { get; set; }
        public bool OverviewEnabled { get; set; }
        public bool OverviewSelected { get; set; }
        public bool OverviewInvoked { get; set; }
        public bool GroupInvoked { get; set; }
        public string OverviewAutomationId { get; set; }
    }
    public sealed class WindowWork {
        private readonly ManualResetEvent completed = new ManualResetEvent(false);
        public WindowObservation Result { get; private set; }
        public bool IsComplete { get { return completed.WaitOne(0); } }
        public static WindowWork Start(IntPtr handle, int processId, string expectedTitle, bool invokeOverview, bool expandGroup) {
            WindowWork work = new WindowWork();
            Thread thread = new Thread(delegate() {
                WindowObservation value = new WindowObservation { Handle = handle.ToInt64(), Reason = "Window not ready." };
                try { Inspect(value, handle, processId, expectedTitle, invokeOverview, expandGroup); }
                catch (Exception error) { value.Reason = error.GetType().Name + ": " + error.Message; }
                finally { work.Result = value; work.completed.Set(); }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            return work;
        }
        private static void Inspect(WindowObservation result, IntPtr handle, int processId, string title, bool invokeOverview, bool expandGroup) {
            AutomationElement window = AutomationElement.FromHandle(handle);
            AutomationElement.AutomationElementInformation current = window.Current;
            result.Title = current.Name;
            result.ProcessId = current.ProcessId;
            result.IsVisible = !current.IsOffscreen;
            result.IsEnabled = current.IsEnabled;
            if (current.ProcessId != processId || current.ControlType != ControlType.Window || current.Name != title ||
                current.IsOffscreen || !current.IsEnabled || current.BoundingRectangle.IsEmpty ||
                current.BoundingRectangle.Width <= 0 || current.BoundingRectangle.Height <= 0) {
                result.Reason = "The expected main window is not visible and enabled for this PID.";
                return;
            }
            object pattern;
            if (!window.TryGetCurrentPattern(WindowPattern.Pattern, out pattern)) {
                result.Reason = "Main window has no WindowPattern.";
                return;
            }
            WindowPattern windowPattern = (WindowPattern)pattern;
            result.InteractionState = windowPattern.Current.WindowInteractionState.ToString();
            if (windowPattern.Current.WindowInteractionState != WindowInteractionState.ReadyForUserInteraction) {
                result.Reason = "Main window is not ready for user interaction.";
                return;
            }
            Condition buttonCondition = new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "overview"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            AutomationElement button = window.FindFirst(TreeScope.Descendants, buttonCondition);
            if (button == null || button.Current.IsOffscreen) {
                if (expandGroup) {
                    Condition groupCondition = new AndCondition(
                        new PropertyCondition(AutomationElement.NameProperty, "\u4F53\u68C0\u4E0E\u5B89\u5168"),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    AutomationElement group = window.FindFirst(TreeScope.Descendants, groupCondition);
                    object groupPattern;
                    if (group != null && !group.Current.IsOffscreen && group.Current.IsEnabled &&
                        group.TryGetCurrentPattern(InvokePattern.Pattern, out groupPattern)) {
                        ((InvokePattern)groupPattern).Invoke();
                        result.GroupInvoked = true;
                    }
                }
                result.Reason = "Overview navigation is not visible yet.";
                return;
            }
            result.OverviewAutomationId = button.Current.AutomationId;
            result.OverviewVisible = !button.Current.IsOffscreen;
            result.OverviewEnabled = button.Current.IsEnabled;
            if (!result.OverviewEnabled || !button.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) {
                result.Reason = "Overview navigation is not enabled or invokable.";
                return;
            }
            if (invokeOverview) {
                ((InvokePattern)pattern).Invoke();
                result.OverviewInvoked = true;
            }
            result.OverviewSelected = button.Current.ItemStatus == "\u5F53\u524D\u9875\u9762";
            result.Passed = result.OverviewSelected;
            result.Reason = result.Passed ? "Visible overview navigation is selected and interactive." : "Waiting for overview selection after Invoke.";
        }
    }
}
'@

function Get-ExpectedProcessIntegrityRid {
    param([int] $ObserverIntegrityRid, [ValidateSet('Observer', 'Medium')] [string] $Mode)
    if ($Mode -eq 'Medium') {
        if ($ObserverIntegrityRid -notin @(0x2000, 0x3000)) {
            throw 'ExpectedProcessIntegrity Medium requires a Medium or High observer.'
        }
        return 0x2000
    }
    return $ObserverIntegrityRid
}

function Test-ExpectedProcessIdentity {
    param([object] $Identity, [string] $Sid, [int] $IntegrityRid, [switch] $RequireUnrestricted)
    if ($null -eq $Identity -or $Identity.Sid -cne $Sid -or $Identity.IntegrityRid -ne $IntegrityRid) { return $false }
    if ($RequireUnrestricted) {
        return $Identity.IsAppContainer -is [bool] -and -not $Identity.IsAppContainer -and
            $Identity.IsRestricted -is [bool] -and -not $Identity.IsRestricted -and
            $null -ne $Identity.RestrictedSids -and @($Identity.RestrictedSids).Count -eq 0
    }
    return $true
}

function Test-OwnedProcess {
    if ($null -eq $process -or $null -eq $launchedStartTicks -or $null -eq $observerIdentity -or $null -eq $expectedProcessIntegrityRid) { return $false }
    try {
        $process.Refresh()
        if ($process.HasExited -or $process.Id -ne $report.ProcessId -or $process.StartTime.ToUniversalTime().Ticks -ne $launchedStartTicks) { return $false }
        $actualPath = [WinKit.StartupProbe.Native]::ImagePath($process.Id)
        if (-not [string]::Equals($actualPath, $expectedPath, [StringComparison]::OrdinalIgnoreCase)) { return $false }
        $identity = [WinKit.StartupProbe.Native]::ReadIdentity($process.Id)
        return Test-ExpectedProcessIdentity -Identity $identity -Sid $observerIdentity.Sid -IntegrityRid $expectedProcessIntegrityRid -RequireUnrestricted:($ExpectedProcessIntegrity -eq 'Medium')
    } catch { return $false }
}

try {
    if ([string]::IsNullOrWhiteSpace($ReportPath)) {
        $ReportPath = Join-Path $PSScriptRoot ('../../dist/startup-probe/' + [guid]::NewGuid().ToString('N') + '/report.json')
    }
    $ReportPath = [IO.Path]::GetFullPath($ReportPath)
    $report.ReportPath = $ReportPath
    if ($env:OS -ne 'Windows_NT') { throw 'This probe requires Windows and an interactive desktop.' }
    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) { throw 'ExecutablePath is required. Use -Help for usage.' }
    $expectedPath = [IO.Path]::GetFullPath($ExecutablePath)
    $report.ExecutablePath = $expectedPath
    if (-not [IO.File]::Exists($expectedPath)) { throw ('Executable not found: ' + $expectedPath) }
    if ([IO.Path]::GetExtension($expectedPath) -ine '.exe') { throw 'ExecutablePath must identify an EXE.' }
    if ($ExpectedVersion -and $ExpectedVersion -cnotmatch '\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z') {
        throw 'ExpectedVersion must be a canonical stable major.minor.patch value.'
    }
    $report.Sha256 = (Get-FileHash -LiteralPath $expectedPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($expectedPath)
    $report.ProductVersion = $versionInfo.ProductVersion
    $report.FileVersion = $versionInfo.FileVersion
    if ($ExpectedVersion -and ($report.ProductVersion -cne $ExpectedVersion -or $report.FileVersion -cne ($ExpectedVersion + '.0'))) {
        throw ('EXE version does not match ' + $ExpectedVersion + ': product=' + $report.ProductVersion + ', file=' + $report.FileVersion)
    }
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName WindowsBase
    if ($null -eq ('WinKit.StartupProbe.Native' -as [type])) { Add-Type -TypeDefinition $nativeSource -Language CSharp }
    if ($null -eq ('WinKit.StartupProbe.WindowWork' -as [type])) {
        $references = @([System.Windows.Automation.AutomationElement].Assembly.Location,
            [System.Windows.Automation.ControlType].Assembly.Location, [System.Windows.Rect].Assembly.Location)
        if ($PSVersionTable.PSVersion.Major -le 5) { $references += 'System.dll', 'System.Core.dll' }
        else {
            # Explicit references replace Add-Type's defaults. Runtime TPA DLLs can
            # forward types to CoreLib, which Add-Type intentionally excludes.
            # Use this PowerShell host's matching reference assemblies instead.
            $referenceDirectory = Join-Path $PSHOME 'ref'
            if (-not (Test-Path -LiteralPath $referenceDirectory -PathType Container)) {
                throw ('PowerShell reference assemblies are unavailable: ' + $referenceDirectory)
            }
            $externalNames = @($references | ForEach-Object { [IO.Path]::GetFileName($_) })
            $references += @(Get-ChildItem -LiteralPath $referenceDirectory -Filter '*.dll' -File |
                Where-Object { $_.Name -notin $externalNames } | ForEach-Object { $_.FullName })
        }
        Add-Type -TypeDefinition $uiaSource -Language CSharp -ReferencedAssemblies ($references | Select-Object -Unique)
    }
    $observerIdentity = [WinKit.StartupProbe.Native]::ReadIdentity($PID)
    $report.ObserverIdentity = $observerIdentity
    if ($RequireMediumIntegrity -and $observerIdentity.IntegrityRid -ne 0x2000) { throw 'RequireMediumIntegrity needs an unelevated Medium-integrity observer.' }
    $expectedProcessIntegrityRid = Get-ExpectedProcessIntegrityRid -ObserverIntegrityRid $observerIdentity.IntegrityRid -Mode $ExpectedProcessIntegrity
    $report.ExpectedProcessIntegrityRid = $expectedProcessIntegrityRid
    if ($ExpectedProcessIntegrity -eq 'Medium' -and -not (Test-ExpectedProcessIdentity -Identity $observerIdentity -Sid $observerIdentity.Sid -IntegrityRid $observerIdentity.IntegrityRid -RequireUnrestricted)) {
        throw 'ExpectedProcessIntegrity Medium requires a known unrestricted, non-AppContainer observer.'
    }

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $expectedPath
    $startInfo.WorkingDirectory = [IO.Path]::GetDirectoryName($expectedPath)
    $startInfo.Arguments = ''
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
    $startInfo.StandardErrorEncoding = New-Object Text.UTF8Encoding($false)
    # Deliberately do not set CreateNoWindow, a verb, or EnvironmentVariables.
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw 'Process.Start did not return a process.' }
    $report.ProcessId = $process.Id
    $capture = New-Object WinKit.StartupProbe.StreamCapture($process)
    $launchedStartTicks = $process.StartTime.ToUniversalTime().Ticks
    $report.ProcessStartedUtc = $process.StartTime.ToUniversalTime().ToString('o')
    $process.Refresh()
    if ($process.HasExited) { throw ('EXE exited before its main window appeared: ' + $process.ExitCode) }
    $report.ProcessIdentity = [WinKit.StartupProbe.Native]::ReadIdentity($process.Id)
    if (-not (Test-ExpectedProcessIdentity -Identity $report.ProcessIdentity -Sid $observerIdentity.Sid -IntegrityRid $expectedProcessIntegrityRid -RequireUnrestricted:($ExpectedProcessIntegrity -eq 'Medium'))) {
        throw ('Launched process did not match the observer SID and expected integrity RID ' + $expectedProcessIntegrityRid + '.')
    }
    if (-not (Test-OwnedProcess)) {
        $process.Refresh()
        if ($process.HasExited) { throw ('EXE exited before its main window appeared: ' + $process.ExitCode) }
        throw 'Launched PID/image path/start time/token identity did not match.'
    }
    $expectedTitle = 'WinKit ' + (-join [char[]]@(0x7CFB, 0x7EDF, 0x7BA1, 0x5BB6))
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $stableSince = $null
    $passed = $false
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw ('EXE exited while waiting for its main window: ' + $process.ExitCode) }
        $handle = $process.MainWindowHandle
        $matchingWindow = $handle -ne [IntPtr]::Zero -and $process.MainWindowTitle -ceq $expectedTitle -and
            [WinKit.StartupProbe.Native]::WindowBelongsTo($handle, $process.Id)
        if (-not $matchingWindow) { $stableSince = $null; $report.StableSecondsObserved = 0 }
        if ($null -ne $windowWork -and $windowWork.IsComplete) {
            $observation = $windowWork.Result
            $windowWork = $null
            $report.LastWindowObservation = $observation
            if ($observation.GroupInvoked) { $report.OverviewGroupInvoked = $true }
            if ($observation.OverviewInvoked) { $report.OverviewInvoked = $true }
            if ($observation.Passed -and $report.OverviewInvoked -and $matchingWindow -and $observation.Handle -eq $handle.ToInt64()) {
                if ($null -eq $stableSince) { $stableSince = [DateTimeOffset]::UtcNow }
                $report.Window = $observation
                $report.StableSecondsObserved = ([DateTimeOffset]::UtcNow - $stableSince).TotalSeconds
                if ($report.StableSecondsObserved -ge 3) { $passed = $true; break }
            } else { $stableSince = $null; $report.StableSecondsObserved = 0 }
        }
        if ($null -eq $windowWork -and $matchingWindow) {
            $windowWork = [WinKit.StartupProbe.WindowWork]::Start($handle, $process.Id, $expectedTitle,
                (-not $report.OverviewInvoked), (-not $report.OverviewGroupInvoked))
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $passed) {
        $lastReason = 'No matching visible main window; a UI Automation call may also be pending.'
        if ($null -ne $report.LastWindowObservation) { $lastReason = $report.LastWindowObservation.Reason }
        throw ('Real no-argument startup did not pass within ' + $TimeoutSeconds + ' seconds. ' + $lastReason)
    }
    if (-not (Test-OwnedProcess)) { throw 'Process identity changed before normal close.' }
    $report.NormalCloseRequested = $process.CloseMainWindow()
    if (-not $report.NormalCloseRequested) { throw 'The verified main window rejected normal CloseMainWindow.' }
    if (-not $process.WaitForExit(20000)) { throw 'The verified process did not close normally within 20 seconds.' }
    if ($process.ExitCode -ne 0) { throw ('The verified process exited with nonzero code after normal close: ' + $process.ExitCode) }
    $report.NormalCloseSucceeded = $true
    if (-not $capture.Finish(3000)) { throw 'The process exited but redirected streams did not finish.' }
    if ($capture.Error -or $capture.StdoutTruncated -or $capture.StderrTruncated) { throw 'Redirected output capture failed or exceeded the 1 Mi-character limit.' }
    $afterHash = (Get-FileHash -LiteralPath $expectedPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($afterHash -cne $report.Sha256) { throw 'The executable changed during startup verification.' }
    $report.Status = 'passed'
} catch {
    $report.Error = $_.Exception.ToString()
} finally {
    if ($null -ne $process) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                if (Test-OwnedProcess) {
                    $report.Cleanup = 'normal-close-attempted'
                    [void]$process.CloseMainWindow()
                    if (-not $process.WaitForExit(2000)) {
                        if (Test-OwnedProcess) {
                            $process.Kill()
                            $report.ForcedTermination = $true
                            $report.Cleanup = 'terminated-owned-pid'
                            if (-not $process.WaitForExit(5000)) { $report.Cleanup = 'owned-pid-termination-pending' }
                        } else { $report.Cleanup = 'skipped-identity-mismatch' }
                    } else { $report.Cleanup = 'closed-owned-pid' }
                } else { $report.Cleanup = 'skipped-identity-mismatch' }
            }
            $process.Refresh()
            if ($process.HasExited) {
                $report.ExitCode = $process.ExitCode
                $bits = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$process.ExitCode), 0)
                $report.ExitCodeHex = '0x{0:X8}' -f $bits
            }
        } catch { $report.Cleanup = 'cleanup-error: ' + $_.Exception.Message }
    }
    if ($null -ne $capture) {
        [void]$capture.Finish(1000)
        $report.Stdout = $capture.Stdout
        $report.Stderr = $capture.Stderr
        $report.StdoutTruncated = $capture.StdoutTruncated
        $report.StderrTruncated = $capture.StderrTruncated
        $report.CaptureError = $capture.Error
    }
    if ($null -ne $process) { $process.Dispose() }
    $report.FinishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    try {
        if ([string]::IsNullOrWhiteSpace($ReportPath)) { throw 'No report path could be resolved.' }
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($ReportPath))
        $json = $report | ConvertTo-Json -Depth 8
        [IO.File]::WriteAllText($ReportPath, $json + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
        Write-Output ('STARTUP_PROBE_REPORT=' + $ReportPath)
        Write-Output $json
    } catch {
        $report.Status = 'failed'
        [Console]::Error.WriteLine('Could not write startup report: ' + $_.Exception.Message)
    }
}
if ($report.Status -eq 'passed') { exit 0 }
exit 1
