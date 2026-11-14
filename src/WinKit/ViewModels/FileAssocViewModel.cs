using System.Collections.ObjectModel;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class FileAssocViewModel : ObservableObject, IModule
{
    private readonly FileAssocService _service;
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;

    private bool _isBusy;
    private bool _onlyUserChoice;
    private string _searchText = "";
    private string _summary = "";

    public FileAssocViewModel(ChangeJournal journal, Action<string> status, Action<bool>? setBusy = null)
    {
        _service = new FileAssocService(journal);
        _status = status;
        _setBusy = setBusy;

        All = new List<FileAssocItem>();
        Items = new ObservableCollection<FileAssocItem>();

        RefreshCommand = new RelayCommand(Refresh);
        OpenSettingsCommand = new RelayCommand(() => OpenSettings(null));
        OpenForExtensionCommand = new RelayCommand(p => OpenSettings((p as FileAssocItem)?.Extension));
        OpenWithCommand = new RelayCommand(p => ShowOpenWith(p as FileAssocItem));
        CopyCommandCommand = new RelayCommand(p => CopyCommand(p as FileAssocItem));
        CleanInvalidCommand = new RelayCommand(CleanInvalid);
        CopyPathCommand = new RelayCommand(p => CopyPath(p as FileAssocItem));
    }

    public List<FileAssocItem> All { get; }

    public ObservableCollection<FileAssocItem> Items { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenForExtensionCommand { get; }
    public RelayCommand OpenWithCommand { get; }
    public RelayCommand CopyCommandCommand { get; }
    public RelayCommand CleanInvalidCommand { get; }
    public RelayCommand CopyPathCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) _setBusy?.Invoke(value);
        }
    }

    public bool OnlyUserChoice
    {
        get => _onlyUserChoice;
        set
        {
            if (Set(ref _onlyUserChoice, value)) ApplyFilter();
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

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status("正在读取文件关联…");
            var items = await Task.Run(() => _service.Load());

            All.Clear();
            All.AddRange(items);
            ApplyFilter();

            _status($"文件关联：{items.Count} 个扩展名");
        }
        catch (Exception ex)
        {
            _status($"读取文件关联失败：{ex.Message}");
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

        foreach (var item in All)
        {
            if (_onlyUserChoice && !item.IsUserChoice) continue;
            if (keyword.Length > 0 && !item.SearchBlob.Contains(keyword)) continue;
            Items.Add(item);
        }

        var userChoice = All.Count(i => i.IsUserChoice);
        SummaryText = $"共 {All.Count} 个扩展名关联 · 用户指定 {userChoice} 个 · 当前显示 {Items.Count} 个";
    }

    private void OpenSettings(string? extension)
    {
        var (ok, message) = FileAssocService.OpenDefaultAppsSettings(extension);
        _status(message);
        if (!ok) MessageBox.Show(message, "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowOpenWith(FileAssocItem? item)
    {
        if (item is null) return;

        var (ok, message) = FileAssocService.ShowOpenWith(item.Extension);
        _status(message);
        if (!ok) MessageBox.Show(message, "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CopyCommand(FileAssocItem? item)
    {
        if (item is null) return;

        try
        {
            Clipboard.SetText(string.IsNullOrWhiteSpace(item.Command) ? item.ProgId : item.Command);
            _status("已复制打开命令");
        }
        catch (Exception ex)
        {
            _status("复制失败：" + ex.Message);
        }
    }

    private void CopyPath(FileAssocItem? item)
    {
        if (item is null) return;

        var path = $"HKEY_CURRENT_USER\\Software\\Classes\\{item.Extension}";
        try
        {
            Clipboard.SetText(path);
            _status($"已复制：{path}");
        }
        catch (Exception ex)
        {
            _status("复制失败：" + ex.Message);
        }
    }

    private void CleanInvalid()
    {
        var confirm = MessageBox.Show(
            "将清理「当前用户」下那些指向的程序已经不存在了的扩展名关联。\n\n" +
            "只删 HKCU\\Software\\Classes\\<扩展名> 的默认值，不动系统级登记，也不改你手动指定过的关联。\n" +
            "删除前会存快照，可用「撤销上一步」恢复。确定继续吗？",
            "清理失效关联", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            var removed = _service.CleanInvalid(_status);
            _status(removed == 0 ? "没有发现失效的关联" : $"已清理 {removed} 个失效关联");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"清理失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    public void SetError(string? message) => ErrorText = message ?? "";}
