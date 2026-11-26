using WinKit.Services;

namespace WinKit.UpdateInstallerProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--fixture")
        {
            Console.Error.WriteLine("Usage: dotnet run --project tools/UpdateInstallerProbe/UpdateInstallerProbe.csproj -- --fixture <published self-contained WinKit.exe>");
            return 2;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This probe requires Windows for actual pinned handles, rename and reparse-point semantics.");
            return 2;
        }
        var failures = 0;
        string? root = null;
        try
        {
            var fixture = await Fixture.LoadAsync(args[1]);
            root = Path.Combine(Path.GetTempPath(), "WinKit-UpdateInstallerProbe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Console.WriteLine($"Read-only fixture {fixture.Version}; {fixture.Size:N0} bytes. No fixture is executed.");
            var cases = new (string Name, Func<Task> Run)[]
            {
                ("recovery paths are normalized, bounded and isolated", () => RecoveryPathsAsync(root)),
                ("renamed handles retain exact path and file identity", () => RenameIdentityAsync(root)),
                ("successful original-path replacement and complete cleanup", () => SuccessAsync(root, fixture)),
                ("prepared handles prevent target, staging and ancestor mutation", () => PinsAsync(root, fixture)),
                ("Dispose without Apply preserves original and cleans protection", () => AbandonAsync(root, fixture)),
                ("cancelled Prepare preserves the original", () => CancelPrepareAsync(root, fixture)),
                ("cancelled Apply preserves the original", () => CancelApplyAsync(root, fixture)),
                ("wrong original hash rejects before replacement", () => WrongOldHashAsync(root, fixture)),
                ("tampered package rejects without replacing original", () => TamperedPackageAsync(root, fixture)),
                ("trusted digest cannot bypass candidate version validation", () => WrongVersionAsync(root, fixture)),
                ("locked original fails safely", () => LockedTargetAsync(root, fixture)),
                ("locked package fails safely", () => LockedPackageAsync(root, fixture)),
                ("late destination lock retains original and recovery protection", () => LockedApplyAsync(root, fixture)),
                ("hard-linked original is rejected without changing either name", () => HardLinkedTargetAsync(root, fixture)),
                ("hard-linked package is rejected without changing source", () => HardLinkedPackageAsync(root, fixture)),
                ("reparse ancestor of original is rejected", () => JunctionTargetAsync(root, fixture)),
                ("reparse ancestor of package is rejected", () => JunctionPackageAsync(root, fixture))
            };
            foreach (var (name, run) in cases)
            {
                var before = Assert.Count;
                try
                {
                    await run();
                    Console.WriteLine($"PASS {name} ({Assert.Count - before} assertions)");
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.Error.WriteLine("FAIL " + name + Environment.NewLine + ex);
                    WriteFailureDetails(ex);
                }
            }
            Assert.Equal(fixture.Hash, await Fixture.HashAsync(fixture.Path), "The caller's original fixture remains unchanged");
            Console.WriteLine($"UpdateInstallerProbe: {cases.Length - failures}/{cases.Length} groups passed; {Assert.Count} assertions evaluated.");
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine("Probe setup/final verification failed: " + ex);
        }
        finally
        {
            if (root is not null)
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception ex) { failures++; Console.Error.WriteLine("Owned temporary-root cleanup failed: " + ex); }
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static void WriteFailureDetails(Exception error)
    {
        foreach (System.Collections.DictionaryEntry entry in error.Data)
            Console.Error.WriteLine($"  {entry.Key}: {entry.Value}");
        if (error is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions) WriteFailureDetails(inner);
        else if (error.InnerException is not null) WriteFailureDetails(error.InnerException);
    }

    private static async Task RecoveryPathsAsync(string root)
    {
        var target = Path.Combine(root, "WinKit.exe");
        var id = Guid.NewGuid().ToString("N");
        var allowed = AppUpdateInstallerTransaction.ProtectionPathsFor(target, id);
        var message = new AppUpdateInstallerMessage { TransactionId = id, RecoveryPaths = allowed };
        var result = AppUpdateInstaller.ValidateRecoveryPaths(message, target, id);
        Assert.True(result.SequenceEqual(allowed), "Both derived protection paths are accepted");
        result[0] = "changed";
        Assert.Equal(allowed[0], message.RecoveryPaths[0], "Validated paths never alias the wire array");
        Assert.Equal(0, AppUpdateInstaller.ValidateRecoveryPaths(message with { RecoveryPaths = [] }, target, id).Length, "Empty recovery paths are valid");
        var alias = Path.Combine(root, ".", Path.GetFileName(allowed[0]));
        Assert.Equal(allowed[0], AppUpdateInstaller.ValidateRecoveryPaths(message with { RecoveryPaths = [alias] }, target, id)[0], "A single canonical alias is normalized");
        async Task RejectAsync(AppUpdateInstallerMessage input) => await Assert.ThrowsAsync<InvalidDataException>(() =>
        { AppUpdateInstaller.ValidateRecoveryPaths(input, target, id); return Task.CompletedTask; });
        await RejectAsync(message with { RecoveryPaths = [allowed[0], alias] });
        await RejectAsync(message with { RecoveryPaths = [allowed[0], allowed[0].ToUpperInvariant()] });
        await RejectAsync(message with { TransactionId = Guid.NewGuid().ToString("N") });
        await RejectAsync(message with { RecoveryPaths = [Path.Combine(root, "other", Path.GetFileName(allowed[0]))] });
        await RejectAsync(message with { RecoveryPaths = [allowed[0] + ".new"] });
        await RejectAsync(message with { RecoveryPaths = AppUpdateInstallerTransaction.ProtectionPathsFor(target, Guid.NewGuid().ToString("N")) });
        await RejectAsync(message with { RecoveryPaths = [.. allowed, allowed[0]] });
        await RejectAsync(message with { RecoveryPaths = null! });
    }

    private static async Task RenameIdentityAsync(string root)
    {
        var directory = Path.Combine(root, "rename-identity");
        Directory.CreateDirectory(directory);
        var stage = Path.Combine(directory, ".winkit-update-" + Guid.NewGuid().ToString("N") + ".new");
        var target = Path.Combine(directory, "WinKit renamed 中文.exe");
        using var pins = new AppUpdateInstallerNative.DirectoryPins(stage);
        using var staged = AppUpdateInstallerNative.CreateNewPinned(stage);
        await staged.WriteAsync(new byte[] { 1, 2, 3, 4 });
        staged.Flush(true);
        var identity = AppUpdateInstallerNative.Inspect(staged, stage);
        for (var i = 0; i < 128; i++)
        {
            var destination = i % 2 == 0 ? target : stage;
            AppUpdateInstallerNative.RenamePinned(staged, destination);
            Assert.Equal(identity, AppUpdateInstallerNative.Inspect(staged, destination), "Renaming retains the exact file and its expected path");
            Assert.True(File.Exists(destination), "Renaming creates the expected destination entry");
        }
        AppUpdateInstallerNative.DeletePinned(staged);
    }

    private static async Task SuccessAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        using (var transaction = await w.PrepareAsync())
        {
            await w.AssertOriginalAsync();
            Assert.Equal(2, w.TemporaryFiles().Length, "Ready must retain stage and temporary original protection");
            await transaction.ApplyAsync(0, CancellationToken.None);
            Assert.True(File.Exists(w.Target), "Replacement retains the caller's original path and renamed filename.");
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.ApplyAsync(0, CancellationToken.None));
        }
        Assert.Equal(fixture.Hash, await Fixture.HashAsync(w.Target), "The original pathname now contains the entire candidate bundle");
        Assert.Equal(fixture.Size, new FileInfo(w.Target).Length, "Installed size is exact");
        Assert.Equal(fixture.Hash, await Fixture.HashAsync(w.Package), "Applying must not alter or consume the downloaded package");
        Assert.Equal(1, Directory.EnumerateFiles(w.AppDirectory).Count(), "No DLL, runtime, backup or stage is left beside the single EXE");
        w.AssertClean();
    }

    private static async Task PinsAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        using (var transaction = await w.PrepareAsync())
        {
            await w.AssertOriginalAsync();
            var temporary = w.TemporaryFiles();
            Assert.Equal(2, temporary.Length, "Prepare creates two temporary protected objects");
            Assert.Blocked(() => { using var file = new FileStream(w.Target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
            Assert.Blocked(() => File.Delete(w.Target));
            foreach (var path in temporary)
            {
                Assert.Blocked(() => { using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
                Assert.Blocked(() => File.Delete(path));
            }
            Assert.Blocked(() => Directory.Move(w.AppDirectory, w.AppDirectory + "-renamed"));
        }
        await w.AssertOriginalAsync();
        w.AssertClean();
    }

    private static async Task AbandonAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        var transaction = await w.PrepareAsync();
        Assert.Equal(2, w.TemporaryFiles().Length, "Prepared transaction has temporary protection");
        transaction.Dispose();
        transaction.Dispose();
        await w.AssertOriginalAsync();
        w.AssertClean();
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.ApplyAsync(0, CancellationToken.None));
    }

    private static async Task CancelPrepareAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => { using var unexpected = await w.PrepareAsync(cancellation.Token); });
        await w.AssertOriginalAsync();
        w.AssertClean();
    }

    private static async Task CancelApplyAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        using (var transaction = await w.PrepareAsync())
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => transaction.ApplyAsync(0, cancellation.Token));
            await w.AssertOriginalAsync();
        }
        await w.AssertOriginalAsync();
        w.AssertClean();
    }

    private static async Task WrongOldHashAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { using var unexpected = await w.PrepareAsync(oldHash: new string('0', 64)); });
        await w.AssertOriginalAsync();
        w.AssertClean();
    }

    private static async Task TamperedPackageAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        using (var file = new FileStream(w.Package, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            file.Position = file.Length - 1;
            var last = file.ReadByte();
            file.Position--;
            file.WriteByte((byte)(last ^ 0x5a));
        }
        Assert.Equal(fixture.Size, new FileInfo(w.Package).Length, "Tampered package keeps the trusted byte length");
        await Assert.ThrowsAsync<InvalidDataException>(async () => { using var unexpected = await w.PrepareAsync(); });
        await w.AssertOriginalAsync();
        w.AssertClean();
    }

    private static async Task WrongVersionAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        var candidate = new Version(fixture.Version.Major, fixture.Version.Minor, fixture.Version.Build + 1);
        var trusted = fixture.Release(candidate);
        Assert.Equal(fixture.Hash, trusted.Sha256, "Wrong-version payload still has the correctly trusted digest");
        await Assert.ThrowsAsync<InvalidDataException>(async () => { using var unexpected = await w.PrepareAsync(release: trusted); });
        await w.AssertOriginalAsync();
        w.AssertClean();
    }

    private static async Task LockedTargetAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        using (var locked = new FileStream(w.Target, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.RejectedAsync(async () => { using var unexpected = await w.PrepareAsync(); });
        await w.AssertOriginalAsync();
        w.AssertClean();
    }

    private static async Task LockedPackageAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        using (var locked = new FileStream(w.Package, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.RejectedAsync(async () => { using var unexpected = await w.PrepareAsync(); });
        await w.AssertOriginalAsync();
        Assert.Equal(fixture.Hash, await Fixture.HashAsync(w.Package), "Locked package remains intact");
        w.AssertClean();
    }

    private static async Task LockedApplyAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        using (var transaction = await w.PrepareAsync())
        using (var noDeleteShare = new FileStream(w.Target, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.RejectedAsync(() => transaction.ApplyAsync(0, CancellationToken.None));
        await w.AssertOriginalAsync();
        var protection = w.TemporaryFiles();
        Assert.True(protection.All(path => Path.GetExtension(path) == ".original"), "A failed late pin must not leave a ready stage or displaced old pathname.");
        Assert.True(protection.Length <= 1, "Only this failed transaction's temporary recovery protection may remain.");
        foreach (var path in protection)
            Assert.Equal(w.OldHash, await Fixture.HashAsync(path), "Retained recovery protection has the exact original bytes");
    }

    private static async Task HardLinkedTargetAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        var alias = Path.Combine(w.Root, "old-hardlink.exe");
        NativeLinks.HardLink(alias, w.Target);
        await Assert.ThrowsAsync<InvalidDataException>(async () => { using var unexpected = await w.PrepareAsync(); });
        await w.AssertOriginalAsync();
        Assert.Equal(w.OldHash, await Fixture.HashAsync(alias), "Rejecting the old hard link preserves both directory entries");
        w.AssertClean();
    }

    private static async Task HardLinkedPackageAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        var alias = Path.Combine(w.Root, "package-hardlink.exe");
        NativeLinks.HardLink(alias, w.Package);
        await Assert.ThrowsAsync<InvalidDataException>(async () => { using var unexpected = await w.PrepareAsync(); });
        await w.AssertOriginalAsync();
        Assert.Equal(fixture.Hash, await Fixture.HashAsync(w.Package), "Hard-linked package remains unchanged");
        Assert.Equal(fixture.Hash, await Fixture.HashAsync(alias), "The package's other directory entry remains unchanged");
        w.AssertClean();
    }

    private static async Task JunctionTargetAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        var alias = w.JunctionTo(w.AppDirectory);
        Assert.True((File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0, "Fixture must be an actual Windows reparse point.");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { using var unexpected = await w.PrepareAsync(target: Path.Combine(alias, Path.GetFileName(w.Target))); });
        await w.AssertOriginalAsync();
        w.AssertClean();
    }

    private static async Task JunctionPackageAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        var alias = w.JunctionTo(w.PackageDirectory);
        Assert.True((File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0, "Fixture must be an actual Windows reparse point.");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { using var unexpected = await w.PrepareAsync(package: Path.Combine(alias, Path.GetFileName(w.Package))); });
        await w.AssertOriginalAsync();
        Assert.Equal(fixture.Hash, await Fixture.HashAsync(w.Package), "Rejected reparse path leaves the actual package intact");
        w.AssertClean();
    }
}
