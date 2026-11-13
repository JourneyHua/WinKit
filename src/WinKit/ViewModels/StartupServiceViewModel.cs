using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WinKit.Core;
using WinKit.Services;
using WinKit.Views;

namespace WinKit.ViewModels;

public sealed class StartupServiceViewModel : ObservableObject, IModule
{
    private readonly StartupServiceManager _service;
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;

    private List<StartupEntryItem> _allStartups = new();
    private List<WinServiceItem> _allServices = new();
    private List<ScheduledTaskItem> _allTasks = new();

    private string _subView = "startup"; // startup | services | tasks
    private string _searchText = "";
    private bool _onlyThirdParty = true;
    private bool _isBusy;
    private string _summaryText = "";

    public StartupServiceViewModel(ChangeJournal journal, Action<string> status, Action<bool>? setBusy = null)
    {
        _service = new StartupServiceManager(journal);
        _status = status;
        _setBusy = setBusy;

        Startups = new ObservableCollection<StartupEntryItem>();
        Services = new ObservableCollection<WinServiceItem>();
        Tasks = new ObservableCollection<ScheduledTaskItem>();

        ShowStartupViewCommand = new RelayCommand(() => SubView = "startup");
        ShowServicesViewCommand = new RelayCommand(() => SubView = "services");
        ShowTasksViewCommand = new RelayCommand(() => SubView = "tasks");
        RefreshCommand = new RelayCommand(RefreshCurrent);

        OpenStartupFileCommand = new RelayCommand(p => OpenStartupFile(p as StartupEntryItem));
        OpenStartupRegeditCommand = new RelayCommand(p => OpenStartupRegedit(p as StartupEntryItem));
        DeleteStartupCommand = new RelayCommand(p => DeleteStartup(p as StartupEntryItem));

        ToggleServiceRunCommand = new RelayCommand(p => _ = ToggleServiceRunAsync(p as WinServiceItem));
        ChangeServiceStartTypeCommand = new RelayCommand(p => ChangeServiceStartType(p as WinServiceItem));
        OpenServiceRegeditCommand = new RelayCommand(p => OpenServiceRegedit(p as WinServiceItem));
        OpenServicesMscCommand = new RelayCommand(OpenServicesMsc);

        ToggleTaskCommand = new RelayCommand(p => _ = ToggleTaskAsync(p as ScheduledTaskItem));
        RunTaskNowCommand = new RelayCommand(p => _ = RunTaskNowAsync(p as ScheduledTaskItem));
        OpenTaskSchedulerCommand = new RelayCommand(OpenTaskScheduler);
    }

    public ObservableCollection<StartupEntryItem> Startups { get; }
    public ObservableCollection<WinServiceItem> Services { get; }
    public ObservableCollection<ScheduledTaskItem> Tasks { get; }

    public RelayCommand ShowStartupViewCommand { get; }
    public RelayCommand ShowServicesViewCommand { get; }
    public RelayCommand ShowTasksViewCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public RelayCommand OpenStartupFileCommand { get; }
    public RelayCommand OpenStartupRegeditCommand { get; }
    public RelayCommand DeleteStartupCommand { get; }

    public RelayCommand ToggleServiceRunCommand { get; }
    public RelayCommand ChangeServiceStartTypeCommand { get; }
    public RelayCommand OpenServiceRegeditCommand { get; }
    public RelayCommand OpenServicesMscCommand { get; }

    public RelayCommand ToggleTaskCommand { get; }
    public RelayCommand RunTaskNowCommand { get; }
    public RelayCommand OpenTaskSchedulerCommand { get; }

    public string SubView
    {
        get => _subView;
        set
        {
            if (!Set(ref _subView, value)) return;
            OnPropertyChanged(nameof(ShowStartup));
            OnPropertyChanged(nameof(ShowServices));
            OnPropertyChanged(nameof(ShowTasks));

            if (value == "services" && _allServices.Count == 0)
            {
                _ = LoadServicesAsync();
                return;
            }
            if (value == "tasks" && _allTasks.Count == 0)
            {
                _ = LoadTasksAsync();
                return;
            }
            ApplyFilter();
        }
    }

    public bool ShowStartup => _subView == "startup";
    public bool ShowServices => _subView == "services";
    public bool ShowTasks => _subView == "tasks";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) ApplyFilter();
        }
    }

    public bool OnlyThirdParty
    {
        get => _onlyThirdParty;
        set
        {
            if (Set(ref _onlyThirdParty, value)) ApplyFilter();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) _setBusy?.Invoke(value);
        }
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    public void Refresh()
    {
        LoadStartups();
    }

    private void RefreshCurrent()
    {
        if (ShowServices) _ = LoadServicesAsync();
        else if (ShowTasks) _ = LoadTasksAsync();
        else LoadStartups();
    }

    private void LoadStartups()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _allStartups = _service.LoadStartupEntries();
            foreach (var item in _allStartups)
            {
                item.ToggleRequested = OnStartupToggled;
            }
            ApplyFilter();
            _status($"已加载 {_allStartups.Count} 个开机启动项");
        }
        catch (Exception ex)
        {
            _status($"加载开机启动项失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadServicesAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status("正在扫描本机后台服务…");
            _allServices = await _service.LoadServicesAsync();
            ApplyFilter();
            _status($"已加载 {_allServices.Count} 个后台服务");
        }
        catch (Exception ex)
        {
            _status($"加载服务失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadTasksAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status("正在枚举系统计划任务…");
            _allTasks = await _service.LoadScheduledTasksAsync();
            ApplyFilter();
            _status($"已加载 {_allTasks.Count} 个计划任务");
        }
        catch (Exception ex)
        {
            _status($"加载计划任务失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        var kw = _searchText.Trim().ToLowerInvariant();

        if (ShowStartup)
        {
            Startups.Clear();
            var query = _allStartups
                .Where(s => !_onlyThirdParty || !s.IsMicrosoft)
                .Where(s => kw.Length == 0 || s.SearchBlob.Contains(kw));
            foreach (var item in query) Startups.Add(item);

            var enabledCount = _allStartups.Count(s => s.IsEnabled);
            SummaryText = $"开机启动项共 {_allStartups.Count} 项（已启用 {enabledCount} 项）· 当前显示 {Startups.Count} 项";
        }
        else if (ShowServices)
        {
            Services.Clear();
            var query = _allServices
                .Where(s => !_onlyThirdParty || !s.IsMicrosoft)
                .Where(s => kw.Length == 0 || s.SearchBlob.Contains(kw));
            foreach (var item in query) Services.Add(item);

            var runningCount = Services.Count(s => s.IsRunning);
            SummaryText = $"系统服务共 {_allServices.Count} 个 · 当前显示 {Services.Count} 个（运行中 {runningCount} 个）";
        }
        else
        {
            Tasks.Clear();
            var query = _allTasks
                .Where(t => !_onlyThirdParty || !t.IsMicrosoft)
                .Where(t => kw.Length == 0 || t.SearchBlob.Contains(kw));
            foreach (var item in query) Tasks.Add(item);

            var enabledCount = Tasks.Count(t => t.IsEnabled);
            SummaryText = $"计划任务共 {_allTasks.Count} 个 · 当前显示 {Tasks.Count} 个（已启用 {enabledCount} 个）";
        }
    }

    private void OnStartupToggled(StartupEntryItem item, bool enabled)
    {
        try
        {
            _service.SetStartupEnabled(item, enabled);
            _status($"{(enabled ? "已启用" : "已禁用")}开机自启：{item.Name}");
            ApplyFilter();
        }
        catch (Exception ex)
        {
            item.ToggleRequested = null;
            item.IsEnabled = !enabled;
            item.ToggleRequested = OnStartupToggled;
            MessageBox.Show($"修改开机启动状态失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenStartupFile(StartupEntryItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.ExecutablePath)) return;
        var dir = Path.GetDirectoryName(item.ExecutablePath);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show("目标文件所在目录不存在。", "WinKit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = File.Exists(item.ExecutablePath) ? $"/select,\"{item.ExecutablePath}\"" : $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception) { }
    }

    private void OpenStartupRegedit(StartupEntryItem? item)
    {
        if (item is null) return;
        if (item.IsStartupFolder)
        {
            OpenStartupFile(item);
            return;
        }

        var path = RegPath.Of(item.Hive, item.SubKey).FullPath;
        if (Native.OpenInRegedit(path))
            _status($"已在注册表编辑器中定位：{item.Name}");
    }

    private void DeleteStartup(StartupEntryItem? item)
    {
        if (item is null) return;

        var confirm = MessageBox.Show(
            $"确定删除启动项「{item.Name}」吗？\n\n位置：{item.LocationTitle}\n命令：{item.Command}\n\n删除前会自动保存快照，随时可通过顶部「撤销上一步」恢复。",
            "删除启动项", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            _service.DeleteStartupItem(item);
            _allStartups.Remove(item);
            Startups.Remove(item);
            ApplyFilter();
            _status($"已删除启动项：{item.Name}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ToggleServiceRunAsync(WinServiceItem? item)
    {
        if (item is null || IsBusy) return;
        var targetRun = !item.IsRunning;

        if (!targetRun && item.IsMicrosoft)
        {
            var confirm = MessageBox.Show(
                $"「{item.DisplayName}」是微软系统组件服务，停止它可能影响相关系统功能。确定继续吗？",
                "停止系统服务", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;
        }

        IsBusy = true;
        try
        {
            _status($"正在{(targetRun ? "启动" : "停止")}服务：{item.DisplayName}…");
            var (ok, msg) = await _service.StartOrStopServiceAsync(item, targetRun);
            _status(msg);
            if (!ok)
            {
                MessageBox.Show(msg, "服务操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ChangeServiceStartType(WinServiceItem? item)
    {
        if (item is null) return;

        var options = new[]
        {
            "自动 (Auto - 随开机自动启动)",
            "手动 (Demand - 按需由程序拉起)",
            "禁用 (Disabled - 禁止启动)"
        };

        var picked = ChoiceDialog.Pick(
            Application.Current.MainWindow,
            "修改服务自启类型",
            $"服务名称：{item.DisplayName} ({item.ServiceName})\n当前类型：{item.StartTypeText}\n\n修改前将自动保存注册表快照，可随时撤销：",
            options);

        if (picked is null) return;

        var newType = picked.StartsWith("自动") ? 2 : picked.StartsWith("手动") ? 3 : 4;
        if (newType == item.StartType) return;

        try
        {
            _service.SetServiceStartType(item, newType);
            _status($"已将「{item.DisplayName}」设为：{item.StartTypeText}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"修改服务启动类型失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenServiceRegedit(WinServiceItem? item)
    {
        if (item is null) return;
        if (Native.OpenInRegedit(item.RegistryPath))
            _status($"已在注册表编辑器中定位：{item.ServiceName}");
    }

    private void OpenServicesMsc()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "services.msc", UseShellExecute = true });
        }
        catch (Exception) { }
    }

    private async Task ToggleTaskAsync(ScheduledTaskItem? item)
    {
        if (item is null || IsBusy) return;
        IsBusy = true;

        try
        {
            var target = !item.IsEnabled;
            var (ok, msg) = await _service.SetTaskEnabledAsync(item, target);
            _status(msg);
            if (!ok)
                MessageBox.Show(msg, "计划任务", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunTaskNowAsync(ScheduledTaskItem? item)
    {
        if (item is null || IsBusy) return;
        IsBusy = true;

        try
        {
            var (ok, msg) = await _service.RunTaskNowAsync(item);
            _status(msg);
            if (!ok)
                MessageBox.Show(msg, "运行计划任务", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenTaskScheduler()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "taskschd.msc", UseShellExecute = true });
        }
        catch (Exception) { }
    }
}
