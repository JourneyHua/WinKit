# WinKit real-window performance probe

An independent .NET 10 WPF console probe for an already built WinKit assembly. It uses a Reference/HintPath controlled by WinKitAssemblyDir, defaulting to the main project Release output. It has no ProjectReference or linked production source and never builds the main project. Preserve separate before/after build directories before measuring; the examples below use .workbuddy for those local snapshots.

Run from the workspace root (the directory containing WinKit and .workbuddy):

```powershell
dotnet build WinKit/tools/PerformanceProbe/PerformanceProbe.csproj -c Release --nologo -p:WinKitAssemblyDir="$((Resolve-Path .workbuddy/perf-before).Path)" -o WinKit/tools/PerformanceProbe/bin/before
dotnet WinKit/tools/PerformanceProbe/bin/before/PerformanceProbe.dll --assembly-dir .workbuddy/perf-before --output .workbuddy/perf-baseline.json --label before --repeats 10 --idle-seconds 10 --subpages
```

After the main project build completes, use its output directory for the after run. Supply the same directory to build and run:

```powershell
dotnet build WinKit/tools/PerformanceProbe/PerformanceProbe.csproj -c Release --nologo -p:WinKitAssemblyDir="C:/path/to/completed/after-build" -o WinKit/tools/PerformanceProbe/bin/after
dotnet WinKit/tools/PerformanceProbe/bin/after/PerformanceProbe.dll --assembly-dir "C:/path/to/completed/after-build" --output .workbuddy/perf-baseline-after.json --label after --repeats 10 --idle-seconds 10 --subpages
```

The loaded WinKit.dll SHA-256 must equal the selected source SHA-256. JSON records both paths, hashes, MVID and version. Use identical probe build options, window size, repeat count and idle intervals in before/after. Leave OS/file caches intact; each run starts a new process. This is a process-cold comparison, not a system-cold comparison.

The probe constructs WinKit.App and loads its real compiled resources. Before pumping Dispatcher.Run, it registers a Startup guard that throws a unique exception; WinKit.App.OnStartup calls base.OnStartup first, so this stops the remaining normal CLI/settings/update startup path before it can run. The dispatcher handles only that exact guard exception, records that interception occurred, and fails if it did not. Each readiness check asserts exactly one application Window exists, and final output fingerprints user ui.json before/after. It invokes the internal MainViewModel(UiSettings, ChangeJournal, bool) constructor with saveSettings:false, LastModuleKey=filelock and in-memory Light/Compact settings at 1280×820, then shows the real MainWindow(viewModel). The ordinary Loaded event calls Initialize once. Each expected WinKit.Views control must be loaded with a nonzero layout; JSON also records realized DataGrid rows and virtualization flags.

The ChangeJournal path is inside the probe output directory. No update, install, uninstall, registry change, scan or cleanup command is executed. The probe verifies _saveSettings=false, Updater._automaticStarted=false and no updater work before Show and after navigation. It fingerprints the user ui.json before/after and fails if it changed. Avoid another WinKit instance writing that file during measurement.

Stages: startup/filelock, first application-management entry, 10 seconds idle, ten filelock/application-management round trips, 10 seconds idle, optional runtime/tools/uninstall/apps subpage switches, then 10 seconds idle. Ready means ContextIdle/layout completion and false IsBusy/IsGlobalBusy/IsLoading flags through a 25 ms stability check. elapsedMs includes that check and layout; selectCallMs isolates the synchronous navigation/subpage setter. This compares both responsiveness and complete asynchronous loading.

Every stage records private/working-set/managed bytes, handles, threads, CPU time, total GC allocations and collection counts, with before/after deltas. No GC is forced. A background observer samples the root process and discovers its transitive descendants every 1000 ms using CreateToolhelp32Snapshot/Process32FirstW/Process32NextW, never WMI. Children are identified by PID plus start time; their observed CPU and memory peaks and first/last phases are recorded. Children shorter than the interval can be missed; exited or inaccessible children may lack individual metrics. Production WMI provider activity outside this process tree is not attributed as a child.

The observer, reflection, UI heartbeat and diagnostic visual enumeration run inside the measured process, with equal settings in before/after. observer.samplingWallMs reports sampling wall time. elapsedMs is captured before diagnostic enumeration; later snapshots can include retained diagnostic allocations. heartbeatMaxDelayMs is the cumulative maximum delay beyond the 50 ms timer interval, not a per-stage statistic. Raw samples allow inspection of peaks and idle behavior.

The requested JSON is written on success or caught failure. Per-stage checkpoints use the same path plus .log. Exit 0 requires no captured exception, no automatic updater and unchanged preferences. Check errors, counts and status alongside timings: an empty or failed enumeration must not be presented as an improvement.
