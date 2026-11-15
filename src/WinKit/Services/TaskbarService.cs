using System.IO;
using System.Runtime.InteropServices;
using WinKit.Core;

namespace WinKit.Services;

public sealed class TaskbarPinnedItem : ObservableObject
{
    public string Name { get; init; } = "";
    public string ShortcutPath { get; init; } = "";
    public string TargetPath { get; init; } = "";

    /// <summary>null = 无法判定（CLSID 链接，如「文件资源管理器」没有目标路径）。</summary>
    public bool? IsTargetMissing { get; init; }

    /// <summary>CLSID 链接（不是指向某个 exe），系统内置入口。</summary>
    public bool IsShellItem => string.IsNullOrWhiteSpace(TargetPath);

    public bool IsMissing => IsTargetMissing == true;

    public string TargetText => IsShellItem
        ? "系统内置位置（由 CLSID 打开，无目标路径）"
        : TargetPath;

    public string StatusText => IsTargetMissing switch
    {
        true => "目标已失效",
        null => "系统内置",
        _ => "正常"
    };
}

public sealed class TrayIconItem : ObservableObject
{
    private bool _isPromoted;

    /// <summary>注册表子键名（哈希），无法还原成人可读的稳定 ID。</summary>
    public string KeyName { get; init; } = "";

    public string Tooltip { get; init; } = "";

    /// <summary>注册表里的原始值，可能是 KnownFolder GUID 前缀形式。</summary>
    public string RawPath { get; init; } = "";

    /// <summary>解析 GUID 之后的真实路径。</summary>
    public string ResolvedPath { get; init; } = "";

    public bool? IsMissing { get; init; }

    /// <summary>原本是否有 IsPromoted 值（false = 用户从未管过这一条）。</summary>
    public bool HasExplicitValue { get; init; }

    public bool IsMissingFile => IsMissing == true;

    public bool IsPromoted
    {
        get => _isPromoted;
        set
        {
            if (value == _isPromoted) return;
            _isPromoted = value;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(VisibilityText));
            ToggleRequested?.Invoke(this, value);
        }
    }

    public Action<TrayIconItem, bool>? ToggleRequested { get; set; }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Tooltip)) return Tooltip;
            var name = System.IO.Path.GetFileNameWithoutExtension(ResolvedPath);
            return string.IsNullOrWhiteSpace(name) ? KeyName : name;
        }
    }

    public string StatusText => IsMissing switch
    {
        true => "目标已失效",
        null => "无法解析",
        _ => IsPromoted ? "已显示" : "已折叠"
    };

    public string VisibilityText => IsPromoted ? "显示在托盘" : "收进折叠区";
}

/// <summary>
/// 任务栏固定项与通知区域（托盘）图标管理。
///
/// 三条实测结论决定了这里的做法：
/// 1. Win11 没有「固定到任务栏」的公开入口（开始菜单 / AppsFolder 上下文菜单里只有 Pin to Start），
///    所以只能「列出 / 取消固定 / 清理失效」，做不了顺序调整。
/// 2. 取消固定用临时 verb（unpinfromtaskbar），必须按**名字**匹配而不是索引——本机索引 13
///    是因为装了火绒/PowerRename/ToDesk 等一堆第三方右键项，换台机器就漂。
/// 3. 托盘记录里 37 条是 KnownFolder GUID 前缀路径，不先解析会把它们全误判成僵尸。
/// </summary>
public sealed class TaskbarService
{
    private const string NotifyIconKey = @"Control Panel\NotifyIconSettings";

    /// <summary>任务栏固定项存放目录。</summary>
    public static string PinnedFolder { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    private readonly ChangeJournal _journal;

    public TaskbarService(ChangeJournal journal) => _journal = journal;

    // ---------- 固定项 ----------

    public List<TaskbarPinnedItem> LoadPinned()
    {
        var list = new List<TaskbarPinnedItem>();

        if (!Directory.Exists(PinnedFolder)) return list;

        IEnumerable<string> links;
        try
        {
            links = Directory.EnumerateFiles(PinnedFolder, "*.lnk", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            return list;
        }

        foreach (var link in links)
        {
            var target = Shortcuts.ResolveLinkTarget(link);

            list.Add(new TaskbarPinnedItem
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(link),
                ShortcutPath = link,
                TargetPath = target,
                IsTargetMissing = Shortcuts.IsTargetMissing(target)
            });
        }

        return list
            .OrderBy(i => i.IsMissing ? 1 : 0)
            .ThenBy(i => i.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>取消固定。成功与否以 .lnk 是否真的消失为准，不看返回值。</summary>
    public void Unpin(TaskbarPinnedItem item)
    {
        if (!File.Exists(item.ShortcutPath))
            throw new FileNotFoundException($"固定项快捷方式不存在：{item.ShortcutPath}");

        Exception? last = null;

        foreach (var verb in new[] { "unpinfromtaskbar", "taskbarunpin" })
        {
            try
            {
                RunVerb(item.ShortcutPath, verb);
            }
            catch (Exception ex)
            {
                last = ex;
                continue;
            }

            // verb 调起后系统是异步处理固定项文件的，给一小段时间再复查
            for (var i = 0; i < 15; i++)
            {
                if (!File.Exists(item.ShortcutPath)) return;
                Thread.Sleep(100);
            }
        }

        throw new InvalidOperationException(
            $"取消固定失败：系统没有移除该固定项。\n{item.ShortcutPath}" +
            (last is null ? "" : $"\n（最后一次错误：{last.Message}）"));
    }

    // ---------- 托盘图标 ----------

    public List<TrayIconItem> LoadTrayIcons()
    {
        var list = new List<TrayIconItem>();
        var root = RegPath.Hkcu(NotifyIconKey);

        foreach (var keyName in Reg.SubKeyNames(root))
        {
            var path = RegPath.Hkcu($@"{NotifyIconKey}\{keyName}");

            var raw = Reg.GetString(path, "ExecutablePath") ?? "";
            var tooltip = Reg.GetString(path, "InitialTooltip") ?? "";

            if (string.IsNullOrWhiteSpace(raw) && string.IsNullOrWhiteSpace(tooltip)) continue;

            var resolved = Shortcuts.ResolveKnownFolderPath(Shortcuts.Expand(raw));
            var promoted = Reg.GetInt(path, "IsPromoted");

            list.Add(new TrayIconItem
            {
                KeyName = keyName,
                Tooltip = tooltip.Trim(),
                RawPath = raw,
                ResolvedPath = resolved,
                IsMissing = Shortcuts.IsTargetMissing(resolved),
                HasExplicitValue = promoted is not null,
                IsPromoted = promoted == 1
            });
        }

        return list
            .OrderBy(i => i.IsMissingFile ? 1 : 0)
            .ThenBy(i => i.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    public void SetPromoted(TrayIconItem item, bool promoted)
    {
        var subKey = $@"{NotifyIconKey}\{item.KeyName}";
        var path = RegPath.Hkcu(subKey);

        var title = $"{(promoted ? "固定显示" : "折叠")}托盘图标：{item.DisplayName}";

        _journal.Record(title, new[] { (HiveKind.CurrentUser, subKey) },
            () => Reg.SetInt(path, "IsPromoted", promoted ? 1 : 0));

        item.IsPromoted = promoted;
        BroadcastTraySettings();
    }

    /// <summary>
    /// 把指向已不存在文件的记录折叠起来（写 IsPromoted=0），不删注册表记录。
    /// 删记录不可逆，而且系统可能在同一位置重新登记。
    /// </summary>
    public int CollapseMissing(IEnumerable<TrayIconItem> items)
    {
        var targets = items.Where(i => i.IsMissingFile && i.IsPromoted).ToList();
        if (targets.Count == 0) return 0;

        var tracked = targets.Select(i => (HiveKind.CurrentUser, $@"{NotifyIconKey}\{i.KeyName}")).ToList();

        _journal.Record($"折叠 {targets.Count} 条失效托盘图标记录", tracked, () =>
        {
            foreach (var item in targets)
                Reg.SetInt(RegPath.Hkcu($@"{NotifyIconKey}\{item.KeyName}"), "IsPromoted", 0);
        });

        foreach (var item in targets) item.IsPromoted = false;

        BroadcastTraySettings();
        return targets.Count;
    }

    /// <summary>彻底删掉失效的记录（整键删，带注册表快照，可撤销）。</summary>
    public int DeleteMissingRecords(IEnumerable<TrayIconItem> items)
    {
        var targets = items.Where(i => i.IsMissingFile).ToList();
        if (targets.Count == 0) return 0;

        var tracked = targets.Select(i => (HiveKind.CurrentUser, $@"{NotifyIconKey}\{i.KeyName}")).ToList();

        _journal.Record($"删除 {targets.Count} 条失效托盘图标记录", tracked, () =>
        {
            foreach (var item in targets)
                Reg.DeleteTree(RegPath.Hkcu($@"{NotifyIconKey}\{item.KeyName}"));
        });

        BroadcastTraySettings();
        return targets.Count;
    }

    // ---------- 通知 ----------

    private const int HWND_BROADCAST = 0xffff;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);

    /// <summary>通知资源管理器托盘设置变了。失败不影响已写入的值。</summary>
    public static void BroadcastTraySettings()
    {
        try
        {
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero,
                "TraySettings", SMTO_ABORTIFHUNG, 3000, out _);
        }
        catch (Exception)
        {
            // 广播失败时值仍然已经写进注册表，下次登录生效
        }
    }

    // ---------- Shell verb ----------

    private static void RunVerb(string path, string verb) => Native.RunShellVerb(path, verb);
}
