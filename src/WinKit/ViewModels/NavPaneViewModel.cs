using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WinKit.Core;
using WinKit.Services;
using WinKit.Views;

namespace WinKit.ViewModels;

public sealed class NavPaneViewModel : ObservableObject, IModule
{
    private readonly NavPaneService _service;
    private readonly NavPinnedService _pinnedService;
    private readonly StubbornNodeService _stubbornService;
    private readonly Action<string> _status;
    private string _loadError = "";
    private bool _isLoading;

    public NavPaneViewModel(ChangeJournal journal, Action<string> status)
    {
        _service = new NavPaneService(journal);
        _pinnedService = new NavPinnedService(journal);
        _stubbornService = new StubbornNodeService(journal);
        _status = status;

        Pinned = new ObservableCollection<QuickAccessItem>();
        PinnedFolders = new ObservableCollection<PinnedFolderItem>();
        StubbornGroups = new ObservableCollection<StubbornGroup>();
        Toggles = new ObservableCollection<NavToggleItem>();

        RefreshCommand = new RelayCommand(Refresh);
        PinCommand = new RelayCommand(PinFolder);
        UnpinCommand = new RelayCommand(p => Unpin(p as QuickAccessItem));
        OpenCommand = new RelayCommand(p => Open(p as QuickAccessItem));
        MoveUpCommand = new RelayCommand(p => Move(p as QuickAccessItem, -1));
        MoveDownCommand = new RelayCommand(p => Move(p as QuickAccessItem, 1));
        ApplyOrderCommand = new RelayCommand(ApplyOrder);
        RestartExplorerCommand = new RelayCommand(RestartExplorer);
        PurgeStubbornCommand = new RelayCommand(p => PurgeStubborn(p as StubbornGroup));
        StopRebuilderCommand = new RelayCommand(p => StopRebuilder(p as StubbornGroup));
        CopyPinnedPathCommand = new RelayCommand(p => CopyPinnedPath(p as PinnedFolderItem));
        OpenPinnedRegeditCommand = new RelayCommand(p => OpenPinnedRegedit(p as PinnedFolderItem));
    }

    public ObservableCollection<QuickAccessItem> Pinned { get; }
    public ObservableCollection<PinnedFolderItem> PinnedFolders { get; }
    public ObservableCollection<StubbornGroup> StubbornGroups { get; }
    public ObservableCollection<NavToggleItem> Toggles { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand PinCommand { get; }
    public RelayCommand UnpinCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand ApplyOrderCommand { get; }
    public RelayCommand RestartExplorerCommand { get; }
    public RelayCommand PurgeStubbornCommand { get; }
    public RelayCommand StopRebuilderCommand { get; }
    public RelayCommand CopyPinnedPathCommand { get; }
    public RelayCommand OpenPinnedRegeditCommand { get; }

    private void CopyPinnedPath(PinnedFolderItem? item)
    {
        if (item is null) return;
        var text = $"{item.Hive}\\{item.RegistryPath}";
        try
        {
            Clipboard.SetText(text);
            _status($"已复制路径：{text}");
        }
        catch (Exception ex)
        {
            _status("复制失败：" + ex.Message);
        }
    }

    private void OpenPinnedRegedit(PinnedFolderItem? item)
    {
        if (item is null) return;
        var path = $"{item.Hive}\\{item.RegistryPath}";
        if (Native.OpenInRegedit(path))
            _status($"已在注册表编辑器中定位：{item.Clsid}");
        else
            _status("无法打开注册表编辑器");
    }

    public string LoadError
    {
        get => _loadError;
        private set
        {
            if (Set(ref _loadError, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_loadError);

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public void Refresh()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            var (items, error) = _service.LoadQuickAccess();
            LoadError = error ?? "";

            Pinned.Clear();
            foreach (var item in items) Pinned.Add(item);

            PinnedFolders.Clear();
            foreach (var folder in _pinnedService.Load())
            {
                folder.ToggleRequested = OnPinnedFolderToggled;
                PinnedFolders.Add(folder);
            }

            Toggles.Clear();
            foreach (var toggle in _service.LoadToggles())
            {
                toggle.ToggleRequested = OnToggleChanged;
                Toggles.Add(toggle);
            }

            StubbornGroups.Clear();
            foreach (var group in _stubbornService.Load())
                StubbornGroups.Add(group);
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OnPinnedFolderToggled(PinnedFolderItem item, bool visible)
    {
        try
        {
            _pinnedService.SetVisible(item, visible);
            _status($"{(visible ? "已恢复" : "已隐藏")}：{item.DisplayName}");
        }
        catch (Exception ex)
        {
            item.IsVisible = !visible;
            MessageBox.Show($"操作失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnToggleChanged(NavToggleItem item, bool value)
    {
        try
        {
            _service.SetToggle(item, value);
            _status($"{(value ? "已开启" : "已关闭")}：{item.Title}");
        }
        catch (Exception ex)
        {
            item.IsOn = !value;
            MessageBox.Show($"操作失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PinFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要固定到快速访问的文件夹",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            _service.Pin(dialog.FolderName);
            _status($"已固定：{dialog.FolderName}");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"固定失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Unpin(QuickAccessItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path)) return;
        if (!item.IsFolder)
        {
            MessageBox.Show("“最近使用”的条目无法在此取消固定，请在资源管理器中右键该文件选择“从快速访问中取消固定”。",
                "WinKit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _service.Unpin(item.Path);
            _status($"已取消固定：{item.Name}");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"取消固定失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Open(QuickAccessItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.Path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Move(QuickAccessItem? item, int delta)
    {
        if (item is null) return;
        var index = Pinned.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Pinned.Count) return;

        Pinned.Move(index, target);
        for (var i = 0; i < Pinned.Count; i++) Pinned[i].Order = i;
    }

    private void ApplyOrder()
    {
        var paths = Pinned.Where(i => i.IsFolder && Directory.Exists(i.Path))
            .Select(i => i.Path)
            .ToList();

        if (paths.Count == 0) return;

        var result = MessageBox.Show(
            "重新排序的做法是：先取消全部固定，再按当前列表顺序重新固定。\n\n" +
            "过程中快速访问会短暂清空（几秒钟），结束后恢复。\n\n确定应用新顺序吗？",
            "应用新的固定顺序",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        try
        {
            _service.RebuildOrder(paths, msg => _status(msg));
            _status("固定顺序已按新列表重建");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"调整顺序失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PurgeStubborn(StubbornGroup? group)
    {
        if (group is null) return;

        var result = MessageBox.Show(
            $"「{group.DisplayName}」一共注册了 {group.GuidCount} 个不同 GUID 的入口：\n" +
            string.Join("\n", group.Guids) + "\n\n" +
            "这类软件每次启动都会换一个新 GUID 重新注册，所以按单个 ID 关永远关不完。\n" +
            "将一次性删除全部这些注册项（含此电脑与导航栏两处），删除前会存快照。\n\n确定继续吗？",
            "彻底清理顽固节点", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        try
        {
            _stubbornService.Purge(group);
            _status($"已清理「{group.DisplayName}」的 {group.GuidCount} 个入口");
            Refresh();
            MessageBox.Show(
                "已清理。若重启资源管理器后又出现，说明软件仍在后台重建，请点「结束重建进程」后再清一次。",
                "清理完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"清理失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StopRebuilder(StubbornGroup? group)
    {
        if (group is null) return;

        if (group.Processes.Count == 0)
        {
            MessageBox.Show(
                "没有定位到重建进程。\n\n可能是受保护进程（无法读取路径），或它由别的进程按需拉起。",
                "未找到进程", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picked = ChoiceDialog.Pick(Application.Current.MainWindow, "结束哪个进程",
            $"「{group.DisplayName}」的注册项由这些进程重建，可执行文件都在：\n{group.OwnerFolder}\n\n" +
            "建议只结束后台服务（如 wpscloudsvr.exe），不要结束你正在使用的 WPS 主程序，否则未保存的文档会丢失。",
            group.Processes);

        if (picked is null) return;

        var confirm = MessageBox.Show($"确定结束「{picked}」吗？", "确认",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        var ok = _stubbornService.StopProcess(picked);
        _status(ok ? $"已结束：{picked}" : $"结束失败：{picked}");
        Refresh();
    }

    private void RestartExplorer()
    {
        var result = MessageBox.Show("将重启资源管理器使改动立即生效，确定继续吗？",
            "重启资源管理器", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        Native.RestartExplorer();
        _status("已重启资源管理器");
    }
}
