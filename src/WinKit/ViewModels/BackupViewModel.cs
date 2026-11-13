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
    public string TimeText { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string KeyCountText { get; init; } = "";
}

public sealed class BackupViewModel : ObservableObject, IModule
{
    private readonly ChangeJournal _journal;
    private readonly Action<string> _status;

    public BackupViewModel(ChangeJournal journal, Action<string> status)
    {
        _journal = journal;
        _status = status;

        Backups = new ObservableCollection<BackupRow>();
        RefreshCommand = new RelayCommand(Refresh);
        RestoreCommand = new RelayCommand(p => Restore(p as BackupRow));
        OpenFolderCommand = new RelayCommand(OpenFolder);
        RollbackSessionCommand = new RelayCommand(RollbackSession);
    }

    public ObservableCollection<BackupRow> Backups { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand RollbackSessionCommand { get; }

    public string BackupDirectory => ChangeJournal.BackupDirectory;

    public string SessionText => _journal.CanUndo
        ? $"本次运行有 {_journal.SessionCount} 次改动可回滚"
        : "本次运行尚无改动";

    public void Refresh()
    {
        Backups.Clear();

        foreach (var file in _journal.ListBackups())
        {
            var title = file.Name;
            var keyCount = "";

            try
            {
                var snapshot = JsonSerializer.Deserialize<OperationSnapshot>(File.ReadAllText(file.FullName));
                if (snapshot is not null)
                {
                    title = snapshot.Title;
                    keyCount = snapshot.Files.Count > 0 && snapshot.Keys.Count == 0
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

            Backups.Add(new BackupRow
            {
                FilePath = file.FullName,
                Title = title,
                TimeText = file.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"),
                SizeText = ThisPcService.FormatSize(file.Length),
                KeyCountText = keyCount
            });
        }

        OnPropertyChanged(nameof(SessionText));
    }

    private void Restore(BackupRow? row)
    {
        if (row is null) return;

        var result = MessageBox.Show(
            $"将把注册表恢复到「{row.Title}」这次操作之前的状态。\n\n当前与该操作相关的改动会被覆盖。确定继续吗？",
            "从快照还原",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        try
        {
            _journal.RestoreBackup(row.FilePath);
            _status($"已还原快照：{row.Title}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"还原失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RollbackSession()
    {
        if (!_journal.CanUndo)
        {
            MessageBox.Show("本次运行还没有可回滚的改动。", "WinKit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"将按相反顺序撤销本次运行的全部 {_journal.SessionCount} 次改动，回到本次启动前的状态。确定继续吗？",
            "回滚本次会话",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        var message = _journal.RollbackSession();
        _status(message ?? "没有可回滚的改动");
        Refresh();
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(ChangeJournal.BackupDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = ChangeJournal.BackupDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开目录失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
