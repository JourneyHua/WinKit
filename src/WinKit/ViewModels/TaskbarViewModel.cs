using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WinKit.Core;
using WinKit.Services;
using WinKit.Views;

namespace WinKit.ViewModels;

/// <summary>
/// 任务栏管理：固定项（列出 / 取消固定 / 打开所在位置）+ 通知区域图标（显示 / 折叠 / 清理失效记录）。
///
/// 刻意没做「固定到任务栏」和「调整顺序」：Win11 没有对外暴露 Pin to taskbar 入口，
/// 而顺序调整等于「全部取消再按序固定」，没有固定就无从重排，硬做只能靠改
/// Taskband\Favorites 那段私有二进制——实测里面只有 AppUserModelID，没有可用的路径对应关系。
/// </summary>
public sealed class TaskbarViewModel : ObservableObject
{
    private readonly TaskbarService _service;
    private readonly Action<string> _status;

    private readonly List<TrayIconItem> _allTray = new();

    private string _pinnedSummary = "";
    private string _traySummary = "";
    private string _error = "";
    private bool _onlyMissing;

    public TaskbarViewModel(ChangeJournal journal, Action<string> status)
    {
        _service = new TaskbarService(journal);
        _status = status;

        Pinned = new ObservableCollection<TaskbarPinnedItem>();
        TrayIcons = new ObservableCollection<TrayIconItem>();

        RefreshCommand = new RelayCommand(Refresh);
        UnpinCommand = new RelayCommand(p => Unpin(p as TaskbarPinnedItem));
        OpenPinnedLocationCommand = new RelayCommand(p => OpenPinnedLocation(p as TaskbarPinnedItem));
        OpenPinnedTargetCommand = new RelayCommand(p => OpenPinnedTarget(p as TaskbarPinnedItem));
        CollapseMissingCommand = new RelayCommand(() => CollapseMissing(), () => HasMissingTray);
        DeleteMissingRecordsCommand = new RelayCommand(() => DeleteMissingRecords(), () => HasMissingTray);
        OpenTrayLocationCommand = new RelayCommand(p => OpenTrayLocation(p as TrayIconItem));
        OpenPinnedFolderCommand = new RelayCommand(OpenPinnedFolder);
        RefreshTrayCommand = new RelayCommand(RefreshTray);
    }

    public ObservableCollection<TaskbarPinnedItem> Pinned { get; }
    public ObservableCollection<TrayIconItem> TrayIcons { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand UnpinCommand { get; }
    public RelayCommand OpenPinnedLocationCommand { get; }
    public RelayCommand OpenPinnedTargetCommand { get; }
    public RelayCommand CollapseMissingCommand { get; }
    public RelayCommand DeleteMissingRecordsCommand { get; }
    public RelayCommand OpenTrayLocationCommand { get; }
    public RelayCommand OpenPinnedFolderCommand { get; }
    public RelayCommand RefreshTrayCommand { get; }

    public string PinnedSummary
    {
        get => _pinnedSummary;
        private set => Set(ref _pinnedSummary, value);
    }

    public string TraySummary
    {
        get => _traySummary;
        private set => Set(ref _traySummary, value);
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (!Set(ref _error, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_error);

    public bool OnlyMissing
    {
        get => _onlyMissing;
        set
        {
            if (!Set(ref _onlyMissing, value)) return;
            ApplyTrayFilter();
        }
    }

    public bool HasMissingTray => _allTray.Any(i => i.IsMissingFile);

    public string PinnedFolder => TaskbarService.PinnedFolder;

    // ---------- 加载 ----------

    public void Refresh()
    {
        Error = "";
        RefreshPinned();
        RefreshTray();
    }

    private void RefreshPinned()
    {
        try
        {
            Pinned.Clear();

            foreach (var item in _service.LoadPinned()) Pinned.Add(item);

            var missing = Pinned.Count(i => i.IsMissing);
            var shell = Pinned.Count(i => i.IsShellItem);

            PinnedSummary = Pinned.Count == 0
                ? "任务栏没有固定项（WinKit 也不会替你添加——Win11 已不提供该入口）"
                : $"共 {Pinned.Count} 个固定项"
                  + (shell > 0 ? $"，其中 {shell} 个是系统内置入口" : "")
                  + (missing > 0 ? $"，{missing} 个目标已失效" : "");
        }
        catch (Exception ex)
        {
            Error = $"读取任务栏固定项失败：{ex.Message}";
        }
    }

    private void RefreshTray()
    {
        try
        {
            _allTray.Clear();
            _allTray.AddRange(_service.LoadTrayIcons());

            ApplyTrayFilter();

            var missing = _allTray.Count(i => i.IsMissingFile);
            var promoted = _allTray.Count(i => i.IsPromoted);

            TraySummary = $"共 {_allTray.Count} 条通知区域记录，{promoted} 个显示在托盘"
                          + (missing > 0 ? $"，{missing} 个指向已不存在的文件" : "");

            if (missing == 0 && _allTray.Count > 0)
                TraySummary += "。scoop 每升级一次就会多一条历史记录，定期来这里看一眼比较省事。";
        }
        catch (Exception ex)
        {
            Error = $"读取通知区域记录失败：{ex.Message}";
        }

        CollapseMissingCommand.RaiseCanExecuteChanged();
        DeleteMissingRecordsCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasMissingTray));
    }

    private void ApplyTrayFilter()
    {
        TrayIcons.Clear();

        foreach (var item in _allTray)
        {
            if (OnlyMissing && !item.IsMissingFile) continue;

            item.ToggleRequested = OnTrayToggleRequested;
            TrayIcons.Add(item);
        }
    }

    /// <summary>
    /// 复选框被点时的收口。失败要先摘回调再回滚，否则回滚赋值会再次触发回调，
    /// 双向失败会直接把进程递归到 StackOverflow（v1.4.0 踩过这个坑）。
    /// </summary>
    private void OnTrayToggleRequested(TrayIconItem item, bool requested)
    {
        var previous = !requested;

        if (Native.RequireAdmin("显示或隐藏托盘图标") is { } adminNeeded)
        {
            item.ToggleRequested = null;
            item.IsPromoted = previous;
            item.ToggleRequested = OnTrayToggleRequested;
            Error = adminNeeded;
            return;
        }

        try
        {
            _service.SetPromoted(item, requested);
            _status($"{(requested ? "已显示" : "已折叠")}托盘图标：{item.DisplayName}");
        }
        catch (Exception ex)
        {
            item.ToggleRequested = null;
            item.IsPromoted = previous;
            item.ToggleRequested = OnTrayToggleRequested;

            Warn($"修改托盘图标设置失败：{ex.Message}");
        }
    }

    // ---------- 固定项动作 ----------

    private async void Unpin(TaskbarPinnedItem? item)
    {
        if (item is null) return;
        if (Native.RequireAdmin("取消固定") is { } adminNeeded)
        {
            Error = adminNeeded;
            return;
        }

        try
        {
            // 等「固定项文件被删掉」的通知可能要等上一两秒，留在 UI 线程会把窗口冻住
            await Task.Run(() => _service.Unpin(item));
            Pinned.Remove(item);

            PinnedSummary = Pinned.Count == 0
                ? "任务栏没有固定项"
                : $"共 {Pinned.Count} 个固定项";

            _status($"已取消固定：{item.Name}");
        }
        catch (Exception ex)
        {
            Warn($"取消固定失败：{ex.Message}\n\n" +
                 "如果该固定项来自系统内置入口，需要在任务栏上右键取消。");
        }
    }

    private void OpenPinnedFolder()
    {
        try
        {
            Directory.CreateDirectory(TaskbarService.PinnedFolder);
            Native.OpenPath(TaskbarService.PinnedFolder);
        }
        catch (Exception ex)
        {
            _status($"打开失败：{ex.Message}");
        }
    }

    private void OpenPinnedLocation(TaskbarPinnedItem? item)
    {
        if (item is null) return;

        try
        {
            Native.RevealInExplorer(item.ShortcutPath);
        }
        catch (Exception ex)
        {
            _status($"打开失败：{ex.Message}");
        }
    }

    private void OpenPinnedTarget(TaskbarPinnedItem? item)
    {
        if (item is null || item.IsShellItem) return;

        try
        {
            Native.OpenPath(item.TargetPath);
        }
        catch (Exception ex)
        {
            _status($"启动失败：{ex.Message}");
        }
    }

    // ---------- 托盘动作 ----------

    private void CollapseMissing()
    {
        try
        {
            var count = _service.CollapseMissing(_allTray);

            if (count == 0)
            {
                _status("没有需要折叠的失效记录");
                return;
            }

            RefreshTray();
            _status($"已折叠 {count} 条失效托盘图标记录（可在「备份与快照」里撤销）");
        }
        catch (Exception ex)
        {
            Warn($"折叠失效记录失败：{ex.Message}");
        }
    }

    private void DeleteMissingRecords()
    {
        if (Native.RequireAdmin("删除失效托盘记录") is { } adminNeeded)
        {
            Error = adminNeeded;
            return;
        }

        var missing = _allTray.Where(i => i.IsMissingFile).ToList();
        if (missing.Count == 0) return;

        if (MessageBox.Show(Application.Current.MainWindow,
                $"将删除 {missing.Count} 条指向已不存在文件的托盘图标记录。\n\n" +
                "删除是整键删除（含图标缓存），已写入注册表快照，可以在「备份与快照」里撤销。",
                "WinKit", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        try
        {
            var count = _service.DeleteMissingRecords(missing);
            RefreshTray();
            _status($"已删除 {count} 条失效托盘图标记录");
        }
        catch (Exception ex)
        {
            Warn($"删除失效记录失败：{ex.Message}");
        }
    }

    private void OpenTrayLocation(TrayIconItem? item)
    {
        if (item is null) return;

        var path = item.ResolvedPath;

        try
        {
            if (File.Exists(path))
            {
                Native.RevealInExplorer(path);
                return;
            }

            Core.Native.OpenInRegedit($@"HKEY_CURRENT_USER\Control Panel\NotifyIconSettings\{item.KeyName}");
        }
        catch (Exception ex)
        {
            _status($"打开失败：{ex.Message}");
        }
    }

    private static void Warn(string message) => MessageBox.Show(
        Application.Current.MainWindow, message, "WinKit",
        MessageBoxButton.OK, MessageBoxImage.Warning);
}
