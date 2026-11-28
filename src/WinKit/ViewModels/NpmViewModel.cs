using System.Collections.ObjectModel;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class NpmViewModel : ObservableObject, IModule
{
    private readonly NpmService _service = new();
    private readonly Action<string> _status;
    private readonly Action<bool> _setMutationBusy;
    private readonly List<NpmPackageRow> _allPackages = new();
    private NpmEnvironmentInfo _environment = new(false, "", "", "", "", "", "尚未读取 npm 环境。");
    private bool _isLoading;
    private bool _isMutating;
    private bool _hasLoaded;
    private DateTime? _loadedAt;
    private string _searchText = "";
    private string _packageName = "";
    private string _packageVersionTag = "";
    private string _summaryText = "尚未加载全局包。";
    private string _errorText = "";

    public NpmViewModel(Action<string> status, Action<bool> setMutationBusy)
    {
        _status = status;
        _setMutationBusy = setMutationBusy;
        Packages = new ObservableCollection<NpmPackageRow>();
        VisiblePackages = new ObservableCollection<NpmPackageRow>();

        RefreshCommand = new RelayCommand(Refresh, () => !IsLoading && !IsMutating);
        CheckUpdatesCommand = new RelayCommand(() => _ = CheckUpdatesAsync(), () => CanCheckUpdates());
        InstallCommand = new RelayCommand(() => _ = InstallAsync(), () => CanInstall);
        UpdatePackageCommand = new RelayCommand(p => _ = UpdateAsync(p as NpmPackageRow),
            p => p is NpmPackageRow { HasUpdate: true } && !IsLoading && !IsMutating);
        UninstallPackageCommand = new RelayCommand(p => _ = UninstallAsync(p as NpmPackageRow),
            p => p is NpmPackageRow && !IsLoading && !IsMutating);
    }

    public ObservableCollection<NpmPackageRow> Packages { get; }
    public ObservableCollection<NpmPackageRow> VisiblePackages { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand UpdatePackageCommand { get; }
    public RelayCommand UninstallPackageCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!Set(ref _isLoading, value)) return;
            RaiseCommands();
        }
    }

    public bool IsMutating
    {
        get => _isMutating;
        private set
        {
            if (!Set(ref _isMutating, value)) return;
            RaiseCommands();
        }
    }

    public bool IsLoaded => _hasLoaded;

    public DateTime? LoadedAt
    {
        get => _loadedAt;
        private set
        {
            if (!Set(ref _loadedAt, value)) return;
            OnPropertyChanged(nameof(LoadedAtText));
        }
    }

    public string LoadedAtText => LoadedAt is null ? "尚未加载" : $"本机清单 · {LoadedAt.Value:yyyy-MM-dd HH:mm:ss}";

    public string NodeVersionText => _environment.IsAvailable ? _environment.NodeVersion : "不可用";
    public string NodePathText => string.IsNullOrWhiteSpace(_environment.NodePath) ? "未找到 node.exe" : _environment.NodePath;
    public string NpmVersionText => _environment.IsAvailable ? _environment.NpmVersion : "不可用";
    public string NpmPathText => string.IsNullOrWhiteSpace(_environment.NpmPath) ? "未找到 npm" : _environment.NpmPath;
    public string PrefixText => string.IsNullOrWhiteSpace(_environment.Prefix) ? "—" : _environment.Prefix;
    public bool IsEnvironmentAvailable => _environment.IsAvailable;

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) ApplyFilter();
        }
    }

    public string PackageName
    {
        get => _packageName;
        set
        {
            if (!Set(ref _packageName, value)) return;
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(InstallValidationText));
            InstallCommand.RaiseCanExecuteChanged();
        }
    }

    public string PackageVersionTag
    {
        get => _packageVersionTag;
        set
        {
            if (!Set(ref _packageVersionTag, value)) return;
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(InstallValidationText));
            InstallCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanInstall => IsEnvironmentAvailable && !IsLoading && !IsMutating
        && NpmService.TryBuildPackageTarget(PackageName.Trim(), PackageVersionTag.Trim(), out _, out _);

    public string InstallValidationText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PackageName)) return "";
            return NpmService.TryBuildPackageTarget(PackageName.Trim(), PackageVersionTag.Trim(), out _, out var error)
                ? ""
                : error;
        }
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (!Set(ref _errorText, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public void EnsureLoaded()
    {
        if (_hasLoaded || IsLoading || IsMutating) return;
        _ = LoadAsync(refreshEnvironment: false);
    }

    public void Refresh()
    {
        if (IsLoading || IsMutating) return;
        _ = LoadAsync(refreshEnvironment: true);
    }

    private bool CanCheckUpdates()
        => IsEnvironmentAvailable && IsLoaded && Packages.Count > 0 && !IsLoading && !IsMutating;

    private async Task LoadAsync(bool refreshEnvironment)
    {
        if (IsLoading || IsMutating) return;
        IsLoading = true;
        _status(refreshEnvironment ? "正在重新识别 Node/npm 并刷新本机全局包…" : "正在读取本机 npm 全局包…");
        try
        {
            var snapshot = await _service.LoadAsync(refreshEnvironment);
            ApplySnapshot(snapshot);
            _hasLoaded = true;
            OnPropertyChanged(nameof(IsLoaded));
            LoadedAt = DateTime.Now;
            ErrorText = snapshot.Message;
            _status(string.IsNullOrWhiteSpace(snapshot.Message)
                ? $"已读取 {Packages.Count} 个 npm 全局包"
                : snapshot.Message);
        }
        catch (Exception ex)
        {
            _hasLoaded = true;
            OnPropertyChanged(nameof(IsLoaded));
            LoadedAt = DateTime.Now;
            ErrorText = "读取 npm 全局包失败：" + ex.Message;
            _status(ErrorText);
        }
        finally
        {
            IsLoading = false;
            ApplyFilter();
        }
    }

    private async Task CheckUpdatesAsync()
    {
        if (IsLoading || IsMutating) return;
        if (!_hasLoaded)
        {
            await LoadForUpdateCheckAsync();
            if (!IsEnvironmentAvailable || Packages.Count == 0) return;
        }
        if (!IsEnvironmentAvailable || Packages.Count == 0) return;

        IsLoading = true;
        foreach (var package in Packages) package.LatestVersion = "";
        _status("正在通过 npm registry 检查全局包最新版本…");
        ErrorText = "";
        try
        {
            var result = await _service.CheckUpdatesAsync();
            if (!result.Success)
            {
                ErrorText = result.Message;
                _status(result.Message);
                return;
            }

            foreach (var package in Packages)
            {
                if (result.LatestVersions.TryGetValue(package.Name, out var latest))
                    package.LatestVersion = latest;
            }

            var count = Packages.Count(p => p.HasUpdate);
            RefreshSummary();
            _status(count == 0 ? "已检查：当前没有可更新的 npm 全局包。" : $"已检查：发现 {count} 个可更新包。");
        }
        catch (Exception ex)
        {
            ErrorText = "检查 npm 更新失败：" + ex.Message;
            _status(ErrorText);
        }
        finally
        {
            IsLoading = false;
            ApplyFilter();
        }
    }

    private async Task LoadForUpdateCheckAsync()
    {
        IsLoading = true;
        try
        {
            var snapshot = await _service.LoadAsync(refreshEnvironment: false);
            ApplySnapshot(snapshot);
            _hasLoaded = true;
            OnPropertyChanged(nameof(IsLoaded));
            LoadedAt = DateTime.Now;
            ErrorText = snapshot.Message;
        }
        catch (Exception ex)
        {
            ErrorText = "读取 npm 全局包失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task InstallAsync()
    {
        if (!CanInstall) return;
        var target = PackageName.Trim() + (string.IsNullOrWhiteSpace(PackageVersionTag) ? "" : "@" + PackageVersionTag.Trim());
        var confirm = MessageBox.Show(
            $"将通过当前 Node/npm 环境全局安装：\n\n{target}\n\n继续吗？",
            "安装 npm 全局包", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        await RunMutationAsync("正在安装全局包…", () => _service.InstallAsync(PackageName.Trim(), PackageVersionTag.Trim()));
        if (!HasError)
        {
            PackageName = "";
            PackageVersionTag = "";
        }
    }

    private async Task UpdateAsync(NpmPackageRow? package)
    {
        if (package is null || !package.HasUpdate || IsLoading || IsMutating) return;
        var majorNote = package.IsMajorUpdate
            ? "\n\n注意：这是 semver 主版本升级，可能包含不兼容变更。"
            : "";
        var confirm = MessageBox.Show(
            $"将只更新「{package.Name}」：\n\n当前版本：{package.Version}\n目标版本：{package.LatestVersion}\n\n操作会安装该包的 latest dist-tag。{majorNote}\n\n继续吗？",
            package.IsMajorUpdate ? "确认主版本更新" : "更新 npm 包",
            MessageBoxButton.OKCancel,
            package.IsMajorUpdate ? MessageBoxImage.Warning : MessageBoxImage.Information,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        await RunMutationAsync($"正在更新 {package.Name}…", () => _service.UpdateAsync(package.Name));
    }

    private async Task UninstallAsync(NpmPackageRow? package)
    {
        if (package is null || IsLoading || IsMutating) return;
        var confirm = MessageBox.Show(
            $"将从当前 npm 全局环境卸载「{package.Name}」({package.Version})。\n\n继续吗？",
            "卸载 npm 全局包", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        await RunMutationAsync($"正在卸载 {package.Name}…", () => _service.UninstallAsync(package.Name));
    }

    private async Task RunMutationAsync(string progress, Func<Task<NpmOperationResult>> operation)
    {
        if (IsLoading || IsMutating) return;
        IsMutating = true;
        var notifyBusy = true;
        try
        {
            _setMutationBusy(true);
            _status(progress);
            var result = await operation();
            ErrorText = result.Success ? "" : result.Message;
            _status(result.Message);

            IsLoading = true;
            try
            {
                var snapshot = await _service.LoadAsync(refreshEnvironment: false);
                ApplySnapshot(snapshot);
                _hasLoaded = true;
                OnPropertyChanged(nameof(IsLoaded));
                LoadedAt = DateTime.Now;
                if (!string.IsNullOrWhiteSpace(snapshot.Message))
                    ErrorText = string.IsNullOrWhiteSpace(ErrorText)
                        ? "操作后的本机清单刷新失败：" + snapshot.Message
                        : ErrorText + Environment.NewLine + "本机清单刷新失败：" + snapshot.Message;
            }
            catch (Exception ex)
            {
                ErrorText = string.IsNullOrWhiteSpace(ErrorText)
                    ? "操作已结束，但本机清单刷新失败：" + ex.Message
                    : ErrorText + Environment.NewLine + "本机清单刷新失败：" + ex.Message;
            }
            finally
            {
                IsLoading = false;
                ApplyFilter();
            }
        }
        catch (Exception ex)
        {
            ErrorText = "npm 操作失败：" + ex.Message;
            _status(ErrorText);
        }
        finally
        {
            IsMutating = false;
            if (notifyBusy)
            {
                try { _setMutationBusy(false); }
                catch (Exception ex) { _status("恢复导航状态失败：" + ex.Message); }
            }
        }
    }

    private void ApplySnapshot(NpmLoadResult snapshot)
    {
        _environment = snapshot.Environment;
        OnPropertyChanged(nameof(NodeVersionText));
        OnPropertyChanged(nameof(NodePathText));
        OnPropertyChanged(nameof(NpmVersionText));
        OnPropertyChanged(nameof(NpmPathText));
        OnPropertyChanged(nameof(PrefixText));
        OnPropertyChanged(nameof(IsEnvironmentAvailable));
        OnPropertyChanged(nameof(CanInstall));
        InstallCommand.RaiseCanExecuteChanged();

        _allPackages.Clear();
        Packages.Clear();
        foreach (var package in snapshot.Packages)
        {
            var row = new NpmPackageRow(package.Name, package.Version);
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(NpmPackageRow.HasUpdate) or nameof(NpmPackageRow.IsMajorUpdate))
                    RefreshSummary();
            };
            _allPackages.Add(row);
            Packages.Add(row);
        }

        ApplyFilter();
        RefreshSummary();
    }

    private void ApplyFilter()
    {
        VisiblePackages.Clear();
        var keyword = _searchText.Trim();
        foreach (var package in _allPackages)
        {
            if (keyword.Length > 0 && !package.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                && !package.Version.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                && !package.LatestVersion.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                continue;
            VisiblePackages.Add(package);
        }

        RefreshSummary();
    }

    private void RefreshSummary()
    {
        var updates = _allPackages.Count(p => p.HasUpdate);
        var visible = VisiblePackages.Count;
        SummaryText = _hasLoaded
            ? $"全局包 {_allPackages.Count} 个 · 可更新 {updates} 个 · 当前显示 {visible} 个"
            : "尚未加载全局包。";
        CheckUpdatesCommand?.RaiseCanExecuteChanged();
    }

    private void RaiseCommands()
    {
        RefreshCommand?.RaiseCanExecuteChanged();
        CheckUpdatesCommand?.RaiseCanExecuteChanged();
        InstallCommand?.RaiseCanExecuteChanged();
        UpdatePackageCommand?.RaiseCanExecuteChanged();
        UninstallPackageCommand?.RaiseCanExecuteChanged();
    }
}

public sealed class NpmPackageRow : ObservableObject
{
    private string _latestVersion = "";

    public NpmPackageRow(string name, string version)
    {
        Name = name;
        Version = version;
    }

    public string Name { get; }
    public string Version { get; }

    public string LatestVersion
    {
        get => _latestVersion;
        set
        {
            if (!Set(ref _latestVersion, value)) return;
            OnPropertyChanged(nameof(LatestVersionText));
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(IsMajorUpdate));
            OnPropertyChanged(nameof(UpdateButtonText));
        }
    }

    public string LatestVersionText => string.IsNullOrWhiteSpace(LatestVersion) ? "—" : LatestVersion;

    public bool HasUpdate
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LatestVersion)) return false;
            var compare = NpmService.CompareSemVer(LatestVersion, Version);
            return compare is > 0 || compare is null
                && !string.Equals(LatestVersion, Version, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool IsMajorUpdate
    {
        get
        {
            if (!HasUpdate) return false;
            var compare = NpmService.CompareSemVer(LatestVersion, Version);
            if (compare is null or <= 0) return false;
            return TryReadMajor(LatestVersion, out var latestMajor)
                   && TryReadMajor(Version, out var currentMajor)
                   && latestMajor != currentMajor;
        }
    }

    public string UpdateButtonText => IsMajorUpdate ? "更新 · 主版本" : "更新";

    private static bool TryReadMajor(string version, out System.Numerics.BigInteger major)
    {
        major = default;
        var text = version.StartsWith('v') || version.StartsWith('V') ? version[1..] : version;
        var dot = text.IndexOf('.');
        return dot > 0 && System.Numerics.BigInteger.TryParse(
            text.AsSpan(0, dot), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out major);
    }
}
