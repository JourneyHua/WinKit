using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using WinKit.Core;
using WinKit.Services;

namespace WinKit;

public partial class App
{
    private static int RunMotionSmoke(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var log = new StringBuilder();
        try
        {
            ThemeService.Apply(AppTheme.Light);
            RunMotionChecks(outputDir, log);
            return 0;
        }
        catch (Exception ex)
        {
            log.AppendLine(ex.ToString());
            return 1;
        }
        finally { File.WriteAllText(Path.Combine(outputDir, "motion.log"), log.ToString()); }
    }

    private static void RunMotionChecks(string outputDir, StringBuilder log)
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("动效自检失败：" + message);
        }

        var chip = new ToggleButton { Content = "筛选", Style = (Style)Current.FindResource("Chip") };
        var check = new CheckBox { Content = "所选项目", IsThreeState = true, Margin = new Thickness(12, 0, 0, 0) };
        var button = new Button { Content = "继续", Style = (Style)Current.FindResource("BtnPrimary"), Margin = new Thickness(12, 0, 0, 0) };
        var arrow = new TextBlock { Text = "▲", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(chip);
        header.Children.Add(check);
        header.Children.Add(button);
        header.Children.Add(arrow);
        var status = new TextBlock { Text = "正在核验 · 真实进度", Style = (Style)Current.FindResource("Muted") };
        var progress = new ProgressBar
        {
            Height = 5, Margin = new Thickness(0, 4, 0, 10), Style = (Style)Current.FindResource("QuietProgress")
        };
        var grid = new DataGrid
        {
            Style = (Style)Current.FindResource("Grid"),
            ItemsSource = Enumerable.Range(1, 6000).Select(i => new MotionRow(i, $"项目 {i:D5} · 已读取")).ToArray()
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "序号", Binding = new Binding(nameof(MotionRow.Id)), Width = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "名称", Binding = new Binding(nameof(MotionRow.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var root = new DockPanel { ClipToBounds = true };
        foreach (var element in new FrameworkElement[] { header, status, progress })
        {
            DockPanel.SetDock(element, Dock.Top);
            root.Children.Add(element);
        }
        root.Children.Add(grid);
        var window = new Window
        {
            Title = "WinKit 动效验证", Width = 1024, Height = 680,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new Border { Padding = new Thickness(20), Child = root, ClipToBounds = true }
        };
        window.SetResourceReference(Window.BackgroundProperty, "Brush.Window");
        var oldSuppression = Motion.SuppressForVerification;
        try
        {
            window.Show();
            window.UpdateLayout();
            RenderFor(180);
            var fill = (Border)progress.Template.FindName("Fill", progress);
            var feedback = (Border)button.Template.FindName("Feedback", button);
            var checkInk = (Border)check.Template.FindName("CheckInk", check);
            var selection = (Border)chip.Template.FindName("SelectedFill", chip);
            var scroll = FindDescendant(grid, typeof(ScrollViewer)) as ScrollViewer;
            Require(scroll is not null, "找不到原生表格滚动区域");
            var buttonSize = button.RenderSize;
            var trackSize = progress.RenderSize;
            var settingsPermitMotion = Motion.IsEnabled;

            // Interrupt in the same dispatcher turn: a slow test host must not run out the first clock.
            Motion.SuppressForVerification = true;
            Motion.SetLevel(feedback, 0);
            Motion.SuppressForVerification = oldSuppression;
            Motion.SetLevel(feedback, 0.16);
            var current = feedback.Opacity;
            Motion.SetLevel(feedback, 0.07);
            if (settingsPermitMotion)
                Require(Motion.ActiveStartForVerification(feedback, UIElement.OpacityProperty) is double from
                    && Math.Abs(from - current) < 0.001, "反向操作没有接上当前透明度");
            RenderFor(210);
            Require(Math.Abs(feedback.Opacity - 0.07) < 0.001, "反向动效结束后状态不正确");
            Motion.SetLevel(feedback, 0.16);
            Motion.SuppressForVerification = true;
            Require(Motion.ActiveCount == 0 && Math.Abs(feedback.Opacity - 0.16) < 0.001, "关闭动效后没有立即落到正确状态");
            Motion.SetLevel(feedback, 0);
            Motion.SetAngle(arrow, 180);
            var toggle = (IToggleProvider)new CheckBoxAutomationPeer(check).GetPattern(PatternInterface.Toggle)!;
            toggle.Toggle();
            chip.IsChecked = true;
            progress.Value = 65;
            Require(check.IsChecked == true && checkInk.Opacity == 1 && selection.Opacity == 1
                && ((RotateTransform)arrow.RenderTransform).Angle == 180
                && Math.Abs(((ScaleTransform)fill.RenderTransform).ScaleX - 0.65) < 0.001
                && Motion.ActiveCount == 0, "减弱状态下勾选、方向或真实进度没有立即更新");
            progress.IsIndeterminate = true;
            Require(((Border)progress.Template.FindName("Busy", progress)).Visibility == Visibility.Visible && Motion.ActiveCount == 0,
                "不定进度没有静态标记");
            progress.IsIndeterminate = false;
            check.IsChecked = null;
            Require(((Border)check.Template.FindName("Mixed", check)).Visibility == Visibility.Visible, "没有保留三态语义");

            (double[] Frames, int Steps) Measure(bool suppress)
            {
                Motion.SuppressForVerification = true;
                scroll!.ScrollToTop();
                Motion.SetLevel(feedback, 0);
                chip.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                Motion.SetAngle(arrow, 0);
                progress.Value = 0;
                RenderFor(180);
                Motion.SuppressForVerification = suppress;
                var steps = 0;
                var frames = RenderFor(1350, tick =>
                {
                    steps = tick;
                    Motion.SetLevel(feedback, tick % 2 == 0 ? 0.16 : 0);
                    chip.SetCurrentValue(ToggleButton.IsCheckedProperty, tick % 2 == 0);
                    Motion.SetAngle(arrow, tick % 2 == 0 ? 0 : 180);
                    progress.Value = tick % 24 * 4;
                    scroll.ScrollToVerticalOffset(tick * 5);
                });
                return (frames, steps);
            }

            // Reverse the order in the second pair so warmup does not always favor one mode.
            var reducedFirst = Measure(true);
            var defaultFirst = Measure(false);
            var defaultSecond = Measure(false);
            var reducedSecond = Measure(true);
            Motion.SuppressForVerification = false;
            var reducedFrames = reducedFirst.Frames.Concat(reducedSecond.Frames).ToArray();
            var defaultFrames = defaultFirst.Frames.Concat(defaultSecond.Frames).ToArray();
            RenderFor(220);
            Require(Motion.ActiveCount == 0, "动效结束后仍有时钟运行");
            Require(scroll!.VerticalOffset > 0, "长表格没有滚动");
            Require(button.RenderSize == buttonSize && progress.RenderSize == trackSize, "动效改变了按钮命中区域或进度轨道布局");
            grid.Columns[0].Width = new DataGridLength(132);
            window.UpdateLayout();
            Require(Math.Abs(grid.Columns[0].ActualWidth - 132) < 1, "调整列宽没有即时跟随");
            var visibleRows = VisualDescendants(grid).OfType<DataGridRow>().Count();
            Require(visibleRows is > 0 and < 160 && grid.EnableRowVirtualization, "长表格未保持回收虚拟化");
            Capture(window, outputDir, "motion-narrow-light", log);

            ThemeService.Apply(AppTheme.Dark);
            window.Width = 1440;
            window.Height = 820;
            window.UpdateLayout();
            RenderFor(180);
            Capture(window, outputDir, "motion-wide-dark", log);
            var dpi = VisualTreeHelper.GetDpi(root);
            log.AppendLine($"  ux-motion: interrupt=settled reduced=instant layout=stable rows={visibleRows}/6000 colors=light/dark dpi={dpi.PixelsPerInchX:0} system={settingsPermitMotion} result=OK");
            log.AppendLine($"  motion-render: tier={RenderCapability.Tier >> 16} sampled-windows=2x1350ms per mode reduced=steps:{reducedFirst.Steps + reducedSecond.Steps},{FrameSummary(reducedFrames)} default=steps:{defaultFirst.Steps + defaultSecond.Steps},{FrameSummary(defaultFrames)} (WPF callbacks, not display presents)");
        }
        finally
        {
            Motion.SuppressForVerification = true;
            window.Close();
            ThemeService.Apply(AppTheme.Light);
            Motion.SuppressForVerification = oldSuppression;
        }
    }

    private static void RunShellMotionChecks(MainWindow window, string outputDir, StringBuilder log)
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("主窗口动效自检失败：" + message);
        }

        var root = (FrameworkElement)window.Content;
        var vm = (ViewModels.MainViewModel)window.DataContext;
        var before = (window.Left, window.Top, window.Width, window.Height, RootWidth: root.Width, RootHeight: root.Height);
        var oldSuppression = Motion.SuppressForVerification;
        try
        {
            root.Width = double.NaN;
            root.Height = double.NaN;
            window.Left = SystemParameters.WorkArea.Left + 8;
            window.Top = SystemParameters.WorkArea.Top + 8;
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.UpdateLayout();
            var narrowRail = window.NavigationColumn.ActualWidth;
            Require(Math.Abs(narrowRail - 190) < 1, "窄窗口侧栏没有跟随窗口大小");

            var overview = vm.NavItems.First(item => item.Key == "overview");
            var backup = vm.NavItems.First(item => item.Key == "backup");
            NavigateSmoke(window, vm, overview);
            Motion.SuppressForVerification = true;
            window.NavSearchBox.SetCurrentValue(TextBox.TextProperty, backup.Title);
            Pump();
            Require(vm.NavSearchResults.Contains(backup), "窄窗口工具搜索无法找到备份页");
            InvokeButton(FindVisibleButton(window, b => ReferenceEquals(b.DataContext, backup))
                ?? throw new InvalidOperationException("找不到搜索结果的导航按钮"));
            Require(ReferenceEquals(vm.CurrentViewModel, backup.ViewModel) && window.PageHeading.Opacity == 1,
                "减少动态效果后，切页没有立即显示新标题和内容");
            window.NavSearchBox.SetCurrentValue(TextBox.TextProperty, string.Empty);
            Pump();
            Capture(window, outputDir, "shell-narrow-light", log);

            void CheckSettings()
            {
                InvokeButton(window.SettingsButton);
                Require(window.SettingsPopup.IsOpen && window.SettingsPopup.Child.IsVisible
                    && window.ThemeDark.IsVisible && window.ThemeDark.IsEnabled,
                    "设置弹层打开后主题选项不可用");
                window.SettingsPopup.IsOpen = false;
                Require(!window.SettingsPopup.IsOpen, "关闭设置没有立即更新状态");
                // WPF releases the popup's native window on the next dispatcher turn.
                WaitWhileBusy(() => window.SettingsPopup.Child.IsVisible, 1500);
                Require(!window.SettingsPopup.Child.IsVisible, "设置关闭后仍保留可见内容");
            }

            CheckSettings();
            Motion.SuppressForVerification = false;
            window.Width = 1440;
            window.Height = 820;
            ThemeService.Apply(AppTheme.Dark);
            window.UpdateLayout();
            Require(Math.Abs(window.NavigationColumn.ActualWidth - 228) < 1, "宽窗口侧栏没有恢复常规宽度");
            CheckSettings();
            NavigateSmoke(window, vm, overview);
            RenderFor(180);
            Capture(window, outputDir, "shell-wide-dark", log);
            log.AppendLine($"  ux-motion-shell: narrow={window.MinWidth:0} rail={narrowRail:0} wide={window.ActualWidth:0} rail={window.NavigationColumn.ActualWidth:0} search=clicked settings=open/close reduced=instant result=OK");
        }
        finally
        {
            window.SettingsPopup.IsOpen = false;
            Motion.SuppressForVerification = true;
            window.NavSearchBox.SetCurrentValue(TextBox.TextProperty, string.Empty);
            ThemeService.Apply(AppTheme.Light);
            (window.Left, window.Top, window.Width, window.Height, root.Width, root.Height) = before;
            window.UpdateLayout();
            Motion.SuppressForVerification = oldSuppression;
        }
    }

    private static double[] RenderFor(int milliseconds, Action<int>? apply = null)
    {
        var samples = new List<double>();
        var stopwatch = Stopwatch.StartNew();
        TimeSpan? lastRender = null;
        double? lastMs = null;
        var frame = new DispatcherFrame();
        var ticks = 0;
        void OnRendering(object? sender, EventArgs args)
        {
            if (args is not RenderingEventArgs render || render.RenderingTime == lastRender) return;
            var now = stopwatch.Elapsed.TotalMilliseconds;
            if (lastMs is { } previous) samples.Add(now - previous);
            lastRender = render.RenderingTime;
            lastMs = now;
        }
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(42) };
        timer.Tick += (_, _) => { if (stopwatch.ElapsedMilliseconds < milliseconds) apply?.Invoke(++ticks); };
        var stop = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        stop.Tick += (_, _) => frame.Continue = false;
        CompositionTarget.Rendering += OnRendering;
        try
        {
            if (apply is not null) timer.Start();
            stop.Start();
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            stop.Stop();
            timer.Stop();
            CompositionTarget.Rendering -= OnRendering;
        }
        return samples.ToArray();
    }

    private static string FrameSummary(double[] frames)
    {
        if (frames.Length < 4) return "unavailable";
        Array.Sort(frames);
        double Percentile(double p) => frames[(int)Math.Ceiling(p * frames.Length) - 1];
        return string.Create(CultureInfo.InvariantCulture,
            $"n:{frames.Length},p50:{Percentile(0.5):0.0}ms,p95:{Percentile(0.95):0.0}ms,over33ms:{frames.Count(x => x > 33.4)}");
    }

    private sealed record MotionRow(int Id, string Name);
}
