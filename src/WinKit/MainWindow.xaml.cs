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
        Closing += OnClosing;
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
        SyncSettingsToggles();
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.SelectedNav)) SyncNavSelection();
        };

        SyncNavSelection();
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
        if (_vm.IsGlobalBusy)
        {
            e.Cancel = true;
            _vm.RequestCloseWhenIdle();
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        var bounds = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);

        _vm.SaveWindowState(bounds.Width, bounds.Height, bounds.Left, bounds.Top, maximized);
    }

    // ---------- 导航选中态 ----------

    private void NavRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NavItem item })
        {
            _vm.SelectedNav = item;
            SyncNavSelection();
        }
    }

    /// <summary>NavItem 不是 ObservableObject，选中态由窗口手动同步（避免给 21 个模型加 UI 属性）。</summary>
    private void SyncNavSelection()
    {
        foreach (var (button, item) in FindNavContainers(this))
        {
            var selected = ReferenceEquals(item, _vm.SelectedNav);
            if (selected) button.SetResourceReference(BackgroundProperty, "Brush.AccentSoft");
            else button.ClearValue(BackgroundProperty);
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "当前页面" : "");
        }
    }

    private static List<(Button Button, NavItem Item)> FindNavContainers(DependencyObject root)
    {
        var result = new List<(Button, NavItem)>();
        Walk(root, result);
        return result;

        static void Walk(DependencyObject node, List<(Button, NavItem)> acc)
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);

                if (child is Button { DataContext: NavItem item } button
                    && button.Name == "NavRowButton")
                {
                    acc.Add((button, item));
                }

                Walk(child, acc);
            }
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
