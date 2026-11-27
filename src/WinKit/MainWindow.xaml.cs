using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinKit.Services;
using WinKit.ViewModels;

namespace WinKit;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow() : this(new MainViewModel())
    {
    }

    internal MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _vm = viewModel;
        DataContext = _vm;

        Loaded += OnLoaded;
        SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            // Ease the layout constraint, not the resize: the rail tracks the current width
            // directly rather than jumping between two widths at a breakpoint.
            var width = Math.Clamp(190 + (e.NewSize.Width - MinWidth) * 0.25, 190, 228);
            if (NavigationColumn.Width.Value != width) NavigationColumn.Width = new GridLength(width);
        };
        Closing += OnClosing;
        Closed += (_, _) => _vm.OnWindowClosed();
        SourceInitialized += OnSourceInitialized;

        _vm.ShowSettingsRequested += ShowSettingsPopup;
        _vm.CloseRequested += () => Dispatcher.BeginInvoke(new Action(Close));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 普通启动使用 asInvoker；用户主动提权后，进程完整性级别才高于资源管理器。
        // UIPI 会直接丢掉跨完整性级别的拖放消息，表现就是「拖进来毫无反应」，也没有异常可抓。
        // 这里只放行拖放必需的消息，不整体降低窗口的消息过滤级别。
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Core.Native.AllowDragDropFromLowerIntegrity(hwnd);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowBounds();
        _vm.Initialize();
        if (App.RestartNotice is { } notice)
        {
            App.RestartNotice = null;
            _vm.Updater.ShowRestartNotice(notice);
        }
        SyncSettingsToggles();
    }

    private void ApplyWindowBounds()
    {
        var s = _vm.Settings;

        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop))
        {
            // 只在落点仍在可见区域内时恢复，避免拔掉副屏后窗口跑到屏幕外
            var left = s.WindowLeft;
            var top = s.WindowTop;
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

            if (left >= virtualLeft - 100 && top >= virtualTop - 100
                && left < virtualRight - 200 && top < virtualBottom - 200)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (s.WindowWidth >= MinWidth) Width = s.WindowWidth;
        if (s.WindowHeight >= MinHeight) Height = s.WindowHeight;

        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.Updater.IsPreparing && !_vm.Updater.IsRestartCommitted)
        {
            e.Cancel = true;
            _vm.Updater.CancelPendingRestart();
            return;
        }
        if (_vm.IsGlobalBusy)
        {
            e.Cancel = true;
            _vm.RequestCloseWhenIdle();
            return;
        }

        if (_vm.HasPendingPathDrafts && MessageBox.Show(this,
                "关闭 WinKit 会丢弃尚未应用的 PATH 更改。要丢弃更改并关闭吗？\n\n选择「否」可返回检查更改。",
                "关闭 WinKit", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            _vm.ReviewPendingPathDrafts();
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        var bounds = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);

        _vm.SaveWindowState(bounds.Width, bounds.Height, bounds.Left, bounds.Top, maximized);
    }

    // ---------- 导航选中态 ----------

    private void NavRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NavItem item }) _vm.SelectedNav = item;
    }

    private void ClearNavSearch_Click(object sender, RoutedEventArgs e) => NavSearchBox.Focus();

    private void NavSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm.HasNavSearchText)
        {
            _vm.ClearNavSearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _vm.IsNavigationEnabled && _vm.NavSearchResults.Count == 1)
        {
            _vm.SelectedNav = _vm.NavSearchResults[0];
            e.Handled = true;
        }
    }

    // ---------- 设置浮层 ----------

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsPopup();

    private void ShowSettingsPopup()
    {
        SyncSettingsToggles();
        SettingsPopup.IsOpen = true;
    }

    private void SyncSettingsToggles()
    {
        var s = _vm.Settings;
        ThemeSystem.IsChecked = s.Theme == AppTheme.System;
        ThemeLight.IsChecked = s.Theme == AppTheme.Light;
        ThemeDark.IsChecked = s.Theme == AppTheme.Dark;

        DensityCompact.IsChecked = s.Density == TableDensity.Compact;
        DensityComfortable.IsChecked = s.Density == TableDensity.Comfortable;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<AppTheme>(tag, out var mode)) return;

        _vm.Settings.Theme = mode;
        UiSettingsService.Save(_vm.Settings);
        ThemeService.Apply(mode);
        SyncSettingsToggles();
    }

    private void Density_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<TableDensity>(tag, out var density)) return;

        _vm.Settings.Density = density;
        UiSettingsService.Save(_vm.Settings);
        ApplyDensity(density);
        SyncSettingsToggles();
    }

    /// <summary>表格密度：改 Grid 样式的行高，作用于所有 DataGrid。</summary>
    public static void ApplyDensity(TableDensity density)
    {
        var app = Application.Current;
        if (app is null) return;

        var rowHeight = density == TableDensity.Compact ? 30.0 : 34.0;

        foreach (var dict in app.Resources.MergedDictionaries)
        {
            if (dict.Contains("GridRow") && dict["GridRow"] is Style rowStyle)
            {
                // 样式已封存，替换成可写副本
                var writable = new Style(typeof(System.Windows.Controls.DataGridRow), rowStyle);
                writable.Setters.Add(new Setter(System.Windows.Controls.DataGridRow.HeightProperty, rowHeight));
                dict["GridRow"] = writable;
            }
        }
    }
}
