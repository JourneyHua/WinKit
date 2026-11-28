using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WinKit.Core;
using WinKit.Services;
using WinKit.Views;

namespace WinKit.ViewModels;

public sealed class NavPaneViewModel : ObservableObject, IModule, IModuleActivation
{
    private readonly NavPaneService _service;
    private readonly NavPinnedService _pinnedService;
    private readonly StubbornNodeService _stubbornService;
    private readonly QuickAccessOrderStore _orderStore = new();
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;
    private string[]? _fixedSnapshot;
    private IReadOnlyList<string> _desiredOrder;
    private string _quickAccessStatus = "";
    private string _loadError = "";
    private bool _isLoading;
    private bool _isActive = true;
    private bool _refreshNeeded;
    private int _dataVersion;
    private NavigationSnapshot? _pendingSnapshot;
    private sealed record NavigationSnapshot(List<QuickAccessItem> QuickAccess, string? Error,
        List<PinnedFolderItem> Folders, List<NavToggleItem> Toggles, List<StubbornGroup> Stubborn);
    private string _subView = "navpane"; // navpane | taskbar

    public NavPaneViewModel(ChangeJournal journal, Action<string> status, Action<bool>? setBusy = null)
    {
        _service = new NavPaneService(journal);
        _pinnedService = new NavPinnedService(journal);
        _stubbornService = new StubbornNodeService(journal);
        _status = status;
        _setBusy = setBusy;
        _desiredOrder = _orderStore.Load();

        Pinned = new ObservableCollection<QuickAccessItem>();
        PinnedFolders = new ObservableCollection<PinnedFolderItem>();
        StubbornGroups = new ObservableCollection<StubbornGroup>();
        Toggles = new ObservableCollection<NavToggleItem>();
        Taskbar = new TaskbarViewModel(journal, status);

        ShowNavPaneCommand = new RelayCommand(() => SubView = "navpane", () => !IsLoading);
        ShowTaskbarCommand = new RelayCommand(() => SubView = "taskbar", () => !IsLoading);
        RefreshCommand = new RelayCommand(() => { QuickAccessStatus = ""; Refresh(); }, () => !IsLoading);
        PinCommand = new RelayCommand(() => _ = PinFolderAsync(), () => CanManagePins);
        UnpinCommand = new RelayCommand(p => _ = UnpinAsync((QuickAccessItem)p!),
            p => CanManagePins && p is QuickAccessItem { IsPinnedFolder: true } item && Pinned.Contains(item));
        OpenCommand = new RelayCommand(p => Open((QuickAccessItem)p!),
            p => p is QuickAccessItem item && !string.IsNullOrWhiteSpace(item.Path));
        MoveUpCommand = new RelayCommand(p => Move((QuickAccessItem)p!, -1), p => CanMove(p as QuickAccessItem, -1));
        MoveDownCommand = new RelayCommand(p => Move((QuickAccessItem)p!, 1), p => CanMove(p as QuickAccessItem, 1));
        ApplyOrderCommand = new RelayCommand(() => _ = ApplyOrderAsync(), () => CanManagePins && HasOrderDraft);
        DiscardOrderCommand = new RelayCommand(DiscardOrder, () => CanManagePins && HasOrderDraft);
        PurgeStubbornCommand = new RelayCommand(p => PurgeStubborn(p as StubbornGroup));
        StopRebuilderCommand = new RelayCommand(p => StopRebuilder(p as StubbornGroup));
        CopyPinnedPathCommand = new RelayCommand(p => CopyPinnedPath(p as PinnedFolderItem));
        OpenPinnedRegeditCommand = new RelayCommand(p => OpenPinnedRegedit(p as PinnedFolderItem));
    }

    public ObservableCollection<QuickAccessItem> Pinned { get; }
    public ObservableCollection<PinnedFolderItem> PinnedFolders { get; }
    public ObservableCollection<StubbornGroup> StubbornGroups { get; }
    public ObservableCollection<NavToggleItem> Toggles { get; }

    /// <summary>任务栏子视图（固定项 + 托盘图标）。</summary>
    public TaskbarViewModel Taskbar { get; }

    public RelayCommand ShowNavPaneCommand { get; }
    public RelayCommand ShowTaskbarCommand { get; }

    public string SubView
    {
        get => _subView;
        set
        {
            if (!Set(ref _subView, value)) return;
            OnPropertyChanged(nameof(ShowNavPane));
            OnPropertyChanged(nameof(ShowTaskbar));
            if (!_isActive) return;
            if (ShowTaskbar) Taskbar.OnActivated();
            else
            {
                Taskbar.OnDeactivated();
                ShowNavigationSnapshot();
            }
        }
    }

    public bool ShowNavPane
    {
        get => _subView == "navpane";
        set => SubView = value ? "navpane" : "taskbar";
    }

    public bool ShowTaskbar
    {
        get => _subView == "taskbar";
        set => SubView = value ? "taskbar" : "navpane";
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand PinCommand { get; }
    public RelayCommand UnpinCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand ApplyOrderCommand { get; }
    public RelayCommand DiscardOrderCommand { get; }
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

    public string QuickAccessStatus
    {
        get => _quickAccessStatus;
        private set
        {
            if (Set(ref _quickAccessStatus, value)) OnPropertyChanged(nameof(HasQuickAccessStatus));
        }
    }

    public bool HasQuickAccessStatus => QuickAccessStatus.Length > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(CanManagePins));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool CanManagePins => !IsLoading && _fixedSnapshot is not null;
    public bool HasOrderDraft => _fixedSnapshot is not null
        && !Pinned.Where(item => item.IsPinnedFolder).Select(item => item.Path)
            .SequenceEqual(_fixedSnapshot, StringComparer.OrdinalIgnoreCase);

    private void UpdatePinState()
    {
        OnPropertyChanged(nameof(CanManagePins));
        OnPropertyChanged(nameof(HasOrderDraft));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void SetQuickAccessItems(List<QuickAccessItem> items, string? error)
    {
        _fixedSnapshot = null;
        if (error is null)
        {
            try { _fixedSnapshot = NavPaneService.GetFixedFolders(items); }
            catch (InvalidOperationException ex) { error = ex.Message; }
        }
        LoadError = error ?? "";

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_fixedSnapshot is not null)
            for (var i = 0; i < _desiredOrder.Count; i++) rank.TryAdd(_desiredOrder[i], i);
        Pinned.Clear();
        foreach (var item in items.OrderBy(item => !item.IsPinnedFolder)
                     .ThenBy(item => item.IsPinnedFolder && rank.TryGetValue(item.Path, out var index) ? index : int.MaxValue))
            Pinned.Add(item);
        UpdatePinState();
    }

    public void OnActivated()
    {
        _isActive = true;
        if (ShowTaskbar) Taskbar.OnActivated();
        else ShowNavigationSnapshot();
    }

    public void OnDeactivated()
    {
        _isActive = false;
        Taskbar.OnDeactivated();
    }

    private void ShowNavigationSnapshot()
    {
        if (_pendingSnapshot is { } snapshot)
        {
            _pendingSnapshot = null;
            ApplySnapshot(snapshot);
        }
        if (_refreshNeeded) _ = RefreshNavigationAsync();
    }

    public void Refresh()
    {
        _dataVersion++;
        _pendingSnapshot = null;
        _refreshNeeded = true;
        Taskbar.Invalidate();
        if (!_isActive || IsLoading) return;
        if (ShowTaskbar) Taskbar.EnsureLoaded();
        else _ = RefreshNavigationAsync();
    }

    private async Task RefreshNavigationAsync()
    {
        if (IsLoading || !_isActive || !ShowNavPane) return;
        IsLoading = true;
        _refreshNeeded = false;
        var version = _dataVersion;
        try
        {
            var snapshot = await ShellReadWorker.RunAsync(() =>
            {
                var (items, error) = _service.LoadQuickAccess();
                return new NavigationSnapshot(items, error, _pinnedService.Load(),
                    _service.LoadToggles(), _stubbornService.Load());
            });
            if (version != _dataVersion) { _refreshNeeded = true; return; }
            if (_isActive && ShowNavPane) ApplySnapshot(snapshot);
            else _pendingSnapshot = snapshot;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }
        finally
        {
            IsLoading = false;
            if (_isActive && ShowNavPane && _refreshNeeded) _ = RefreshNavigationAsync();
        }
    }

    private void NoteDataChanged()
    {
        _dataVersion++;
        _pendingSnapshot = null;
        if (IsLoading) _refreshNeeded = true;
    }

    private void ApplySnapshot(NavigationSnapshot snapshot)
    {
        SetQuickAccessItems(snapshot.QuickAccess, snapshot.Error);
        PinnedFolders.Clear();
        foreach (var folder in snapshot.Folders)
        {
            folder.ToggleRequested = OnPinnedFolderToggled;
            PinnedFolders.Add(folder);
        }
        Toggles.Clear();
        foreach (var toggle in snapshot.Toggles)
        {
            toggle.ToggleRequested = OnToggleChanged;
            Toggles.Add(toggle);
        }
        StubbornGroups.Clear();
        foreach (var group in snapshot.Stubborn) StubbornGroups.Add(group);
    }

    private void OnPinnedFolderToggled(PinnedFolderItem item, bool visible)
    {
        try
        {
            _pinnedService.SetVisible(item, visible);
            NoteDataChanged();
            _status($"{(visible ? "已恢复" : "已隐藏")}：{item.DisplayName}");
        }
        catch (Exception ex)
        {
            // 回滚时先摘掉回调，否则赋值会再次触发本方法，双向失败会无限递归
            item.ToggleRequested = null;
            item.IsVisible = !visible;
            item.ToggleRequested = OnPinnedFolderToggled;
            MessageBox.Show($"操作失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnToggleChanged(NavToggleItem item, bool value)
    {
        if (Native.RequireAdmin("修改导航栏项") is { } adminNeeded)
        {
            item.ToggleRequested = null;
            item.IsOn = !value;
            item.ToggleRequested = OnToggleChanged;
            LoadError = adminNeeded;
            return;
        }

        try
        {
            _service.SetToggle(item, value);
            NoteDataChanged();
            _status($"{(value ? "已开启" : "已关闭")}：{item.Title}");
        }
        catch (Exception ex)
        {
            item.ToggleRequested = null;
            item.IsOn = !value;
            item.ToggleRequested = OnToggleChanged;
            MessageBox.Show($"操作失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task PinFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要固定到快速访问的文件夹",
            Multiselect = false
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        var path = dialog.FolderName;
        await RunPinChangeAsync("正在固定文件夹…", "固定文件夹未完成", () =>
        {
            _service.Pin(path);
            return new($"已固定：{path}");
        });
    }

    private Task UnpinAsync(QuickAccessItem item)
        => RunPinChangeAsync("正在取消固定…", "取消固定未完成", () =>
        {
            _service.Unpin(item.Path);
            return new($"已取消固定：{item.Name}");
        });

    private void Open(QuickAccessItem item)
    {
        if (!Native.OpenPath(item.Path)) ReportQuickAccess($"无法打开：{item.Path}");
    }

    private bool CanMove(QuickAccessItem? item, int delta)
    {
        if (!CanManagePins || item is not { IsPinnedFolder: true }) return false;
        var index = Pinned.IndexOf(item);
        var target = index + delta;
        return index >= 0 && target >= 0 && target < Pinned.Count && Pinned[target].IsPinnedFolder;
    }

    private void Move(QuickAccessItem item, int delta)
    {
        if (!CanMove(item, delta)) return;
        var index = Pinned.IndexOf(item);
        Pinned.Move(index, index + delta);
        UpdatePinState();
        SaveOrderDraft(HasOrderDraft ? DesiredOrder() : [], HasOrderDraft
            ? "新顺序已保存为草稿，尚未应用到资源管理器。"
            : "已恢复资源管理器中的顺序。");
    }

    private string[] DesiredOrder() => Pinned.Where(item => item.IsPinnedFolder).Select(item => item.Path).ToArray();

    private void DiscardOrder()
    {
        SaveOrderDraft([], "已放弃草稿，未更改资源管理器中的顺序。");
        SetQuickAccessItems(Pinned.OrderBy(item => item.Order).ToList(), null);
    }

    private void SaveOrderDraft(IReadOnlyList<string> paths, string message)
    {
        _desiredOrder = paths;
        try { _orderStore.Save(paths); }
        catch (Exception ex) { message = $"本次更改已保留，但无法保存供下次使用：{ex.Message}"; }
        ReportQuickAccess(message);
    }

    private Task ApplyOrderAsync()
    {
        if (_fixedSnapshot is not { } expected || !ApplyOrderCommand.CanExecute(null)) return Task.CompletedTask;
        var desired = DesiredOrder();
        var confirmation = MessageBox.Show(Application.Current.MainWindow,
            "资源管理器会暂时取消固定需要移动的文件夹，再按新顺序固定。文件和文件夹内容不会更改。",
            "应用快速访问顺序", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirmation != MessageBoxResult.OK) return Task.CompletedTask;

        return RunPinChangeAsync("正在调整快速访问顺序…", "未能应用新顺序", () =>
        {
            var result = _service.RebuildOrder(expected, desired);
            var message = result.Outcome switch
            {
                QuickAccessOrderOutcome.Applied => "已在资源管理器应用新顺序。",
                QuickAccessOrderOutcome.Changed => "资源管理器的固定项已变化，WinKit 未修改。请核对列表和草稿再应用。",
                QuickAccessOrderOutcome.NotApplied => $"未修改固定项。{result.Reason}",
                QuickAccessOrderOutcome.Restored => $"未能应用新顺序，已确认原顺序。\n{result.Reason}",
                _ when result.Missing is { Count: > 0 } missing =>
                    $"未能恢复原顺序，以下文件夹目前未固定：\n{string.Join("\n", missing)}\n{result.Reason}",
                _ when result.Missing is not null => $"未能恢复原顺序，原有文件夹仍已固定。\n{result.Reason}",
                _ => $"未能确认调整结果，请刷新列表核对。\n{result.Reason}"
            };
            return new(message, result.Outcome == QuickAccessOrderOutcome.Applied);
        });
    }

    private sealed record PinChangeResult(string Message, bool AppliedOrder = false);

    private async Task RunPinChangeAsync(string progress, string failure, Func<PinChangeResult> change)
    {
        if (!CanManagePins) return;
        IsLoading = true;
        _setBusy?.Invoke(true);
        ReportQuickAccess(progress);
        try
        {
            var (result, items, error) = await ShellReadWorker.RunAsync(() =>
            {
                PinChangeResult outcome;
                try { outcome = change(); }
                catch (Exception ex) { outcome = new($"{failure}：{ex.Message}"); }
                var (loadedItems, loadError) = _service.LoadQuickAccess();
                return (outcome, loadedItems, loadError);
            });
            if (result.AppliedOrder) SaveOrderDraft([], result.Message);
            else ReportQuickAccess(result.Message);
            SetQuickAccessItems(items, error);
        }
        finally
        {
            IsLoading = false;
            _setBusy?.Invoke(false);
        }
    }

    private void ReportQuickAccess(string message)
    {
        QuickAccessStatus = message;
        _status(message.Replace('\n', ' '));
    }

    private void PurgeStubborn(StubbornGroup? group)
    {
        if (group is null) return;

        var result = MessageBox.Show(
            $"「{group.DisplayName}」一共注册了 {group.GuidCount} 个不同 GUID 的入口：\n" +
            string.Join("\n", group.Guids) + "\n\n" +
            "这类软件每次启动都会换一个新 GUID 重新注册，所以按单个 ID 关永远关不完。\n" +
            "将一次性删除全部这些注册项（含此电脑与导航栏两处），删除前会存快照。\n\n确定继续吗？",
            "彻底清理顽固节点", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

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
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        var ok = _stubbornService.StopProcess(picked);
        _status(ok ? $"已结束：{picked}" : $"结束失败：{picked}");
        Refresh();
    }

}
