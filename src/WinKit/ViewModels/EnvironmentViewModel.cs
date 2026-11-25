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
    private readonly Func<EnvScope, string> _readPath;
    private readonly Func<List<EnvVarItem>> _readVariables;
    private readonly Dictionary<EnvScope, PathDraft> _pathDrafts = new();
    private bool _displayingPath;

    private sealed class PathDraft
    {
        public PathDraft(string raw, IEnumerable<PathEntry> entries)
        {
            BaselineRaw = LatestRaw = raw;
            Entries = entries.ToList();
            BaselineEntries = Entries.Select(e => e.Raw).ToArray();
        }

        public string BaselineRaw { get; }
        public string LatestRaw { get; set; }
        private string[] BaselineEntries { get; }
        public List<PathEntry> Entries { get; set; }
        public bool IsDirty => HasConflict || !BaselineEntries.SequenceEqual(Entries.Select(e => e.Raw), StringComparer.Ordinal);
        public bool HasConflict => !string.Equals(BaselineRaw, LatestRaw, StringComparison.Ordinal);
    }

    private bool _isBusy;
    private string _searchText = "";
    private string _summary = "";
    private EnvVarItem? _selectedVar;
    private EnvScope _pathScope = EnvScope.User;

    public EnvironmentViewModel(ChangeJournal journal, Action<string> status, Action<bool>? setBusy = null)
        : this(journal, status, setBusy, null, null)
    {
    }

    // 可注入只读快照，供冒烟检查验证草稿生命周期，不触碰真实环境变量。
    internal EnvironmentViewModel(ChangeJournal journal, Action<string> status, Action<bool>? setBusy,
        Func<EnvScope, string>? readPath, Func<List<EnvVarItem>>? readVariables)
    {
        _service = new EnvironmentService(journal);
        _status = status;
        _setBusy = setBusy;
        _readPath = readPath ?? (scope => _service.GetRawPath(scope));
        _readVariables = readVariables ?? (() => _service.LoadAll());

        All = new List<EnvVarItem>();
        Vars = new ObservableCollection<EnvVarItem>();
        PathEntries = new ObservableCollection<PathEntry>();

        RefreshCommand = new RelayCommand(Refresh, () => !IsBusy);
        EditCommand = new RelayCommand(p => Edit(p as EnvVarItem), p => !IsBusy && p is EnvVarItem);
        AddCommand = new RelayCommand(Add, () => !IsBusy);
        DeleteCommand = new RelayCommand(p => Delete(p as EnvVarItem), p => !IsBusy && p is EnvVarItem);
        CopyCommand = new RelayCommand(p => Copy(p as EnvVarItem));

        ShowUserPathCommand = new RelayCommand(() => SetPathScope(EnvScope.User), () => !IsBusy);
        ShowMachinePathCommand = new RelayCommand(() => SetPathScope(EnvScope.Machine), () => !IsBusy);
        AddPathEntryCommand = new RelayCommand(AddPathEntry, () => CanEditPath);
        RemovePathEntryCommand = new RelayCommand(p => RemovePathEntry(p as PathEntry),
            p => CanEditPath && p is PathEntry entry && PathEntries.Contains(entry));
        MovePathUpCommand = new RelayCommand(p => MovePath(p as PathEntry, -1), p => CanMovePath(p as PathEntry, -1));
        MovePathDownCommand = new RelayCommand(p => MovePath(p as PathEntry, 1), p => CanMovePath(p as PathEntry, 1));
        ApplyPathCommand = new RelayCommand(ApplyPath, () => CanApplyPath);
        DiscardPathCommand = new RelayCommand(ConfirmDiscardPathDraft, () => !IsBusy && HasPathChanges);
        OpenPathEntryCommand = new RelayCommand(p => OpenPathEntry(p as PathEntry));
        PathEntries.CollectionChanged += (_, _) => CapturePathDraft();
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
    public RelayCommand DiscardPathCommand { get; }
    public RelayCommand OpenPathEntryCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            _setBusy?.Invoke(value);
            NotifyPathState();
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
        }
    }

    public bool IsUserPath => _pathScope == EnvScope.User;
    public bool IsMachinePath => _pathScope == EnvScope.Machine;

    private PathDraft? CurrentPathDraft => _pathDrafts.GetValueOrDefault(_pathScope);
    private string PathScopeText => IsUserPath ? "用户" : "系统";
    public bool CanEditPath => !IsBusy && CurrentPathDraft is not null;
    public bool HasPathChanges => CurrentPathDraft?.IsDirty == true;
    public bool HasPathConflict => CurrentPathDraft?.HasConflict == true;
    public bool CanApplyPath => CanEditPath && HasPathChanges && !HasPathConflict;
    public bool HasPendingPathDrafts => _pathDrafts.Values.Any(d => d.IsDirty);
    public string PendingPathDraftText => "未应用草稿：" + string.Join("、",
        _pathDrafts.Where(p => p.Value.IsDirty).OrderBy(p => p.Key)
            .Select(p => (p.Key == EnvScope.User ? "用户 PATH" : "系统 PATH")
                + (p.Value.HasConflict ? "（外部已更改）" : "")));
    public string PathDraftStatus => CurrentPathDraft is null ? "PATH 尚未读取。"
        : HasPathConflict ? $"{PathScopeText} PATH 已在外部更改。草稿已保留；请先放弃草稿并读取最新值，再重新编辑。"
        : HasPathChanges ? $"{PathScopeText} PATH 有未应用更改（{PathEntries.Count} 条）。切换作用域或刷新会保留草稿，点击“应用 PATH 更改”后才写入。"
        : $"{PathScopeText} PATH 已与读取值同步（{PathEntries.Count} 条），暂无更改。";

    public void Refresh()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            _status("正在读取环境变量…");
            var variables = _readVariables();
            LoadPath();
            All.Clear();
            All.AddRange(variables);
            ApplyFilter();
            SetError(null);
            _status(HasPendingPathDrafts ? "环境变量已刷新，未应用的 PATH 草稿已保留" : "环境变量已刷新");
        }
        catch (Exception ex)
        {
            SetError($"读取环境变量失败：{ex.Message}。原有 PATH 草稿已保留。");
            _status($"读取环境变量失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
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

    private void SetPathScope(EnvScope scope)
    {
        if (IsBusy) return;
        try
        {
            UpdatePathDraft(scope, _readPath(scope));
            PathScope = scope;
            DisplayPathDraft();
        }
        catch (Exception ex)
        {
            SetError($"读取 PATH 失败：{ex.Message}。原有草稿已保留。");
        }
        finally
        {
            // ToggleButton 的点击也可能取消当前选中，始终恢复为实际作用域。
            OnPropertyChanged(nameof(IsUserPath));
            OnPropertyChanged(nameof(IsMachinePath));
        }
    }

    private void LoadPath()
    {
        // 先读取两个作用域，读取失败时不触碰现有列表与基线。
        var userRaw = _readPath(EnvScope.User);
        var machineRaw = _readPath(EnvScope.Machine);
        UpdatePathDraft(EnvScope.User, userRaw);
        UpdatePathDraft(EnvScope.Machine, machineRaw);
        DisplayPathDraft();
    }

    private void UpdatePathDraft(EnvScope scope, string raw)
    {
        if (_pathDrafts.TryGetValue(scope, out var draft) && draft.IsDirty)
            draft.LatestRaw = raw;
        else
            _pathDrafts[scope] = new PathDraft(raw, _service.ParsePath(raw));
    }

    private void DisplayPathDraft()
    {
        _displayingPath = true;
        try
        {
            PathEntries.Clear();
            if (CurrentPathDraft is { } draft)
                foreach (var entry in draft.Entries) PathEntries.Add(entry);
        }
        finally
        {
            _displayingPath = false;
        }
        NotifyPathState();
    }

    private void CapturePathDraft()
    {
        if (_displayingPath || CurrentPathDraft is not { } draft) return;
        draft.Entries = PathEntries.ToList();
        for (var i = 0; i < PathEntries.Count; i++) PathEntries[i].Index = i;
        NotifyPathState();
    }

    private void NotifyPathState()
    {
        OnPropertyChanged(nameof(CanEditPath));
        OnPropertyChanged(nameof(HasPathChanges));
        OnPropertyChanged(nameof(HasPathConflict));
        OnPropertyChanged(nameof(CanApplyPath));
        OnPropertyChanged(nameof(PathDraftStatus));
        OnPropertyChanged(nameof(HasPendingPathDrafts));
        OnPropertyChanged(nameof(PendingPathDraftText));
        ApplyPathCommand.RaiseCanExecuteChanged();
    }

    private void ConfirmDiscardPathDraft()
    {
        if (IsBusy || !HasPathChanges) return;
        var confirm = MessageBox.Show(
            $"放弃{PathScopeText} PATH 的未应用更改，并读取最新的 PATH？\n\n此操作不会写入环境变量；其它作用域的草稿会保留。",
            "放弃 PATH 草稿", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;
        try
        {
            DiscardPathDraft();
            _status($"已放弃{PathScopeText} PATH 草稿并读取最新值");
        }
        catch (Exception ex)
        {
            SetError($"读取最新 PATH 失败：{ex.Message}。草稿已保留。");
        }
    }

    // 只读重载；确认由调用方负责，也用于无需弹框的草稿冒烟断言。
    internal void DiscardPathDraft()
    {
        var raw = _readPath(_pathScope);
        _pathDrafts[_pathScope] = new PathDraft(raw, _service.ParsePath(raw));
        DisplayPathDraft();
        SetError(null);
    }

    internal bool ValidatePathBaseline()
    {
        if (CurrentPathDraft is not { IsDirty: true } draft) return false;
        draft.LatestRaw = _readPath(_pathScope);
        NotifyPathState();
        if (!draft.HasConflict) return true;
        SetError($"{PathScopeText} PATH 已在外部更改，未写入。草稿已保留；请先放弃草稿并读取最新值，再重新编辑。");
        return false;
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
            SetError($"新增失败：{ex.Message}");
        }
    }

    private void Edit(EnvVarItem? item)
    {
        // 用户级变量写在 HKCU，不需要管理员；系统级写在 HKLM，必须提权
        if (item is { Scope: not EnvScope.User } && Native.RequireAdmin("修改系统环境变量") is { } adminNeeded)
        {
            SetError(adminNeeded);
            return;
        }
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
            SetError($"修改失败：{ex.Message}");
        }
    }

    private void Delete(EnvVarItem? item)
    {
        if (item is { Scope: not EnvScope.User } && Native.RequireAdmin("删除系统环境变量") is { } adminNeeded)
        {
            SetError(adminNeeded);
            return;
        }
        if (item is null) return;

        var confirm = MessageBox.Show(
            $"确定删除环境变量「{item.Name}」（{item.ScopeText}）吗？\n\n删除前会存快照，可用「撤销上一步」恢复。",
            "删除环境变量", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            _service.DeleteVariable(item.Scope, item.Name);
            _status($"已删除：{item.Name}");
            Refresh();
        }
        catch (Exception ex)
        {
            SetError($"删除失败：{ex.Message}");
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
            SetError("复制失败：" + ex.Message);
        }
    }

    private void AddPathEntry()
    {
        if (!CanEditPath) return;
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
        if (!CanEditPath || entry is null) return;
        PathEntries.Remove(entry);
    }

    private bool CanMovePath(PathEntry? entry, int delta)
    {
        if (!CanEditPath || entry is null) return false;
        var index = PathEntries.IndexOf(entry);
        var target = index + delta;
        return index >= 0 && target >= 0 && target < PathEntries.Count;
    }

    private void MovePath(PathEntry? entry, int delta)
    {
        if (!CanMovePath(entry, delta)) return;
        var index = PathEntries.IndexOf(entry!);
        PathEntries.Move(index, index + delta);
    }

    private void ApplyPath()
    {
        if (!CanApplyPath) return;
        if (_pathScope != EnvScope.User && Native.RequireAdmin("应用系统 PATH") is { } adminNeeded)
        {
            SetError(adminNeeded);
            return;
        }

        var scope = _pathScope;
        var scopeText = PathScopeText;
        var entries = PathEntries.Select(e => e.Raw).ToArray();
        var missing = PathEntries.Count(e => !e.Exists);
        var wrote = false;
        IsBusy = true;
        try
        {
            SetError(null);
            if (!ValidatePathBaseline()) return;
            var confirm = MessageBox.Show(
                $"将按当前列表改写{scopeText} PATH（列表 {entries.Length} 条；写入时去重、去空，保留其余顺序）。\n\n" +
                (missing == 0 ? "" : $"其中 {missing} 条目录当前不存在，仍会保留。\n\n") +
                "只改 PATH 字符串，不删除任何目录；写入前会存快照，可用「撤销上一步」恢复。确定继续吗？",
                "应用 PATH 更改", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (confirm != MessageBoxResult.OK) return;

            // 确认框停留期间，安装器或其它工具也可能更新 PATH，写入前再次核实基线。
            if (!ValidatePathBaseline()) return;
            _service.SetPath(scope, entries);
            wrote = true;
            DiscardPathDraft();
            var variables = _readVariables();
            All.Clear();
            All.AddRange(variables);
            ApplyFilter();
            _status($"已更新{scopeText} PATH（{PathEntries.Count} 条），新开的进程生效");
        }
        catch (Exception ex)
        {
            var message = wrote
                ? $"{scopeText} PATH 已写入，但重新读取失败：{ex.Message}。请刷新核实；可用「撤销上一步」恢复。"
                : $"写入 PATH 失败：{ex.Message}。草稿已保留。";
            SetError(message);
            _status(message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenPathEntry(PathEntry? entry)
    {
        if (entry is null || !entry.Exists) return;

        try
        {
            Native.OpenPath(entry.Expanded);
        }
        catch (Exception ex)
        {
            _status($"打开失败：{ex.Message}");
        }
    }

    private string _errorText = "";

    /// <summary>页面级错误：失败时统一由 ErrorBar 展示，不再只靠底部状态栏一行灰字。</summary>
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (Set(ref _errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorText);

    public void SetError(string? message) => ErrorText = message ?? "";
}
