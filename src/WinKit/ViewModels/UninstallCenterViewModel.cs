using System.Collections.ObjectModel;
using System.Diagnostics;
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

    private readonly UninstallService _uninstall = new();
    private readonly ResidualScanService _residual = new();

    private CancellationTokenSource? _scanCts;

    private string _targetPath = "";
    private string _targetKindText = "";
    private string _targetFolder = "";
    private string _identifySummary = "";
    private string _scanStatus = "";
    private string _scanSummary = "";
    private string _archivePath = "";
    private string _archiveStatusFile = "";
    private bool _isScanning;
    private bool _isBusy;

    public UninstallCenterViewModel(ChangeJournal journal, Action<string> status)
    {
        _journal = journal;
        _status = status;

        Matches = new ObservableCollection<UninstallEntry>();
        Candidates = new ObservableCollection<CandidateUninstaller>();
        Residuals = new ObservableCollection<ResidualItem>();

        PickFolderCommand = new RelayCommand(PickFolder);
        PickShortcutCommand = new RelayCommand(PickShortcut);
        ClearCommand = new RelayCommand(Clear);
        RunMatchedCommand = new RelayCommand(p => RunMatched(p as UninstallEntry));
        RunCandidateCommand = new RelayCommand(p => RunCandidate(p as CandidateUninstaller));
        ScanCommand = new RelayCommand(() => _ = ScanAsync(false));
        DeepScanCommand = new RelayCommand(() => _ = ScanAsync(true));
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);
        CleanSelectedCommand = new RelayCommand(() => CleanSelected(), () => HasResiduals && !IsBusy);
        ForceDeleteCommand = new RelayCommand(() => ForceDeleteFolder(), () => HasTargetFolder && !IsBusy);
        MeasureCommand = new RelayCommand(() => _ = MeasureAsync(), () => HasResiduals && !IsScanning);
        SelectConfirmedCommand = new RelayCommand(() => SetSelection(true));
        ClearSelectionCommand = new RelayCommand(() => SetSelection(false));
        OpenTargetCommand = new RelayCommand(OpenTarget);
        OpenResidualCommand = new RelayCommand(p => OpenResidual(p as ResidualItem));
        OpenArchiveCommand = new RelayCommand(OpenArchive, () => File.Exists(_archiveStatusFile));
        CopyTargetCommand = new RelayCommand(CopyTarget);
        PastePathCommand = new RelayCommand(PastePath);
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
    public RelayCommand ForceDeleteCommand { get; }
    public RelayCommand MeasureCommand { get; }
    public RelayCommand SelectConfirmedCommand { get; }
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
            OnPropertyChanged(nameof(TargetFolderName));
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
        private set
        {
            if (!Set(ref _scanSummary, value)) return;
            OnPropertyChanged(nameof(HasResiduals));
        }
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
            OnPropertyChanged(nameof(ScanHint));
            RaiseCommandStates();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            RaiseCommandStates();
        }
    }

    public bool HasTarget => !string.IsNullOrWhiteSpace(_targetPath);
    public bool HasTargetFolder => !string.IsNullOrWhiteSpace(_targetFolder) && Directory.Exists(_targetFolder);
    public bool HasMatches => Matches.Count > 0;
    public bool HasCandidates => Candidates.Count > 0;
    public bool HasResiduals => Residuals.Count > 0;
    public bool ShowEmptyOverall => !HasTarget && !HasResiduals;

    /// <summary>强制删除要用户照着敲的文件夹名。</summary>
    public string TargetFolderName => Path.GetFileName(_targetFolder.TrimEnd('\\'));

    public string ScanHint => IsScanning ? "正在扫描…" : "";

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
            folder = raw.TrimEnd('\\');
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
            IdentifySummary = $"路径不存在：{raw}";
            _status("拖入的路径不存在");
            return;
        }

        ResetResiduals();

        TargetPath = raw;
        TargetKindText = kind;
        TargetFolder = folder;

        var leaf = Path.GetFileName(folder.TrimEnd('\\'));
        var entries = _uninstall.LoadAll();
        var matches = _uninstall.Match(folder, leaf, entries);

        Matches.Clear();
        foreach (var entry in matches) Matches.Add(entry);

        Candidates.Clear();
        foreach (var candidate in _uninstall.FindCandidateUninstallers(folder)) Candidates.Add(candidate);

        OnPropertyChanged(nameof(HasMatches));
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(ShowEmptyOverall));

        IdentifySummary = matches.Count switch
        {
            0 when Candidates.Count > 0 =>
                $"没有对应的卸载登记，但在目录里找到 {Candidates.Count} 个候选卸载器——" +
                "这类软件通常把卸载记录写在自己的配置里，直接运行候选卸载器即可。",
            0 =>
                $"没有对应的卸载登记，也没找到卸载器。「{leaf}」可能是绿色软件，或安装记录已被别的工具清理。" +
                "可以继续做残留扫描，或直接强制删除这个文件夹。",
            1 => $"已匹配到卸载登记：{matches[0].Name}",
            _ => $"匹配到 {matches.Count} 个卸载登记，请确认要运行哪一个。"
        };

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
        TargetPath = "";
        TargetKindText = "";
        TargetFolder = "";
        IdentifySummary = "";
        Matches.Clear();
        Candidates.Clear();
        ResetResiduals();
        OnPropertyChanged(nameof(HasMatches));
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(ShowEmptyOverall));
    }

    private void ResetResiduals()
    {
        Residuals.Clear();
        ScanSummary = "";
        ScanStatus = "";
        OnPropertyChanged(nameof(HasResiduals));
        OnPropertyChanged(nameof(SelectedSummary));
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

    private void RunCandidate(CandidateUninstaller? candidate)
    {
        if (candidate is null) return;

        if (!Confirm($"这个卸载器没有登记在系统卸载列表里，请确认它属于「{TargetFolderName}」：\n\n{candidate.FilePath}"))
            return;

        if (Native.OpenPath(candidate.FilePath, workingDirectory: candidate.Folder))
            _status($"已调起候选卸载器：{candidate.Name}");
        else
            Warn($"启动卸载器失败：{candidate.FilePath}");
    }

    // ---------- 残留扫描 ----------

    private void CancelScan()
    {
        _scanCts?.Cancel();
        ScanStatus = "正在取消…";
    }

    private async Task ScanAsync(bool deep)
    {
        if (IsScanning) return;

        if (!HasTarget)
        {
            Warn("请先拖入或选择要卸载的文件夹 / 快捷方式。");
            return;
        }

        var request = BuildRequest(deep);

        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        ResetResiduals();
        ScanStatus = deep
            ? "深度扫描：递归遍历所有目录，可能需要几分钟…"
            : "正在扫描（目录 + 快捷方式 + 服务 + 计划任务 + 启动项 + 文件关联）…";

        var token = _scanCts.Token;

        try
        {
            var result = await Task.Run(() => _residual.Scan(request,
                p => ScanStatus = p, token), token);

            foreach (var item in result.Items) Residuals.Add(item);

            OnPropertyChanged(nameof(HasResiduals));
            OnPropertyChanged(nameof(SelectedSummary));

            ScanStatus = "";
            ScanSummary = result.Cancelled
                ? $"扫描已取消，已找到 {result.Items.Count} 项。"
                : $"扫描完成：{result.ConfirmedCount} 项确定、{result.PossibleCount} 项可能，" +
                  $"检查了 {result.ScannedDirectories} 个目录，用时 {result.Elapsed.TotalSeconds:F1} 秒。";

            foreach (var warning in result.Warnings.Take(3))
                ScanSummary += "\n" + warning;

            _status(ScanSummary.Replace("\n", " "));
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "";
            ScanSummary = $"扫描已取消，已找到 {Residuals.Count} 项。";
            _status("扫描已取消");
        }
        catch (Exception ex)
        {
            ScanStatus = "";
            ScanSummary = $"扫描失败：{ex.Message}";
            _status("残留扫描失败");
        }
        finally
        {
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private ResidualScanRequest BuildRequest(bool deep)
    {
        var leaf = Path.GetFileName(TargetFolder.TrimEnd('\\'));

        var exact = new List<string>();
        if (!string.IsNullOrWhiteSpace(leaf)) exact.Add(leaf);
        foreach (var match in Matches) exact.Add(match.Name);

        var partial = new List<string>();
        if (!string.IsNullOrWhiteSpace(leaf)) partial.Add(leaf);
        // 「QuarkUpdater」这种带后缀的目录名，去掉常见后缀后再当一次模糊词
        foreach (var suffix in new[] { "updater", "update", "bin", "app" })
        {
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && leaf.Length > suffix.Length + 2)
                partial.Add(leaf[..^suffix.Length].TrimEnd(' ', '-', '_'));
        }

        // 服务/计划任务/启动项/文件关联靠路径片段，安装目录与卸载器路径都是硬证据
        var fragments = new List<string>();
        if (HasTargetFolder) fragments.Add(TargetFolder);
        foreach (var match in Matches)
            if (!string.IsNullOrWhiteSpace(match.InstallLocation)) fragments.Add(match.InstallLocation.Trim());
        foreach (var candidate in Candidates.Take(4)) fragments.Add(candidate.FilePath);

        return new ResidualScanRequest
        {
            ExactNames = exact,
            PartialNames = partial,
            PathFragments = fragments,
            // 拖进来的目录本身作为额外根，这样装在非系统盘（如 D:\Quark）也能扫到同级残留
            ExtraRoots = HasTargetFolder ? new[] { Path.GetDirectoryName(TargetFolder) ?? "" } : Array.Empty<string>(),
            MaxDepth = deep ? 64 : 3
        };
    }

    private void SetSelection(bool confirmedOnly)
    {
        foreach (var item in Residuals)
            item.IsSelected = confirmedOnly && item.CanClean && item.IsConfirmed;

        OnPropertyChanged(nameof(SelectedSummary));
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
        if (Native.RequireAdmin("清理残留") is { } adminNeeded)
        {
            Warn(adminNeeded);
            return;
        }

        var selected = Residuals.Where(i => i.IsSelected && i.CanClean).ToList();
        if (selected.Count == 0)
        {
            Warn("没有选中任何可清理的项。");
            return;
        }

        var directories = selected.Where(i => i.Kind == ResidualKind.Directory).ToList();
        var shortcuts = selected.Where(i => i.Kind == ResidualKind.Shortcut).ToList();
        var services = selected.Where(i => i.Kind == ResidualKind.Service).ToList();
        var tasks = selected.Where(i => i.Kind == ResidualKind.ScheduledTask).ToList();
        var startups = selected.Where(i => i.Kind == ResidualKind.Startup).ToList();

        var message = new StringBuilder("将要清理：\n");
        if (directories.Count > 0) message.AppendLine($"· {directories.Count} 个目录（移到回收站）");
        if (shortcuts.Count > 0) message.AppendLine($"· {shortcuts.Count} 个快捷方式（移到回收站）");
        if (services.Count > 0) message.AppendLine($"· {services.Count} 个服务注册项（写注册表快照，可撤销）");
        if (tasks.Count > 0) message.AppendLine($"· {tasks.Count} 个计划任务（备份任务文件，可撤销）");
        if (startups.Count > 0) message.AppendLine($"· {startups.Count} 个开机启动项（写注册表快照，可撤销）");
        message.AppendLine();
        message.AppendLine("目录和快捷方式走回收站，需要手工从回收站还原；注册表与计划任务可以在「备份与快照」里一键撤销。");

        if (!Confirm(message.ToString())) return;

        IsBusy = true;
        try
        {
            var tracked = services.Select(i => (i.Hive ?? HiveKind.LocalMachine, i.SubKey))
                .Concat(startups.Select(i => (i.Hive ?? HiveKind.CurrentUser, i.SubKey)))
                .Where(t => !string.IsNullOrWhiteSpace(t.Item2))
                .Distinct()
                .ToList();

            var trackedFiles = shortcuts.Select(i => i.Path)
                .Concat(tasks.Select(i => i.Path))
                .Where(p => File.Exists(p))
                .ToList();

            var failures = new List<string>();
            var done = 0;

            // 停服务最多等 20 × 250ms，还要叠加 sc.exe 每次几百毫秒的启动开销，多服务串行能拖到分钟级。
            // 这段留在 UI 线程上就是整窗口假死、进度也刷不出来，所以整体搬到线程池。
            await Task.Run(() =>
            {
                _journal.Record($"清理「{TargetFolderName}」的卸载残留（{selected.Count} 项）",
                    tracked, () =>
                    {
                        foreach (var service in services)
                        {
                            if (TryStopService(service, out var error))
                            {
                                TryRegistryAction(() => Reg.DeleteTree(RegPath.Of(
                                    service.Hive ?? HiveKind.LocalMachine, service.SubKey)), failures, service.Path);
                            }
                            else
                            {
                                failures.Add($"{service.Path}：{error}");
                            }
                        }

                        foreach (var task in tasks)
                            DeleteScheduledTask(task, failures);

                        foreach (var startup in startups)
                            TryRegistryAction(() => Reg.DeleteValue(
                                RegPath.Of(startup.Hive ?? HiveKind.CurrentUser, startup.SubKey),
                                startup.ValueName), failures, startup.Path);
                    },
                    trackedFiles);

                done += services.Count + tasks.Count + startups.Count;
            });

            // 文件与目录不进 journal 的还原闭环（回收站里的东西由用户自己决定要不要还原）。
            // 这两个循环必须留在 UI 线程：回收站走 SHFileOperation，依赖 STA，换到线程池会直接失败。
            foreach (var shortcut in shortcuts)
                if (RecycleFile(shortcut.Path, out var error)) done++;
                else failures.Add($"{shortcut.Path}：{error}");

            foreach (var directory in directories)
                if (RecycleDirectory(directory.Path, out var error)) done++;
                else failures.Add($"{directory.Path}：{error}");

            _archiveStatusFile = WriteArchive(selected, failures);
            ArchivePath = _archiveStatusFile;

            foreach (var item in selected) item.IsSelected = false;

            if (failures.Count > 0)
            {
                Warn($"清理完成，但有 {failures.Count} 项失败（不会假装成功）：\n\n" +
                     string.Join("\n", failures.Take(8)) +
                     (failures.Count > 8 ? $"\n… 其余 {failures.Count - 8} 项见存档" : "") +
                     $"\n\n存档：{_archiveStatusFile}");
            }
            else
            {
                _status($"已清理 {done} 项残留，存档：{_archiveStatusFile}");
            }

            OnPropertyChanged(nameof(SelectedSummary));
            OpenArchiveCommand.RaiseCanExecuteChanged();

            // 清理完立刻重扫一遍，让列表反映真实情况，而不是留着刚删掉的行
            _ = ScanAsync(false);
        }
        catch (Exception ex)
        {
            Warn($"清理失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void TryRegistryAction(Action action, List<string> failures, string label)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failures.Add($"{label}：{ex.Message}");
        }
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

    private void DeleteScheduledTask(ResidualItem item, List<string> failures)
    {
        var tasksRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");

        var name = Path.GetRelativePath(tasksRoot, item.Path);
        if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = name.Replace('/', '\\');

        try
        {
            RunProcess("schtasks.exe", $"/Delete /TN \"{name}\" /F", out var code);

            // schtasks 成功或任务文件已不存在都算干净；否则退回直接删文件
            if (code != 0 && File.Exists(item.Path))
                File.Delete(item.Path);
        }
        catch (Exception ex)
        {
            failures.Add($"{item.Path}：{ex.Message}");
        }
    }

    private static bool RecycleFile(string path, out string error)
    {
        error = "";
        try
        {
            if (!File.Exists(path)) return true;

            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool RecycleDirectory(string path, out string error)
    {
        error = "";
        try
        {
            if (!Directory.Exists(path)) return true;

            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ---------- 强制删除 ----------

    private void ForceDeleteFolder()
    {
        if (Native.RequireAdmin("强制删除文件夹") is { } adminNeeded)
        {
            Warn(adminNeeded);
            return;
        }

        if (!HasTargetFolder)
        {
            Warn("没有可删除的文件夹。");
            return;
        }

        var folder = TargetFolder.TrimEnd('\\');
        var name = Path.GetFileName(folder);

        // 只删一层，不递归到父目录；防止用户拖了盘符根目录过来
        if (folder.Length <= 3 || string.IsNullOrWhiteSpace(name))
        {
            Warn($"拒绝删除「{folder}」：这看起来是盘符根目录或系统目录。");
            return;
        }

        var typed = InputDialog.Prompt(Application.Current.MainWindow,
            "确认强制删除",
            $"强制删除会把整个文件夹移入回收站，且不经过软件自己的卸载流程。\n" +
            $"要删除的是：{folder}\n\n请输入文件夹名「{name}」以确认：",
            "");

        if (typed is null) return;

        if (!string.Equals(typed.Trim(), name, StringComparison.Ordinal))
        {
            Warn($"输入的名称与文件夹名不一致，已取消。");
            return;
        }

        IsBusy = true;
        try
        {
            var (bytes, files) = _residual.MeasureDirectory(folder);

            if (!RecycleDirectory(folder, out var error))
            {
                Warn($"删除失败：{error}\n\n可能被正在运行的进程占用，可以先在「文件占用解锁」里查一下。");
                return;
            }

            var lines = new List<string>
            {
                $"# 强制删除 {name}",
                "",
                $"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"- 路径：{folder}",
                $"- 规模：{files} 个文件 / {ThisPcService.FormatSize(bytes)}",
                $"- 目标类型：{TargetKindText}",
                $"- 原路径来源：{TargetPath}",
                "",
                "删除方式：移入回收站（未永久删除）。需要还原请到回收站。",
                "",
                "## 当时识别到的卸载登记",
                ""
            };

            lines.AddRange(Matches.Count == 0
                ? new[] { "（没有匹配到卸载登记）" }
                : Matches.Select(m => $"- {m.Name} {m.Version} · {m.InstallLocation}"));

            lines.Add("");
            lines.Add("## 当时识别到的候选卸载器");
            lines.Add("");
            lines.AddRange(Candidates.Count == 0
                ? new[] { "（没有找到候选卸载器）" }
                : Candidates.Select(c => $"- {c.FilePath}"));

            if (Residuals.Count > 0)
            {
                lines.Add("");
                lines.Add("## 删除时仍存在的残留项（未处理）");
                lines.Add("");
                lines.AddRange(Residuals.Select(r => $"- [{r.ConfidenceText}] {r.KindText}：{r.Path}"));
            }

            _archiveStatusFile = WriteArchiveFile(name, lines);
            ArchivePath = _archiveStatusFile;
            OpenArchiveCommand.RaiseCanExecuteChanged();

            Clear();
            ArchivePath = _archiveStatusFile;

            _status($"已把「{name}」移入回收站（{ThisPcService.FormatSize(bytes)}），存档：{_archiveStatusFile}");
        }
        catch (Exception ex)
        {
            Warn($"强制删除失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------- 存档 ----------

    private static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinKit", "UninstallLogs");

    private string WriteArchive(IReadOnlyList<ResidualItem> cleaned, IReadOnlyList<string> failures)
    {
        var lines = new List<string>
        {
            $"# 卸载残留清理：{TargetFolderName}",
            "",
            $"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"- 目标：{TargetPath}",
            $"- 安装目录：{(HasTargetFolder ? TargetFolder : "（未确定）")}",
            "",
            "## 识别到的卸载登记",
            ""
        };

        lines.AddRange(Matches.Count == 0
            ? new[] { "（没有匹配到卸载登记）" }
            : Matches.Select(m => $"- {m.Name} {m.Version} · {m.InstallLocation}"));

        lines.Add("");
        lines.Add("## 已清理");
        lines.Add("");

        foreach (var item in cleaned)
        {
            var how = item.Kind switch
            {
                ResidualKind.Directory => "移入回收站",
                ResidualKind.Shortcut => "移入回收站",
                ResidualKind.Service => "已停止并删除服务注册项（注册表快照）",
                ResidualKind.ScheduledTask => "已注销计划任务（任务文件已备份）",
                ResidualKind.Startup => "已删除启动项值（注册表快照）",
                _ => "已处理"
            };

            lines.Add($"- [{item.ConfidenceText}] {item.KindText}：{item.Path} —— {how}");
        }

        if (failures.Count > 0)
        {
            lines.Add("");
            lines.Add("## 失败项（未完成，不假装成功）");
            lines.Add("");
            lines.AddRange(failures.Select(f => $"- {f}"));
        }

        lines.Add("");
        lines.Add("## 未清理的残留（可能项，需人工判断）");
        lines.Add("");
        lines.AddRange(Residuals.Where(r => !cleaned.Contains(r))
            .Select(r => $"- [{r.ConfidenceText}] {r.KindText}：{r.Path}"));

        return WriteArchiveFile(TargetFolderName, lines);
    }

    private static string WriteArchiveFile(string name, IReadOnlyList<string> lines)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            var file = Path.Combine(LogDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeName}.md");

            File.WriteAllText(file, string.Join(Environment.NewLine, lines), new UTF8Encoding(false));
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

        var target = item.Kind switch
        {
            ResidualKind.Directory => item.Path,
            ResidualKind.Shortcut or ResidualKind.ScheduledTask => Path.GetDirectoryName(item.Path) ?? "",
            _ => null
        };

        if (target is null || string.IsNullOrWhiteSpace(target))
        {
            _status("注册表项请在「注册表审查」里查看");
            return;
        }

        if (!Native.OpenPath(target))
            _status("打开失败：路径可能已被移动或删除");
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
            text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
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
        MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    private static void Warn(string message) => MessageBox.Show(
        Application.Current.MainWindow, message, "WinKit",
        MessageBoxButton.OK, MessageBoxImage.Warning);

    private void RaiseCommandStates()
    {
        CleanSelectedCommand.RaiseCanExecuteChanged();
        ForceDeleteCommand.RaiseCanExecuteChanged();
        CancelScanCommand.RaiseCanExecuteChanged();
        MeasureCommand.RaiseCanExecuteChanged();
    }
}
