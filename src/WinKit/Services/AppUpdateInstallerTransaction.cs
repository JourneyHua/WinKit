using System.IO;
using static WinKit.Services.AppUpdateInstallerNative;

namespace WinKit.Services;

/// <summary>A live same-volume transaction; temporary original protection is removed after verified success.</summary>
internal sealed class AppUpdateInstallerTransaction : IDisposable
{
    private readonly string _target;
    private readonly string _stage;
    private readonly string _backup;
    private readonly string _displaced;
    private readonly string _oldHash;
    private readonly AppUpdateRelease _release;
    private readonly DirectoryPins _directories;
    private readonly FileIdentity _oldIdentity;
    private FileStream? _old;
    private FileStream? _staged;
    private FileStream? _protectedOld;
    private bool _originalMoved;
    private bool _originalAvailableAtTarget = true;
    private bool _replaced;
    private bool _verified;
    private bool _disposed;
    private readonly List<string> _recoveryPaths = [];
    internal string TransactionId { get; } = Guid.NewGuid().ToString("N");
    internal string[] RecoveryPaths => _recoveryPaths.ToArray();

    internal static string[] ProtectionPathsFor(string target, string transactionId)
    {
        if (!Guid.TryParseExact(transactionId, "N", out _)) throw new InvalidDataException("更新事务身份无效。");
        var prefix = Path.Combine(Path.GetDirectoryName(NormalizePath(target))!, ".winkit-update-" + transactionId);
        return [prefix + ".original", prefix + ".previous"];
    }

    private AppUpdateInstallerTransaction(string target, string oldHash, AppUpdateRelease release)
    {
        _target = NormalizePath(target); _oldHash = oldHash; _release = release;
        _directories = new DirectoryPins(_target);
        var prefix = Path.Combine(Path.GetDirectoryName(_target)!, ".winkit-update-" + TransactionId);
        _stage = prefix + ".new"; _backup = prefix + ".original"; _displaced = prefix + ".previous";
        try { _old = OpenReadPinned(_target); _oldIdentity = Inspect(_old, _target); }
        catch { _directories.Dispose(); throw; }
    }

    internal static async Task<AppUpdateInstallerTransaction> PrepareAsync(string target, string oldHash,
        string package, AppUpdateRelease trusted, int parentPid, int coordinatorPid, CancellationToken ct)
    {
        var transaction = new AppUpdateInstallerTransaction(target, oldHash, trusted);
        try
        {
            if (!string.Equals(await HashAsync(transaction._old!, ct).ConfigureAwait(false), oldHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原程序文件已变化，更新已停止。");
            EnsureNoOtherInstances(target, parentPid, coordinatorPid);
            using (var sourceDirectories = new DirectoryPins(package))
            using (var source = OpenReadPinned(package))
            {
                using var service = new AppUpdateService();
                if (!await service.VerifyDownloadAsync(package, trusted, ct).ConfigureAwait(false))
                    throw new InvalidDataException("下载的更新文件校验失败，请重新下载。");
                transaction._staged = await CopyNewAsync(source, transaction._stage, ct).ConfigureAwait(false);
            }
            // Both objects remain pinned from CREATE_NEW through rename/deletion. Exact copied hash also
            // carries forward the source's verified PE, bundle, runtime, architecture and version metadata.
            if (transaction._staged.Length != trusted.Size ||
                !string.Equals(await HashAsync(transaction._staged, ct).ConfigureAwait(false), trusted.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("同卷暂存文件校验失败。");
            transaction._protectedOld = await CopyNewAsync(transaction._old!, transaction._backup, ct).ConfigureAwait(false);
            if (!string.Equals(await HashAsync(transaction._protectedOld, ct).ConfigureAwait(false), oldHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("无法保护原程序，更新已停止。");
            EnsureNoOtherInstances(target, parentPid, coordinatorPid);
            return transaction;
        }
        catch { transaction.Dispose(); throw; }
    }

    private static async Task<FileStream> CopyNewAsync(FileStream source, string destination, CancellationToken ct)
    {
        var output = CreateNewPinned(destination);
        try
        {
            source.Position = 0;
            await source.CopyToAsync(output, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false); output.Flush(true);
            source.Position = 0; output.Position = 0;
            return output;
        }
        catch
        {
            try { DeletePinned(output); } catch { }
            output.Dispose(); throw;
        }
    }

    internal async Task ApplyAsync(int coordinatorPid, CancellationToken ct)
    {
        if (_disposed || _old is null || _staged is null || _protectedOld is null || _originalMoved || _verified)
            throw new InvalidOperationException("更新事务尚未就绪或已经使用。");
        EnsureNoOtherInstances(_target, 0, coordinatorPid);
        if (Inspect(_old, _target) != _oldIdentity ||
            !string.Equals(await HashAsync(_old, ct).ConfigureAwait(false), _oldHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(await HashAsync(_staged, ct).ConfigureAwait(false), _release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新前的文件身份校验失败，原程序已保留。");
        _originalAvailableAtTarget = false;
        _old.Dispose(); _old = null;
        _old = OpenForRename(_target); // exact old object now pinned with DELETE and without delete/write sharing
        if (Inspect(_old, _target) != _oldIdentity ||
            !string.Equals(await HashAsync(_old, ct).ConfigureAwait(false), _oldHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("原程序在替换前已变化。");
        _originalAvailableAtTarget = true;
        ct.ThrowIfCancellationRequested();
        try
        {
            // Windows has no compare-file-ID-and-replace API. Move the fixed original object to a unique
            // protected entry, then atomically install only into an absent target. Never clobber an entry
            // concurrently inserted by another process. The original and flushed backup survive any failure.
            RenamePinned(_old, _displaced); _originalMoved = true; _originalAvailableAtTarget = false;
            RenamePinned(_staged, _target); _replaced = true;
            _ = Inspect(_staged, _target);
            if (_staged.Length != _release.Size ||
                !string.Equals(await HashAsync(_staged, CancellationToken.None).ConfigureAwait(false), _release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新后的程序未通过校验。");
            _verified = true;
        }
        catch (Exception failure)
        {
            if (_originalMoved)
            {
                try
                {
                    if (_replaced) { RenamePinned(_staged, _stage); _replaced = false; }
                    RenamePinned(_old, _target); _originalMoved = false; _originalAvailableAtTarget = true;
                }
                catch (Exception restoreFailure)
                {
                    throw new IOException("原路径被占用或无法还原；原文件仍保存在本次更新的临时保护副本中，未覆盖占用文件。",
                        new AggregateException(failure, restoreFailure));
                }
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Clean only exact handles that this transaction owns; never reopen a generated name to delete it.
        // Keep the installed new object pinned until the old protection has been cleaned successfully.
        if (_originalMoved && _old is not null && (!_verified || !TryDeletePinned(_old)))
            RecordRecovery(_old, _displaced);
        if (!_replaced && _staged is not null) TryDeletePinned(_staged);
        if (_protectedOld is not null && (!(_verified || _originalAvailableAtTarget) || !TryDeletePinned(_protectedOld)))
            RecordRecovery(_protectedOld, _backup);
        _old?.Dispose(); _old = null;
        _protectedOld?.Dispose(); _protectedOld = null;
        _staged?.Dispose(); _staged = null;
        _directories.Dispose();
    }

    private void RecordRecovery(FileStream file, string path)
    {
        try
        {
            _ = Inspect(file, path);
            if (string.Equals(HashAsync(file, CancellationToken.None).GetAwaiter().GetResult(), _oldHash, StringComparison.OrdinalIgnoreCase))
                _recoveryPaths.Add(path);
        }
        catch { } // only report a protection file authenticated through this transaction's own pinned handle
    }

    private static bool TryDeletePinned(FileStream file)
    {
        try { DeletePinned(file); return true; } catch { return false; }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
