using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class BackupRow
{
    public string FilePath { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime CreatedTime { get; init; }
    public string TimeText { get; init; } = "";
    public long SizeBytes { get; init; }
    public string SizeText { get; init; } = "";
    public int KeyCount { get; init; }
    public string KeyCountText { get; init; } = "";
}

public sealed class BackupViewModel : ObservableObject, IModule
{
    private readonly ChangeJournal _journal;
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;
    private readonly Func<bool> _isGlobalBusy;
    private bool _isBusy;
    private bool _refreshPending;
    private BackupRow? _selectedBackup;
    private BackupRow? _previewRow;
    private BackupPreview? _preview;

    public BackupViewModel(ChangeJournal journal, Action<string> status, Action<bool>? setBusy = null, Func<bool>? isGlobalBusy = null)
    {
        _journal = journal;
        _status = status;
        _setBusy = setBusy;
        _isGlobalBusy = isGlobalBusy ?? (() => false);

        Backups = new ObservableCollection<BackupRow>();
        RefreshCommand = new RelayCommand(Refresh, () => CanInteract);
        RestoreCommand = new RelayCommand(p => Restore(p as BackupRow), CanUseRow);
        ViewDetailCommand = new RelayCommand(p => ViewDetail(p as BackupRow), CanUseRow);
        DeleteBackupCommand = new RelayCommand(p => DeleteBackup(p as BackupRow), CanUseRow);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => CanInteract);
        RollbackSessionCommand = new RelayCommand(RollbackSession, () => CanInteract && _journal.CanUndo);
        ConfirmRestoreCommand = new RelayCommand(ConfirmRestore, () => _preview is not null && CanUseRow(_previewRow));
        ClosePreviewCommand = new RelayCommand(ClosePreview, () => !_isBusy);
        _journal.Changed += _ => OnUi(() =>
        {
            _refreshPending = true;
            NotifyGlobalStateChanged();
        });
        _journal.OperationStateChanged += NotifyGlobalStateChanged;
    }

    public ObservableCollection<BackupRow> Backups { get; }

    public BackupRow? SelectedBackup
    {
        get => _selectedBackup;
        set => Set(ref _selectedBackup, value);
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand ViewDetailCommand { get; }
    public RelayCommand DeleteBackupCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand RollbackSessionCommand { get; }
    public RelayCommand ConfirmRestoreCommand { get; }
    public RelayCommand ClosePreviewCommand { get; }
    public bool HasPreview => _preview is not null;
    public string PreviewTitle => _preview?.Title ?? "";
    public string PreviewText => _preview?.TargetsText ?? "";

    public string BackupDirectory => _journal.StorageDirectory;

    private string _errorText = "";
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (Set(ref _errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    private bool CanInteract => !_isBusy && !_journal.IsOperationActive && !_isGlobalBusy();

    private bool CanUseRow(object? parameter)
        => CanInteract && parameter is BackupRow row && File.Exists(row.FilePath);

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(action);
        else action();
    }

    public void NotifyGlobalStateChanged() => OnUi(() =>
    {
        OnPropertyChanged(nameof(SessionText));
        RefreshCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        ViewDetailCommand.RaiseCanExecuteChanged();
        DeleteBackupCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        RollbackSessionCommand.RaiseCanExecuteChanged();
        ConfirmRestoreCommand.RaiseCanExecuteChanged();
        ClosePreviewCommand.RaiseCanExecuteChanged();
        RefreshPending();
    });

    private void SetBusy(bool value)
    {
        if (_isBusy == value) return;
        _isBusy = value;
        _setBusy?.Invoke(value);
        NotifyGlobalStateChanged();
    }

    private void ReportError(string message)
    {
        ErrorText = message;
        _status(message.Split('\n')[0]);
    }

    public string SessionText => _journal.CanUndo
        ? $"本次运行有 {_journal.SessionCount} 次改动可回滚"
        : "本次运行尚无改动";

    public void Refresh() => OnUi(() =>
    {
        _refreshPending = true;
        RefreshPending();
    });

    private void RefreshPending()
    {
        if (!_refreshPending || !CanInteract) return;
        _refreshPending = false;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!CanInteract)
        {
            _refreshPending = true;
            return;
        }
        SetBusy(true);
        try
        {
            var journal = _journal;

            // 单个快照最多可含两万个键或整个文件的 Base64，逐个读盘+反序列化放在 UI 线程会明显卡顿
            var rows = await Task.Run(() =>
            {
                var list = new List<BackupRow>();
                foreach (var file in journal.ListBackups())
                {
                    var title = file.Name;
                    var keyCountText = "";
                    var keyCount = 0;

                    try
                    {
                        var snapshot = JsonSerializer.Deserialize<OperationSnapshot>(File.ReadAllText(file.FullName));
                        if (snapshot is not null)
                        {
                            title = snapshot.Title;
                            keyCount = snapshot.KeyCount;
                            keyCountText = snapshot.Files.Count > 0 && snapshot.Keys.Count == 0
                                ? $"{snapshot.Files.Count} 个文件"
                                : snapshot.Files.Count > 0
                                    ? $"{snapshot.KeyCount} 项"
                                    : $"{snapshot.KeyCount} 个键";
                        }
                    }
                    catch (Exception)
                    {
                        // 解析失败就用文件名
                    }

                    list.Add(new BackupRow
                    {
                        FilePath = file.FullName,
                        Title = title,
                        CreatedTime = file.CreationTime,
                        TimeText = file.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        SizeBytes = file.Length,
                        SizeText = ThisPcService.FormatSize(file.Length),
                        KeyCount = keyCount,
                        KeyCountText = keyCountText
                    });
                }

                return list;
            });

            var selectedPath = SelectedBackup?.FilePath;
            Backups.Clear();
            foreach (var row in rows) Backups.Add(row);
            SelectedBackup = Backups.FirstOrDefault(row => string.Equals(row.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase));

            OnPropertyChanged(nameof(SessionText));
        }
        catch (Exception ex) { ReportError($"读取备份列表失败：{ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async void ViewDetail(BackupRow? row)
    {
        if (row is null || !CanUseRow(row)) return;
        ClosePreview();
        SetBusy(true);
        ErrorText = "";
        try
        {
            var preview = await Task.Run(() => _journal.PreviewBackup(row.FilePath));
            _previewRow = row;
            _preview = preview;
            NotifyPreviewChanged();
        }
        catch (Exception ex) { ReportError($"读取快照详情失败：{ex.Message}"); }
        finally { SetBusy(false); }
    }

    private void NotifyPreviewChanged()
    {
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(PreviewTitle));
        OnPropertyChanged(nameof(PreviewText));
        ConfirmRestoreCommand.RaiseCanExecuteChanged();
    }

    private void ClosePreview()
    {
        _previewRow = null;
        _preview = null;
        NotifyPreviewChanged();
    }

    private void DeleteBackup(BackupRow? row)
    {
        if (row is null || !CanUseRow(row)) return;

        var confirm = MessageBox.Show(
            $"确定删除快照「{row.Title}」（{row.TimeText}）吗？\n\n删除后将无法再通过该快照文件还原对应改动。",
            "删除快照记录", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK || !CanUseRow(row)) return;

        try
        {
            ErrorText = "";
            File.Delete(row.FilePath);
            if (string.Equals(_previewRow?.FilePath, row.FilePath, StringComparison.OrdinalIgnoreCase)) ClosePreview();
            Backups.Remove(row);
            DeleteBackupCommand.RaiseCanExecuteChanged();
            _status($"已删除快照：{row.Title}");
        }
        catch (Exception ex)
        {
            ReportError($"删除快照失败：{ex.Message}");
        }
    }

    private void Restore(BackupRow? row) => ViewDetail(row);

    private async void ConfirmRestore()
    {
        if (_previewRow is not { } row || _preview is not { } preview || !CanUseRow(row)) return;
        if (preview.RequiresElevation && Native.RequireAdmin("恢复系统备份") is { } adminNeeded)
        {
            ReportError(adminNeeded);
            return;
        }

        SetBusy(true);
        ErrorText = "";
        try
        {
            await Task.Run(() => _journal.RestoreBackup(row.FilePath, preview.ContentHash));
            _status($"已还原快照：{preview.Title}（可撤销上一步）");
            ClosePreview();
        }
        catch (Exception ex) { ReportError($"还原失败：{ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async void RollbackSession()
    {
        if (!RollbackSessionCommand.CanExecute(null)) return;

        var result = MessageBox.Show(
            $"将按相反顺序撤销本次运行的全部 {_journal.SessionCount} 次改动，回到本次启动前的状态。确定继续吗？",
            "回滚本次会话",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (result != MessageBoxResult.OK || !RollbackSessionCommand.CanExecute(null)) return;

        SetBusy(true);
        ErrorText = "";
        try
        {
            var outcome = await Task.Run(_journal.RollbackSessionResult);
            if (outcome.Succeeded) _status(outcome.Message);
            else ReportError(outcome.Message);
        }
        catch (Exception ex) { ReportError($"回滚失败：{ex.Message}"); }
        finally { SetBusy(false); }
    }

    private void OpenFolder()
    {
        if (!CanInteract) return;
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            Native.OpenPath(BackupDirectory);
        }
        catch (Exception ex)
        {
            ReportError($"打开目录失败：{ex.Message}");
        }
    }
}
