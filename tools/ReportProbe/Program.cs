using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using WinKit.Services;
using WinKit.ViewModels;

internal static class Program
{
    private static readonly List<object> Results = [];
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var frame = new DispatcherFrame();
        var suite = RunAsync();
        _ = suite.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        try
        {
            suite.GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(new { passed = Results.Count, results = Results }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        await CheckAsync("first read stays asynchronous; repeated entries share in-flight work and completed snapshot", FirstReadAsync);
        await CheckAsync("manual recheck replaces report and timestamp; export serializers receive the current snapshot", RecheckAndExportAsync);
        await CheckAsync("unknown maintenance state stays cached; explicit maintenance refresh bypasses it", MaintenanceRefreshAsync);
        await CheckAsync("failed recheck preserves the last usable report and permits retry", FailedRecheckAsync);
        await CheckAsync("failed first collection retries on re-entry without repeating maintenance lookup", FailedFirstReadAsync);
        await CheckAsync("a new view-model session performs its own collection", SessionIsolationAsync);
        await CheckAsync("an observed running task keeps polling after navigation without duplicate reads", ExistingRunAsync);
        await CheckAsync("an explicit maintenance action rereads status and its completion observer remains active", ExplicitActionAsync);
    }

    private static async Task CheckAsync(string name, Func<Task<object>> test)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var evidence = await test();
            Results.Add(new { name, elapsedMs = clock.ElapsedMilliseconds, evidence });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(name, ex);
        }
    }

    private static async Task<object> FirstReadAsync()
    {
        using var reportGate = new ManualResetEventSlim();
        using var maintenanceGate = new ManualResetEventSlim();
        var builds = 0;
        var reads = 0;
        var reportThread = 0;
        var maintenanceThread = 0;
        var uiThread = Environment.CurrentManagedThreadId;
        var vm = Create(_ =>
        {
            reportThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref builds);
            Require(reportGate.Wait(TimeSpan.FromSeconds(10)), "report gate timed out");
            return Report(1);
        }, () =>
        {
            maintenanceThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref reads);
            Require(maintenanceGate.Wait(TimeSpan.FromSeconds(10)), "maintenance gate timed out");
            return Enabled();
        });
        try
        {
            vm.Refresh();
            Require(vm.IsBusy && !vm.HasReport, "first entry must return while collection is blocked");
            Require(!vm.GenerateCommand.CanExecute(null) && !vm.ExportJsonCommand.CanExecute(null), "in-flight commands must stay guarded");
            await UntilAsync(() => builds == 1 && reads == 1);
            Enter(vm, 10);
            Require(builds == 1 && reads == 1, "in-flight work must not duplicate");
            Require(reportThread != uiThread && maintenanceThread != uiThread, "both reads must run off the UI thread");
        }
        finally
        {
            reportGate.Set();
            maintenanceGate.Set();
        }
        await IdleAsync(vm);
        var snapshot = CurrentReport(vm);
        var section = vm.Sections.Single();
        var generated = vm.GeneratedText;
        var collectionChanges = 0;
        vm.Sections.CollectionChanged += (_, _) => collectionChanges++;
        Enter(vm, 10);
        Require(builds == 1 && reads == 1, "completed entry must not recollect");
        Require(ReferenceEquals(snapshot, CurrentReport(vm)) && ReferenceEquals(section, vm.Sections.Single()), "cached content identity must survive navigation");
        Require(vm.GeneratedText == generated && collectionChanges == 0, "navigation must preserve timestamp and collection bindings");
        return new { builds, maintenanceReads = reads, repeatedEntries = 20, collectionChanges, generated };
    }

    private static async Task<object> RecheckAndExportAsync()
    {
        var builds = 0;
        var reads = 0;
        var vm = Create(_ => Report(Interlocked.Increment(ref builds)), () => { Interlocked.Increment(ref reads); return Enabled(); });
        vm.Refresh();
        await IdleAsync(vm);
        var before = CurrentReport(vm);
        vm.GenerateCommand.Execute(null);
        Enter(vm, 10);
        vm.GenerateCommand.Execute(null);
        await IdleAsync(vm);
        var after = CurrentReport(vm);
        Require(builds == 2 && reads == 2, "manual recheck must bypass both snapshots exactly once");
        Require(!ReferenceEquals(before, after) && after.GeneratedAt > before.GeneratedAt, "manual recheck must expose new data and time");
        Require(ReferenceEquals(after.Sections.Single(), vm.Sections.Single()), "visible sections must belong to new report");
        Require(vm.ExportMarkdownCommand.CanExecute(null) && vm.ExportJsonCommand.CanExecute(null), "cached report must remain exportable");
        var markdown = HealthReportService.ToMarkdown(after);
        using var json = JsonDocument.Parse(HealthReportService.ToJson(after));
        Require(markdown.Contains(after.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss")) && markdown.Contains(@"fresh\|2"), "Markdown must contain new time and escaped current values");
        Require(json.RootElement.GetProperty("GeneratedAt").GetDateTime() == after.GeneratedAt, "JSON must preserve generated time");
        Require(json.RootElement.GetProperty("Sections")[0].GetProperty("Items")[0].GetProperty("Value").GetString() == "fresh|2", "JSON must export new report content");
        return new { builds, maintenanceReads = reads, before = before.GeneratedAt, after = after.GeneratedAt, serializers = new[] { "Markdown", "JSON" } };
    }

    private static async Task<object> MaintenanceRefreshAsync()
    {
        var builds = 0;
        var reads = 0;
        var vm = Create(_ => Report(Interlocked.Increment(ref builds)), () => Interlocked.Increment(ref reads) == 1
            ? new MaintenanceStatus { Note = "probe unavailable" } : Enabled());
        vm.Refresh();
        await IdleAsync(vm);
        Enter(vm, 10);
        Require(reads == 1 && vm.ShowRefreshTask && vm.MaintenanceText == "probe unavailable", "unknown status needs explicit recovery without entry retries");
        vm.RefreshTaskCommand.Execute(null);
        vm.RefreshTaskCommand.Execute(null);
        await IdleAsync(vm);
        Enter(vm, 10);
        Require(reads == 2 && builds == 1 && vm.ShowRunTask && !vm.ShowRefreshTask, "explicit status refresh must bypass its snapshot and update commands");
        return new { builds, maintenanceReads = reads, explicitRefreshes = 1 };
    }

    private static async Task<object> FailedRecheckAsync()
    {
        var builds = 0;
        var reads = 0;
        var vm = Create(_ => Interlocked.Increment(ref builds) == 2 ? throw new IOException("simulated collection failure") : Report(builds),
            () => { Interlocked.Increment(ref reads); return Enabled(); });
        vm.Refresh();
        await IdleAsync(vm);
        var snapshot = CurrentReport(vm);
        var generated = vm.GeneratedText;
        vm.GenerateCommand.Execute(null);
        await IdleAsync(vm);
        Require(vm.HasError && vm.ErrorText.Contains("simulated collection failure"), "collection failure must remain visible");
        Require(ReferenceEquals(snapshot, CurrentReport(vm)) && vm.GeneratedText == generated && vm.ExportJsonCommand.CanExecute(null), "failed recheck must retain exportable old snapshot");
        Enter(vm, 10);
        Require(builds == 2 && reads == 2 && vm.HasError, "navigation must preserve failed-recheck context");
        vm.GenerateCommand.Execute(null);
        await IdleAsync(vm);
        Require(builds == 3 && reads == 3 && !vm.HasError && !ReferenceEquals(snapshot, CurrentReport(vm)), "explicit retry must still run and recover");
        return new { builds, maintenanceReads = reads, failedRecheckPreservedReport = true };
    }

    private static async Task<object> FailedFirstReadAsync()
    {
        var builds = 0;
        var reads = 0;
        var vm = Create(_ => Interlocked.Increment(ref builds) == 1 ? throw new IOException("first read failure") : Report(builds),
            () => { Interlocked.Increment(ref reads); return Enabled(); });
        vm.Refresh();
        await IdleAsync(vm);
        Require(!vm.HasReport && vm.HasError, "initial failure must not create a successful cache entry");
        vm.Refresh();
        await IdleAsync(vm);
        Require(vm.HasReport && !vm.HasError && builds == 2 && reads == 1, "re-entry should retry missing report and reuse completed status");
        return new { builds, maintenanceReads = reads };
    }

    private static async Task<object> SessionIsolationAsync()
    {
        var builds = 0;
        var reads = 0;
        HealthReport Build(Action<string> _) => Report(Interlocked.Increment(ref builds));
        MaintenanceStatus Read() { Interlocked.Increment(ref reads); return Enabled(); }
        var first = Create(Build, Read);
        first.Refresh();
        await IdleAsync(first);
        var second = Create(Build, Read);
        Require(!second.HasReport, "a new instance must start without another session's snapshot");
        second.Refresh();
        await IdleAsync(second);
        Require(builds == 2 && reads == 2 && !ReferenceEquals(CurrentReport(first), CurrentReport(second)), "sessions must remain independent");
        return new { sessions = 2, builds, maintenanceReads = reads };
    }

    private static async Task<object> ExistingRunAsync()
    {
        var reads = 0;
        var vm = Create(_ => Report(1), () => Interlocked.Increment(ref reads) == 1 ? Enabled(running: true) : Enabled(last: Epoch));
        vm.Refresh();
        await UntilAsync(() => !vm.IsBusy && Field<bool>(vm, "_waitingForRun"));
        Enter(vm, 10);
        Require(reads == 1 && !vm.RefreshTaskCommand.CanExecute(null), "navigation must not duplicate a running observer");
        await UntilAsync(() => !Field<bool>(vm, "_waitingForRun"), 8000);
        Require(reads == 2 && vm.MaintenanceProgress == "维护已完成。" && vm.ShowRunTask, "observer must read completion while page is away");
        Enter(vm, 10);
        Require(reads == 2, "completed observer snapshot must survive navigation");
        return new { maintenanceReads = reads, pollIntervalSeconds = 5, navigationBlocked = vm.IsBusy };
    }

    private static async Task<object> ExplicitActionAsync()
    {
        var reads = 0;
        var actions = 0;
        var busy = new List<bool>();
        var vm = new ReportViewModel(_ => { }, busy.Add, _ => Report(1), () =>
        {
            var read = Interlocked.Increment(ref reads);
            return Enabled(last: read >= 3 ? Epoch : null);
        });
        vm.Refresh();
        await IdleAsync(vm);
        var action = (Task<bool>)typeof(ReportViewModel).GetMethod("RunTaskActionAsync", InstancePrivate)!.Invoke(vm,
            ["probe action", (Func<(bool Ok, string Message)>)(() => { Interlocked.Increment(ref actions); return (true, "probe accepted"); })])!;
        Require(await action, "explicit action wrapper must report acceptance");
        Require(actions == 1 && reads == 2 && busy.SequenceEqual(new[] { true, false }), "explicit action must re-read status and balance busy callbacks");
        var observation = (Task)typeof(ReportViewModel).GetMethod("ObserveRunAsync", InstancePrivate)!.Invoke(vm, [null, false])!;
        Enter(vm, 10);
        Require(!vm.IsBusy && !vm.RefreshTaskCommand.CanExecute(null), "active observer must permit navigation and prevent duplicate status refreshes");
        await observation.WaitAsync(TimeSpan.FromSeconds(8));
        Require(reads == 3 && vm.MaintenanceProgress == "维护已完成。", "explicit action completion must still be observed");
        return new { actions, maintenanceReads = reads, busyTransitions = busy, simulatedActionOnly = true };
    }

    private static ReportViewModel Create(Func<Action<string>, HealthReport> build, Func<MaintenanceStatus> read)
        => new(_ => { }, null, build, read);

    private static HealthReport Report(int sequence) => new()
    {
        GeneratedAt = Epoch.AddSeconds(sequence), Machine = "probe", User = "probe", Score = 90 + sequence,
        Sections = [new ReportSection { Title = $"report-{sequence}", Items = [new ReportItem { Label = "cache", Value = $"fresh|{sequence}", Level = "ok" }] }]
    };

    private static MaintenanceStatus Enabled(bool running = false, DateTime? last = null) => new()
    {
        Known = true, TaskExists = true, IsEnabled = true, IsRunning = running, LastRunAt = last, LastResultText = running ? "正在运行" : "已完成"
    };

    private static T Field<T>(ReportViewModel vm, string name) => (T)typeof(ReportViewModel).GetField(name, InstancePrivate)!.GetValue(vm)!;
    private static HealthReport CurrentReport(ReportViewModel vm) => Field<HealthReport>(vm, "_report");
    private static void Enter(ReportViewModel vm, int count) { for (var i = 0; i < count; i++) vm.Refresh(); }
    private static Task IdleAsync(ReportViewModel vm) => UntilAsync(() => !vm.IsBusy);

    private static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("probe condition did not settle");
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
