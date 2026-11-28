using System.Diagnostics;
using System.IO;
using System.Reflection;
using WinKit.Core;
using WinKit.Services;

internal static class Program
{
    private static int _checks;
    private static string _fixtureRoot = "";

    private static int Main()
    {
        _fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_fixtureRoot);
        try
        {
            CheckSnapshotCache();
            CheckInvalidationDuringRead();
            CheckServiceSnapshotConsumers();
            CheckScoopManifests();
            CheckExecutableSearch();
            Console.WriteLine($"PASS: {_checks} deterministic application-data checks; no installed-software scan or package command ran.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            // The unique absolute path is created above, inside this probe's output folder.
            if (Path.GetDirectoryName(_fixtureRoot) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))
                Directory.Delete(_fixtureRoot, true);
        }
    }

    private static InstalledSoftwareEntry Entry(string name, string version = "1.0", string uninstall = "remove.exe",
        bool system = false, string parent = "")
        => new(HiveKind.CurrentUser, "Fixture/" + name, name, version, "Fixture", _fixtureRoot,
            uninstall, "fixture.exe", parent, system, 1024, "20261127");

    private static void CheckSnapshotCache()
    {
        var uninstallReads = 0;
        var storeReads = 0;
        var appPathReads = 0;
        var source = new List<InstalledSoftwareEntry> { Entry("first") };
        var cache = new InstalledSoftwareSnapshotCache(
            () => { Interlocked.Increment(ref uninstallReads); return source; },
            () => { storeReads++; return Array.Empty<InstalledStorePackage>(); },
            () => { appPathReads++; return new Dictionary<string, string> { ["test.exe"] = "first" }; });
        Check("construction performs no provider work", uninstallReads + storeReads + appPathReads == 0);
        Parallel.For(0, 32, _ => cache.GetUninstallEntries());
        Check("concurrent consumers share one uninstall read", uninstallReads == 1);
        var first = cache.GetUninstallEntries();
        source.Add(Entry("second"));
        Check("cache owns an immutable copy", first.Count == 1);
        Check("unused Store and App Paths stay lazy", storeReads == 0 && appPathReads == 0);
        Check("same generation returns same record list", ReferenceEquals(first, cache.GetUninstallEntries()));
        cache.GetStorePackages(); cache.GetStorePackages();
        var paths = cache.GetAppPaths(); cache.GetAppPaths();
        Check("each other provider reads once", storeReads == 1 && appPathReads == 1);
        Check("App Paths lookup stays case insensitive", paths.ContainsKey("TEST.EXE"));
        cache.Invalidate();
        Check("invalidation itself performs no provider work", uninstallReads == 1 && storeReads == 1 && appPathReads == 1);
        Check("new generation sees fresh records", cache.GetUninstallEntries().Count == 2 && uninstallReads == 2);
        cache.GetStorePackages(); cache.GetAppPaths();
        Check("invalidation clears all three providers", storeReads == 2 && appPathReads == 2);

        var attempts = 0;
        var retry = new InstalledSoftwareSnapshotCache(
            () => ++attempts == 1 ? throw new IOException("fixture transient") : Array.Empty<InstalledSoftwareEntry>(),
            () => Array.Empty<InstalledStorePackage>(), () => new Dictionary<string, string>());
        try { retry.GetUninstallEntries(); } catch (IOException) { }
        Check("provider failure can be retried", retry.GetUninstallEntries().Count == 0 && attempts == 2);
    }

    private static void CheckInvalidationDuringRead()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var cache = new InstalledSoftwareSnapshotCache(() =>
        {
            var number = Interlocked.Increment(ref reads);
            if (number == 1) { entered.Set(); release.Wait(); }
            return new[] { Entry("generation-" + number) };
        }, () => Array.Empty<InstalledStorePackage>(), () => new Dictionary<string, string>());
        var old = Task.Run(cache.GetUninstallEntries);
        Check("fixture provider starts in background", entered.Wait(TimeSpan.FromSeconds(5)));
        var invalidation = Task.Run(cache.Invalidate);
        bool completed;
        try { completed = invalidation.Wait(TimeSpan.FromSeconds(1)); }
        finally { release.Set(); }
        Task.WaitAll(old, invalidation);
        Check("invalidate does not wait for slow provider", completed);
        Check("old in-flight read completes for its caller", old.Result[0].Name == "generation-1");
        Check("old result cannot overwrite new cache generation", cache.GetUninstallEntries()[0].Name == "generation-2");
    }

    private static void CheckServiceSnapshotConsumers()
    {
        var reads = 0;
        var records = new[]
        {
            Entry("visible"), Entry("visible"), Entry("system-component", system: true),
            Entry("child-component", parent: "parent"), Entry("no-uninstall", uninstall: "")
        };
        var store = new[]
        {
            new InstalledStorePackage(HiveKind.CurrentUser, "Fixture/Store", "Vendor.Reader_1.0_x64__test", "Reader", ""),
            new InstalledStorePackage(HiveKind.LocalMachine, "Fixture/Duplicate", "Vendor.Reader_1.0_x64__test", "Reader", ""),
            new InstalledStorePackage(HiveKind.CurrentUser, "Fixture/Infrastructure", "Microsoft.WindowsAppRuntime.1.6_6000.1_x64__test", "Infrastructure", "")
        };
        var cache = new InstalledSoftwareSnapshotCache(() => { reads++; return records; },
            () => store, () => new Dictionary<string, string>());
        var apps = new AppManagerService(new ChangeJournal(), cache, () => new List<ScoopPackage>());
        var loaded = apps.LoadAll();
        Check("application filters hide system, child and non-removable records", loaded.Count(a => a.Kind == AppSourceKind.Desktop) == 1);
        Check("Store duplicate and infrastructure filtering is preserved", loaded.Count(a => a.Kind == AppSourceKind.Store) == 1);
        Check("raw snapshot retains all runtime/tool input records", cache.GetUninstallEntries().Count == 5);
        var runtimeService = new RuntimeCheckService(cache);
        var runtimes = Invoke<List<(string Name, string Version)>>(runtimeService, "LoadAllUninstallEntriesIncludingSystem");
        Check("runtime consumer still sees SystemComponent and children", runtimes.Count == 5 && runtimes.Any(x => x.Name == "system-component"));
        var runtimeItems = new List<RuntimeComponentItem>();
        Invoke<object?>(runtimeService, "AddWindowsAppRuntime", runtimeItems);
        Check("runtime reads infrastructure excluded from application list", runtimeItems.Single().IsInstalled && runtimeItems.Single().InstalledVersion == "6000.1");
        var toolEntries = Invoke<List<(string Name, string Version, string Location, string IconExe)>>(
            new SystemToolCatalogService(cache), "LoadUninstallSnapshot");
        Check("tool consumer keeps unfiltered source records", toolEntries.Count == 5);
        Check("three services reuse one original uninstall read", reads == 1);
        cache.Invalidate(); apps.LoadAll();
        Check("service reload after owner invalidation reads again", reads == 2);
    }

    private static T Invoke<T>(object instance, string method, params object[] args)
        => (T)instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args)!;

    private static void CheckScoopManifests()
    {
        var root = Path.Combine(_fixtureRoot, "scoop");
        void Write(string relative, string json)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
        void App(string name, string file, string json) => Write($"apps/{name}/current/{file}", json);
        App("newstyle", "scoop-manifest.json", """{"version":"1.0"}""");
        App("oldstyle", "manifest.json", """{"version":"2.0"}""");
        App("bucketless", "scoop-manifest.json", """{"version":"3.0"}""");
        Directory.CreateDirectory(Path.Combine(root, "apps", "selfboot", "current"));
        App("pinned", "scoop-manifest.json", """{"version":"4.0"}""");
        App("pinned", "scoop-install.json", """{"bucket":"extras"}""");
        App("broken", "scoop-manifest.json", """{"version":"5.0","bucket":"main"}""");
        App("fallback", "scoop-manifest.json", "{ invalid");
        App("fallback", "manifest.json", """{"version":"6.0","bucket":"main"}""");
        App("missing", "manifest.json", """{"version":"7.0","bucket":"missing-bucket"}""");
        Directory.CreateDirectory(Path.Combine(root, "apps", "no-current"));
        Write("buckets/main/bucket/newstyle.json", """{"version":"1.1"}""");
        Write("buckets/main/bucket/oldstyle.json", """{"version":"2.0"}""");
        Write("buckets/main/bucket/bucketless.json", """{"version":"3.0"}""");
        Write("buckets/main/bucket/pinned.json", """{"version":"99.0"}""");
        Write("buckets/extras/bucket/pinned.json", """{"version":"4.1"}""");
        Write("buckets/main/bucket/broken.json", "{ invalid");
        Write("buckets/main/bucket/fallback.json", """{"version":"6.0"}""");
        Write("buckets/main/bucket/missing.json", """{"version":"99.0"}""");
        for (var i = 0; i < 40; i++) Write($"buckets/main/bucket/unrelated-{i}.json", "{ invalid");
        var reads = new List<string>();
        var service = new ScoopService(new[] { root }, path => { reads.Add(path); return File.ReadAllText(path); });
        var packages = service.Load();
        ScoopPackage Package(string name) => packages.Single(x => x.Name == name);
        Check("Scoop both manifest filenames stay supported", Package("newstyle").Version == "1.0" && Package("oldstyle").Version == "2.0");
        Check("Scoop compares exact installed package version", Package("newstyle").HasUpdate && Package("newstyle").LatestVersion == "1.1");
        Check("Scoop equal version is not update", !Package("oldstyle").HasUpdate);
        Check("Scoop missing bucket metadata can be inferred", Package("bucketless").Bucket == "main");
        Check("Scoop self-bootstrap unknown version is not update", Package("selfboot").Version == "版本未知" && !Package("selfboot").HasUpdate);
        Check("Scoop install metadata selects its own bucket", Package("pinned").LatestVersion == "4.1" && Package("pinned").Bucket == "extras");
        Check("Scoop does not borrow same-name package from different bucket", !Package("missing").HasUpdate);
        Check("Scoop corrupt bucket falls back to installed version", !Package("broken").HasUpdate && Package("broken").LatestVersion == "5.0");
        Check("Scoop corrupt modern manifest falls back to legacy filename", Package("fallback").Version == "6.0");
        Check("Scoop skips directories without current", packages.All(x => x.Name != "no-current"));
        Check("Scoop never reads unrelated bucket manifests", reads.All(path => !Path.GetFileName(path).StartsWith("unrelated-")));
        Check("Scoop known origin never reads other-bucket duplicate", !reads.Contains(Path.Combine(root, "buckets", "main", "bucket", "pinned.json")));
        var bucketReads = reads.Count(path => path.Contains(Path.DirectorySeparatorChar + "buckets" + Path.DirectorySeparatorChar));
        Check("Scoop bucket reads scale with installed targets", bucketReads == 6);
        Write("buckets/main/bucket/newstyle.json", """{"version":"1.2"}""");
        Check("Scoop fresh scan sees changed target manifest", service.Load().Single(x => x.Name == "newstyle").LatestVersion == "1.2");
    }

    private static void CheckExecutableSearch()
    {
        var directory = Path.Combine(_fixtureRoot, "tool");
        var started = 0;
        var visited = 0;
        var disposed = 0;
        IEnumerable<string> Enumerate(string path)
        {
            started++;
            try
            {
                foreach (var name in new[] { "beta.exe", "unrelated.txt", "alpha.exe", "extra.exe" })
                {
                    visited++;
                    yield return Path.Combine(path, name);
                }
            }
            finally { disposed++; }
        }
        var candidates = new[] { "missing.exe", "alpha.exe", "beta.exe" };
        using (var search = new ToolExecutableSearch(candidates, Enumerate))
        {
            Check("tool finds recursive requested executable", search.Find(directory, new[] { "alpha.exe" }).EndsWith("alpha.exe"));
            Check("tool search stops after match", started == 1 && visited == 3 && disposed == 0);
            Check("tool caches earlier candidate from same traversal", search.Find(directory + Path.DirectorySeparatorChar + ".", new[] { "BETA.EXE" }).EndsWith("beta.exe") && visited == 3);
            Check("tool caches missing candidate", search.Find(directory, new[] { "missing.exe" }) == "" && search.Find(directory, new[] { "missing.exe" }) == "");
            Check("tool scans same normalized directory only once", started == 1 && visited == 4 && disposed == 1);
        }
        using (var search = new ToolExecutableSearch(candidates, Enumerate))
        {
            search.Find(directory, new[] { "beta.exe" });
            Check("tool new detection has its own fresh directory scan", started == 2);
        }
        Check("tool early-stop enumerator is disposed at detection end", disposed == 2);
        using (var search = new ToolExecutableSearch(candidates, Enumerate))
            Check("tool respects candidate order with cached fallback", search.Find(directory, new[] { "missing.exe", "beta.exe" }).EndsWith("beta.exe"));
        using (var search = new ToolExecutableSearch(candidates, _ => throw new UnauthorizedAccessException()))
            Check("tool inaccessible directory is non-fatal", search.Find(directory, candidates) == "");

        var catalog = (Array)typeof(SystemToolCatalogService)
            .GetField("Catalog", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var allCandidates = catalog.Cast<object>()
            .SelectMany(spec => (string[])spec.GetType().GetProperty("ExeCandidates")!.GetValue(spec)!).ToArray();
        var allNames = allCandidates.Select(path => Path.GetFileName(Environment.ExpandEnvironmentVariables(path)))
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var catalogScans = 0;
        IEnumerable<string> EnumerateCatalog(string path)
        {
            catalogScans++;
            foreach (var name in allNames) yield return Path.Combine(path, name);
        }
        using (var search = new ToolExecutableSearch(allCandidates, EnumerateCatalog))
        {
            // Consume to the last target first, so subsequent requests must come from the target-name index.
            Check("tool catalog target index includes every configured filename", allNames.Length > 10
                && allNames.Reverse().All(name => search.Find(directory, new[] { name }).EndsWith(name, StringComparison.OrdinalIgnoreCase)));
            Check("complete tool catalog candidate set shares one enumeration", catalogScans == 1);
        }
        Console.WriteLine($"Tool target coverage: {catalog.Length} catalog entries, {allNames.Length} unique candidate filenames.");
    }

    private static void Check(string description, bool passed)
    {
        if (!passed) throw new InvalidOperationException("FAIL: " + description);
        _checks++;
    }
}
