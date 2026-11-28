using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinKit.Core;
using WinKit.Services;
using WinKit.ViewModels;

namespace PerformanceProbe;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly List<object> Stages = [];
    private static readonly List<string> Errors = [];
    private static string _output = "";
    private static string _phase = "setup";
    private static MainViewModel _vm = null!;
    private static Window _window = null!;
    private static TreeSampler _sampler = null!;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<double> HeartbeatDelays = [];
    private static int _exitCode;
    private static bool _startupGuardIntercepted;
    private const string StartupGuardMessage = "Unexpected application startup in perf probe";
    private const int SampleIntervalMs = 1000;

    [STAThread]
    private static int Main(string[] args)
    {
        _output = Path.GetFullPath(Option(args, "--output", "perf-baseline.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(_output)!);
        var sourceDir = Path.GetFullPath(Option(args, "--assembly-dir", ".workbuddy/perf-before"));
        var sourceDll = Path.Combine(sourceDir, "WinKit.dll");
        var loadedDll = typeof(MainViewModel).Assembly.Location;
        var sourceHash = Hash(sourceDll);
        var loadedHash = Hash(loadedDll);
        if (sourceHash != loadedHash) throw new InvalidOperationException("Loaded WinKit assembly does not match --assembly-dir. Rebuild the probe with -p:WinKitAssemblyDir=<same directory>.");
        var settingsFile = UiSettingsService.FilePath;
        var settingsBefore = Fingerprint(settingsFile);
        var idleSeconds = int.Parse(Option(args, "--idle-seconds", "10"));
        var repeats = int.Parse(Option(args, "--repeats", "10"));
        using var sampler = new TreeSampler(() => _phase, Clock);
        _sampler = sampler;
        var startup = Metrics.Take();
        sampler.Start();
        try
        {
            var app = new WinKit.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent(); // Load the exact compiled WinKit resources.
            // WinKit.App.OnStartup calls base first; throwing from Startup stops its remaining normal startup logic.
            app.Startup += (_, _) => throw new InvalidOperationException(StartupGuardMessage);
            WinKit.MainWindow.ApplyDensity(TableDensity.Compact);
            var settings = new UiSettings
            {
                LastModuleKey = "filelock", Theme = AppTheme.Light, Density = TableDensity.Compact,
                WindowWidth = 1280, WindowHeight = 820, WindowLeft = double.NaN, WindowTop = double.NaN
            };
            var journal = new ChangeJournal(Path.Combine(AppContext.BaseDirectory, "data", "journal"));
            var constructor = typeof(MainViewModel).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(UiSettings), typeof(ChangeJournal), typeof(bool)], null)
                ?? throw new MissingMethodException("MainViewModel(settings, journal, saveSettings)");
            _vm = (MainViewModel)constructor.Invoke([settings, journal, false]);
            AssertNoAutomaticUpdate();
            _window = (Window)(typeof(WinKit.MainWindow).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(MainViewModel)], null) ?? throw new MissingMethodException("MainWindow(viewModel)"))
                .Invoke([_vm]);
            _window.ShowInTaskbar = false;
            _window.ShowActivated = false;
            _window.Title = "WinKit PerformanceProbe (read only)";
            var lastTick = Clock.Elapsed.TotalMilliseconds;
            var heartbeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            heartbeat.Tick += (_, _) =>
            {
                var now = Clock.Elapsed.TotalMilliseconds;
                HeartbeatDelays.Add(Math.Max(0, now - lastTick - 50));
                lastTick = now;
            };
            heartbeat.Start();
            app.DispatcherUnhandledException += (_, e) =>
            {
                if (e.Exception is InvalidOperationException ex && ex.Message == StartupGuardMessage)
                {
                    _startupGuardIntercepted = true;
                    e.Handled = true;
                    return;
                }
                Errors.Add(e.Exception.ToString());
                _exitCode = 1;
                e.Handled = true;
            };
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    _phase = "startup-filelock";
                    _window.Show(); // Loaded calls real MainViewModel.Initialize once.
                    await Ready("FileLockView");
                    Record("startup-filelock", startup, Clock.Elapsed.TotalMilliseconds, null);
                    await MeasureNavigation("apps-first", "apps", "AppManagerView");
                    await MeasureIdle("apps-first-idle", idleSeconds);
                    for (var i = 1; i <= repeats; i++)
                    {
                        await MeasureNavigation($"away-{i:00}", "filelock", "FileLockView");
                        await MeasureNavigation($"apps-return-{i:00}", "apps", "AppManagerView");
                    }
                    await MeasureIdle("apps-repeat-idle", idleSeconds);
                    if (args.Contains("--subpages"))
                    {
                        foreach (var subpage in new[] { "runtimes", "tools", "uninstall", "apps" })
                            await MeasureSubpage(subpage);
                        await MeasureIdle("apps-subpages-idle", idleSeconds);
                    }
                    AssertNoAutomaticUpdate();
                }
                catch (Exception ex)
                {
                    Errors.Add(ex.ToString());
                    _exitCode = 1;
                    Console.Error.WriteLine(ex);
                }
                finally
                {
                    heartbeat.Stop();
                    _phase = "shutdown";
                    try { _window.Close(); } catch (Exception ex) { Errors.Add(ex.ToString()); }
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
            if (!_startupGuardIntercepted) throw new InvalidOperationException("The application startup guard was not triggered.");
        }
        catch (Exception ex) { Errors.Add(ex.ToString()); _exitCode = 1; }
        finally
        {
            sampler.Stop();
            var settingsAfter = Fingerprint(settingsFile);
            if (settingsBefore != settingsAfter) { Errors.Add("User ui.json changed during probe."); _exitCode = 1; }
            var result = new
            {
                schemaVersion = 1, label = Option(args, "--label", "baseline"),
                startedUtc = DateTimeOffset.UtcNow - Clock.Elapsed, elapsedMs = Clock.Elapsed.TotalMilliseconds,
                environment = new { os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(),
                    cpuCount = Environment.ProcessorCount, processId = Environment.ProcessId, is64Bit = Environment.Is64BitProcess },
                assembly = new { sourceDll, loadedDll, sha256 = sourceHash, loadedSha256 = loadedHash,
                    mvid = typeof(MainViewModel).Assembly.ManifestModule.ModuleVersionId,
                    version = typeof(MainViewModel).Assembly.GetName().Version?.ToString() },
                method = new { realMainWindow = true, normalAppStartupPathBlocked = _startupGuardIntercepted, saveSettings = false,
                    initialModule = "filelock", updateCommandsInvoked = false, mutationCommandsInvoked = false,
                    sampleIntervalMs = SampleIntervalMs, dispatcherHeartbeatMs = 50, readinessStabilityMs = 25,
                    repeats, idleSeconds, forcedGc = false,
                    note = "Process metrics and total GC allocations include the same in-process observer in before/after. Toolhelp snapshots at 1000 ms can miss shorter children. Durations include real navigation, async completion, layout, and a 25 ms stability check. selectCallMs isolates the navigation setter. No WMI polling." },
                settings = new { path = settingsFile, before = settingsBefore, after = settingsAfter, unchanged = settingsBefore == settingsAfter },
                stages = Stages, errors = Errors,
                observer = sampler.Summary(), samples = sampler.Samples, childProcesses = sampler.Children()
            };
            File.WriteAllText(_output, JsonSerializer.Serialize(result, Json));
            Console.WriteLine($"RESULT {_output} stages={Stages.Count} errors={Errors.Count}");
        }
        return _exitCode;
    }

    private static async Task MeasureNavigation(string name, string key, string viewName)
    {
        _phase = name;
        var before = Metrics.Take();
        var start = Clock.Elapsed.TotalMilliseconds;
        var nav = _vm.NavItems.Single(n => n.Key == key);
        var sw = Stopwatch.StartNew();
        _vm.SelectedNav = nav;
        var callMs = sw.Elapsed.TotalMilliseconds;
        await Ready(viewName);
        if (_vm.SelectedNav?.Key != key) throw new InvalidOperationException($"Navigation to {key} was refused.");
        Record(name, before, Clock.Elapsed.TotalMilliseconds - start, callMs);
    }

    private static async Task MeasureSubpage(string subpage)
    {
        _phase = "subpage-" + subpage;
        var before = Metrics.Take();
        var start = Clock.Elapsed.TotalMilliseconds;
        var model = _vm.CurrentViewModel ?? throw new InvalidOperationException("No active module.");
        var sw = Stopwatch.StartNew();
        model.GetType().GetProperty("SubView")!.SetValue(model, subpage);
        var callMs = sw.Elapsed.TotalMilliseconds;
        await Ready("AppManagerView");
        Record(_phase, before, Clock.Elapsed.TotalMilliseconds - start, callMs);
    }

    private static async Task MeasureIdle(string name, int seconds)
    {
        _phase = name;
        var before = Metrics.Take();
        var start = Clock.Elapsed.TotalMilliseconds;
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Record(name, before, Clock.Elapsed.TotalMilliseconds - start, null);
    }

    private static async Task Ready(string viewName)
    {
        var started = Clock.Elapsed;
        while (true)
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            _window.UpdateLayout();
            if (!Busy(_vm) && !Busy(_vm.CurrentViewModel))
            {
                await Task.Delay(25);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                if (!Busy(_vm) && !Busy(_vm.CurrentViewModel)) break;
            }
            if (Clock.Elapsed - started > TimeSpan.FromSeconds(150)) throw new TimeoutException($"Still busy in {_phase}.");
            await Task.Delay(25);
        }
        var view = Visuals(_window).OfType<FrameworkElement>().FirstOrDefault(e => e.GetType().Name == viewName);
        if (view is null || !view.IsLoaded || view.ActualWidth <= 0 || view.ActualHeight <= 0)
            throw new InvalidOperationException($"Real loaded/layout view {viewName} missing in {_phase}.");
        if (Application.Current.Windows.Count != 1 || !ReferenceEquals(Application.Current.Windows[0], _window))
            throw new InvalidOperationException("Probe must own exactly the one injected MainWindow.");
        AssertNoAutomaticUpdate();
    }

    private static bool Busy(object? model)
    {
        if (model is null) return false;
        foreach (var name in new[] { "IsBusy", "IsGlobalBusy", "IsLoading" })
            if (model.GetType().GetProperty(name)?.GetValue(model) is true) return true;
        return false;
    }

    private static void AssertNoAutomaticUpdate()
    {
        if (typeof(MainViewModel).GetField("_saveSettings", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_vm) is not false)
            throw new InvalidOperationException("saveSettings:false was not applied.");
        var updater = _vm.Updater;
        if (updater.GetType().GetField("_automaticStarted", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(updater) is not false)
            throw new InvalidOperationException("Automatic updater was started or its guard could not be verified.");
        if (updater.IsWorking) throw new InvalidOperationException("Unexpected updater work.");
    }

    private static void Record(string name, Metrics before, double elapsedMs, double? callMs)
    {
        var after = Metrics.Take();
        var model = _vm.CurrentViewModel;
        var counts = new Dictionary<string, int>();
        if (model is not null)
            foreach (var property in new[] { "Apps", "Runtimes", "Tools" })
                if (model.GetType().GetProperty(property)?.GetValue(model) is ICollection collection) counts[property] = collection.Count;
        var views = Visuals(_window).OfType<FrameworkElement>().Where(e => e.GetType().Namespace == "WinKit.Views")
            .Select(e => new { type = e.GetType().Name, e.IsLoaded, e.IsVisible, width = e.ActualWidth, height = e.ActualHeight }).ToArray();
        var grids = Visuals(_window).OfType<DataGrid>().Select(g => new
        {
            g.Name, items = g.Items.Count,
            realizedRows = Visuals(g).OfType<DataGridRow>().Count(),
            virtualizing = VirtualizingPanel.GetIsVirtualizing(g), enableRowVirtualization = g.EnableRowVirtualization
        }).ToArray();
        Stages.Add(new { name, elapsedMs, selectCallMs = callMs, before, after,
            delta = new { privateBytes = after.PrivateBytes - before.PrivateBytes, workingSetBytes = after.WorkingSetBytes - before.WorkingSetBytes,
                allocatedBytes = after.TotalAllocatedBytes - before.TotalAllocatedBytes, cpuMs = after.CpuMs - before.CpuMs,
                handles = after.Handles - before.Handles, gen0 = after.Gen0 - before.Gen0, gen1 = after.Gen1 - before.Gen1, gen2 = after.Gen2 - before.Gen2 },
            currentModule = _vm.SelectedNav?.Key, subView = model?.GetType().GetProperty("SubView")?.GetValue(model), counts,
            status = _vm.StatusText, summary = model?.GetType().GetProperty("SummaryText")?.GetValue(model),
            error = model?.GetType().GetProperty("ErrorMessage")?.GetValue(model),
            hwndCreated = new WindowInteropHelper(_window).Handle != IntPtr.Zero, views, grids,
            automaticUpdaterStarted = false, heartbeatMaxDelayMs = HeartbeatDelays.Count == 0 ? 0 : HeartbeatDelays.Max() });
        Console.WriteLine($"{name}: ready={elapsedMs:F1}ms call={callMs:F1}ms private={after.PrivateBytes / 1048576.0:F1}MiB working={after.WorkingSetBytes / 1048576.0:F1}MiB alloc={(after.TotalAllocatedBytes - before.TotalAllocatedBytes) / 1048576.0:F1}MiB cpu={after.CpuMs - before.CpuMs:F1}ms handles={after.Handles}");
        File.WriteAllText(_output + ".log", string.Join(Environment.NewLine, Stages.Select(s => JsonSerializer.Serialize(s))) + Environment.NewLine);
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static string Option(string[] args, string name, string fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Fingerprint(string path) => File.Exists(path) ? $"{File.GetLastWriteTimeUtc(path):O}:{Hash(path)}" : "absent";

    private sealed record Metrics(long PrivateBytes, long WorkingSetBytes, long TotalAllocatedBytes, long ManagedBytes,
        int Handles, int Threads, double CpuMs, int Gen0, int Gen1, int Gen2)
    {
        public static Metrics Take()
        {
            using var process = Process.GetCurrentProcess();
            return new(process.PrivateMemorySize64, process.WorkingSet64, GC.GetTotalAllocatedBytes(false), GC.GetTotalMemory(false),
                process.HandleCount, process.Threads.Count, process.TotalProcessorTime.TotalMilliseconds,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        }
    }

    private sealed class TreeSampler(Func<string> phase, Stopwatch clock) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Dictionary<string, ChildObservation> _seen = [];
        private readonly HashSet<uint> _knownDescendants = [(uint)Environment.ProcessId];
        private Task? _task;
        private long _samplingTicks;
        public List<object> Samples { get; } = [];
        public int Failures { get; private set; }
        public void Start() => _task = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(SampleIntervalMs));
            do
            {
                var stamp = Stopwatch.GetTimestamp();
                try { Sample(); } catch { Failures++; }
                _samplingTicks += Stopwatch.GetTimestamp() - stamp;
            } while (await timer.WaitForNextTickAsync(_stop.Token));
        });
        public void Stop()
        {
            _stop.Cancel();
            try { _task?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
        public object Summary() => new { sampleCount = Samples.Count, failureCount = Failures,
            samplingWallMs = _samplingTicks * 1000.0 / Stopwatch.Frequency,
            childCount = _seen.Count, intervalMs = SampleIntervalMs };
        public object[] Children() => _seen.Values.Cast<object>().ToArray();
        private void Sample()
        {
            var entries = Snapshot();
            var present = entries.Select(e => e.ProcessId).ToHashSet();
            _knownDescendants.RemoveWhere(pid => pid != Environment.ProcessId && !present.Contains(pid));
            var descendants = new HashSet<uint> { (uint)Environment.ProcessId };
            bool changed;
            do
            {
                changed = false;
                foreach (var entry in entries)
                    if ((descendants.Contains(entry.ParentProcessId) || _knownDescendants.Contains(entry.ProcessId)) && descendants.Add(entry.ProcessId)) changed = true;
            } while (changed);
            _knownDescendants.UnionWith(descendants);
            var now = clock.Elapsed.TotalMilliseconds;
            long childPrivate = 0, childWorking = 0;
            double childCpu = 0;
            var children = entries.Where(e => e.ProcessId != Environment.ProcessId && descendants.Contains(e.ProcessId)).ToArray();
            foreach (var entry in children)
            {
                try
                {
                    using var process = Process.GetProcessById((int)entry.ProcessId);
                    var started = process.StartTime.ToUniversalTime();
                    var key = $"{entry.ProcessId}:{started.Ticks}";
                    if (!_seen.TryGetValue(key, out var child))
                        _seen[key] = child = new ChildObservation { Pid = entry.ProcessId, ParentPid = entry.ParentProcessId,
                            Name = entry.ExeFile, StartedUtc = started, FirstSeenMs = now, FirstPhase = phase() };
                    child.LastSeenMs = now;
                    child.LastPhase = phase();
                    child.PeakPrivateBytes = Math.Max(child.PeakPrivateBytes, process.PrivateMemorySize64);
                    child.PeakWorkingSetBytes = Math.Max(child.PeakWorkingSetBytes, process.WorkingSet64);
                    child.CpuMs = Math.Max(child.CpuMs, process.TotalProcessorTime.TotalMilliseconds);
                    childPrivate += process.PrivateMemorySize64;
                    childWorking += process.WorkingSet64;
                    childCpu += process.TotalProcessorTime.TotalMilliseconds;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            using var root = Process.GetCurrentProcess();
            Samples.Add(new { elapsedMs = now, phase = phase(), privateBytes = root.PrivateMemorySize64,
                workingSetBytes = root.WorkingSet64, handles = root.HandleCount, cpuMs = root.TotalProcessorTime.TotalMilliseconds,
                totalAllocatedBytes = GC.GetTotalAllocatedBytes(false), childCount = children.Length,
                childPrivateBytes = childPrivate, childWorkingSetBytes = childWorking, childCpuMs = childCpu });
        }
        public void Dispose() { Stop(); _stop.Dispose(); }
    }

    private sealed class ChildObservation
    {
        public uint Pid { get; init; }
        public uint ParentPid { get; init; }
        public string Name { get; init; } = "";
        public DateTime StartedUtc { get; init; }
        public double FirstSeenMs { get; init; }
        public double LastSeenMs { get; set; }
        public string FirstPhase { get; init; } = "";
        public string LastPhase { get; set; } = "";
        public long PeakPrivateBytes { get; set; }
        public long PeakWorkingSetBytes { get; set; }
        public double CpuMs { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    private static List<ProcessEntry> Snapshot()
    {
        var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            List<ProcessEntry> entries = [];
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32FirstW(handle, ref entry))
                do { entries.Add(entry); } while (Process32NextW(handle, ref entry));
            return entries;
        }
        finally { CloseHandle(handle); }
    }
}
