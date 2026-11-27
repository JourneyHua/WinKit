using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WinKit.Core;

namespace WinKit.Services;

public sealed class QuickAccessItem : ObservableObject
{
    private int _order;

    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsFolder { get; init; }
    public bool? IsPinned { get; init; }
    public bool IsPinnedFolder => IsFolder && IsPinned is true;

    public string KindText => IsPinned is null ? "未确认固定状态"
        : IsPinned is true ? "已固定"
        : IsFolder ? "常用文件夹" : "最近使用";

    public int Order
    {
        get => _order;
        set => Set(ref _order, value);
    }
}

public sealed class NavToggleItem : ObservableObject
{
    private bool _isOn;

    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (value == _isOn) return;
            _isOn = value;
            OnPropertyChanged();
            ToggleRequested?.Invoke(this, value);
        }
    }

    public Action<NavToggleItem, bool>? ToggleRequested { get; set; }
}

public sealed class NavPaneService
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string QuickAccessClsid = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    private readonly ChangeJournal _journal;

    public NavPaneService(ChangeJournal journal) => _journal = journal;

    public (List<QuickAccessItem> Items, string? Error) LoadQuickAccess()
    {
        var items = new List<QuickAccessItem>();

        object? shell = null;
        object? folder = null;
        object? collection = null;

        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return (items, "系统未注册 Shell.Application");

            shell = Activator.CreateInstance(type);
            if (shell is null) return (items, "无法创建 Shell.Application");

            folder = ((dynamic)shell).Namespace(QuickAccessClsid);
            if (folder is null) return (items, "无法打开快速访问命名空间");

            collection = ((dynamic)folder).Items();
            var count = (int)((dynamic)collection).Count;
            var order = 0;

            for (var i = 0; i < count; i++)
            {
                object? item = null;
                try
                {
                    item = ((dynamic)collection).Item(i);
                    if (item is null) return (items, "快速访问列表已变化，请刷新后重试。");

                    string name = "";
                    string path = "";
                    try { name = (string)((dynamic)item).Name ?? ""; } catch (Exception) { }
                    try { path = (string)((dynamic)item).Path ?? ""; } catch (Exception) { }

                    // 常用文件夹也是目录，但不是固定项。读取 Explorer 自己的固定标记，
                    // 不用 Directory.Exists 猜测（离线或不可访问的固定项也不能忽略）。
                    var isFolder = (bool)((dynamic)item).IsFolder;
                    bool? isPinned = null;
                    try { isPinned = (object?)((dynamic)item).ExtendedProperty("System.Home.IsPinned") as bool?; }
                    catch (Exception) { /* 不确定的固定项只读展示。 */ }

                    items.Add(new QuickAccessItem
                    {
                        Name = !string.IsNullOrWhiteSpace(name) ? name : !string.IsNullOrWhiteSpace(path) ? path : "未命名项目",
                        Path = path,
                        IsFolder = isFolder,
                        IsPinned = isPinned,
                        Order = order++
                    });
                }
                finally
                {
                    ReleaseCom(item);
                }
            }
        }
        catch (Exception ex)
        {
            return (items, ex.Message);
        }
        finally
        {
            ReleaseCom(collection);
            ReleaseCom(folder);
            ReleaseCom(shell);
        }

        return (items.OrderBy(i => i.IsPinnedFolder ? 0 : 1).ToList(), null);
    }

    internal static string[] GetFixedFolders(IEnumerable<QuickAccessItem> items)
    {
        var candidates = items.Where(item => item.IsFolder).ToArray();
        var paths = candidates.Where(item => item.IsPinned is true).Select(item => item.Path).ToArray();
        if (candidates.Any(item => item.IsPinned is null) || paths.Any(string.IsNullOrWhiteSpace)
            || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new InvalidOperationException("无法确认所有固定文件夹，请刷新列表后重试。");
        return paths;
    }

    private string[] ReadFixedFolders()
    {
        var (items, error) = LoadQuickAccess();
        if (error is not null) throw new InvalidOperationException(error);
        return GetFixedFolders(items);
    }

    private static bool CanPin(string path) => Path.IsPathFullyQualified(path) && Directory.Exists(path);

    public void Pin(string folderPath) => SetPinned(folderPath, true);
    public void Unpin(string folderPath) => SetPinned(folderPath, false);

    private void SetPinned(string path, bool pinned)
    {
        bool Check() => ReadFixedFolders().Contains(path, StringComparer.OrdinalIgnoreCase) == pinned;
        if (Check()) return;
        if (pinned && !CanPin(path)) throw new DirectoryNotFoundException($"无法访问文件夹：{path}");
        RunVerb(path, pinned ? "pintohome" : "unpinfromhome");
        for (var i = 0; i < 12; i++)
        {
            if (i != 0) Thread.Sleep(100);
            if (Check()) return;
        }
        throw new InvalidOperationException($"资源管理器未确认{(pinned ? "固定" : "取消固定")}：{path}");
    }

    public QuickAccessOrderResult RebuildOrder(IReadOnlyList<string> expected, IReadOnlyList<string> desired)
        => QuickAccessOrder.Apply(expected, desired, ReadFixedFolders,
            path => RunVerb(path, "pintohome"), path => RunVerb(path, "unpinfromhome"), CanPin, () => Thread.Sleep(100));

    public List<NavToggleItem> LoadToggles()
    {
        var advanced = RegPath.Hkcu(AdvancedKey);
        var explorer = RegPath.Hkcu(ExplorerKey);

        return new List<NavToggleItem>
        {
            new()
            {
                Id = "showAllFolders",
                Title = "显示所有文件夹",
                Description = "在导航窗格中把“此电脑”的每个文件夹都平铺出来（对应 NavPaneShowAllFolders）",
                IsOn = (Reg.GetInt(advanced, "NavPaneShowAllFolders") ?? 0) == 1
            },
            new()
            {
                Id = "expandToCurrent",
                Title = "自动展开到当前文件夹",
                Description = "导航窗格自动定位并展开你当前所在的位置（NavPaneExpandToCurrentFolder）",
                IsOn = (Reg.GetInt(advanced, "NavPaneExpandToCurrentFolder") ?? 0) == 1
            },
            new()
            {
                Id = "showRecent",
                Title = "显示最近使用的文件",
                Description = "快速访问里列出最近打开的文件（ShowRecent）",
                IsOn = (Reg.GetInt(explorer, "ShowRecent") ?? 1) == 1
            },
            new()
            {
                Id = "showFrequent",
                Title = "显示常用文件夹",
                Description = "快速访问里列出你经常访问的文件夹（ShowFrequent）",
                IsOn = (Reg.GetInt(explorer, "ShowFrequent") ?? 1) == 1
            }
        };
    }

    public void SetToggle(NavToggleItem item, bool value)
    {
        var (subKey, name) = item.Id switch
        {
            "showAllFolders" => (AdvancedKey, "NavPaneShowAllFolders"),
            "expandToCurrent" => (AdvancedKey, "NavPaneExpandToCurrentFolder"),
            "showRecent" => (ExplorerKey, "ShowRecent"),
            "showFrequent" => (ExplorerKey, "ShowFrequent"),
            _ => throw new ArgumentOutOfRangeException(nameof(item))
        };

        _journal.Record($"{(value ? "开启" : "关闭")}：{item.Title}", new[] { (HiveKind.CurrentUser, subKey) },
            () => Reg.SetInt(RegPath.Hkcu(subKey), name, value ? 1 : 0));

        item.IsOn = value;
        Native.NotifyAssociationsChanged();
    }

    private static void RunVerb(string path, string verb) => Native.RunShellVerb(path, verb);

    private static void ReleaseCom(object? comObject)
    {
        if (comObject is null) return;
        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.FinalReleaseComObject(comObject);
        }
        catch (Exception)
        {
            // 释放失败不影响功能
        }
    }
}
