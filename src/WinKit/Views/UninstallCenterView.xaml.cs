using System.Windows;
using System.Windows.Controls;
using WinKit.ViewModels;

namespace WinKit.Views;

public partial class UninstallCenterView : UserControl
{
    public UninstallCenterView() => InitializeComponent();

    private UninstallCenterViewModel? Vm => DataContext as UninstallCenterViewModel;

    /// <summary>
    /// 同时挂在 Preview 和冒泡两个通道上，理由不同：
    /// - Preview 是隧道：右侧残留表格（DataGrid）自己会处理 DragOver/Drop 做列重排，
    ///   一旦它把事件标成 Handled，冒泡阶段的处理器就收不到了，拖到表格区域会「没反应」。
    /// - 冒泡是兜底：子控件自己处理掉时至少这里还能拿到未处理的拖放。
    /// 两条通道都只在确实带着文件时才介入，避免打断表格自己的拖拽。
    /// </summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        e.Effects = Vm is { CanChangeTarget: true } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        // 同时拖多个时只取第一个：一次识别一个软件，结果区才不会串台
        Vm?.AcceptPath(paths[0]);
    }
}
