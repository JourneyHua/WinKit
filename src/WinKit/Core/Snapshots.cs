using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace WinKit.Core;

public sealed class SnapshotValue
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "String";
    public JsonNode? Data { get; set; }
}

public sealed class SnapshotKey
{
    public string Hive { get; set; } = "HKCU";
    public string SubKey { get; set; } = "";

    /// <summary>Default / X64 / X86。还原时必须写回同一个视图，否则 32 位登记会被写到 64 位侧。</summary>
    public string View { get; set; } = "Default";

    public bool Existed { get; set; }
    public List<SnapshotValue> Values { get; set; } = new();
    public List<SnapshotKey> Children { get; set; } = new();

    /// <summary>读取时被拒绝：既不能当作“原本不存在”，也不能参与任何删除。</summary>
    public bool CaptureFailed { get; set; }

    /// <summary>子键数超过预算被截断：还原时不能做整树删除，否则会删掉没被记下来的子树。</summary>
    public bool Truncated { get; set; }
}

public sealed class SnapshotFile
{
    public string Path { get; set; } = "";
    public bool Existed { get; set; }
    public string? Base64Data { get; set; }
}

public sealed class OperationSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Title { get; set; } = "";
    public List<SnapshotKey> Keys { get; set; } = new();
    public List<SnapshotFile> Files { get; set; } = new();
    public int KeyCount { get; set; }

    [JsonIgnore]
    public string? SavedFilePath { get; set; }

    [JsonIgnore]
    public string LocalTimeText => TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
}

public static class SnapshotEngine
{
    private const int MaxNodes = 20000;

    public static SnapshotKey Capture(HiveKind hive, string subKey, int budget = MaxNodes)
        => Capture(new RegPath(hive, subKey), new Budget(budget));

    /// <summary>带视图的捕获：32 位视图的键必须按 32 位读，否则 WOW6432Node 的内容根本进不了快照。</summary>
    public static SnapshotKey Capture(RegPath path, int budget = MaxNodes)
        => Capture(path, new Budget(budget));

    /// <summary>
    /// 递归预算必须是共享计数器：按值传 budget 时每个子键都会拿到一份新的预算，
    /// 整棵树实际节点数会远超 MaxNodes（CLSID 这种上万子键的树会直接吃满内存）。
    /// </summary>
    private sealed class Budget
    {
        private int _remaining;

        public Budget(int remaining) => _remaining = remaining;

        public bool TryTake() => _remaining-- > 0;
    }

    private static SnapshotKey Capture(RegPath path, Budget budget)
    {
        var node = new SnapshotKey
        {
            Hive = path.Hive == HiveKind.CurrentUser ? "HKCU" : "HKLM",
            SubKey = path.SubKey,
            View = path.View.ToString()
        };

        var (key, status) = Reg.TryOpen(path, false);
        if (status == Reg.OpenStatus.Denied)
        {
            // 明确区分“拒绝访问”：不能记成 Existed=false，否则撤销时会删掉真实存在的键
            node.CaptureFailed = true;
            return node;
        }

        using var owned = key;
        node.Existed = status == Reg.OpenStatus.Ok;
        if (owned is null) return node;

        foreach (var valueName in owned.GetValueNames())
        {
            var kind = owned.GetValueKind(valueName);
            var raw = owned.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            node.Values.Add(new SnapshotValue
            {
                Name = valueName,
                Kind = kind.ToString(),
                Data = ToJson(kind, raw)
            });
        }

        foreach (var child in owned.GetSubKeyNames())
        {
            if (!budget.TryTake())
            {
                // 预算耗尽意味着快照不完整：标记出来，还原时禁止整树删除
                node.Truncated = true;
                break;
            }

            node.Children.Add(Capture(path with { SubKey = path.SubKey + "\\" + child }, budget));
        }

        return node;
    }

    private static JsonNode? ToJson(RegistryValueKind kind, object? raw)
    {
        switch (kind)
        {
            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
                return JsonValue.Create(raw?.ToString() ?? "");
            case RegistryValueKind.DWord:
                return JsonValue.Create(Convert.ToInt32(raw ?? 0));
            case RegistryValueKind.QWord:
                return JsonValue.Create(Convert.ToInt64(raw ?? 0L));
            case RegistryValueKind.MultiString:
                return raw is string[] arr
                    ? new JsonArray(arr.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())
                    : new JsonArray();
            case RegistryValueKind.Binary:
                return JsonValue.Create(raw is byte[] bytes ? Convert.ToBase64String(bytes) : "");
            case RegistryValueKind.None:
                // None 值也可能是带字节的（REG_NONE 存二进制），丢了就还原不回去
                return raw is byte[] noneBytes && noneBytes.Length > 0
                    ? JsonValue.Create(Convert.ToBase64String(noneBytes))
                    : null;
            default:
                // Unknown 等未覆盖类型会返回 byte[]；直接 ToString() 会得到 "System.Byte[]"
                if (raw is byte[] unknownBytes) return JsonValue.Create(Convert.ToBase64String(unknownBytes));
                return JsonValue.Create(raw?.ToString() ?? "");
        }
    }

    private static HiveKind ParseHive(string? hive)
    {
        // 只接受明确的两种取值：手改/损坏的备份文件不能靠“非 HKCU 即 HKLM”落到系统区
        if (string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase)) return HiveKind.CurrentUser;
        if (string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)) return HiveKind.LocalMachine;

        throw new InvalidOperationException($"快照里的注册表根无效：'{hive}'，已拒绝还原以避免写错位置");
    }

    private static RegViewKind ParseView(string? view)
    {
        if (string.IsNullOrWhiteSpace(view)) return RegViewKind.Default;
        if (!Enum.TryParse<RegViewKind>(view, true, out var parsed) || !Enum.IsDefined(parsed))
            throw new InvalidOperationException($"快照里的注册表视图无效：'{view}'，已拒绝还原");

        return parsed;
    }

    internal static void Validate(SnapshotKey node)
    {
        if (node.CaptureFailed || node.Truncated)
            throw new InvalidOperationException($"快照不完整，已拒绝还原 {node.Hive}\\{node.SubKey}");
        ParseHive(node.Hive);
        ParseView(node.View);
        if (string.IsNullOrWhiteSpace(node.SubKey?.Trim('\\')) || node.Values is null || node.Children is null)
            throw new InvalidOperationException("快照中的注册表路径或内容无效");

        // 在任何删除/写入之前检查整棵树，不能改完父键才发现子快照不完整。
        foreach (var value in node.Values)
        {
            if (!Enum.TryParse<RegistryValueKind>(value.Kind, out var kind) || !Enum.IsDefined(kind))
                throw new InvalidOperationException($"快照中的注册表值类型无效：{value.Kind}");
            _ = FromJson(kind, value.Data);
        }
        foreach (var child in node.Children)
        {
            if (!string.Equals(child.Hive, node.Hive, StringComparison.OrdinalIgnoreCase)
                || ParseView(child.View) != ParseView(node.View)
                || !string.Equals(child.SubKey[..Math.Max(0, child.SubKey.LastIndexOf('\\'))],
                    node.SubKey.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("快照中的子键位置与父键不一致");
            Validate(child);
        }
    }

    public static void Restore(SnapshotKey node)
    {
        Validate(node);
        RestoreCore(node);
    }

    private static void RestoreCore(SnapshotKey node)
    {
        var path = new RegPath(ParseHive(node.Hive), node.SubKey, ParseView(node.View));
        if (!node.Existed)
        {
            if (!Reg.DeleteTree(path))
                throw new IOException($"无法删除注册表键以还原：{path.DisplayPath}");
            return;
        }

        using var key = Reg.Create(path);
        if (key is null)
            throw new InvalidOperationException($"无法创建注册表键以还原：{node.Hive}\\{node.SubKey}");

        // 差量还原：不删整键，保留原安全描述符，也不会把键短暂置为不存在
        var snapshotValues = node.Values.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in key.GetValueNames())
        {
            if (!snapshotValues.Contains(existing)) key.DeleteValue(existing, false);
        }

        foreach (var value in node.Values)
        {
            var kind = Enum.TryParse<RegistryValueKind>(value.Kind, out var parsed)
                ? parsed
                : RegistryValueKind.String;
            key.SetValue(value.Name, FromJson(kind, value.Data), kind);
        }

        var snapshotChildren = node.Children
            .Select(c => c.SubKey[(c.SubKey.LastIndexOf('\\') + 1)..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var existingChild in key.GetSubKeyNames())
        {
            if (!snapshotChildren.Contains(existingChild))
                key.DeleteSubKeyTree(existingChild, false);
        }

        foreach (var child in node.Children)
            RestoreCore(child);
    }

    private static object FromJson(RegistryValueKind kind, JsonNode? node)
    {
        if (node is null)
        {
            if (kind == RegistryValueKind.None) return Array.Empty<byte>();
            throw new InvalidOperationException("快照缺少注册表值内容");
        }

        switch (kind)
        {
            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
                return node.GetValue<string>();
            case RegistryValueKind.DWord:
                return node.GetValue<int>();
            case RegistryValueKind.QWord:
                return node.GetValue<long>();
            case RegistryValueKind.MultiString:
                if (node is not JsonArray array || array.Any(v => v is null))
                    throw new InvalidOperationException("快照中的多字符串内容无效");
                return array.Select(v => v!.GetValue<string>()).ToArray();
            case RegistryValueKind.Binary:
                var b64 = node.GetValue<string>();
                return string.IsNullOrEmpty(b64) ? Array.Empty<byte>() : Convert.FromBase64String(b64);
            case RegistryValueKind.None:
                var noneB64 = node.GetValue<string>();
                return string.IsNullOrEmpty(noneB64) ? Array.Empty<byte>() : Convert.FromBase64String(noneB64);
            default:
                if (node is JsonValue jv && jv.TryGetValue<string>(out var text))
                {
                    // 未知类型按 Base64 记录，原样写回
                    try { return Convert.FromBase64String(text); }
                    catch (FormatException) { return text; }
                }

                return node.GetValue<string>();
        }
    }

    public static int CountKeys(IEnumerable<SnapshotKey> keys)
        => keys.Sum(k => 1 + CountKeys(k.Children));

    public static SnapshotFile CaptureFile(string path)
    {
        path = Path.GetFullPath(path);
        bool existed;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException($"跟踪的文件路径实际是目录：{path}");
            existed = true;
        }
        catch (FileNotFoundException) { existed = false; }
        catch (DirectoryNotFoundException) { existed = false; }

        return new SnapshotFile
        {
            Path = path,
            Existed = existed,
            Base64Data = existed ? Convert.ToBase64String(File.ReadAllBytes(path)) : null
        };
    }

    internal static void Validate(SnapshotFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Path) || !Path.IsPathFullyQualified(file.Path))
            throw new InvalidOperationException("快照中的文件路径必须是完整路径");
        if (string.Equals(Path.GetFullPath(file.Path).TrimEnd('\\'),
            Path.GetPathRoot(file.Path)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("快照不能以磁盘根目录作为文件目标");
        if (file.Existed)
            _ = Convert.FromBase64String(file.Base64Data
                ?? throw new InvalidOperationException($"快照缺少文件内容：{file.Path}"));
    }

    public static void RestoreFile(SnapshotFile file)
    {
        Validate(file);
        if (!file.Existed)
        {
            // File.Exists 会把拒绝访问也当作不存在；直接删除才能可靠报告失败。
            try { File.Delete(file.Path); }
            catch (DirectoryNotFoundException) { } // 父目录也不存在时，目标已经是快照要求的状态。
            return;
        }

        var bytes = Convert.FromBase64String(file.Base64Data!);
        var dir = Path.GetDirectoryName(file.Path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(file.Path, bytes);
    }
}

public sealed record JournalRestoreResult(
    bool Succeeded,
    bool HasChanges,
    string Message,
    string? Title = null,
    int RestoredCount = 0,
    IReadOnlyList<string>? Failures = null);

public sealed record BackupPreview(
    string Title,
    string TargetsText,
    string ContentHash,
    bool RequiresElevation);

public sealed class ChangeJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly List<OperationSnapshot> _session = new();
    private readonly object _sessionLock = new();
    private int _operationActive;

    public ChangeJournal() : this(BackupDirectory) { }

    public ChangeJournal(string backupDirectory)
    {
        StorageDirectory = Path.GetFullPath(backupDirectory);
    }

    public static string BackupDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinKit", "Backups");

    public string StorageDirectory { get; }

    public event Action<string>? Changed;

    public event Action<JournalRestoreResult>? ResultReported;

    /// <summary>从捕获前到提交/还原完成保持忙碌；通知可能来自后台线程。</summary>
    public event Action? OperationStateChanged;

    public bool IsOperationActive => Volatile.Read(ref _operationActive) != 0;

    private OperationLease EnterOperation()
    {
        // 不能阻塞等待：RecordAsync 的续体可能正在等待调用者所在的 UI 线程。
        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0)
            throw new InvalidOperationException("另一项改动或还原正在进行，请等待完成后重试。");

        var lease = new OperationLease(this);
        try { OperationStateChanged?.Invoke(); }
        catch
        {
            lease.Dispose();
            throw;
        }
        return lease;
    }

    private sealed class OperationLease(ChangeJournal journal) : IDisposable
    {
        private ChangeJournal? _journal = journal;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _journal, null);
            if (owner is null) return;
            Volatile.Write(ref owner._operationActive, 0);
            owner.OperationStateChanged?.Invoke();
        }
    }

    private JournalRestoreResult ReportResult(JournalRestoreResult result)
    {
        Changed?.Invoke(result.Message);
        ResultReported?.Invoke(result);
        return result;
    }

    public bool CanUndo { get { lock (_sessionLock) return _session.Count > 0; } }

    public int SessionCount { get { lock (_sessionLock) return _session.Count; } }

    public string? LastTitle { get { lock (_sessionLock) return _session.Count > 0 ? _session[^1].Title : null; } }

    public OperationSnapshot Record(
        string title,
        IEnumerable<(HiveKind Hive, string SubKey)> tracked,
        Action action,
        IEnumerable<string>? trackedFiles = null)
    {
        using var operation = EnterOperation();
        var snapshot = CaptureSnapshot(title, tracked, trackedFiles);
        return ExecuteSnapshot(snapshot, action);
    }

    /// <summary>
    /// 异步版本：schtasks / pnputil 这类需要 await 的写操作也要能进快照，
    /// 否则界面上的「撤销上一步」对它们是空的。
    /// </summary>
    public async Task<OperationSnapshot> RecordAsync(
        string title,
        IEnumerable<(HiveKind Hive, string SubKey)> tracked,
        Func<Task> action,
        IEnumerable<string>? trackedFiles = null)
    {
        using var operation = EnterOperation();
        var snapshot = CaptureSnapshot(title, tracked, trackedFiles);
        Save(snapshot); // 必须先持久化；保存失败时不执行任何改动。

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            HandleActionFailure(snapshot, ex);
            throw;
        }

        Commit(snapshot);
        return snapshot;
    }

    private static OperationSnapshot CaptureSnapshot(
        string title,
        IEnumerable<(HiveKind Hive, string SubKey)> tracked,
        IEnumerable<string>? trackedFiles)
    {
        var snapshot = new OperationSnapshot { Title = title };
        foreach (var (hive, subKey) in tracked.Distinct())
            snapshot.Keys.Add(SnapshotEngine.Capture(hive, subKey));

        if (trackedFiles is not null)
        {
            foreach (var file in trackedFiles.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
                snapshot.Files.Add(SnapshotEngine.CaptureFile(file));
        }

        ValidateSnapshot(snapshot);
        snapshot.KeyCount = SnapshotEngine.CountKeys(snapshot.Keys) + snapshot.Files.Count;
        return snapshot;
    }

    private static void ValidateSnapshot(OperationSnapshot snapshot)
    {
        if (snapshot.Keys is null || snapshot.Files is null)
            throw new InvalidOperationException("快照中的注册表或文件清单无效");
        foreach (var key in snapshot.Keys) SnapshotEngine.Validate(key);
        foreach (var file in snapshot.Files) SnapshotEngine.Validate(file);
    }

    private OperationSnapshot ExecuteSnapshot(OperationSnapshot snapshot, Action action, string? preservedBackup = null)
    {
        Save(snapshot);
        try
        {
            action();
        }
        catch (Exception ex)
        {
            HandleActionFailure(snapshot, ex);
            throw;
        }

        Commit(snapshot, preservedBackup);
        return snapshot;
    }

    private void HandleActionFailure(OperationSnapshot snapshot, Exception actionError)
    {
        var failures = RestoreSnapshot(snapshot);
        if (failures.Count == 0)
        {
            TryDeleteFile(snapshot);
            ReportResult(new(false, true, $"执行「{snapshot.Title}」失败，已自动回滚：{actionError.Message}", snapshot.Title));
            return;
        }

        // 自动回滚也失败时，磁盘与会话都必须保留原状态，供用户释放占用/提权后重试。
        lock (_sessionLock) _session.Add(snapshot);
        var message = $"执行「{snapshot.Title}」失败，自动回滚有 {failures.Count} 项未能还原；已保留撤销记录，可重试。";
        ReportResult(new(false, true, message, snapshot.Title, Failures: failures));
        throw new AggregateException(message + "\n" + string.Join("\n", failures), actionError,
            new InvalidOperationException(string.Join("\n", failures)));
    }

    private void Commit(OperationSnapshot snapshot, string? preservedBackup = null)
    {
        lock (_sessionLock) _session.Add(snapshot);
        Prune(preservedBackup);
        Changed?.Invoke($"已执行：{snapshot.Title}");
    }

    private static List<string> RestoreSnapshot(OperationSnapshot snapshot)
    {
        var failures = new List<string>();
        try { ValidateSnapshot(snapshot); }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
            return failures;
        }

        foreach (var key in snapshot.Keys)
        {
            try
            {
                SnapshotEngine.Restore(key);
            }
            catch (Exception ex)
            {
                failures.Add($"{key.Hive}\\{key.SubKey}：{ex.Message}");
            }
        }

        foreach (var file in snapshot.Files)
        {
            try
            {
                SnapshotEngine.RestoreFile(file);
            }
            catch (Exception ex)
            {
                failures.Add($"{file.Path}：{ex.Message}");
            }
        }

        return failures;
    }

    // 兼容旧调用者的返回约定；新界面应使用结构化结果，不把错误字符串当作标题。
    public string? UndoLast()
    {
        var result = UndoLastResult();
        return !result.HasChanges ? null : result.Succeeded ? result.Title : result.Message;
    }

    public JournalRestoreResult UndoLastResult()
    {
        using var operation = EnterOperation();
        OperationSnapshot snapshot;
        lock (_sessionLock)
        {
            if (_session.Count == 0)
                return new(true, false, "没有可撤销的改动");
            snapshot = _session[^1];
        }
        var failures = RestoreSnapshot(snapshot);
        if (failures.Count > 0)
        {
            var message = $"撤销「{snapshot.Title}」时 {failures.Count} 项未能还原；已保留记录，可重试：\n"
                          + string.Join("\n", failures);
            return ReportResult(new(false, true, message, snapshot.Title, Failures: failures));
        }

        lock (_sessionLock) _session.RemoveAt(_session.Count - 1);
        TryDeleteFile(snapshot);
        var success = $"已撤销：{snapshot.Title}";
        return ReportResult(new(true, true, success, snapshot.Title, 1));
    }

    public string? RollbackSession()
    {
        var result = RollbackSessionResult();
        return result.HasChanges ? result.Message : null;
    }

    public JournalRestoreResult RollbackSessionResult()
    {
        using var operation = EnterOperation();
        if (!CanUndo)
            return new(true, false, "没有可回滚的改动");

        var restored = 0;
        while (CanUndo)
        {
            OperationSnapshot snapshot;
            lock (_sessionLock) snapshot = _session[^1];
            var failures = RestoreSnapshot(snapshot);
            if (failures.Count > 0)
            {
                // 必须在失败处停下：较早快照可能覆盖同一路径，继续还原会使之后重试倒退。
                var message = $"回滚未完成：已回滚 {restored} 次改动；「{snapshot.Title}」有 {failures.Count} 项未能还原。"
                              + $"已保留 {SessionCount} 条记录，可重试：\n" + string.Join("\n", failures);
                return ReportResult(new(false, true, message, snapshot.Title, restored, failures));
            }

            lock (_sessionLock) _session.RemoveAt(_session.Count - 1);
            TryDeleteFile(snapshot);
            restored++;
        }

        var success = $"已回滚本次会话的 {restored} 次改动";
        return ReportResult(new(true, true, success, RestoredCount: restored));
    }

    private void Save(OperationSnapshot snapshot)
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(StorageDirectory);
            var safeTitle = string.Concat(snapshot.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (safeTitle.Length > 40) safeTitle = safeTitle[..40];
            var shortId = snapshot.Id.Length >= 8 ? snapshot.Id[..8] : snapshot.Id;
            var name = $"{snapshot.TimestampUtc.ToLocalTime():yyyyMMdd_HHmmss_fff}_{shortId}_{safeTitle}.json";
            var path = Path.Combine(StorageDirectory, name);
            temporaryPath = path + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporaryPath, path);
            snapshot.SavedFilePath = path;
        }
        catch (Exception ex)
        {
            throw new IOException("无法保存还原快照，未执行改动：" + ex.Message, ex);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch (Exception) { }
            }
        }
    }

    private void Prune(string? preservedBackup = null)
    {
        try
        {
            HashSet<string> protectedPaths;
            lock (_sessionLock)
                protectedPaths = _session.Where(s => s.SavedFilePath is not null)
                    .Select(s => s.SavedFilePath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (preservedBackup is not null) protectedPaths.Add(Path.GetFullPath(preservedBackup));
            var files = new DirectoryInfo(StorageDirectory)
                .GetFiles("*.json")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Where(f => !protectedPaths.Contains(f.FullName))
                .Skip(Math.Max(0, 50 - protectedPaths.Count))
                .ToList();

            foreach (var file in files) file.Delete();
        }
        catch (Exception)
        {
            // 清理失败不影响已经持久化的快照，也不能删除仍可重试的会话快照。
        }
    }

    private static void TryDeleteFile(OperationSnapshot snapshot)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(snapshot.SavedFilePath))
                File.Delete(snapshot.SavedFilePath);
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    public IReadOnlyList<FileInfo> ListBackups()
    {
        try
        {
            return new DirectoryInfo(StorageDirectory)
                .GetFiles("*.json")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<FileInfo>();
        }
    }

    private static (OperationSnapshot Snapshot, string Hash) ReadBackup(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var snapshot = JsonSerializer.Deserialize<OperationSnapshot>(bytes, JsonOptions)
                       ?? throw new InvalidOperationException("备份文件无法解析");
        ValidateSnapshot(snapshot);
        return (snapshot, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public BackupPreview PreviewBackup(string filePath)
    {
        var (snapshot, hash) = ReadBackup(filePath);
        var targets = new List<string>();
        foreach (var key in snapshot.Keys)
            targets.Add($"• {key.Hive}\\{key.SubKey}（{key.View}；" +
                (key.Existed ? "完全恢复此键及子项，会移除备份后新增的值和子键）" : "备份时不存在，还原会删除此键及子项）"));
        foreach (var file in snapshot.Files)
            targets.Add($"• {file.Path}（" + (file.Existed ? "覆盖为备份内容）" : "备份时不存在，还原会删除文件）"));
        if (targets.Count == 0) throw new InvalidOperationException("该快照没有可还原的目标");
        return new BackupPreview(snapshot.Title, string.Join("\n", targets), hash,
            snapshot.Keys.Any(key => string.Equals(key.Hive, "HKLM", StringComparison.OrdinalIgnoreCase)));
    }

    public void RestoreBackup(string filePath, string? expectedContentHash = null)
    {
        using var operation = EnterOperation();
        var (snapshot, hash) = ReadBackup(filePath);
        if (expectedContentHash is not null && !string.Equals(hash, expectedContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("快照内容在预览后发生变化，未执行还原。请重新查看目标后重试。");
        var current = new OperationSnapshot { Title = $"还原备份：{snapshot.Title}" };
        foreach (var key in snapshot.Keys)
        {
            var hive = string.Equals(key.Hive, "HKCU", StringComparison.OrdinalIgnoreCase)
                ? HiveKind.CurrentUser : HiveKind.LocalMachine;
            var view = string.IsNullOrWhiteSpace(key.View)
                ? RegViewKind.Default : Enum.Parse<RegViewKind>(key.View, true);
            current.Keys.Add(SnapshotEngine.Capture(new RegPath(hive, key.SubKey, view)));
        }
        foreach (var file in snapshot.Files)
            current.Files.Add(SnapshotEngine.CaptureFile(file.Path));
        ValidateSnapshot(current);
        current.KeyCount = SnapshotEngine.CountKeys(current.Keys) + current.Files.Count;

        ExecuteSnapshot(current, () =>
        {
            var failures = RestoreSnapshot(snapshot);
            if (failures.Count > 0)
                throw new InvalidOperationException(
                    $"从备份还原「{snapshot.Title}」时 {failures.Count} 项未能还原：\n" + string.Join("\n", failures));
        }, filePath);
        ReportResult(new(true, true, $"已从备份还原：{snapshot.Title}（可撤销上一步）", current.Title));
    }
}
