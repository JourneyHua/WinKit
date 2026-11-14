using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinKit.ViewModels;

namespace WinKit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var smokeIndex = Array.IndexOf(e.Args, "--smoke");
        if (smokeIndex >= 0)
        {
            var output = smokeIndex + 1 < e.Args.Length
                ? e.Args[smokeIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "smoke");

            RunSmoke(output);
            Shutdown(0);
            return;
        }

        if (e.Args.Contains("--manifest-probe"))
        {
            RunManifestProbe();
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ShowFatal(args.ExceptionObject as Exception);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatal(e.Exception);
        e.Handled = true;
    }

    private static void ShowFatal(Exception? ex)
    {
        MessageBox.Show(
            $"发生了未处理的错误：\n\n{ex?.Message}\n\n{ex?.StackTrace}",
            "WinKit 系统管家",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    /// <summary>
    /// 包清单可读性探测：确认管理员下能否读到 AppxManifest，决定一级菜单页是精确版还是标注版。
    /// 用法：WinKit.exe --manifest-probe
    /// </summary>
    private static void RunManifestProbe()
    {
        var log = new StringBuilder();
        log.AppendLine("=== WinKit 包清单探测 ===");
        log.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine($"提权：{(Core.Native.IsElevated() ? "是" : "否")}");
        log.AppendLine();

        foreach (var dir in new[]
                 {
                     @"C:\ProgramData\Microsoft\Windows\AppRepository\Packages",
                     @"C:\Program Files\WindowsApps"
                 })
        {
            try
            {
                var count = Directory.Exists(dir) ? Directory.GetDirectories(dir).Length : -1;
                log.AppendLine($"目录 {dir} 可枚举={Directory.Exists(dir)} 子目录数={count}");
            }
            catch (Exception ex)
            {
                log.AppendLine($"目录 {dir} 无法访问：{ex.GetType().Name} {ex.Message}");
            }
        }

        log.AppendLine();

        var journal = new Core.ChangeJournal();
        var service = new Services.NewMenuService(journal);
        var items = service.Load();
        var (found, total) = service.LastManifestStats;

        log.AppendLine($"包总数（PackagedCom）：{total}，清单读取成功：{found}");
        log.AppendLine($"以清单为准后，真实菜单项：{items.Count} 个软件");
        log.AppendLine();
        log.AppendLine("明细：");

        foreach (var item in items)
        {
            log.AppendLine($"  {item.DisplayName,-32} 作用域={(item.ScopeConfirmed ? item.ScopeText : "无法确认"),-16} 类数={item.Clsids.Count}  位置={item.Location}");
        }

        var path = Path.Combine(AppContext.BaseDirectory, "manifest-probe.txt");
        File.WriteAllText(path, log.ToString(), new UTF8Encoding(true));

        MessageBox.Show(
            $"已写入：\n{path}\n\n包总数 {total}，清单成功 {found}，真实菜单项 {items.Count} 个软件。",
            "WinKit 清单探测",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    /// <summary>
    /// 离屏渲染自检：逐个模块截图，用于开发期验证界面是否正常渲染。
    /// 用法：WinKit.exe --smoke &lt;输出目录&gt;
    /// </summary>
    private static void RunSmoke(string outputDir)
    {
        var log = new StringBuilder();

        try
        {
            Directory.CreateDirectory(outputDir);

            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false,
                Width = 1200,
                Height = 780
            };

            window.Show();

            var vm = (MainViewModel)window.DataContext;
            vm.Initialize();

            foreach (var nav in vm.NavItems)
            {
                vm.SelectedNav = nav;
                Pump();
                window.UpdateLayout();
                Pump();

                Capture(window, outputDir, nav.Key, log);

                if (vm.CurrentViewModel is ContextMenuViewModel menuVm)
                {
                    menuVm.ShowNewMenu = false;
                    Pump();
                    window.UpdateLayout();
                    Pump();
                    Capture(window, outputDir, "menu-classic", log);

                    menuVm.OnlyThirdParty = false;
                    menuVm.SearchText = "";
                    menuVm.SelectedDomain = menuVm.Domains.FirstOrDefault(d => d.Kind == Services.MenuDomainKind.Drive);
                    Pump();
                    window.UpdateLayout();
                    Pump();
                    Capture(window, outputDir, "menu-drive", log);

                    menuVm.ShowNewMenu = true;
                    menuVm.SelectedNewDomain = menuVm.NewDomains.FirstOrDefault(d => d.Kind == Services.MenuDomainKind.Drive);
                    Pump();
                    window.UpdateLayout();
                    Pump();
                    Capture(window, outputDir, "menu-new-drive", log);
                }

                if (vm.CurrentViewModel is FileLockViewModel lockVm)
                {
                    lockVm.PathText = typeof(App).Assembly.Location;
                    lockVm.Refresh();
                    Pump(2500);
                    window.UpdateLayout();
                    Pump();
                    Capture(window, outputDir, "filelock-scanned", log);
                    log.AppendLine($"  filelock: lockers={lockVm.Lockers.Count} result=\"{OneLine(lockVm.ResultText)}\"");
                }

                if (vm.CurrentViewModel is EventLogViewModel logVm)
                {
                    logVm.Refresh();
                    Pump(1500);
                    window.UpdateLayout();
                    Pump();
                    Capture(window, outputDir, "events-loaded", log);
                    log.AppendLine($"  events: groups={logVm.Groups.Count} summary=\"{OneLine(logVm.SummaryText)}\" error=\"{OneLine(logVm.ErrorText)}\"");
                }

                if (vm.CurrentViewModel is WindowsUpdateViewModel updateVm)
                {
                    updateVm.Refresh();
                    Pump(2500);
                    window.UpdateLayout();
                    Pump();
                    Capture(window, outputDir, "wupdate-loaded", log);
                    log.AppendLine($"  wupdate: installed={updateVm.Installed.Count} state=\"{OneLine(updateVm.StateText)}\" note=\"{OneLine(updateVm.NoteText)}\"");
                }

                if (vm.CurrentViewModel is NetworkViewModel networkVm)
                {
                    networkVm.Refresh();
                    Pump(6000);
                    window.UpdateLayout();
                    Pump();
                    Capture(window, outputDir, "network-loaded", log);
                    log.AppendLine($"  network: adapters={networkVm.Adapters.Count} pings={networkVm.Pings.Count} tcp={networkVm.TcpResults.Count} dns={networkVm.DnsResults.Count} summary=\"{OneLine(networkVm.SummaryText)}\" wifi=\"{OneLine(networkVm.Wifi.State)}\"");
                }

                if (vm.CurrentViewModel is OverviewViewModel overviewVm)
                {
                    overviewVm.Refresh();
                    Pump(1200);
                    window.UpdateLayout();
                    Pump();
                    Capture(window, outputDir, "overview-loaded", log);
                    var itemCount = overviewVm.Cards.Sum(c => c.Items.Count);
                    log.AppendLine($"  overview: cards={overviewVm.Cards.Count} items={itemCount} summary=\"{OneLine(overviewVm.SummaryText)}\"");
                }

                if (vm.CurrentViewModel is AppManagerViewModel appVm)
                {
                    appVm.ShowRuntimes = true;
                    Pump();
                    window.UpdateLayout();
                    Pump();
                    Capture(window, outputDir, "apps-runtimes", log);

                    appVm.ShowTools = true;
                    Pump();
                    window.UpdateLayout();
                    Pump();
                    Capture(window, outputDir, "apps-tools", log);
                    appVm.ShowTools = false;
                }

                if (vm.CurrentViewModel is SystemTweakViewModel tweakVm)
                {
                    tweakVm.SubView = "ports";
                    Pump();
                    window.UpdateLayout();
                    Pump();
                    Capture(window, outputDir, "tweaks-ports", log);

                    tweakVm.SubView = "panels";
                    Pump();
                    window.UpdateLayout();
                    Pump();
                    Capture(window, outputDir, "tweaks-panels", log);
                    tweakVm.SubView = "tweaks";
                }
            }

            window.Close();
            log.AppendLine("SMOKE OK");
        }
        catch (Exception ex)
        {
            log.AppendLine("SMOKE FAILED");
            log.AppendLine(ex.ToString());
        }

        File.WriteAllText(Path.Combine(outputDir, "smoke.log"), log.ToString(), new UTF8Encoding(true));
    }

    private static void Pump(int waitMs = 0)
    {
        if (waitMs > 0)
        {
            // 等异步加载（事件日志 / 网络探测）落地，同时保持消息泵运行
            var deadline = Environment.TickCount64 + waitMs;
            while (Environment.TickCount64 < deadline)
            {
                PumpOnce();
                Thread.Sleep(50);
            }

            return;
        }

        PumpOnce();
    }

    private static void PumpOnce()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static string OneLine(string? text)
        => string.IsNullOrWhiteSpace(text) ? "" : text.Replace("\r", " ").Replace("\n", " ").Trim();

    private static void Capture(Window window, string outputDir, string name, StringBuilder log)
    {
        var target = (FrameworkElement)window.Content;
        var width = Math.Max(1, (int)target.ActualWidth);
        var height = Math.Max(1, (int)target.ActualHeight);

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(target);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var file = Path.Combine(outputDir, $"{name}.png");
        using (var stream = File.Create(file)) encoder.Save(stream);

        log.AppendLine($"OK {name} -> {file} ({width}x{height})");
    }
}
