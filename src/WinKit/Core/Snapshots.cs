using System.IO;
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
        if (!Enum.TryParse<RegViewKind>(view, true, out var parsed))
            throw new InvalidOperationException($"快照里的注册表视图无效：'{view}'，已拒绝还原");

        return parsed;
    }

    public static void Restore(SnapshotKey node)
    {
        // 读取被拒的键，我们并不知道它原本长什么样，任何写入/删除都可能造成丢失
        if (node.CaptureFailed) return;

        var hive = ParseHive(node.Hive);
        // 必须写回捕获时的视图：32 位登记被写到 64 位侧等于没还原
        var path = new RegPath(hive, node.SubKey, ParseView(node.View));

        if (!node.Existed)
        {
            Reg.DeleteTree(path);
            return;
        }

        if (node.Truncated)
        {
            // 截断的快照只记了部分子键，整树还原会把没记下来的子树删掉
            throw new InvalidOperationException(
                $"快照不完整（子键超过上限），已拒绝还原 {node.Hive}\\{node.SubKey}，请手工核对");
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
            Restore(child);
    }

    private static object FromJson(RegistryValueKind kind, JsonNode? node)
    {
        if (node is null) return Array.Empty<byte>();

        switch (kind)
        {
            case RegistryValueKind.DWord:
                return node.GetValue<int>();
            case RegistryValueKind.QWord:
                return node.GetValue<long>();
            case RegistryValueKind.MultiString:
                return node is JsonArray array
                    ? array.Select(v => v?.GetValue<string>() ?? "").ToArray()
                    : Array.Empty<string>();
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
        var existed = File.Exists(path);
        return new SnapshotFile
        {
            Path = path,
            Existed = existed,
            Base64Data = existed ? Convert.ToBase64String(File.ReadAllBytes(path)) : null
        };
    }

    public static void RestoreFile(SnapshotFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Path)) return;

        try
        {
            if (!file.Existed)
            {
                if (File.Exists(file.Path)) File.Delete(file.Path);
                return;
            }

            var dir = Path.GetDirectoryName(file.Path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var bytes = string.IsNullOrEmpty(file.Base64Data)
                ? Array.Empty<byte>()
                : Convert.FromBase64String(file.Base64Data);
            File.WriteAllBytes(file.Path, bytes);
        }
        catch (Exception)
        {
            // 单个文件写回失败不应阻断整体还原
        }
    }
}

public sealed class ChangeJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly List<OperationSnapshot> _session = new();

    public ChangeJournal()
    {
        Directory.CreateDirectory(BackupDirectory);
    }

    public static string BackupDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinKit", "Backups");

    public event Action<string>? Changed;

    public bool CanUndo => _session.Count > 0;

    public int SessionCount => _session.Count;

    public string? LastTitle => _session.Count > 0 ? _session[^1].Title : null;

    public OperationSnapshot Record(
        string title,
        IEnumerable<(HiveKind Hive, string SubKey)> tracked,
        Action action,
        IEnumerable<string>? trackedFiles = null)
    {
        var snapshot = CaptureSnapshot(title, tracked, trackedFiles);

        try
        {
            action();
        }
        catch
        {
            // 操作失败立即自动回滚，避免留下半截状态
            RestoreSnapshot(snapshot);
            throw;
        }

        Commit(snapshot);
        return snapshot;
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
        var snapshot = CaptureSnapshot(title, tracked, trackedFiles);

        try
        {
            await action();
        }
        catch
        {
            RestoreSnapshot(snapshot);
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

        snapshot.KeyCount = SnapshotEngine.CountKeys(snapshot.Keys) + snapshot.Files.Count;
        return snapshot;
    }

    private void Commit(OperationSnapshot snapshot)
    {
        _session.Add(snapshot);
        Save(snapshot);
        Changed?.Invoke($"已执行：{snapshot.Title}");
    }

    private static List<string> RestoreSnapshot(OperationSnapshot snapshot)
    {
        var failures = new List<string>();

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

    public string? UndoLast()
    {
        if (_session.Count == 0) return null;

        var snapshot = _session[^1];

        // 先还原、成功后才摘掉记录：还原抛异常时不能把撤销历史一起丢掉
        var failures = RestoreSnapshot(snapshot);

        if (failures.Count > 0)
        {
            Changed?.Invoke($"撤销「{snapshot.Title}」有 {failures.Count} 项未还原");
            return $"撤销「{snapshot.Title}」时 {failures.Count} 项未能还原：\n" + string.Join("\n", failures);
        }

        _session.RemoveAt(_session.Count - 1);
        TryDeleteFile(snapshot);
        Changed?.Invoke($"已撤销：{snapshot.Title}");
        return snapshot.Title;
    }

    public string? RollbackSession()
    {
        if (_session.Count == 0) return null;

        var failures = new List<string>();
        for (var i = _session.Count - 1; i >= 0; i--)
        {
            // 单条快照写不回去不能中断整个回滚，否则会留下“半还原”且用户无从判断
            var itemFailures = RestoreSnapshot(_session[i]);

            if (itemFailures.Count == 0)
            {
                TryDeleteFile(_session[i]);
            }
            else
            {
                foreach (var f in itemFailures) failures.Add($"{_session[i].Title} → {f}");
            }
        }

        var count = _session.Count;
        _session.Clear();

        if (failures.Count > 0)
        {
            Changed?.Invoke($"回滚完成，但有 {failures.Count} 次改动未能还原");
            return $"已回滚 {count} 次改动，其中 {failures.Count} 次未能还原：\n" + string.Join("\n", failures);
        }

        Changed?.Invoke($"已回滚本次会话的 {count} 次改动");
        return $"已回滚 {count} 次改动";
    }

    private void Save(OperationSnapshot snapshot)
    {
        try
        {
            var safeTitle = string.Concat(snapshot.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (safeTitle.Length > 40) safeTitle = safeTitle[..40];
            var shortId = snapshot.Id.Length >= 8 ? snapshot.Id[..8] : snapshot.Id;
            var name = $"{snapshot.TimestampUtc.ToLocalTime():yyyyMMdd_HHmmss_fff}_{shortId}_{safeTitle}.json";
            var path = Path.Combine(BackupDirectory, name);
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
            snapshot.SavedFilePath = path;
            Prune();
        }
        catch (Exception)
        {
            // 备份失败不应阻断用户操作，但会在状态栏提示
        }
    }

    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(BackupDirectory)
                .GetFiles("*.json")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(50)
                .ToList();

            foreach (var file in files) file.Delete();
        }
        catch (Exception)
        {
            // 忽略清理失败
        }
    }

    private static void TryDeleteFile(OperationSnapshot snapshot)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(snapshot.SavedFilePath) && File.Exists(snapshot.SavedFilePath))
            {
                File.Delete(snapshot.SavedFilePath);
                return;
            }

            var shortId = snapshot.Id.Length >= 8 ? snapshot.Id[..8] : snapshot.Id;
            foreach (var file in new DirectoryInfo(BackupDirectory).GetFiles("*" + shortId + "*.json"))
                file.Delete();
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
            return new DirectoryInfo(BackupDirectory)
                .GetFiles("*.json")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<FileInfo>();
        }
    }

    public void RestoreBackup(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var snapshot = JsonSerializer.Deserialize<OperationSnapshot>(json, JsonOptions)
                       ?? throw new InvalidOperationException("备份文件无法解析");

        var failures = RestoreSnapshot(snapshot);

        if (failures.Count > 0)
        {
            Changed?.Invoke($"从备份还原「{snapshot.Title}」有 {failures.Count} 项未还原");
            throw new InvalidOperationException(
                $"从备份还原「{snapshot.Title}」时 {failures.Count} 项未能还原：\n" + string.Join("\n", failures));
        }

        Changed?.Invoke($"已从备份还原：{snapshot.Title}");
    }
}
