using System.Collections.ObjectModel;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class CategoryNode : ObservableObject
{
    private int _count = -1;

    public RegistryCategory Kind { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";

    public int Count
    {
        get => _count;
        set
        {
            if (Set(ref _count, value)) OnPropertyChanged(nameof(CountText));
        }
    }

    public string CountText => _count < 0 ? "" : _count.ToString();
}

public sealed class RegistryAuditViewModel : ObservableObject, IModule
{
    private readonly RegistryAuditService _service;
    private readonly Action<string> _status;

    private CategoryNode? _selectedCategory;
    private string _searchText = "";
    private bool _onlyInvalid = true;
    private string _noteText = "";
    private string _summaryText = "";
    private bool _isBusy;

    public RegistryAuditViewModel(ChangeJournal journal, Action<string> status)
    {
        _service = new RegistryAuditService(journal);
        _status = status;

        Categories = new ObservableCollection<CategoryNode>
        {
            new() { Kind = RegistryCategory.Startup, Title = "启动项", Description = "Run / RunOnce / Winlogon，指向的文件不存在即标为可疑" },
            new() { Kind = RegistryCategory.ContextMenu, Title = "右键菜单", Description = "各位置域的条目汇总（管理请去右键菜单页）" },
            new() { Kind = RegistryCategory.FileAssoc, Title = "文件关联", Description = ".ext → ProgID → 打开命令" },
            new() { Kind = RegistryCategory.UninstallInfo, Title = "卸载信息", Description = "卸载程序或安装目录已失效的记录" },
            new() { Kind = RegistryCategory.InvalidClsid, Title = "失效残留", Description = "CLSID 指向的 DLL/EXE 已不存在" },
            new() { Kind = RegistryCategory.Policies, Title = "策略与限制", Description = "Policies 树下的限制项，只展示不代删" },
            new() { Kind = RegistryCategory.PathEnv, Title = "环境变量 / PATH", Description = "PATH 里已不存在的目录" }
        };

        Items = new ObservableCollection<RegistryItem>();
        RefreshCommand = new RelayCommand(() => Load(SelectedCategory));
        RemoveCommand = new RelayCommand(p => Remove(p as RegistryItem));
        RemoveAllCommand = new RelayCommand(RemoveAll);
        CopyPathCommand = new RelayCommand(p => CopyPath(p as RegistryItem));
        CopyDetailCommand = new RelayCommand(p => CopyDetail(p as RegistryItem));

        _selectedCategory = Categories[0];
    }

    public ObservableCollection<CategoryNode> Categories { get; }

    public ObservableCollection<RegistryItem> Items { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand RemoveAllCommand { get; }

    public RelayCommand CopyPathCommand { get; }

    public RelayCommand CopyDetailCommand { get; }

    public bool CanRemoveAll => Items.Any(i => i.CanRemove);

    public string RemoveAllText => $"清理全部可疑项（{Items.Count(i => i.CanRemove)}）";

    public CategoryNode? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!Set(ref _selectedCategory, value)) return;
            Load(value);
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

    public bool OnlyInvalid
    {
        get => _onlyInvalid;
        set
        {
            if (Set(ref _onlyInvalid, value)) ApplyFilter();
        }
    }

    public string NoteText
    {
        get => _noteText;
        private set
        {
            if (Set(ref _noteText, value)) OnPropertyChanged(nameof(HasNote));
        }
    }

    public bool HasNote => !string.IsNullOrWhiteSpace(_noteText);

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    private List<RegistryItem> _all = new();
    private string _scannedText = "";

    public void Refresh() => Load(SelectedCategory);

    private void Load(CategoryNode? category)
    {
        if (category is null || IsBusy) return;

        IsBusy = true;
        try
        {
            _status($"正在扫描：{category.Title}…");
            var result = _service.Load(category.Kind);

            _all = result.Items;
            category.Count = result.Items.Count(i => i.IsInvalid);
            NoteText = result.Note ?? "";
            _scannedText = result.Truncated
                ? $"已扫描 {result.Scanned} 项（超过上限，结果可能不完整）"
                : $"已扫描 {result.Scanned} 项";

            ApplyFilter();
            _status($"{category.Title}：扫描完成");
        }
        catch (Exception ex)
        {
            _status($"扫描失败：{ex.Message}");
            MessageBox.Show($"扫描「{category.Title}」失败：{ex.Message}", "WinKit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        Items.Clear();
        var keyword = _searchText.Trim().ToLowerInvariant();

        var query = _all
            .Where(i => !_onlyInvalid || i.IsInvalid)
            .Where(i => keyword.Length == 0
                        || i.Name.ToLowerInvariant().Contains(keyword)
                        || i.Detail.ToLowerInvariant().Contains(keyword))
            .OrderByDescending(i => i.IsInvalid)
            .ThenBy(i => i.Name, StringComparer.CurrentCulture);

        foreach (var item in query) Items.Add(item);
        OnPropertyChanged(nameof(Items.Count));
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (SelectedCategory is null) return;
        var invalid = _all.Count(i => i.IsInvalid);
        SelectedCategory.Count = invalid;
        SummaryText = $"{_scannedText} · 可疑 {invalid} 项 · 当前显示 {Items.Count} 项";
        OnPropertyChanged(nameof(CanRemoveAll));
        OnPropertyChanged(nameof(RemoveAllText));
    }

    private void RemoveAll()
    {
        var targets = Items.Where(i => i.CanRemove).ToList();
        if (targets.Count == 0) return;

        var preview = string.Join("\n• ", targets.Take(8).Select(t => t.Name));
        if (targets.Count > 8) preview += $"\n…等共 {targets.Count} 项";

        var result = MessageBox.Show(
            $"将一次性清理当前列表中的 {targets.Count} 个可疑项：\n\n• {preview}\n\n" +
            "整批操作会合并保存为 1 份快照，点一次「撤销上一步」即可整批恢复。确定继续吗？",
            "批量清理可疑项", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        try
        {
            var removed = _service.RemoveBatch(targets, SelectedCategory?.Title ?? "注册表项");
            foreach (var item in targets)
            {
                _all.Remove(item);
                Items.Remove(item);
            }

            UpdateSummary();
            _status($"已批量清理 {removed} 项");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"批量清理失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyPath(RegistryItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.CopyablePath)) return;
        try
        {
            Clipboard.SetText(item.CopyablePath);
            _status($"已复制路径：{item.CopyablePath}");
        }
        catch (Exception)
        {
            // 剪贴板被占用时忽略
        }
    }

    private void CopyDetail(RegistryItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Detail)) return;
        try
        {
            Clipboard.SetText(item.Detail);
            _status("已复制详情");
        }
        catch (Exception)
        {
            // 剪贴板被占用时忽略
        }
    }

    private void Remove(RegistryItem? item)
    {
        if (item is null) return;

        if (!item.CanRemove)
        {
            MessageBox.Show(
                "这一项没有可证明的失效证据，WinKit 不允许删除。\n\n" +
                "如果你确定要处理，请到 regedit 里手动操作，或先在右键菜单页禁用。",
                "不允许删除", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"将删除：\n{item.KeyPath}\n{(item.ValueName is null ? "（整个键）" : $"值：{item.ValueName}")}\n\n" +
            "删除前会自动保存快照，可用「撤销上一步」恢复。确定继续吗？",
            "清理注册表项", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        try
        {
            _service.Remove(item);
            _all.Remove(item);
            Items.Remove(item);
            _status($"已清理：{item.Name}");
            UpdateSummary();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
