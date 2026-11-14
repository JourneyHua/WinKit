using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WinKit.Core;
using WinKit.Services;
using WinKit.Views;

namespace WinKit.ViewModels;

public sealed class EnvironmentViewModel : ObservableObject, IModule
{
    private readonly EnvironmentService _service;
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;

    private bool _isBusy;
    private string _searchText = "";
    private string _summary = "";
    private EnvVarItem? _selectedVar;
    private EnvScope _pathScope = EnvScope.User;

    public EnvironmentViewModel(ChangeJournal journal, Action<string> status, Action<bool>? setBusy = null)
    {
        _service = new EnvironmentService(journal);
        _status = status;
        _setBusy = setBusy;

        All = new List<EnvVarItem>();
        Vars = new ObservableCollection<EnvVarItem>();
        PathEntries = new ObservableCollection<PathEntry>();

        RefreshCommand = new RelayCommand(Refresh);
        EditCommand = new RelayCommand(p => Edit(p as EnvVarItem));
        AddCommand = new RelayCommand(Add);
        DeleteCommand = new RelayCommand(p => Delete(p as EnvVarItem));
        CopyCommand = new RelayCommand(p => Copy(p as EnvVarItem));

        ShowUserPathCommand = new RelayCommand(() => SetPathScope(EnvScope.User));
        ShowMachinePathCommand = new RelayCommand(() => SetPathScope(EnvScope.Machine));
        AddPathEntryCommand = new RelayCommand(AddPathEntry);
        RemovePathEntryCommand = new RelayCommand(p => RemovePathEntry(p as PathEntry));
        MovePathUpCommand = new RelayCommand(p => MovePath(p as PathEntry, -1));
        MovePathDownCommand = new RelayCommand(p => MovePath(p as PathEntry, 1));
        ApplyPathCommand = new RelayCommand(ApplyPath);
        OpenPathEntryCommand = new RelayCommand(p => OpenPathEntry(p as PathEntry));
    }

    public List<EnvVarItem> All { get; }
    public ObservableCollection<EnvVarItem> Vars { get; }
    public ObservableCollection<PathEntry> PathEntries { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand ShowUserPathCommand { get; }
    public RelayCommand ShowMachinePathCommand { get; }
    public RelayCommand AddPathEntryCommand { get; }
    public RelayCommand RemovePathEntryCommand { get; }
    public RelayCommand MovePathUpCommand { get; }
    public RelayCommand MovePathDownCommand { get; }
    public RelayCommand ApplyPathCommand { get; }
    public RelayCommand OpenPathEntryCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) _setBusy?.Invoke(value);
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) ApplyFilter();
        }
    }

    public string SummaryText
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public EnvVarItem? SelectedVar
    {
        get => _selectedVar;
        set => Set(ref _selectedVar, value);
    }

    public EnvScope PathScope
    {
        get => _pathScope;
        private set
        {
            if (!Set(ref _pathScope, value)) return;
            OnPropertyChanged(nameof(IsUserPath));
            OnPropertyChanged(nameof(IsMachinePath));
            LoadPath();
        }
    }

    public bool IsUserPath => _pathScope == EnvScope.User;
    public bool IsMachinePath => _pathScope == EnvScope.Machine;

    public void Refresh()
    {
        try
        {
            _status("正在读取环境变量…");
            All.Clear();
            All.AddRange(_service.LoadAll());
            ApplyFilter();
            LoadPath();
        }
        catch (Exception ex)
        {
            _status($"读取环境变量失败：{ex.Message}");
        }
    }

    private void ApplyFilter()
    {
        Vars.Clear();
        var keyword = _searchText.Trim().ToLowerInvariant();

        foreach (var item in All)
        {
            if (keyword.Length > 0 && !item.SearchBlob.Contains(keyword)) continue;
            Vars.Add(item);
        }

        SummaryText = $"共 {All.Count} 个变量 · 当前显示 {Vars.Count} 个";
    }

    private void SetPathScope(EnvScope scope) => PathScope = scope;

    private void LoadPath()
    {
        PathEntries.Clear();
        var raw = _service.GetRawPath(_pathScope);
        foreach (var entry in _service.ParsePath(raw)) PathEntries.Add(entry);
    }

    private void Add()
    {
        var owner = Application.Current.MainWindow;

        var name = InputDialog.Prompt(owner, "新增环境变量", "变量名（例如 JAVA_HOME）", "");
        if (string.IsNullOrWhiteSpace(name)) return;

        var value = InputDialog.Prompt(owner, "新增环境变量", $"「{name.Trim()}」的值（支持 %SystemRoot% 这类引用）", "");
        if (value is null) return;

        try
        {
            _service.SetVariable(EnvScope.User, name.Trim(), value);
            _status($"已新增环境变量：{name.Trim()}");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"新增失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Edit(EnvVarItem? item)
    {
        if (item is null) return;

        var value = InputDialog.Prompt(Application.Current.MainWindow, "修改环境变量",
            $"{item.Name}（{item.ScopeText}）的新值", item.Value);

        if (value is null) return;

        try
        {
            _service.SetVariable(item.Scope, item.Name, value);
            _status($"已修改：{item.Name}");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"修改失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Delete(EnvVarItem? item)
    {
        if (item is null) return;

        var confirm = MessageBox.Show(
            $"确定删除环境变量「{item.Name}」（{item.ScopeText}）吗？\n\n删除前会存快照，可用「撤销上一步」恢复。",
            "删除环境变量", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            _service.DeleteVariable(item.Scope, item.Name);
            _status($"已删除：{item.Name}");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Copy(EnvVarItem? item)
    {
        if (item is null) return;

        try
        {
            Clipboard.SetText(item.Value);
            _status($"已复制 {item.Name} 的值");
        }
        catch (Exception ex)
        {
            _status("复制失败：" + ex.Message);
        }
    }

    private void AddPathEntry()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要加入 PATH 的文件夹",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        PathEntries.Add(new PathEntry
        {
            Raw = dialog.FolderName,
            Expanded = dialog.FolderName,
            Exists = Directory.Exists(dialog.FolderName),
            Index = PathEntries.Count
        });
    }

    private void RemovePathEntry(PathEntry? entry)
    {
        if (entry is null) return;
        PathEntries.Remove(entry);
    }

    private void MovePath(PathEntry? entry, int delta)
    {
        if (entry is null) return;

        var index = PathEntries.IndexOf(entry);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= PathEntries.Count) return;

        PathEntries.Move(index, target);
    }

    private void ApplyPath()
    {
        var scopeText = _pathScope == EnvScope.User ? "用户" : "系统";
        var missing = PathEntries.Count(e => !e.Exists);

        var confirm = MessageBox.Show(
            $"将把{scopeText} PATH 改写为当前列表的 {PathEntries.Count} 条（顺序即生效顺序）。\n\n" +
            (missing == 0 ? "" : $"其中 {missing} 条目录当前不存在，仍会保留。\n\n") +
            "只改 PATH 字符串，不删除任何目录；写入前会存快照，可用「撤销上一步」恢复。确定继续吗？",
            "应用 PATH", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            _service.SetPath(_pathScope, PathEntries.Select(e => e.Raw));
            _status($"已更新{scopeText} PATH（{PathEntries.Count} 条），新开的进程生效");
            LoadPath();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"写入失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenPathEntry(PathEntry? entry)
    {
        if (entry is null || !entry.Exists) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = entry.Expanded,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _status($"打开失败：{ex.Message}");
        }
    }
}
