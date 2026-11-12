using System.Diagnostics;
using System.IO;
using WinKit.Core;

namespace WinKit.Services;

public sealed class StubbornGroup
{
    public string DisplayName { get; init; } = "";
    public List<(HiveKind Hive, string SubKey)> Entries { get; init; } = new();
    public List<string> Guids { get; init; } = new();
    public string OwnerFolder { get; init; } = "";
    public List<string> Processes { get; set; } = new();

    public int GuidCount => Guids.Count;
    public string GuidListText => string.Join("、", Guids);
    public string ProcessText => Processes.Count == 0 ? "未发现" : string.Join("、", Processes);
}

/// <summary>
/// 处理“会自我重建”的导航栏/此电脑节点（典型是 WPS 云盘）。
/// 这类软件每次运行都换一个新 GUID 重新注册，按单个 GUID 隐藏永远追不上，
/// 所以按显示名归族，一次清掉全部，并能定位正在重建它的进程。
/// </summary>
public sealed class StubbornNodeService
{
    private const string NameSpaceHklm = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace";
    private const string NameSpaceHkcu = @"Software\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace";

    private static readonly string[] PinValueNames =
    {
        "System.IsPinnedToNameSpaceTree",
        "System.IsPinnedToNamespaceTree"
    };

    private readonly ChangeJournal _journal;

    public StubbornNodeService(ChangeJournal journal) => _journal = journal;

    public List<StubbornGroup> Load()
    {
        var byName = new Dictionary<string, StubbornGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { HiveKind.LocalMachine, HiveKind.CurrentUser })
        {
            foreach (var clsid in Reg.SubKeyNames(RegPath.Of(hive, @"Software\Classes\CLSID")))
            {
                if (!clsid.StartsWith('{')) continue;

                var path = RegPath.Of(hive, $@"Software\Classes\CLSID\{clsid}");
                using var key = Reg.Open(path, false);
                if (key is null) continue;

                var hasPin = PinValueNames.Any(n =>
                    key.GetValueNames().Any(v => v.Equals(n, StringComparison.OrdinalIgnoreCase)));
                if (!hasPin) continue;

                var name = ContextMenuService.ResolveClsidName(clsid);
                if (string.IsNullOrWhiteSpace(name)) continue;

                var owner = ResolveOwnerFolder(hive, clsid);
                Add(hive, path.SubKey, clsid, name!, owner);
            }

            var nsRoot = hive == HiveKind.LocalMachine ? NameSpaceHklm : NameSpaceHkcu;
            foreach (var clsid in Reg.SubKeyNames(RegPath.Of(hive, nsRoot)))
            {
                if (!clsid.StartsWith('{')) continue;

                var nsPath = RegPath.Of(hive, $@"{nsRoot}\{clsid}");

                // 命名空间项的显示名就在自己的默认值上，CLSID 键可能根本不存在
                var name = Reg.GetString(nsPath)
                           ?? ContextMenuService.ResolveClsidName(clsid);
                if (string.IsNullOrWhiteSpace(name))
                    name = ContextMenuService.ResolveShellName(nsPath, "");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var owner = ResolveOwnerFolder(hive, clsid);
                Add(hive, nsPath.SubKey, clsid, name!, owner);
            }
        }

        void Add(HiveKind hive, string subKey, string clsid, string name, string owner)
        {
            if (!byName.TryGetValue(name, out var group))
            {
                group = new StubbornGroup
                {
                    DisplayName = name,
                    Entries = new List<(HiveKind, string)>(),
                    Guids = new List<string>(),
                    OwnerFolder = owner
                };
                byName[name] = group;
            }

            group.Entries.Add((hive, subKey));
            if (!group.Guids.Contains(clsid, StringComparer.OrdinalIgnoreCase))
                group.Guids.Add(clsid);
        }

        // 只有“同一名字出现多个 GUID”才说明它在自我重建
        var result = byName.Values.Where(g => g.Guids.Count >= 2).ToList();

        foreach (var group in result)
            group.Processes = FindProcesses(group.OwnerFolder);

        return result.OrderByDescending(g => g.GuidCount).ToList();
    }

    private static string ResolveOwnerFolder(HiveKind hive, string clsid)
    {
        foreach (var server in new[] { "DefaultIcon", "InprocServer32", "LocalServer32" })
        {
            var raw = Reg.GetString(RegPath.Of(hive, $@"Software\Classes\CLSID\{clsid}\{server}"));
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var path = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            if (path.Contains(',')) path = path.Split(',')[0].Trim(); // "dll,57"

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir!;
            }
            catch (Exception)
            {
                // 忽略非法路径
            }
        }

        return "";
    }

    private static List<string> FindProcesses(string folder)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(folder)) return result;

        var root = folder.TrimEnd('\\');
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var file = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(file)) continue;
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                var label = $"{process.ProcessName}.exe ({process.Id})";
                if (!result.Contains(label)) result.Add(label);
            }
            catch (Exception)
            {
                // 受保护进程读不到路径，跳过
            }
            finally
            {
                process.Dispose();
            }
        }

        // 重建节点的通常是带 cloud/svr/spatial 字样的后台服务，主程序（wps.exe 等）排后面
        return result
            .OrderByDescending(n => n.Contains("cloudsvr", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(n => n.Contains("svr", StringComparison.OrdinalIgnoreCase))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>按 "name.exe (1234)" 结束单个进程；返回是否成功。</summary>
    public bool StopProcess(string label)
    {
        var start = label.LastIndexOf('(');
        var end = label.LastIndexOf(')');
        if (start < 0 || end <= start) return false;
        if (!int.TryParse(label[(start + 1)..end], out var pid)) return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>把这个名字下所有 GUID 的注册项一次清掉。</summary>
    public void Purge(StubbornGroup group)
    {
        if (group.Entries.Count == 0) return;

        _journal.Record($"彻底清理顽固节点：{group.DisplayName}（{group.GuidCount} 个 GUID）",
            group.Entries.ToArray(),
            () =>
            {
                foreach (var (hive, subKey) in group.Entries)
                    Reg.DeleteTree(RegPath.Of(hive, subKey));
            });

        Native.NotifyAssociationsChanged();
    }

    /// <summary>结束正在重建该节点的进程，返回结束的数量。</summary>
    public int StopProcesses(StubbornGroup group)
    {
        if (string.IsNullOrWhiteSpace(group.OwnerFolder)) return 0;

        var root = group.OwnerFolder.TrimEnd('\\');
        var killed = 0;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var file = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(file)) continue;
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                process.Kill();
                killed++;
            }
            catch (Exception)
            {
                // 结束失败就跳过，不重试
            }
            finally
            {
                process.Dispose();
            }
        }

        return killed;
    }
}
