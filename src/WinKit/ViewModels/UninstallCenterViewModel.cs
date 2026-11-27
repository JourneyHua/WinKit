using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using WinKit.Core;
using WinKit.Services;
using WinKit.Views;

namespace WinKit.ViewModels;

/// <summary>
/// 卸载中心：拖入文件夹或快捷方式 → 认出这是哪个软件 → 调起官方卸载器 →
/// 扫描遗留项 → 清理（回收站 + 快照）。
///
/// 面板顺序照着实际使用顺序排，因为这里最容易出的错是「以为卸载了其实没卸」：
/// 先确认身份，再运行卸载器，最后才谈残留。
/// </summary>
public sealed class UninstallCenterViewModel : ObservableObject
{
    private readonly ChangeJournal _journal;
    private readonly Action<string> _status;
    private readonly Action<bool>? _setGlobalBusy;

    // 自检可替换读取边界，无需覆盖当前桌面的剪贴板。正常操作始终使用系统读取器。
    internal Func<string> ClipboardTextReader { get; set; } =
        () => Clipboard.ContainsText() ? Clipboard.GetText() : "";

    private readonly UninstallService _uninstall = new();
    private readonly ResidualScanService _residual = new();

    private CancellationTokenSource? _scanCts;

    private string _targetPath = "";
    private string _targetKindText = "";
    private string _targetFolder = "";
    private string _identifySummary = "";
    private string _scanStatus = "";
    private string _scanSummary = "";
    private string _cleanupFeedback = "";
    private string _archivePath = "";
    private string _archiveStatusFile = "";
    private bool _isScanning;
    private bool _isBusy;
    private ResidualScanService.DirectoryTarget? _folderAtSelection;

    private sealed record TargetSnapshot(string Path, string Folder, string Kind, string Name,
        UninstallEntry[] Matches, CandidateUninstaller[] Candidates, ResidualItem[] Residuals);

    public UninstallCenterViewModel(ChangeJournal journal, Action<string> status, Action<bool>? setGlobalBusy = null)
    {
        _journal = journal;
        _status = status;
        _setGlobalBusy = setGlobalBusy;

        Matches = new ObservableCollection<UninstallEntry>();
        Candidates = new ObservableCollection<CandidateUninstaller>();
        Residuals = new ObservableCollection<ResidualItem>();

        PickFolderCommand = new RelayCommand(PickFolder, () => CanChangeTarget);
        PickShortcutCommand = new RelayCommand(PickShortcut, () => CanChangeTarget);
        ClearCommand = new RelayCommand(Clear, () => HasTarget && CanChangeTarget);
        RunMatchedCommand = new RelayCommand(p => RunMatched((UninstallEntry)p!), p => CanChangeTarget && p is UninstallEntry entry && Matches.Contains(entry));
        RunCandidateCommand = new RelayCommand(p => RunCandidate((CandidateUninstaller)p!), p => CanChangeTarget && p is CandidateUninstaller entry && Candidates.Contains(entry));
        ScanCommand = new RelayCommand(() => _ = ScanAsync(false), () => HasScanTarget && CanChangeTarget);
        DeepScanCommand = new RelayCommand(() => _ = ScanAsync(true), () => HasScanTarget && CanChangeTarget);
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning && _scanCts is { IsCancellationRequested: false });
        CleanSelectedCommand = new RelayCommand(CleanSelected, () => HasSelection && CanChangeTarget);
        RecycleTargetCommand = new RelayCommand(RecycleTargetFolder, () => CanRecycleTarget && CanChangeTarget);
        MeasureCommand = new RelayCommand(() => _ = MeasureAsync(), () => HasDirectorySelection && CanChangeTarget);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => HasSelection && CanChangeTarget);
        OpenTargetCommand = new RelayCommand(OpenTarget);
        OpenResidualCommand = new RelayCommand(p => OpenResidual(p as ResidualItem));
        OpenArchiveCommand = new RelayCommand(OpenArchive, () => File.Exists(_archiveStatusFile));
        CopyTargetCommand = new RelayCommand(CopyTarget);
        PastePathCommand = new RelayCommand(PastePath, () => CanChangeTarget);
    }

    // ---------- 集合 ----------

    public ObservableCollection<UninstallEntry> Matches { get; }
    public ObservableCollection<CandidateUninstaller> Candidates { get; }
    public ObservableCollection<ResidualItem> Residuals { get; }

    // ---------- 命令 ----------

    public RelayCommand PickFolderCommand { get; }
    public RelayCommand PickShortcutCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand RunMatchedCommand { get; }
    public RelayCommand RunCandidateCommand { get; }
    public RelayCommand ScanCommand { get; }
    public RelayCommand DeepScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand CleanSelectedCommand { get; }
    public RelayCommand RecycleTargetCommand { get; }
    public RelayCommand MeasureCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand OpenTargetCommand { get; }
    public RelayCommand OpenResidualCommand { get; }
    public RelayCommand OpenArchiveCommand { get; }
    public RelayCommand CopyTargetCommand { get; }
    public RelayCommand PastePathCommand { get; }

    // ---------- 状态 ----------

    public string TargetPath
    {
        get => _targetPath;
        private set
        {
            if (!Set(ref _targetPath, value)) return;
            OnPropertyChanged(nameof(HasTarget));
        }
    }

    public string TargetKindText
    {
        get => _targetKindText;
        private set => Set(ref _targetKindText, value);
    }

    public string TargetFolder
    {
        get => _targetFolder;
        private set
        {
            if (!Set(ref _targetFolder, value)) return;
            OnPropertyChanged(nameof(HasTargetFolder));
        }
    }

    public string IdentifySummary
    {
        get => _identifySummary;
        private set => Set(ref _identifySummary, value);
    }

    public string ScanStatus
    {
        get => _scanStatus;
        private set => Set(ref _scanStatus, value);
    }

    public string ScanSummary
    {
        get => _scanSummary;
        private set => Set(ref _scanSummary, value);
    }

    public string CleanupFeedback
    {
        get => _cleanupFeedback;
        private set => Set(ref _cleanupFeedback, value);
    }

    public string ArchivePath
    {
        get => _archivePath;
        private set => Set(ref _archivePath, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value)) return;
            OnPropertyChanged(nameof(CanChangeTarget));
            RaiseCommandStates();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanChangeTarget));
            RaiseCommandStates();
        }
    }

    public bool HasTarget => !string.IsNullOrWhiteSpace(_targetPath);
    public bool HasTargetFolder => !string.IsNullOrWhiteSpace(_targetFolder) && Directory.Exists(_targetFolder);
    public bool HasCandidates => Candidates.Count > 0;
    public bool HasUninstallers => Matches.Count > 0 || HasCandidates;
    public bool HasResiduals => Residuals.Count > 0;
    public bool HasScanTarget => _folderAtSelection is not null;
    public bool CanRecycleTarget => HasScanTarget && HasTargetFolder;
    public bool CanChangeTarget => !IsScanning && !IsBusy;
    public bool HasSelection => Residuals.Any(IsSelectedForCleanup);
    public bool HasDirectorySelection => Residuals.Any(item => IsSelectedForCleanup(item) && item.Kind == ResidualKind.Directory);

    public string ResidualEmptyText => !HasTarget ? "先选择软件的安装文件夹或快捷方式。"
        : !HasScanTarget ? "未识别到可扫描的软件目录。"
        : string.IsNullOrEmpty(ScanSummary) ? "软件卸载完成后，可扫描相关项目。"
        : "没有可显示的扫描结果。";

    public string ScanHint => IsScanning ? "正在扫描…" : "";

    private static bool IsSelectedForCleanup(ResidualItem item) => item.IsSelected && item.CanClean;

    private TargetSnapshot CaptureTarget() => new(TargetPath, TargetFolder, TargetKindText,
        Path.GetFileName(TargetFolder), Matches.ToArray(), Candidates.ToArray(), Residuals.ToArray());

    public string SelectedSummary
    {
        get
        {
            var selected = Residuals.Where(i => i.IsSelected && i.CanClean).ToList();
            if (selected.Count == 0) return "未选中任何项";
            var size = selected.Sum(i => i.SizeBytes ?? 0);
            return $"已选中 {selected.Count} 项" + (size > 0 ? $"，约 {ThisPcService.FormatSize(size)}" : "");
        }
    }

    // ---------- 识别 ----------

    public void AcceptPath(string path)
    {
        if (!CanChangeTarget) return;
        if (string.IsNullOrWhiteSpace(path))
        {
            _status("没有拿到可识别的路径");
            return;
        }

        var raw = Shortcuts.Expand(path.Trim().Trim('"'));
        var kind = "";
        var folder = "";

        if (Directory.Exists(raw))
        {
            kind = "文件夹";
            folder = Path.TrimEndingDirectorySeparator(raw);
        }
        else if (raw.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(raw))
        {
            kind = "快捷方式";
            var resolved = UninstallService.ResolveShortcut(raw);
            folder = resolved.Folder;
        }
        else if (File.Exists(raw))
        {
            kind = "文件";
            folder = Path.GetDirectoryName(raw) ?? "";
        }
        else
        {
            CleanupFeedback = $"路径不存在：{raw}。当前目标没有改变。";
            _status("选择的路径不存在");
            return;
        }

        ResetResiduals();
        ResetArchive();
        CleanupFeedback = "";
        Matches.Clear();
        Candidates.Clear();
        _folderAtSelection = null;

        TargetPath = raw;
        TargetKindText = kind;
        TargetFolder = folder;
        if (!ResidualScanService.TryGetDirectoryTarget(folder, out _folderAtSelection, out var reason))
        {
            IdentifySummary = string.IsNullOrWhiteSpace(folder)
                ? "无法从这个快捷方式识别软件目录。请选择安装文件夹。"
                : $"不能将此位置作为软件目录。{reason}";
            UpdateTargetState();
            return;
        }

        TargetFolder = _folderAtSelection!.Path;
        var leaf = Path.GetFileName(TargetFolder);
        var entries = _uninstall.LoadAll();
        var byPath = entries.Where(entry => ResidualScanService.NormalizeLocalPath(entry.InstallLocation) is { } location &&
            (string.Equals(location, TargetFolder, StringComparison.OrdinalIgnoreCase) ||
             location.StartsWith(TargetFolder + '\\', StringComparison.OrdinalIgnoreCase))).ToList();
        if (byPath.Count == 0)
        {
            byPath = entries.Where(entry => string.IsNullOrWhiteSpace(entry.InstallLocation) &&
                string.Equals(entry.Name, leaf, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        foreach (var entry in byPath) Matches.Add(entry);
        if (Matches.Count == 0)
            foreach (var candidate in _uninstall.FindCandidateUninstallers(TargetFolder)) Candidates.Add(candidate);

        IdentifySummary = Matches.Count > 0 ? $"找到 {Matches.Count} 个卸载登记。请核对程序名称后再运行。"
            : Candidates.Count > 0 ? $"找到 {Candidates.Count} 个候选卸载器。文件名仅作线索，请确认来源后再运行。"
            : "未找到卸载程序。若软件已经卸载，可以继续扫描残留。";
        UpdateTargetState();
        _status($"已识别：{kind} {leaf}");
    }

    private void PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要卸载的软件文件夹",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true) AcceptPath(dialog.FolderName);
    }

    private void PickShortcut()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择软件快捷方式",
            Filter = "快捷方式 (*.lnk)|*.lnk",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true) AcceptPath(dialog.FileName);
    }

    private void Clear()
    {
        ClearTarget();
        ResetArchive();
        CleanupFeedback = "";
    }

    private void ClearTarget()
    {
        _folderAtSelection = null;
        TargetPath = "";
        TargetKindText = "";
        TargetFolder = "";
        IdentifySummary = "";
        Matches.Clear();
        Candidates.Clear();
        ResetResiduals();
        UpdateTargetState();
    }

    private void UpdateTargetState()
    {
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(HasUninstallers));
        OnPropertyChanged(nameof(HasScanTarget));
        OnPropertyChanged(nameof(CanRecycleTarget));
        OnPropertyChanged(nameof(ResidualEmptyText));
        RaiseCommandStates();
    }

    private void ResetArchive()
    {
        _archiveStatusFile = "";
        ArchivePath = "";
        OpenArchiveCommand.RaiseCanExecuteChanged();
    }

    private void ResetResiduals()
    {
        foreach (var item in Residuals) item.PropertyChanged -= OnResidualChanged;
        Residuals.Clear();
        ScanSummary = "";
        ScanStatus = "";
        NotifyResidualState();
    }

    private void OnResidualChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ResidualItem.IsSelected) or nameof(ResidualItem.SizeText))
            NotifySelection();
    }

    private void NotifyResidualState()
    {
        OnPropertyChanged(nameof(HasResiduals));
        OnPropertyChanged(nameof(ResidualEmptyText));
        NotifySelection();
    }

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasDirectorySelection));
        RaiseCommandStates();
    }

    // ---------- 运行卸载器 ----------

    private void RunMatched(UninstallEntry? entry)
    {
        if (entry is null) return;

        if (!Confirm($"确定要运行「{entry.Name}」的卸载器吗？\n\n{entry.UninstallString}"))
            return;

        try
        {
            _uninstall.Launch(entry);
            _status($"已调起卸载器：{entry.Name}。卸载完成后回来点「扫描残留」。");
        }
        catch (Exception ex)
        {
            Warn($"启动卸载器失败：{ex.Message}");
        }
    }

    private void RunCandidate(CandidateUninstaller candidate)
    {
        var target = CaptureTarget();
        if (!Confirm($"文件名看起来像卸载程序，但 WinKit 无法验证它属于「{target.Name}」。仅在信任来源时运行：\n\n{candidate.FilePath}"))
            return;

        if (Native.OpenPath(candidate.FilePath, workingDirectory: candidate.Folder))
            _status($"已打开候选卸载器：{candidate.Name}");
        else
            CleanupFeedback = $"无法打开卸载器：{candidate.FilePath}";
    }

    // ---------- 残留扫描 ----------

    private void CancelScan()
    {
        _scanCts?.Cancel();
        ScanStatus = "正在停止扫描…";
        CancelScanCommand.RaiseCanExecuteChanged();
    }

    private async Task ScanAsync(bool deep)
    {
        if (!CanChangeTarget || !HasScanTarget) return;
        var target = CaptureTarget();
        var request = BuildRequest(target, deep);

        using var source = new CancellationTokenSource();
        _scanCts = source;
        IsScanning = true;
        ResetResiduals();
        ScanStatus = deep ? $"正在深度扫描「{target.Name}」的相关项目，可能需要几分钟…"
            : $"正在扫描「{target.Name}」的相关项目…";

        var token = source.Token;
        IProgress<string> progress = new Progress<string>(message =>
        {
            if (ReferenceEquals(_scanCts, source) && !token.IsCancellationRequested) ScanStatus = message;
        });
        try
        {
            var result = await Task.Run(() => _residual.Scan(request, progress.Report, token), token);
            token.ThrowIfCancellationRequested();
            foreach (var item in result.Items)
            {
                item.PropertyChanged += OnResidualChanged;
                Residuals.Add(item);
            }

            ScanSummary = result.Items.Count == 0 ? "未找到与目标有关的项目。"
                : $"找到 {result.Items.Count} 个待核对的项目：{result.PathReferenceCount} 项引用目标路径，{result.PossibleCount} 项名称相似。默认不勾选。";
            foreach (var warning in result.Warnings.Take(3)) ScanSummary += "\n" + warning;
            _status(ScanSummary.Replace("\n", " "));
        }
        catch (OperationCanceledException)
        {
            ScanSummary = "已停止扫描，没有更改任何内容。";
            _status(ScanSummary);
        }
        catch (Exception ex)
        {
            ScanSummary = $"扫描失败：{ex.Message}";
            _status("残留扫描失败");
        }
        finally
        {
            _scanCts = null;
            ScanStatus = "";
            IsScanning = false;
            OnPropertyChanged(nameof(CanRecycleTarget));
            NotifyResidualState();
        }
    }

    private static ResidualScanRequest BuildRequest(TargetSnapshot target, bool deep)
    {
        var partial = new List<string> { target.Name };
        foreach (var suffix in new[] { "updater", "update", "bin", "app" })
        {
            if (target.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && target.Name.Length > suffix.Length + 2)
                partial.Add(target.Name[..^suffix.Length].TrimEnd(' ', '-', '_'));
        }

        return new ResidualScanRequest
        {
            ExactNames = [target.Name, .. target.Matches.Select(match => match.Name)],
            PartialNames = partial,
            PathFragments = [target.Folder],
            ExtraRoots = [Path.GetDirectoryName(target.Folder) ?? ""],
            ExcludedPaths = [target.Folder],
            MaxDepth = deep ? 64 : 3
        };
    }

    private void ClearSelection()
    {
        foreach (var item in Residuals) item.IsSelected = false;
    }

    private async Task MeasureAsync()
    {
        var targets = Residuals.Where(i => i.IsSelected && i.Kind == ResidualKind.Directory).ToList();
        if (targets.Count == 0)
        {
            _status("没有选中目录，无需计算大小");
            return;
        }

        IsBusy = true;
        try
        {
            foreach (var item in targets)
            {
                var path = item.Path;
                var (bytes, _) = await Task.Run(() => _residual.MeasureDirectory(path));
                item.SizeBytes = bytes;
                item.RaiseSizeChanged();
            }

            OnPropertyChanged(nameof(SelectedSummary));
            _status($"已计算 {targets.Count} 个目录的大小");
        }
        catch (Exception ex)
        {
            _status($"计算大小失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------- 清理 ----------

    private async void CleanSelected()
    {
        if (!CanChangeTarget) return;
        var target = CaptureTarget();
        var selected = target.Residuals.Where(IsSelectedForCleanup).ToArray();
        if (selected.Length == 0) return;
        var registryItems = selected.Where(item => item.Kind is ResidualKind.Service or ResidualKind.Startup).ToArray();
        if (registryItems.Any(item => item.Hive == HiveKind.LocalMachine) &&
            Native.RequireAdmin("清理所选系统项") is { } adminNeeded)
        {
            CleanupFeedback = adminNeeded;
            return;
        }

        var files = selected.Length - registryItems.Length;
        var message = new StringBuilder($"目标：{target.Folder}\n将处理你勾选的 {selected.Length} 项。\n\n");
        if (files > 0) message.AppendLine($"· {files} 个文件夹或快捷方式：请求移入回收站。若 Windows 提示永久删除，请取消。");
        if (registryItems.Length > 0) message.AppendLine($"· {registryItems.Length} 个服务或启动项：备份后移除注册表设置。停止的服务不会因撤销而自动重启。");
        message.AppendLine("\n扫描只提供线索，请仅清理你确认不再使用的项目。");

        IsBusy = true;
        var confirmed = false;
        var completed = new List<ResidualItem>();
        var failures = new List<string>();
        try
        {
            if (!Confirm(message.ToString())) return;
            confirmed = true;
            _setGlobalBusy?.Invoke(true);
            CleanupFeedback = $"正在处理「{target.Name}」的 {selected.Length} 个所选项目…";
            if (registryItems.Length > 0)
                await Task.Run(() => CleanRegistryItems(target.Name, registryItems, completed, failures));

            // Windows 的回收界面依赖 STA；这部分在界面线程交给系统处理。
            foreach (var item in selected.Where(item => item.Kind == ResidualKind.Shortcut))
            {
                if (RecycleFile(item, out var error)) completed.Add(item);
                else failures.Add($"{item.Path}：{error}");
            }
            string[] excludedDirectories = [target.Folder];
            foreach (var item in selected.Where(item => item.Kind == ResidualKind.Directory).OrderByDescending(item => item.Path.Length))
            {
                if (RecycleDirectory(item.DirectoryTarget!, out var error, excludedDirectories)) completed.Add(item);
                else failures.Add($"{item.Path}：{error}");
            }

            ResetArchive();
            _archiveStatusFile = WriteArchive(target, selected, completed, failures);
            ArchivePath = _archiveStatusFile;
            OpenArchiveCommand.RaiseCanExecuteChanged();
            foreach (var item in completed) item.IsSelected = false;
            var archiveNote = string.IsNullOrEmpty(ArchivePath) ? "未能保存清理记录。" : "可在「清理记录」查看详情。";
            CleanupFeedback = failures.Count == 0 ? $"已处理 {completed.Count} 项。{archiveNote}"
                : $"已处理 {completed.Count} 项；{failures.Count} 项未完成。{archiveNote}\n" + string.Join("\n", failures.Take(3));
            _status($"已处理 {completed.Count} 项，{failures.Count} 项未完成");
        }
        catch (Exception ex)
        {
            CleanupFeedback = $"清理未完成：{ex.Message}";
        }
        finally
        {
            try
            {
                IsBusy = false;
                // 保留失败项供核对；只从当前结果中移除已完成的项目。
                foreach (var item in completed)
                {
                    item.PropertyChanged -= OnResidualChanged;
                    Residuals.Remove(item);
                }
                if (completed.Count > 0)
                    ScanSummary = Residuals.Count == 0 ? "当前结果已处理完毕。" : $"扫描结果中还剩 {Residuals.Count} 项。";
                NotifyResidualState();
            }
            finally
            {
                if (confirmed) _setGlobalBusy?.Invoke(false);
            }
        }
    }

    private void CleanRegistryItems(string targetName, ResidualItem[] items, List<ResidualItem> completed, List<string> failures)
    {
        RegPath RegistryPath(ResidualItem item) => RegPath.Of(item.Hive!.Value, item.SubKey);
        bool IsStillSame(ResidualItem item) => string.Equals(Reg.GetString(RegistryPath(item),
            item.Kind == ResidualKind.Service ? "ImagePath" : item.ValueName), item.Detail, StringComparison.Ordinal);
        var unchanged = items.Where(item =>
        {
            if (IsStillSame(item)) return true;
            failures.Add($"{item.Path}：扫描后已变化，未处理。请重新扫描。");
            return false;
        }).ToArray();
        if (unchanged.Length == 0) return;

        var processed = new List<ResidualItem>();
        _journal.Record($"清理「{targetName}」的残留注册表设置", unchanged.Select(item => (item.Hive!.Value, item.SubKey)), () =>
        {
            foreach (var item in unchanged)
            {
                try
                {
                    if (!IsStillSame(item)) throw new IOException("扫描后已变化，未处理。请重新扫描。");
                    var path = RegistryPath(item);
                    if (item.Kind == ResidualKind.Service)
                    {
                        if (!TryStopService(item, out var error)) throw new IOException(error);
                        if (!IsStillSame(item)) throw new IOException("服务配置已变化，未删除注册项。");
                        Reg.DeleteTree(path);
                    }
                    else Reg.DeleteValue(path, item.ValueName);
                    processed.Add(item);
                }
                catch (Exception ex) { failures.Add($"{item.Path}：{ex.Message}"); }
            }
        });
        completed.AddRange(processed);
    }

    private bool TryStopService(ResidualItem item, out string error)
    {
        error = "";
        var name = item.SubKey.Split('\\').LastOrDefault() ?? "";

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "无法从注册表路径解析服务名";
            return false;
        }

        try
        {
            // sc.exe 的输出在中文 Windows 上是 OEM 代码页，这里只看退出码与状态关键字
            var output = RunProcess("sc.exe", $"query \"{name}\"", out var code);
            if (code == 0 && output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                RunProcess("sc.exe", $"stop \"{name}\"", out _);

                for (var i = 0; i < 20; i++)
                {
                    var state = RunProcess("sc.exe", $"query \"{name}\"", out var queryCode);
                    if (queryCode != 0 || !state.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                        return true;
                    Thread.Sleep(250);
                }

                error = "服务仍在运行，未删除注册项（先手工停止服务再清理）";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool RecycleFile(ResidualItem item, out string error)
    {
        error = "";
        try
        {
            var path = ResidualScanService.NormalizeLocalPath(item.Path);
            if (path is null || new DriveInfo(path[..3]).DriveType != DriveType.Fixed ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                !string.Equals(Shortcuts.ResolveLinkTarget(path), item.ObservedShortcutTarget, StringComparison.OrdinalIgnoreCase))
                throw new IOException("快捷方式位置不受支持，或扫描后已变化。请重新扫描。");
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            if (File.Exists(path)) throw new IOException("原路径仍有文件，未确认操作完成。");
            return true;
        }
        catch (OperationCanceledException) { error = "已取消。"; return false; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static bool RecycleDirectory(ResidualScanService.DirectoryTarget expected, out string error,
        IReadOnlyList<string>? excludedPaths = null)
    {
        if (!ResidualScanService.TryGetDirectoryTarget(expected.Path, out var actual, out error, excludedPaths)) return false;
        if (actual != expected)
        {
            error = "这个位置的文件夹已变化，请重新选择或扫描。";
            return false;
        }
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(expected.Path,
                Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            if (Directory.Exists(expected.Path)) throw new IOException("原路径仍有文件夹，未确认操作完成。");
            return true;
        }
        catch (OperationCanceledException) { error = "已取消。"; return false; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // ---------- 整目录回收 ----------

    private async void RecycleTargetFolder()
    {
        if (!CanChangeTarget || _folderAtSelection is not { } expected) return;
        var target = CaptureTarget();
        IsBusy = true;
        var confirmed = false;
        try
        {
            if (!ResidualScanService.TryGetDirectoryTarget(target.Folder, out var actual, out var reason) || actual != expected)
            {
                CleanupFeedback = string.IsNullOrEmpty(reason) ? "这个位置的文件夹已变化，请重新选择。" : reason;
                return;
            }

            var typed = InputDialog.Prompt(Application.Current.MainWindow,
                "将整个文件夹移入回收站",
                $"这不是卸载。将处理这个文件夹内的所有数据：\n{target.Folder}\n\n" +
                $"若 Windows 提示永久删除，请取消。请输入文件夹名「{target.Name}」继续：", "");
            if (typed is null) return;
            if (!string.Equals(typed.Trim(), target.Name, StringComparison.OrdinalIgnoreCase))
            {
                CleanupFeedback = "输入的名称不一致，没有处理任何内容。";
                return;
            }

            confirmed = true;
            _setGlobalBusy?.Invoke(true);
            CleanupFeedback = "正在准备将文件夹移入回收站…";
            var (bytes, files) = await Task.Run(() => _residual.MeasureDirectory(target.Folder));
            if (!RecycleDirectory(expected, out var error))
            {
                CleanupFeedback = $"未能处理文件夹：{error}";
                return;
            }

            var lines = new List<string>
            {
                $"# 处理整个文件夹：{target.Name}",
                "",
                $"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"- 路径：{target.Folder}",
                $"- 估算：{files} 个文件 / {ThisPcService.FormatSize(bytes)}",
                $"- 目标类型：{target.Kind}",
                $"- 原路径来源：{target.Path}",
                "",
                "已请求移入系统回收站；没有运行卸载程序。只有仍在回收站的项目可以从那里还原。",
                "",
                "## 识别到的卸载登记及候选卸载器（未运行）"
            };
            lines.AddRange(target.Matches.Select(match => $"- {match.Name} {match.Version} · {match.InstallLocation}"));
            lines.AddRange(target.Candidates.Select(candidate => $"- {candidate.FilePath}"));
            lines.Add("");
            lines.Add("## 之前扫描到的其他项目（未处理）");
            lines.AddRange(target.Residuals.Select(item => $"- [{item.ConfidenceText}] {item.KindText}：{item.Path}"));
            ResetArchive();
            _archiveStatusFile = WriteArchiveFile(target.Name, lines);
            ArchivePath = _archiveStatusFile;
            OpenArchiveCommand.RaiseCanExecuteChanged();

            ClearTarget();
            CleanupFeedback = $"「{target.Name}」已从原位置移除，未运行卸载程序。可在回收站查找仍保留的文件。" +
                (string.IsNullOrEmpty(ArchivePath) ? "未能保存清理记录。" : "");
            _status(CleanupFeedback);
        }
        catch (Exception ex) { CleanupFeedback = $"未能处理文件夹：{ex.Message}"; }
        finally
        {
            try
            {
                IsBusy = false;
                OnPropertyChanged(nameof(CanRecycleTarget));
            }
            finally
            {
                if (confirmed) _setGlobalBusy?.Invoke(false);
            }
        }
    }

    // ---------- 存档 ----------

    private static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinKit", "UninstallLogs");

    private static string WriteArchive(TargetSnapshot target, IReadOnlyList<ResidualItem> selected,
        IReadOnlyList<ResidualItem> completed, IReadOnlyList<string> failures)
    {
        var lines = new List<string>
        {
            $"# 残留清理：{target.Name}",
            "",
            $"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"- 原路径来源：{target.Path}",
            $"- 目标文件夹：{target.Folder}",
            "",
            "## 识别到的卸载登记"
        };
        lines.AddRange(target.Matches.Select(match => $"- {match.Name} {match.Version} · {match.InstallLocation}"));
        lines.Add("");
        lines.Add("## 已处理");
        foreach (var item in completed)
        {
            var how = item.Kind is ResidualKind.Directory or ResidualKind.Shortcut ? "已从原位置移除（请求移入回收站）"
                : "已备份并移除注册表设置";
            lines.Add($"- [{item.ConfidenceText}] {item.KindText}：{item.Path} —— {how}");
        }
        if (failures.Count > 0)
        {
            lines.Add("");
            lines.Add("## 未完成");
            lines.AddRange(failures.Select(failure => $"- {failure}"));
        }
        lines.Add("");
        lines.Add("## 未选择");
        lines.AddRange(target.Residuals.Except(selected)
            .Select(item => $"- [{item.ConfidenceText}] {item.KindText}：{item.Path}"));
        return WriteArchiveFile(target.Name, lines);
    }

    private static string WriteArchiveFile(string name, IReadOnlyList<string> lines)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            var file = Path.Combine(LogDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}-{safeName}.md");

            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(string.Join(Environment.NewLine, lines));
            return file;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private void OpenArchive()
    {
        if (!File.Exists(_archiveStatusFile)) return;

        if (!Native.OpenPath(_archiveStatusFile))
            _status("打开存档失败：文件可能已被移动或删除");
    }

    // ---------- 杂项 ----------

    private void OpenTarget()
    {
        var target = HasTargetFolder ? TargetFolder : TargetPath;
        if (string.IsNullOrWhiteSpace(target)) return;

        if (!Native.OpenPath(target))
            _status("打开失败：路径可能已被移动或删除");
    }

    private void OpenResidual(ResidualItem? item)
    {
        if (item is null) return;

        var opened = item.Kind switch
        {
            ResidualKind.Directory => Native.OpenPath(item.Path),
            ResidualKind.Shortcut => Native.OpenPath(Path.GetDirectoryName(item.Path) ?? ""),
            ResidualKind.ScheduledTask => Native.OpenPath("taskschd.msc"),
            _ => Native.OpenInRegedit(item.Path)
        };
        if (!opened) CleanupFeedback = "未能打开该位置。项目可能已被移动、删除，或需要系统权限。";
    }

    /// <summary>
    /// 从剪贴板取路径来识别。存在的意义：拖放在「WinKit 提权、资源管理器不提权」的组合下
    /// 依赖 UIPI 放行，一旦被安全软件或系统策略拦掉，用户至少还有一条不用翻对话框的快路
    /// ——在资源管理器里 Ctrl+C，回到这里点一下即可。
    /// </summary>
    private void PastePath()
    {
        string text;
        try
        {
            text = ClipboardTextReader();
        }
        catch (Exception)
        {
            _status("读不到剪贴板内容");
            return;
        }

        // 资源管理器复制文件时是「一行一个、带引号」的列表，取第一条可用路径
        var candidate = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Trim('"').Trim())
            .FirstOrDefault(line => line.Length > 0);

        if (string.IsNullOrWhiteSpace(candidate))
        {
            _status("剪贴板里没有可用的路径");
            return;
        }

        AcceptPath(candidate);
    }

    private void CopyTarget()
    {
        var text = HasTargetFolder ? TargetFolder : TargetPath;
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            Clipboard.SetText(text);
            _status("路径已复制");
        }
        catch (Exception)
        {
            _status("复制失败");
        }
    }

    /// <summary>
    /// 同步版控制台调用，只为「ChangeJournal 的 action 委托必须是同步 Action」这个约束而存在。
    /// 实现直接复用 <see cref="AppManagerService.RunProcessAsync"/>：OEM 代码页解码、
    /// 超时真的杀进程这些细节都在那边，不再各写一份。它内部用了 ConfigureAwait(false)，
    /// 所以在 UI 线程上 GetResult() 也不会和等待 dispatcher 的续体互等死锁。
    /// </summary>
    private static string RunProcess(string fileName, string arguments, out int exitCode)
    {
        var (code, stdout, _) = AppManagerService
            .RunProcessAsync(fileName, arguments, TimeSpan.FromSeconds(30), AppManagerService.GetOemEncoding())
            .GetAwaiter()
            .GetResult();

        exitCode = code;
        return stdout;
    }

    private static bool Confirm(string message) => MessageBox.Show(
        Application.Current.MainWindow, message, "WinKit",
        MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private static void Warn(string message) => MessageBox.Show(
        Application.Current.MainWindow, message, "WinKit",
        MessageBoxButton.OK, MessageBoxImage.Warning);

    private void RaiseCommandStates()
    {
        CleanSelectedCommand.RaiseCanExecuteChanged();
        RecycleTargetCommand.RaiseCanExecuteChanged();
        CancelScanCommand.RaiseCanExecuteChanged();
        MeasureCommand.RaiseCanExecuteChanged();
    }
}
