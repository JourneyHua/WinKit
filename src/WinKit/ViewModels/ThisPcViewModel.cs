using System.Collections.ObjectModel;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class ThisPcViewModel : ObservableObject, IModule
{
    private readonly ThisPcService _service;
    private readonly Action<string> _status;
    private bool _isLoading;

    public ThisPcViewModel(ChangeJournal journal, Action<string> status)
    {
        _service = new ThisPcService(journal);
        _status = status;

        Folders = new ObservableCollection<ThisPcItem>();
        Nodes = new ObservableCollection<ThisPcItem>();
        Drives = new ObservableCollection<ThisPcItem>();
        RefreshCommand = new RelayCommand(Refresh);
        CopyPathCommand = new RelayCommand(p => CopyPath(p as ThisPcItem));
    }

    public ObservableCollection<ThisPcItem> Folders { get; }
    public ObservableCollection<ThisPcItem> Nodes { get; }
    public ObservableCollection<ThisPcItem> Drives { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CopyPathCommand { get; }

    private void CopyPath(ThisPcItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Detail)) return;
        try
        {
            Clipboard.SetText(item.Detail);
            _status($"已复制路径：{item.Detail}");
        }
        catch (Exception ex)
        {
            _status("复制失败：" + ex.Message);
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public void Refresh()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            Folders.Clear();
            foreach (var item in _service.LoadFolders())
            {
                item.VisibilityRequested = OnFolderToggled;
                Folders.Add(item);
            }

            Nodes.Clear();
            foreach (var item in _service.LoadNodes())
            {
                item.VisibilityRequested = OnNodeToggled;
                Nodes.Add(item);
            }

            Drives.Clear();
            foreach (var item in _service.LoadDrives())
            {
                item.VisibilityRequested = OnDriveToggled;
                Drives.Add(item);
            }
        }
        catch (Exception ex)
        {
            _status("加载此电脑设置失败：" + ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OnFolderToggled(ThisPcItem item, bool visible)
    {
        var guid = item.Id.Replace("folder:", "");
        try
        {
            _service.SetFolderVisible(guid, visible);
            _status($"{(visible ? "已显示" : "已隐藏")}：{item.Title}");
        }
        catch (Exception ex)
        {
            item.IsVisible = !visible;
            MessageBox.Show($"操作失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnNodeToggled(ThisPcItem item, bool visible)
    {
        var clsid = item.Id.Replace("node:", "");
        try
        {
            _service.SetNodeVisible(clsid, visible);
            _status($"{(visible ? "已显示" : "已隐藏")}：{item.Title}");
        }
        catch (Exception ex)
        {
            item.IsVisible = !visible;
            MessageBox.Show($"操作失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDriveToggled(ThisPcItem item, bool visible)
    {
        var letter = item.Id.Replace("drive:", "")[0];
        try
        {
            _service.SetDriveVisible(letter, visible);
            _status($"{(visible ? "已显示" : "已隐藏")}驱动器 {letter}:");
        }
        catch (Exception ex)
        {
            item.IsVisible = !visible;
            MessageBox.Show($"操作失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
