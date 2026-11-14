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
    public bool Existed { get; set; }
    public List<SnapshotValue> Values { get; set; } = new();
    public List<SnapshotKey> Children { get; set; } = new();
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
        => Capture(hive, subKey, new Budget(budget));

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

    private static SnapshotKey Capture(HiveKind hive, string subKey, Budget budget)
    {
        var node = new SnapshotKey
        {
            Hive = hive == HiveKind.CurrentUser ? "HKCU" : "HKLM",
            SubKey = subKey
        };

        using var key = Reg.Open(new RegPath(hive, subKey), false);
        node.Existed = key is not null;
        if (key is null) return node;

        foreach (var valueName in key.GetValueNames())
        {
            var kind = key.GetValueKind(valueName);
            var raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            node.Values.Add(new SnapshotValue
            {
                Name = valueName,
                Kind = kind.ToString(),
                Data = ToJson(kind, raw)
            });
        }

        foreach (var child in key.GetSubKeyNames())
        {
            if (!budget.TryTake()) break;
            node.Children.Add(Capture(hive, subKey + "\\" + child, budget));
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
                return null;
            default:
                return JsonValue.Create(raw?.ToString() ?? "");
        }
    }

    public static void Restore(SnapshotKey node)
    {
        var hive = node.Hive == "HKCU" ? HiveKind.CurrentUser : HiveKind.LocalMachine;
        var path = new RegPath(hive, node.SubKey);

        if (!node.Existed)
        {
            Reg.DeleteTree(path);
            return;
        }

        Reg.DeleteTree(path);
        using var key = Reg.Create(path);
        if (key is null) return;

        foreach (var value in node.Values)
        {
            try
            {
                var kind = Enum.TryParse<RegistryValueKind>(value.Kind, out var parsed)
                    ? parsed
                    : RegistryValueKind.String;
                key.SetValue(value.Name, FromJson(kind, value.Data), kind);
            }
            catch (Exception)
            {
                // 单个值写回失败不应阻断整体还原
            }
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
                return Array.Empty<byte>();
            default:
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
        var snapshot = new OperationSnapshot { Title = title };
        foreach (var (hive, subKey) in tracked.Distinct())
            snapshot.Keys.Add(SnapshotEngine.Capture(hive, subKey));

        if (trackedFiles is not null)
        {
            foreach (var file in trackedFiles.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
                snapshot.Files.Add(SnapshotEngine.CaptureFile(file));
        }

        snapshot.KeyCount = SnapshotEngine.CountKeys(snapshot.Keys) + snapshot.Files.Count;

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

        _session.Add(snapshot);
        Save(snapshot);
        Changed?.Invoke($"已执行：{title}");
        return snapshot;
    }

    private static void RestoreSnapshot(OperationSnapshot snapshot)
    {
        foreach (var key in snapshot.Keys)
            SnapshotEngine.Restore(key);
        foreach (var file in snapshot.Files)
            SnapshotEngine.RestoreFile(file);
    }

    public string? UndoLast()
    {
        if (_session.Count == 0) return null;
        var snapshot = _session[^1];
        _session.RemoveAt(_session.Count - 1);
        RestoreSnapshot(snapshot);
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
            try
            {
                RestoreSnapshot(_session[i]);
                TryDeleteFile(_session[i]);
            }
            catch (Exception ex)
            {
                failures.Add($"{_session[i].Title}：{ex.Message}");
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
        RestoreSnapshot(snapshot);
        Changed?.Invoke($"已从备份还原：{snapshot.Title}");
    }
}
