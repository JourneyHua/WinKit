using System.Windows;
using System.Windows.Controls;
using WinKit.ViewModels;

namespace WinKit.Views;

public partial class UninstallCenterView : UserControl
{
    public UninstallCenterView() => InitializeComponent();

    private UninstallCenterViewModel? Vm => DataContext as UninstallCenterViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        // 同时拖多个时只取第一个：一次识别一个软件，结果区才不会串台
        Vm?.AcceptPath(paths[0]);
    }
}