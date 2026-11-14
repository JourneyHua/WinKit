using System.IO;
using System.Text.Json;
using WinKit.Core;

namespace WinKit.Services;

public sealed class ThisPcItem : ObservableObject
{
    private bool _isVisible = true;

    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool IsSystem { get; init; } = true;
    public bool CanToggle { get; init; } = true;
    public string Note { get; init; } = "";

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (value == _isVisible) return;
            _isVisible = value;
            OnPropertyChanged(nameof(StatusText));
            VisibilityRequested?.Invoke(this, value);
        }
    }

    public Action<ThisPcItem, bool>? VisibilityRequested { get; set; }

    public string StatusText => IsVisible ? "显示中" : "已隐藏";
}

public sealed class ThisPcService
{
    private const string FolderDescriptions = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FolderDescriptions";
    private const string NameSpaceHklm = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace";
    private const string NameSpaceHkcu = @"Software\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace";
    private const string PoliciesExplorer = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    private readonly ChangeJournal _journal;

    public ThisPcService(ChangeJournal journal) => _journal = journal;

    public static readonly (string Guid, string Title)[] KnownFolders =
    {
        ("{31C0DD25-9439-4F12-BF41-7FF4EDA38722}", "3D 对象"),
        ("{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", "桌面"),
        ("{374DE290-123F-4565-9164-39C4925E467B}", "下载"),
        ("{FDD39AD0-238F-46AF-ADB4-6C85480369C7}", "文档"),
        ("{33E28130-4E1E-4676-835A-98395C3BC3BB}", "图片"),
        ("{4BD8D571-6D19-48D3-BE97-422220080E43}", "音乐"),
        ("{18989B1D-99B5-455B-841C-AB7C74E4DDFC}", "视频")
    };

    public List<ThisPcItem> LoadFolders()
    {
        var list = new List<ThisPcItem>();

        foreach (var (guid, title) in KnownFolders)
        {
            var path = RegPath.Hklm($@"{FolderDescriptions}\{guid}\PropertyBag");
            var policy = Reg.GetString(path, "ThisPCPolicy");
            var hidden = string.Equals(policy, "Hide", StringComparison.OrdinalIgnoreCase);

            list.Add(new ThisPcItem
            {
                Id = "folder:" + guid,
                Title = title,
                Subtitle = "",
                Detail = RegPath.Hklm($@"{FolderDescriptions}\{guid}").FullPath,
                IsVisible = !hidden,
                CanToggle = true,
                Note = hidden ? "通过 ThisPCPolicy=Hide 隐藏" : ""
            });
        }

        return list;
    }

    public void SetFolderVisible(string guid, bool visible)
    {
        var subKey = $@"{FolderDescriptions}\{guid}\PropertyBag";
        _journal.Record($"{(visible ? "显示" : "隐藏")}此电脑文件夹", new[] { (HiveKind.LocalMachine, subKey) },
            () => Reg.SetString(RegPath.Hklm(subKey), "ThisPCPolicy", visible ? "Show" : "Hide"));
        Native.NotifyAssociationsChanged();
    }

    private static readonly Dictionary<string, string> KnownNodeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{21EC2020-3AEA-1069-A2DD-08002B30309D}"] = "控制面板",
        ["{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}"] = "网络",
        ["{59031a47-3f72-44a7-89c5-5595fe6b30ee}"] = "用户文件夹",
        ["{D20EA4E1-3957-11d2-A40B-0C5020524153}"] = "管理工具",
        ["{26EE0668-A00A-44D7-9371-BEB064C98683}"] = "控制面板",
        ["{645FF040-5081-101B-9F08-00AA002F954E}"] = "回收站",
        ["{031E4825-7B94-4dc3-B131-E946B44C8DD5}"] = "库",
        ["{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}"] = "桌面",
        ["{374DE290-123F-4565-9164-39C4925E467B}"] = "下载",
        ["{FDD39AD0-238F-46AF-ADB4-6C85480369C7}"] = "文档",
        ["{33E28130-4E1E-4676-835A-98395C3BC3BB}"] = "图片",
        ["{4BD8D571-6D19-48D3-BE97-422220080E43}"] = "音乐",
        ["{18989B1D-99B5-455B-841C-AB7C74E4DDFC}"] = "视频",
        ["{088e3905-0323-4b02-9826-5d99428e115f}"] = "下载",
        ["{1CF1260C-4DD0-4ebb-811F-33C572699FDE}"] = "音乐",
        ["{24ad3ad4-a569-4530-98e1-ab02f9417aa8}"] = "图片",
        ["{3ADD1653-EB32-4cb0-BBD7-DFA0ABB5ACCA}"] = "图片",
        ["{3dfdf296-dbec-4fb4-81d1-6a3438bcf4de}"] = "音乐",
        ["{A0953C92-50DC-43bf-BE83-3742FED03C9C}"] = "视频",
        ["{A8CDFF1C-4878-43be-B5FD-F8091C1C60D0}"] = "文档",
        ["{d3162b92-9365-467a-956b-92703aca08af}"] = "文档",
        ["{f86fa3ab-70d2-4fc7-9c99-fcbf05467f3a}"] = "视频",
        ["{A8A4D9C4-2C3B-4C7E-A6BF-4D5B4A4A6A61}"] = "设备与打印机",
        ["{BB06C0E4-D293-4f75-8A90-CB05B6477EEE}"] = "系统信息"
    };

    private static readonly HashSet<string> ManagedFolderGuids = new(
        KnownFolders.Select(f => f.Guid), StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> FolderTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "3D 对象", "桌面", "下载", "文档", "图片", "音乐", "视频"
    };

    public List<ThisPcItem> LoadNodes()
    {
        var list = new List<ThisPcItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { HiveKind.LocalMachine, HiveKind.CurrentUser })
        {
            var root = hive == HiveKind.LocalMachine ? NameSpaceHklm : NameSpaceHkcu;
            foreach (var clsid in Reg.SubKeyNames(RegPath.Of(hive, root)))
            {
                // DelegateFolders 是内部容器，不是用户可感知的节点
                if (!clsid.StartsWith('{')) continue;

                // 六个用户文件夹由 ThisPCPolicy 管理，不在这里重复出现，避免两套开关互相打架
                if (ManagedFolderGuids.Contains(clsid)) continue;

                var display = ResolveNodeName(clsid);
                if (FolderTitles.Contains(display)) continue;
                if (!seenTitles.Add(display)) continue;
                if (!seen.Add(clsid)) continue;

                // HiddenByDefault=1 的节点虽然注册着，但资源管理器默认不显示
                var hiddenByDefault = (Reg.GetInt(RegPath.Of(hive, $@"{root}\{clsid}"), "HiddenByDefault") ?? 0) == 1;
                list.Add(BuildNode(clsid, display, !hiddenByDefault, hive));
            }
        }

        // 之前被 WinKit 隐藏（键已删除）的节点也要列出来，否则用户永远没法在界面里恢复
        foreach (var backup in LoadNodeBackups())
        {
            if (!seen.Add(backup.Clsid)) continue;
            if (ManagedFolderGuids.Contains(backup.Clsid)) continue;

            var display = ResolveNodeName(backup.Clsid);
            if (FolderTitles.Contains(display)) continue;
            if (!seenTitles.Add(display)) continue;

            list.Add(BuildNode(backup.Clsid, display, false, backup.Hive, hidden: true));
        }

        return list.OrderBy(i => i.IsSystem ? 1 : 0)
            .ThenBy(i => i.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    private static string ResolveNodeName(string clsid)
    {
        if (KnownNodeNames.TryGetValue(clsid, out var known)) return known;
        return ContextMenuService.ResolveClsidName(clsid) ?? clsid;
    }

    private ThisPcItem BuildNode(string clsid, string name, bool visible, HiveKind hive, bool hidden = false)
    {
        var module = ContextMenuService.ResolveClsidModule(clsid) ?? "";
        var isSystem = KnownNodeNames.ContainsKey(clsid)
                       || name.Equals("DelegateFolders", StringComparison.OrdinalIgnoreCase)
                       || module.ToLowerInvariant().Contains(@"\windows\");

        return new ThisPcItem
        {
            Id = "node:" + clsid,
            Title = name,
            Subtitle = isSystem ? "系统节点" : "第三方命名空间",
            Detail = clsid,
            IsVisible = visible,
            IsSystem = isSystem,
            CanToggle = true,
            Note = hidden
                ? "已被 WinKit 隐藏，勾选可原样恢复（含原默认值与 HiddenByDefault）"
                : isSystem ? "" : "由第三方软件注册，隐藏会删除其注册表项（可撤销）"
        };
    }

    public void SetNodeVisible(string clsid, bool visible)
    {
        var hklmKey = $@"{NameSpaceHklm}\{clsid}";
        var hkcuKey = $@"{NameSpaceHkcu}\{clsid}";

        if (visible)
        {
            var hklmPath2 = RegPath.Hklm(hklmKey);
            var hkcuPath2 = RegPath.Hkcu(hkcuKey);
            var keyStillExists = Reg.HasValue(hklmPath2, "HiddenByDefault") || Reg.HasValue(hkcuPath2, "HiddenByDefault");

            if (keyStillExists)
            {
                // 隐藏时只是把 HiddenByDefault 置 1，键和原默认值都还在，清掉标志即可原样恢复
                _journal.Record($"恢复此电脑节点：{clsid}", new[]
                {
                    (HiveKind.LocalMachine, hklmKey),
                    (HiveKind.CurrentUser, hkcuKey)
                }, () =>
                {
                    if (Reg.HasValue(hklmPath2, "HiddenByDefault")) Reg.SetInt(hklmPath2, "HiddenByDefault", 0);
                    if (Reg.HasValue(hkcuPath2, "HiddenByDefault")) Reg.SetInt(hkcuPath2, "HiddenByDefault", 0);
                });

                Native.NotifyAssociationsChanged();
                return;
            }

            var backup = ReadNodeBackup(clsid);
            _journal.Record($"恢复此电脑节点：{clsid}", new[]
            {
                (HiveKind.LocalMachine, hklmKey),
                (HiveKind.CurrentUser, hkcuKey)
            }, () =>
            {
                if (backup is not null)
                {
                    // 用隐藏前抓到的完整快照还原，保留原默认值（CLSID_xxx 名称引用）与 HiddenByDefault
                    foreach (var key in backup.Keys) SnapshotEngine.Restore(key);
                    DeleteNodeBackup(clsid);
                    return;
                }

                // 没有备份时（例如用户手动删过键）退回最小重建
                var hive = Reg.Exists(RegPath.Hklm(hklmKey)) ? HiveKind.LocalMachine : HiveKind.CurrentUser;
                var target = hive == HiveKind.LocalMachine ? hklmKey : hkcuKey;
                Reg.SetString(RegPath.Of(hive, target), "", clsid);
            });
        }
        else
        {
            // 有 HiddenByDefault 的系统节点优先改值，不删键，恢复时零损失
            var hklmPath = RegPath.Hklm(hklmKey);
            var hkcuPath = RegPath.Hkcu(hkcuKey);
            var hasHiddenFlag = Reg.HasValue(hklmPath, "HiddenByDefault") || Reg.HasValue(hkcuPath, "HiddenByDefault");

            if (hasHiddenFlag)
            {
                _journal.Record($"隐藏此电脑节点：{clsid}", new[]
                {
                    (HiveKind.LocalMachine, hklmKey),
                    (HiveKind.CurrentUser, hkcuKey)
                }, () =>
                {
                    if (Reg.HasValue(hklmPath, "HiddenByDefault")) Reg.SetInt(hklmPath, "HiddenByDefault", 1);
                    if (Reg.HasValue(hkcuPath, "HiddenByDefault")) Reg.SetInt(hkcuPath, "HiddenByDefault", 1);
                });
            }
            else
            {
                // 第三方节点没有 HiddenByDefault，只能删键；删前把整键内容存到 WinKit 自己的备份目录
                var backup = new NodeBackup
                {
                    Clsid = clsid,
                    Hive = Reg.Exists(hklmPath) ? HiveKind.LocalMachine : HiveKind.CurrentUser,
                    Keys = new List<SnapshotKey>
                    {
                        SnapshotEngine.Capture(HiveKind.LocalMachine, hklmKey),
                        SnapshotEngine.Capture(HiveKind.CurrentUser, hkcuKey)
                    }
                };

                _journal.Record($"隐藏此电脑节点：{clsid}", new[]
                {
                    (HiveKind.LocalMachine, hklmKey),
                    (HiveKind.CurrentUser, hkcuKey)
                }, () =>
                {
                    WriteNodeBackup(backup);
                    Reg.DeleteTree(hklmPath);
                    Reg.DeleteTree(hkcuPath);
                });
            }
        }

        Native.NotifyAssociationsChanged();
    }

    private sealed class NodeBackup
    {
        public string Clsid { get; set; } = "";
        public HiveKind Hive { get; set; }
        public List<SnapshotKey> Keys { get; set; } = new();
    }

    private static string NodeBackupDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinKit", "NodeBackups");

    private static string NodeBackupFile(string clsid)
        => Path.Combine(NodeBackupDirectory, clsid.Trim('{', '}') + ".json");

    private static void WriteNodeBackup(NodeBackup backup)
    {
        try
        {
            Directory.CreateDirectory(NodeBackupDirectory);
            File.WriteAllText(NodeBackupFile(backup.Clsid), JsonSerializer.Serialize(backup));
        }
        catch (Exception)
        {
            // 备份失败不阻断隐藏，但恢复时会退回最小重建
        }
    }

    private static NodeBackup? ReadNodeBackup(string clsid)
    {
        try
        {
            var file = NodeBackupFile(clsid);
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<NodeBackup>(File.ReadAllText(file));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void DeleteNodeBackup(string clsid)
    {
        try
        {
            var file = NodeBackupFile(clsid);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private static IEnumerable<NodeBackup> LoadNodeBackups()
    {
        if (!Directory.Exists(NodeBackupDirectory)) yield break;

        foreach (var file in Directory.GetFiles(NodeBackupDirectory, "*.json"))
        {
            NodeBackup? backup = null;
            try
            {
                backup = JsonSerializer.Deserialize<NodeBackup>(File.ReadAllText(file));
            }
            catch (Exception)
            {
                // 损坏的备份跳过
            }

            if (backup is not null && !string.IsNullOrWhiteSpace(backup.Clsid)) yield return backup;
        }
    }

    public List<ThisPcItem> LoadDrives()
    {
        var list = new List<ThisPcItem>();
        var noDrives = Reg.GetInt(RegPath.Hkcu(PoliciesExplorer), "NoDrives") ?? 0;

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.Name.Length < 2) continue;
            var letter = drive.Name[0];
            if (letter < 'A' || letter > 'Z') continue;
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;

            var bit = 1 << (letter - 'A');
            var hidden = (noDrives & bit) != 0;

            var label = "";
            var capacity = "";
            try
            {
                if (drive.IsReady)
                {
                    label = drive.VolumeLabel;
                    capacity = $"{FormatSize(drive.TotalSize)}，可用 {FormatSize(drive.AvailableFreeSpace)}";
                }
            }
            catch (Exception)
            {
                // 读不到卷信息时保持空白
            }

            list.Add(new ThisPcItem
            {
                Id = "drive:" + letter,
                Title = $"{letter}: {(string.IsNullOrWhiteSpace(label) ? TypeText(drive.DriveType) : label)}",
                Subtitle = capacity,
                Detail = $"NoDrives 位：0x{bit:X}",
                IsVisible = !hidden,
                CanToggle = true,
                Note = hidden ? "已通过 NoDrives 隐藏" : ""
            });
        }

        return list;
    }

    public void SetDriveVisible(char letter, bool visible)
    {
        var current = Reg.GetInt(RegPath.Hkcu(PoliciesExplorer), "NoDrives") ?? 0;
        var bit = 1 << (letter - 'A');
        var next = visible ? current & ~bit : current | bit;

        _journal.Record($"{(visible ? "显示" : "隐藏")}驱动器 {letter}:", new[] { (HiveKind.CurrentUser, PoliciesExplorer) },
            () =>
            {
                if (next == 0) Reg.DeleteValue(RegPath.Hkcu(PoliciesExplorer), "NoDrives");
                else Reg.SetInt(RegPath.Hkcu(PoliciesExplorer), "NoDrives", next);
            });

        Native.NotifyAssociationsChanged();
    }

    private static string TypeText(DriveType type) => type switch
    {
        DriveType.Fixed => "本地磁盘",
        DriveType.Removable => "可移动磁盘",
        DriveType.Network => "网络驱动器",
        _ => "驱动器"
    };

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
