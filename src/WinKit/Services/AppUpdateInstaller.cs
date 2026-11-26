using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using static WinKit.Services.AppUpdateInstallerNative;

namespace WinKit.Services;

public sealed record AppUpdateInstallNotice(bool Updated, string VersionText, string Message);

/// <summary>Prepares an authenticated, windowless copy of the current single EXE. Never closes the caller's UI.</summary>
public sealed class AppUpdateInstaller
{
    public const string HelperSwitch = "--winkit-update-helper";
    public const string ResultSwitch = "--winkit-update-result";
    private static readonly TimeSpan PreparationTimeout = TimeSpan.FromMinutes(5);

    public AppUpdateInstaller() { }

    public static bool IsHelperInvocation(string[] args) => args.Contains(HelperSwitch, StringComparer.Ordinal);

    public static bool TryReadRestartNotice(string[] args, out AppUpdateInstallNotice? notice)
    {
        notice = null;
        if (args.Length != 4 || args[0] != ResultSwitch || args[1] is not ("updated" or "failed") ||
            !AppUpdateVersion.TryParse(args[2], out _, out var prerelease) || prerelease ||
            args[3] is not ("complete" or "replace-failed" or "restart-failed" or "verification-failed")) return false;
        if ((args[1] == "updated") != (args[3] == "complete")) return false;
        notice = new(args[1] == "updated", args[2], MessageForCode(args[3]));
        return true;
    }

    public static bool IsUpdateInProgressForCurrentExecutable()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return AppUpdateInstallerLease.IsHeld(NormalizePath(Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径。")));
    }

    public async Task<AppUpdateRestartSession> PrepareRestartAsync(AppUpdateDownload download,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(download);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("原路径更新仅支持 Windows 11 x64 单文件版本。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PreparationTimeout); var ct = timeout.Token;
        using var parent = new ProcessIdentity(Environment.ProcessId);
        var target = parent.Path;
        if (!Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            !IsSingleFileDeployment())
            throw new InvalidOperationException("请使用 WinKit 正式发布的独立单 EXE 版本进行原路径更新。");
        using var targetDirectories = new DirectoryPins(target);
        using var original = OpenReadPinned(target);
        var oldHash = await HashAsync(original, ct).ConfigureAwait(false);
        var package = NormalizePath(download.FilePath);
        if (SamePath(package, target)) throw new InvalidDataException("下载文件不能是正在运行的程序。");
        var trusted = await ResolveTrustedAsync(download.Release.VersionText, target, ct).ConfigureAwait(false);
        if (!string.Equals(trusted.Sha256, download.Release.Sha256, StringComparison.OrdinalIgnoreCase) ||
            trusted.Size != download.Release.Size || trusted.DownloadUri != download.Release.DownloadUri)
            throw new InvalidDataException("发布资产信息已变化，请重新检查并下载更新。");
        using (var packageDirectories = new DirectoryPins(package))
        using (var file = OpenReadPinned(package))
        using (var service = new AppUpdateService())
            if (!await service.VerifyDownloadAsync(package, trusted, ct).ConfigureAwait(false))
                throw new InvalidDataException("下载的更新文件校验失败，请重新下载。");
        EnsureNoOtherInstances(target, parent.Pid);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinKit", "Updates", "Helpers");
        Directory.CreateDirectory(directory);
        var helperPath = NormalizePath(Path.Combine(directory, "WinKit-update-" + Guid.NewGuid().ToString("N") + ".exe"));
        DirectoryPins? helperDirectories = null; FileStream? helperPin = null;
        NamedPipeServerStream? server = null; Process? child = null; AppUpdateInstallerPipe? channel = null;
        try
        {
            helperDirectories = new DirectoryPins(helperPath);
            await using (var helper = new FileStream(helperPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                _ = Inspect(helper, helperPath);
                original.Position = 0; await original.CopyToAsync(helper, ct).ConfigureAwait(false);
                await helper.FlushAsync(ct).ConfigureAwait(false); helper.Flush(true);
            }
            helperPin = OpenReadPinned(helperPath);
            if (!string.Equals(await HashAsync(helperPin, ct).ConfigureAwait(false), oldHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新助手副本校验失败。");
            var pipeName = AppUpdateInstallerPipe.PipeName(); var capability = AppUpdateInstallerPipe.Secret();
            server = AppUpdateInstallerPipe.CreateServer(pipeName, false);
            var arguments = new AppUpdateInstallerArguments("coordinator", pipeName, capability, parent.Pid, parent.StartedUtcTicks, oldHash);
            child = StartHelper(helperPath, arguments, false);
            await AppUpdateInstallerPipe.AuthenticateClientAsync(server, child, helperPath, oldHash, ct).ConfigureAwait(false);
            channel = new(server, capability);
            await channel.SendAsync(new() { Kind = "prepare", ParentPid = parent.Pid, ParentStartedUtcTicks = parent.StartedUtcTicks,
                Target = target, OriginalHash = oldHash, PackagePath = package, VersionText = trusted.VersionText }, ct).ConfigureAwait(false);
            await channel.ExpectAsync("ready", ct).ConfigureAwait(false);
            // Returning this session is the only permission for the caller to begin its final idle/close checks.
            var session = new AppUpdateRestartSession(channel, child, helperPin, helperDirectories, helperPath);
            channel = null; server = null; child = null; helperPin = null; helperDirectories = null;
            return session;
        }
        catch
        {
            if (channel is not null) { try { await channel.SendAsync(new() { Kind = "cancel" }, CancellationToken.None).ConfigureAwait(false); } catch { } }
            channel?.Dispose(); server?.Dispose(); child?.Dispose(); helperPin?.Dispose(); helperDirectories?.Dispose();
            AppUpdateInstallerTransaction.TryDelete(helperPath);
            throw;
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty Assembly.Location is intentionally used to reject non-single-file deployments before starting an updater.")]
    private static bool IsSingleFileDeployment() => string.IsNullOrEmpty(typeof(AppUpdateInstaller).Assembly.Location);

    /// <summary>App must call this before WPF creates a window. All malformed internal invocations fail closed.</summary>
    public static async Task<int> RunHelperAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !AppUpdateInstallerArguments.TryParse(args, out var parsed) || parsed is null) return 2;
        string? selfPath = null;
        try
        {
            using var parent = new ProcessIdentity(parsed.ParentPid, parsed.ParentStartedUtcTicks);
            using var self = new ProcessIdentity(Environment.ProcessId);
            if (SamePath(self.Path, parent.Path) && parsed.Role == "coordinator") return 2;
            using var selfDirectories = new DirectoryPins(self.Path);
            using var selfImage = OpenReadPinned(self.Path);
            if (!string.Equals(await HashAsync(selfImage, cancellationToken).ConfigureAwait(false), parsed.OriginalHash, StringComparison.OrdinalIgnoreCase)) return 2;
            using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectionTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var client = await AppUpdateInstallerPipe.ConnectAsync(parsed.PipeName, parsed.Role == "applier", connectionTimeout.Token).ConfigureAwait(false);
            if (PipePeerPid(client.SafePipeHandle, false) != parent.Pid) return 2;
            using (var parentImage = OpenReadPinned(parent.Path))
                if (!string.Equals(await HashAsync(parentImage, cancellationToken).ConfigureAwait(false), parsed.OriginalHash, StringComparison.OrdinalIgnoreCase)) return 2;
            using var channel = new AppUpdateInstallerPipe(client, parsed.Capability);
            var request = await channel.ExpectAsync("prepare", connectionTimeout.Token).ConfigureAwait(false);
            if (parsed.Role == "coordinator" ? !SamePath(NormalizePath(request.Target), parent.Path) :
                !SamePath(self.Path, parent.Path) || SamePath(NormalizePath(request.Target), self.Path)) return 2;
            selfPath = self.Path; // only an authenticated generated helper is eligible for self-cleanup
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preparation.CancelAfter(PreparationTimeout);
            try
            {
                if (parsed.Role == "coordinator")
                    return await RunCoordinatorAsync(channel, request, parsed, parent, self, preparation.Token, cancellationToken).ConfigureAwait(false);
                return await RunApplierAsync(channel, request, parsed, parent, self, preparation.Token, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { await channel.SendAsync(new() { Kind = "error", Code = CodeFor(ex) }, CancellationToken.None).ConfigureAwait(false); } catch { }
                return ex is OperationCanceledException ? 3 : 1;
            }
        }
        catch { return 2; }
        finally
        {
            // The running image is deleted by a kernel delete-on-close handle after its image section is released.
            // No second executable, command shell or reboot task is installed for cleanup.
            if (selfPath is not null) TryDeleteOwnHelper(selfPath);
        }
    }

    private static async Task<int> RunCoordinatorAsync(AppUpdateInstallerPipe upstream, AppUpdateInstallerMessage request,
        AppUpdateInstallerArguments args, ProcessIdentity original, ProcessIdentity self, CancellationToken preparation, CancellationToken lifetime)
    {
        if (request.ParentPid != original.Pid || request.ParentStartedUtcTicks != original.StartedUtcTicks ||
            !SamePath(NormalizePath(request.Target), original.Path) || !string.Equals(request.OriginalHash, args.OriginalHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新目标不是发起更新的 WinKit 程序。");
        var package = NormalizePath(request.PackagePath);
        if (SamePath(package, original.Path) || SamePath(package, self.Path)) throw new InvalidDataException("更新下载路径无效。");
        using var lease = await AppUpdateInstallerLease.AcquireAsync(original.Path, preparation).ConfigureAwait(false);
        using var directories = new DirectoryPins(original.Path);
        using var originalImage = OpenReadPinned(original.Path);
        if (!string.Equals(await HashAsync(originalImage, preparation).ConfigureAwait(false), args.OriginalHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("原程序文件已变化。");
        EnsureNoOtherInstances(original.Path, original.Pid);
        var trusted = await ResolveTrustedAsync(request.VersionText, original.Path, preparation).ConfigureAwait(false);
        AppUpdateInstallerTransaction? transaction = null;
        AppUpdateInstallerPipe? worker = null;
        NamedPipeServerStream? workerServer = null;
        Process? workerProcess = null;
        ProcessIdentity? workerIdentity = null;
        var commitAccepted = false;
        var transactionId = "";
        string[] recoveryPaths = [];
        var recoveryConfirmed = false;
        try
        {
            if (CanReplace(original.Path))
            {
                transaction = await AppUpdateInstallerTransaction.PrepareAsync(original.Path, args.OriginalHash, package,
                    trusted, original.Pid, 0, preparation).ConfigureAwait(false);
                transactionId = transaction.TransactionId;
            }
            else
            {
                if (IsElevated()) throw new UnauthorizedAccessException("即使管理员权限也无法更新原路径；原程序已保留。");
                var name = AppUpdateInstallerPipe.PipeName(); var capability = AppUpdateInstallerPipe.Secret();
                workerServer = AppUpdateInstallerPipe.CreateServer(name, true);
                var workerArgs = new AppUpdateInstallerArguments("applier", name, capability, self.Pid, self.StartedUtcTicks, args.OriginalHash);
                workerProcess = StartHelper(self.Path, workerArgs, true);
                workerIdentity = new ProcessIdentity(workerProcess);
                await AppUpdateInstallerPipe.AuthenticateClientAsync(workerServer, workerProcess, self.Path, args.OriginalHash, preparation).ConfigureAwait(false);
                worker = new(workerServer, capability);
                await worker.SendAsync(request with { Kind = "prepare" }, preparation).ConfigureAwait(false);
                var ready = await worker.ExpectAsync("ready", preparation).ConfigureAwait(false);
                transactionId = ready.TransactionId;
            }
            // The authenticated applier supplies only a GUID. Paths are derived from the exact original
            // directory; no received path can become an authority for file access, deletion or launching.
            recoveryPaths = AppUpdateInstallerTransaction.ProtectionPathsFor(original.Path, transactionId);
            EnsureNoOtherInstances(original.Path, original.Pid, workerProcess?.Id ?? 0);
            await upstream.SendAsync(new() { Kind = "ready" }, preparation).ConfigureAwait(false);
            using var decisionTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            decisionTimeout.CancelAfter(TimeSpan.FromMinutes(5));
            var decision = await upstream.ReadAsync(decisionTimeout.Token).ConfigureAwait(false);
            if (decision?.Kind != "commit" || original.HasExited) return 3;
            commitAccepted = true;
            if (worker is not null)
            {
                await worker.SendAsync(new() { Kind = "commit" }, decisionTimeout.Token).ConfigureAwait(false);
                await worker.ExpectAsync("committed", decisionTimeout.Token).ConfigureAwait(false);
            }
            await upstream.SendAsync(new() { Kind = "committed" }, decisionTimeout.Token).ConfigureAwait(false);
            if (!await WaitForOriginalExitAsync(original, upstream, lifetime).ConfigureAwait(false))
            {
                if (original.HasExited) throw new IOException("原窗口已退出，但更新授权被取消。");
                return 3;
            }
            originalImage.Dispose();
            if (worker is not null)
            {
                using var completion = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                completion.CancelAfter(TimeSpan.FromMinutes(2));
                await worker.SendAsync(new() { Kind = "apply" }, completion.Token).ConfigureAwait(false);
                var result = await worker.ExpectAsync("result", completion.Token).ConfigureAwait(false);
                recoveryPaths = ValidateRecoveryPaths(result, original.Path, transactionId);
                recoveryConfirmed = true;
                if (result.Code != "complete") throw new IOException("更新执行器未能完成替换。");
            }
            else
            {
                await transaction!.ApplyAsync(0, lifetime).ConfigureAwait(false);
                transaction.Dispose(); recoveryPaths = transaction.RecoveryPaths; recoveryConfirmed = true; transaction = null;
            }
            if (workerIdentity is not null)
            {
                using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await WaitForExitAsync(workerIdentity, exited.Token).ConfigureAwait(false);
            }
            lease.Dispose();
            await RestartOriginalAsync(original.Path, trusted.VersionText, true, "complete", trusted.Sha256, lifetime).ConfigureAwait(false);
            return 0;
        }
        catch (Exception failure)
        {
            if (!commitAccepted || !original.HasExited) throw; // the original UI still owns ordinary error feedback
            originalImage.Dispose();
            if (transaction is not null)
            {
                transaction.Dispose(); recoveryPaths = transaction.RecoveryPaths; recoveryConfirmed = true; transaction = null;
            }
            if (worker is not null) await TryCancelAsync(worker).ConfigureAwait(false);
            var workerStopped = workerIdentity is null;
            if (workerIdentity is not null)
            {
                try
                {
                    using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await WaitForExitAsync(workerIdentity, exited.Token).ConfigureAwait(false);
                    workerStopped = true;
                }
                catch { workerStopped = false; }
            }
            if (workerStopped)
            {
                lease.Dispose();
                try
                {
                    using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    if (await TryRestartKnownOriginalAsync(original.Path, trusted.VersionText, args.OriginalHash,
                        trusted.Sha256, CodeFor(failure), recovery.Token).ConfigureAwait(false)) return 1;
                }
                catch { } // only the original-token coordinator may show the final emergency message
            }
            ShowUpdateFailure(original.Path, recoveryPaths, recoveryConfirmed);
            return 1;
        }
        finally
        {
            if (worker is not null) { await TryCancelAsync(worker).ConfigureAwait(false); worker.Dispose(); }
            workerServer?.Dispose(); workerIdentity?.Dispose(); workerProcess?.Dispose(); transaction?.Dispose();
        }
    }

    internal static string[] ValidateRecoveryPaths(AppUpdateInstallerMessage message, string target, string transactionId)
    {
        if (!string.Equals(message.TransactionId, transactionId, StringComparison.Ordinal))
            throw new InvalidDataException("更新执行器返回了其它事务的保护信息。");
        if (message.RecoveryPaths is null)
            throw new InvalidDataException("更新执行器返回了无效的保护路径。");
        var normalized = message.RecoveryPaths.Select(NormalizePath).ToArray();
        var allowed = AppUpdateInstallerTransaction.ProtectionPathsFor(target, transactionId);
        if (normalized.Length > 2 || normalized.Any(path =>
            !allowed.Any(candidate => SamePath(path, candidate))) ||
            normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new InvalidDataException("更新执行器返回了无效的保护路径。");
        return normalized;
    }

    private static async Task TryCancelAsync(AppUpdateInstallerPipe channel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await channel.SendAsync(new() { Kind = "cancel" }, timeout.Token).ConfigureAwait(false); } catch { }
    }

    private static async Task<bool> TryRestartKnownOriginalAsync(string target, string version, string oldHash,
        string newHash, string code, CancellationToken ct)
    {
        using var directories = new DirectoryPins(target);
        using var image = OpenReadPinned(target);
        var actual = await HashAsync(image, ct).ConfigureAwait(false);
        if (!string.Equals(actual, oldHash, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(actual, newHash, StringComparison.OrdinalIgnoreCase)) return false;
        var updated = string.Equals(actual, newHash, StringComparison.OrdinalIgnoreCase);
        await RestartOriginalAsync(target, version, updated, updated ? "complete" : code, actual, ct).ConfigureAwait(false);
        return true;
    }

    private static async Task<int> RunApplierAsync(AppUpdateInstallerPipe upstream, AppUpdateInstallerMessage request,
        AppUpdateInstallerArguments args, ProcessIdentity coordinator, ProcessIdentity self, CancellationToken preparation, CancellationToken lifetime)
    {
        if (!IsElevated() || !SamePath(coordinator.Path, self.Path)) throw new InvalidDataException("更新执行器来源无效。");
        using var original = new ProcessIdentity(request.ParentPid, request.ParentStartedUtcTicks);
        var target = original.Path;
        if (SamePath(target, self.Path) || !SamePath(target, NormalizePath(request.Target)) ||
            !string.Equals(args.OriginalHash, request.OriginalHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新执行器拒绝了不匹配的原程序路径。");
        using (var image = OpenReadPinned(target))
            if (!string.Equals(await HashAsync(image, preparation).ConfigureAwait(false), args.OriginalHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原程序与更新助手不一致。");
        var trusted = await ResolveTrustedAsync(request.VersionText, target, preparation).ConfigureAwait(false);
        using var transaction = await AppUpdateInstallerTransaction.PrepareAsync(target, args.OriginalHash,
            NormalizePath(request.PackagePath), trusted, original.Pid, coordinator.Pid, preparation).ConfigureAwait(false);
        await upstream.SendAsync(new() { Kind = "ready", TransactionId = transaction.TransactionId }, preparation).ConfigureAwait(false);
        using var decision = CancellationTokenSource.CreateLinkedTokenSource(lifetime); decision.CancelAfter(TimeSpan.FromMinutes(7));
        var commit = await upstream.ReadAsync(decision.Token).ConfigureAwait(false);
        if (commit?.Kind != "commit" || original.HasExited || coordinator.HasExited) return 3;
        await upstream.SendAsync(new() { Kind = "committed" }, decision.Token).ConfigureAwait(false);
        var apply = await upstream.ReadAsync(decision.Token).ConfigureAwait(false);
        if (apply?.Kind != "apply" || !original.HasExited || coordinator.HasExited) return 3;
        var success = false;
        try { await transaction.ApplyAsync(coordinator.Pid, decision.Token).ConfigureAwait(false); success = true; }
        catch { }
        transaction.Dispose();
        await upstream.SendAsync(new() { Kind = "result", Code = success ? "complete" : "replace-failed",
            TransactionId = transaction.TransactionId, RecoveryPaths = transaction.RecoveryPaths }, lifetime).ConfigureAwait(false);
        return success ? 0 : 1; // applier never starts a GUI, even when UAC used another administrator account
    }

    private static async Task<bool> WaitForOriginalExitAsync(ProcessIdentity original, AppUpdateInstallerPipe upstream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        Task<AppUpdateInstallerMessage?>? incoming = upstream.ReadAsync(timeout.Token);
        try
        {
            while (!original.HasExited)
            {
                if (incoming is not null && incoming.IsCompleted)
                {
                    AppUpdateInstallerMessage? message;
                    try { message = await incoming.ConfigureAwait(false); } catch (IOException) { message = null; }
                    if (message?.Kind == "cancel") return false;
                    if (message is not null) return false;
                    incoming = null; // committed Dispose closes the pipe; only the fixed process handle now governs exit
                }
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            timeout.Cancel();
            if (incoming is not null) { try { await incoming.ConfigureAwait(false); } catch { } }
        }
    }

    internal static async Task<AppUpdateRelease> ResolveTrustedAsync(string versionText, string target, CancellationToken ct)
    {
        if (!AppUpdateVersion.TryParse(versionText, out var requested, out var prerelease) || prerelease)
            throw new InvalidDataException("更新版本无效。");
        var current = FileVersionInfo.GetVersionInfo(target);
        var currentVersion = new Version(Math.Max(0, current.FileMajorPart), Math.Max(0, current.FileMinorPart), Math.Max(0, current.FileBuildPart));
        if (requested <= currentVersion) throw new InvalidDataException("更新版本必须高于当前版本。");
        using var service = new AppUpdateService();
        return await service.GetReleaseAsync(versionText, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("此版本不再是官方仓库的有效正式发行，请重新检查更新。");
    }

    private static Process StartHelper(string path, AppUpdateInstallerArguments args, bool elevate)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = elevate, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(path)! };
        if (elevate) start.Verb = "runas";
        args.AddTo(start);
        try { return Process.Start(start) ?? throw new IOException("无法启动更新助手，当前窗口会保留。"); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new OperationCanceledException("已取消管理员授权，当前窗口和原程序均已保留。", ex); }
    }

    private static async Task RestartOriginalAsync(string target, string version, bool updated, string code, string expectedHash, CancellationToken ct)
    {
        using var directories = new DirectoryPins(target);
        using var image = OpenReadPinned(target);
        if (!string.Equals(await HashAsync(image, ct).ConfigureAwait(false), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("原路径已被其它文件占用，更新助手拒绝启动未知程序。");
        var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
        foreach (var value in new[] { ResultSwitch, updated ? "updated" : "failed", version,
            updated ? "complete" : code is "verification-failed" ? code : "replace-failed" }) start.ArgumentList.Add(value);
        // This process inherited the original token, so its direct child has exactly the original identity/elevation.
        using var child = Process.Start(start) ?? throw new IOException("更新已结束，但无法重新启动 WinKit。");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    internal static string MessageForCode(string code) => code switch
    {
        "complete" => "WinKit 已更新到新版本。",
        "busy" => "另一个 WinKit 窗口、维护任务或其它程序仍在使用原文件；请先正常退出它，再重试。",
        "cancelled" => "更新已取消，当前窗口和原程序均已保留。",
        "permission" => "无法在原路径更新程序；请检查该目录的写入权限后重试。",
        "verification-failed" => "更新文件校验失败，原程序已保留；请重新下载后重试。",
        "restart-failed" => "更新事务已结束，但未能自动重启；请重新打开原路径的 WinKit。",
        _ => "更新未能完成，原程序已保留；请检查文件占用与目录权限后重试。"
    };

    private static string CodeFor(Exception ex) => ex switch
    {
        OperationCanceledException => "cancelled",
        UnauthorizedAccessException => "permission",
        InvalidDataException => "verification-failed",
        _ => "replace-failed"
    };

    private static void TryDeleteOwnHelper(string path)
    {
        // Only our own generated helper name may be cleaned. A hand-written CLI can never turn this into arbitrary deletion.
        if (!Path.GetFileName(path).StartsWith("WinKit-update-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path)["WinKit-update-".Length..], "N", out _)) return;
        AppUpdateInstallerNative.MarkOwnImageForDeletion(path);
    }
}

public sealed class AppUpdateRestartSession : IAsyncDisposable
{
    private readonly AppUpdateInstallerPipe _channel;
    private readonly Process _helper;
    private readonly FileStream _helperImage;
    private readonly AppUpdateInstallerNative.DirectoryPins _helperDirectories;
    private readonly string _helperPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _committed;
    private bool _cancelled;
    private bool _disposed;
    public bool Ready => !_disposed && !_cancelled;

    internal AppUpdateRestartSession(AppUpdateInstallerPipe channel, Process helper, FileStream helperImage,
        AppUpdateInstallerNative.DirectoryPins helperDirectories, string helperPath)
    { _channel = channel; _helper = helper; _helperImage = helperImage; _helperDirectories = helperDirectories; _helperPath = helperPath; }

    public async Task AllowExitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cancelled) throw new OperationCanceledException("更新已取消。");
            if (_committed) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await _channel.SendAsync(new() { Kind = "commit" }, timeout.Token).ConfigureAwait(false);
            await _channel.ExpectAsync("committed", timeout.Token).ConfigureAwait(false);
            _committed = true;
        }
        finally { _gate.Release(); }
    }

    public async Task CancelAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _cancelled) return;
            _cancelled = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _channel.SendAsync(new() { Kind = "cancel" }, timeout.Token).ConfigureAwait(false); } catch { }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (!_committed && !_cancelled)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _channel.SendAsync(new() { Kind = "cancel" }, timeout.Token).ConfigureAwait(false); } catch { }
            }
            _disposed = true;
            _channel.Dispose(); _helperImage.Dispose(); _helperDirectories.Dispose();
            // Never kill an updater or another GUI. Its authenticated cancellation/parent timeout governs cleanup.
            if (_helper.HasExited) AppUpdateInstallerTransaction.TryDelete(_helperPath);
            _helper.Dispose();
        }
        finally { _gate.Release(); }
    }
}
