using System.Collections;
using System.IO;
using System.Reflection;
using WinKit.Core;
using WinKit.Services;
using WinKit.ViewModels;

internal static class Program
{
    private static int _checks;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinKitNavigationProbe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckShellSta();
            CheckTaskbarCache(new ChangeJournal(Path.Combine(root, "journal")));
            CheckNavigationCache(new ChangeJournal(Path.Combine(root, "journal")));
            CheckContextCache(new ChangeJournal(Path.Combine(root, "journal")));
            CheckManifestCache(Path.Combine(root, "manifests"));
            CheckProcessMatching();
            Console.WriteLine($"PASS: {_checks} assertions; synthetic snapshots/local fixtures only; no system scan or uninstall.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    private static void Set(object target, string name, object? value) =>
        target.GetType().GetField(name, Instance)!.SetValue(target, value);
    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetField(name, Instance)!.GetValue(target)!;
    private static object? Call(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, Instance)!.Invoke(target, args);
    private static object Snapshot(Type owner, string name, params object?[] args) =>
        Activator.CreateInstance(owner.GetNestedType(name, BindingFlags.NonPublic)!, args)!;

    private static void CheckShellSta()
    {
        var caller = Environment.CurrentManagedThreadId;
        var state = ShellReadWorker.RunAsync(() =>
            (Apartment: Thread.CurrentThread.GetApartmentState(), Thread: Environment.CurrentManagedThreadId,
             Background: Thread.CurrentThread.IsBackground)).GetAwaiter().GetResult();
        Check(state.Apartment == ApartmentState.STA && state.Thread != caller && state.Background,
            "Shell worker must run on its own background STA.");
        var expected = new InvalidOperationException("fixture exception");
        try { ShellReadWorker.RunAsync<int>(() => throw expected).GetAwaiter().GetResult(); }
        catch (Exception ex) { Check(ReferenceEquals(expected, ex), "STA exception must reach the caller unchanged."); return; }
        throw new InvalidOperationException("STA exception was swallowed.");
    }

    private static void CheckTaskbarCache(ChangeJournal journal)
    {
        var vm = new TaskbarViewModel(journal, _ => { });
        vm.OnDeactivated();
        vm.Refresh();
        Check(!vm.IsLoading && Get<bool>(vm, "_needsPinned") && Get<bool>(vm, "_needsTray"),
            "Hidden taskbar refresh must only invalidate.");
        var item = new TaskbarPinnedItem { Name = "fixture" };
        Set(vm, "_pendingPinned", new List<TaskbarPinnedItem> { item });
        Set(vm, "_pendingTray", new List<TrayIconItem>());
        Set(vm, "_needsPinned", false);
        Set(vm, "_needsTray", false);
        vm.OnActivated();
        Check(vm.Pinned.Count == 1 && ReferenceEquals(vm.Pinned[0], item), "Activation must commit the pending snapshot.");
        for (var i = 0; i < 20; i++) { vm.OnDeactivated(); vm.OnActivated(); }
        Check(!vm.IsLoading && ReferenceEquals(vm.Pinned[0], item), "Repeated taskbar activation must reuse the snapshot.");
        Set(vm, "_isLoading", true);
        vm.Refresh();
        Check(!Get<bool>(vm, "_needsPinned"), "An overlapping manual taskbar refresh must coalesce.");
        Set(vm, "_isLoading", false);
        vm.OnDeactivated();
    }

    private static void CheckNavigationCache(ChangeJournal journal)
    {
        var vm = new NavPaneViewModel(journal, _ => { });
        vm.OnDeactivated();
        vm.Refresh();
        Check(!vm.IsLoading && Get<bool>(vm, "_refreshNeeded"), "Hidden navigation refresh must defer reading.");
        var group = new StubbornGroup { DisplayName = "fixture" };
        Set(vm, "_pendingSnapshot", Snapshot(typeof(NavPaneViewModel), "NavigationSnapshot",
            new List<QuickAccessItem>(), null, new List<PinnedFolderItem>(), new List<NavToggleItem>(),
            new List<StubbornGroup> { group }));
        Set(vm, "_refreshNeeded", false);
        Set(vm.Taskbar, "_needsPinned", false);
        Set(vm.Taskbar, "_needsTray", false);
        vm.OnActivated();
        Check(vm.StubbornGroups.Count == 1 && ReferenceEquals(vm.StubbornGroups[0], group),
            "Navigation activation must commit only the pending snapshot.");
        for (var i = 0; i < 20; i++) { vm.SubView = "taskbar"; vm.SubView = "navpane"; }
        Check(!vm.IsLoading && !vm.Taskbar.IsLoading && ReferenceEquals(vm.StubbornGroups[0], group),
            "Switching navigation subviews must not rescan already loaded lists.");
        vm.OnDeactivated();
    }

    private static void CheckContextCache(ChangeJournal journal)
    {
        using var vm = new ContextMenuViewModel(journal, _ => { });
        vm.OnDeactivated();
        vm.Refresh();
        Check(!vm.IsLoading && Get<bool>(vm, "_refreshNeeded"), "Hidden menu refresh must only invalidate.");
        var item = new MenuEntry { DisplayName = "fixture", Domain = MenuDomainKind.File };
        Set(vm, "_pendingSnapshot", Snapshot(typeof(ContextMenuViewModel), "MenuSnapshot",
            new List<MenuEntry> { item }, new List<NewMenuItem>(), false, (0, 0)));
        Set(vm, "_refreshNeeded", false);
        vm.OnActivated();
        Check(vm.Entries.Count == 1 && ReferenceEquals(vm.Entries[0], item), "Menu activation must apply the pending snapshot.");
        for (var i = 0; i < 20; i++) { vm.OnDeactivated(); vm.OnActivated(); }
        Check(!vm.IsLoading && ReferenceEquals(vm.Entries[0], item), "Repeated menu activation must reuse the session snapshot.");
        Set(vm, "_isLoading", true);
        Call(vm, "ManualRefresh");
        Check(!Get<bool>(vm, "_refreshNeeded"), "An overlapping manual menu refresh must coalesce.");
        vm.Refresh();
        Check(Get<bool>(vm, "_refreshNeeded") && vm.IsLoading,
            "External invalidations during a read must coalesce into deferred work.");
        Set(vm, "_refreshNeeded", false);
        Set(vm, "_isLoading", false);
        var watching = new NewMenuItem { DisplayName = "fixture" };
        Call(vm, "StartResidualWatch", watching);
        vm.OnDeactivated();
        Check(Get<object?>(vm, "_uninstallTimer") is not null, "An explicitly started uninstall watch may continue while hidden.");
        Set(vm, "_uninstallDeadline", DateTime.UtcNow.AddSeconds(-1));
        ((Task)Call(vm, "CheckWatchAsync")!).GetAwaiter().GetResult();
        Check(Get<object?>(vm, "_uninstallTimer") is null && Get<object?>(vm, "_watching") is null,
            "The uninstall watch deadline must stop even while hidden.");
        Call(vm, "StartResidualWatch", watching);
        vm.Dispose();
        Check(Get<object?>(vm, "_uninstallTimer") is null, "Disposal must release the uninstall timer.");
    }

    private const string Xml = "<Package><FileExplorerContextMenus><Item Type='Directory'><Verb Clsid='abc'/></Item></FileExplorerContextMenus></Package>";
    private static ManifestScopeInfo ReadManifest(string path) =>
        (ManifestScopeInfo)typeof(AppxManifest).GetMethod("ReadCached", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [path])!;

    private static void CheckManifestCache(string root)
    {
        Directory.CreateDirectory(root);
        var cache = (IDictionary)typeof(AppxManifest).GetField("Cache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        cache.Clear();
        var path = Path.Combine(root, "AppxManifest.xml");
        File.WriteAllText(path, Xml);
        var first = ReadManifest(path);
        Check(first.Found && first.ScopeTypes.SequenceEqual(new[] { "Directory" }), "Fixture manifest must parse.");
        first.ScopeTypes.Add("poison");
        first.Clsids.Clear();
        var second = ReadManifest(path);
        Check(second.Clsids.Count == 1 && second.ScopeTypes.Count == 1, "Cached manifest lists must be copied before handing them out.");
        Check(cache.Count == 1, "Unchanged manifest must reuse one cache entry.");
        File.WriteAllText(path, Xml.Replace("Directory", "DirectoryBackground"));
        var changed = ReadManifest(path);
        Check(changed.ScopeTypes.SequenceEqual(new[] { "DirectoryBackground" }), "File length/time changes must invalidate cached data.");
        File.Delete(path);
        Check(ReadManifest(path) is null && cache.Count == 0, "Deleted manifests must not stay cached.");
        File.WriteAllText(path, Xml);
        Check(ReadManifest(path).Found, "Previously missing manifests must be retried.");
        for (var i = 0; i < 260; i++)
        {
            var fixture = Path.Combine(root, $"{i}.xml");
            File.WriteAllText(fixture, Xml);
            ReadManifest(fixture);
        }
        Check(cache.Count == 256, "Session manifest cache must remain bounded.");
    }

    private static void CheckProcessMatching()
    {
        var processes = new List<(string Path, string Label)>
        {
            (@"C:\App\main.exe", "main.exe (1)"),
            (@"C:\AppOther\wrong.exe", "wrong.exe (2)"),
            (@"C:\App\cloudsvr.exe", "cloudsvr.exe (3)")
        };
        var matches = (List<string>)typeof(StubbornNodeService).GetMethod("FindProcesses", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [@"C:\App", processes])!;
        Check(matches.Count == 2 && matches[0] == "cloudsvr.exe (3)" && !matches.Any(x => x.Contains("wrong")),
            "One process snapshot must respect directory boundaries and preserve service-first order.");
    }
}
