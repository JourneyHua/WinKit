using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinKit.Core;
using WinKit.ViewModels;

namespace WinKit;

public partial class App : Application
{
    /// <summary>本程序认识的命令行开关。不在这张表里的开关一律当误用处理。</summary>
    private static readonly string[] KnownSwitches =
    [
        "--smoke",
        "--manifest-probe",
        "--maintain",
        "--help",
        "-?"
    ];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 这里以前没有 else 分支：随手传一个不认识的开关——最典型的是把 tools/Probe 的
        // --selftest 当成 WinKit.exe 的参数——不会报错，会静默启动完整 GUI 并弹 UAC，
        // 让人以为工具坏了或是自测跑起来了。宁可明确拒绝，也不要糊里糊涂跑起来。
        var badSwitches = e.Args
            .Where(a => a.StartsWith("-", StringComparison.Ordinal) && !KnownSwitches.Contains(a))
            .ToList();

        if (badSwitches.Count > 0)
        {
            ShowUsage(badSwitches);
            Shutdown(2);
            return;
        }

        if (e.Args.Contains("--help") || e.Args.Contains("-?"))
        {
            ShowUsage([]);
            Shutdown(0);
            return;
        }

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

        // 无人值守维护：由计划任务拉起，跑完即退，不建窗口
        if (e.Args.Contains("--maintain"))
        {
            var crashFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinKit", "Logs", "maintain-crash.txt");

            int exitCode;
            try
            {
                // 必须整体放到线程池上：维护里是同步调异步（GetResult），
                // 在 UI 线程上等会与 await 的 dispatcher 续体互相等死。
                exitCode = Task.Run(() => Services.MaintenanceService.RunMaintenance())
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                // 双保险：既写维护日志，也单独落一份崩溃文件（日志本身可能就没建起来）
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(crashFile)!);
                    File.WriteAllText(crashFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{ex}");
                }
                catch (Exception)
                {
                    // 崩溃文件也写不了就只能放弃
                }

                Services.MaintenanceService.AppendLog($"  [FAILED] 未处理异常：{ex}");
                exitCode = 1;
            }

            Shutdown(exitCode);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ShowFatal(args.ExceptionObject as Exception);

        // 启动时应用主题与表格密度（必须在 MainWindow 解析资源之前）
        var settings = Services.UiSettingsService.Load();
        Services.ThemeService.Apply(settings.Theme);
        WinKit.MainWindow.ApplyDensity(settings.Density);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatal(e.Exception);
        e.Handled = true;
    }

    /// <summary>
    /// 兜底异常处理：写日志 + 提示一句人话。
    /// 以前是把整个 StackTrace 塞进 MessageBox 然后 e.Handled = true —— 用户看不懂、复制不出来，
    /// 关掉窗口就什么都没留下，等于把故障现场销毁了。现在完整堆栈落盘，框里只给人话和日志路径。
    /// </summary>
    private static void ShowFatal(Exception? ex)
    {
        var logPath = "";

        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinKit", "Logs");
            Directory.CreateDirectory(logDir);
            logPath = Path.Combine(logDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(
                logPath,
                $"=== WinKit 未处理异常 ==={Environment.NewLine}" +
                $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                $"版本：{typeof(App).Assembly.GetName().Version}{Environment.NewLine}" +
                $"提权：{(Core.Native.IsElevated() ? "是" : "否")}{Environment.NewLine}{Environment.NewLine}" +
                (ex?.ToString() ?? "（异常对象为 null）"),
                new UTF8Encoding(true));
        }
        catch (Exception)
        {
            // 日志都写不进去（磁盘满、目录被锁）就只能退回到只弹框
        }

        var body = $"WinKit 遇到了一个未处理的错误，功能可能已经不正常。{Environment.NewLine}{Environment.NewLine}" +
                   $"错误：{ex?.Message ?? "未知"}";

        body += string.IsNullOrEmpty(logPath)
            ? $"{Environment.NewLine}{Environment.NewLine}（详细堆栈写入日志失败，可能是日志目录不可写）"
            : $"{Environment.NewLine}{Environment.NewLine}完整堆栈已写入：{Environment.NewLine}{logPath}";

        MessageBox.Show(body, "WinKit 系统管家", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>
    /// 打印用法。GUI 程序没有控制台，双击误传参数时只写 stderr 等于什么都没说，
    /// 所以除了落一份日志文件，还要弹一次框。
    /// </summary>
    private static void ShowUsage(IReadOnlyCollection<string> badSwitches)
    {
        var sb = new StringBuilder();

        if (badSwitches.Count > 0)
        {
            sb.AppendLine($"无法识别的参数：{string.Join(" ", badSwitches)}");

            // 这条误用太常见了（README 与旧文档都容易让人以为 --selftest 是 exe 参数），单独说清楚
            if (badSwitches.Contains("--selftest"))
            {
                sb.AppendLine();
                sb.AppendLine("--selftest 是 tools/Probe 的参数，不是 WinKit.exe 的：");
                sb.AppendLine("  dotnet run --project tools/Probe -- --selftest");
                sb.AppendLine("或直接运行它的产物 WinKitProbe.exe --selftest。");
            }

            sb.AppendLine();
        }

        sb.AppendLine("可用参数：");
        sb.AppendLine("  （无参数）            正常启动");
        sb.AppendLine("  --smoke <输出目录>    界面离屏自检：逐模块渲染 PNG 后退出");
        sb.AppendLine("  --manifest-probe      枚举菜单项 / 快速访问 / 驱动器并输出报告");
        sb.AppendLine("  --maintain            无人值守维护（计划任务用），跑完即退");
        sb.AppendLine("  --help                显示这段说明");

        var text = sb.ToString();

        // 从计划任务或脚本里误调时没有人在屏幕前，日志是唯一的线索
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinKit", "Logs");
            Directory.CreateDirectory(logDir);
            File.WriteAllText(Path.Combine(logDir, "command-line.txt"), text, new UTF8Encoding(true));
        }
        catch (Exception)
        {
            // 日志写不了不影响把话说到用户眼前，下面还有弹框
        }

        MessageBox.Show(
            text,
            "WinKit 系统管家",
            MessageBoxButton.OK,
            badSwitches.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
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

        Native.OpenPath(path);
    }

    /// <summary>
    /// 离屏渲染自检：逐个模块截图，用于开发期验证界面是否正常渲染。
    /// 用法：WinKit.exe --smoke &lt;输出目录&gt;
    /// </summary>
    private static void RunSmoke(string outputDir)
    {
        var log = new StringBuilder();

        // 目录和日志必须在任何可能失败的操作之前就绪：
        // CI 的无交互桌面会话里窗口可能起不来，但数据 dump 仍要能落盘。
        Directory.CreateDirectory(outputDir);
        var logPath = Path.Combine(outputDir, "smoke.log");

        try
        {
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false,
                Width = 1200,
                Height = 780
            };

            var shown = false;
            try
            {
                window.Show();
                shown = true;
            }
            catch (Exception ex)
            {
                // 无桌面会话时窗口起不来：不中断，继续走数据 dump（CI 只断言数据）
                log.AppendLine($"WARN 窗口无法显示，跳过渲染：{ex.GetType().Name} {OneLine(ex.Message)}");
            }

            // 布局尺寸必须钉死，不能依赖窗口实际大小：窗口可用尺寸受屏幕分辨率
            // 与「记住窗口大小」影响——CI runner 是 1024x768 的虚拟屏幕（内容区只有
            // 1028x749），本机是 1280x820，同一份列宽配置在两处量出的视口分别是
            // 733 和 969，columns-* 断言就没法通用。给内容根元素固定尺寸，
            // 让它在任何屏幕上按同一尺寸排版，超出窗口的部分被裁掉（离屏渲染
            // 只关心布局尺寸，不关心可见性）。
            if (shown && window.Content is FrameworkElement smokeRoot)
            {
                smokeRoot.Width = 1264;
                smokeRoot.Height = 781;
                window.UpdateLayout();
                Pump();
            }

            // 提权进程要接住资源管理器的拖放，必须先放行 UIPI 的三个拖放消息。
            // 这里在 smoke 窗口上真的调一次：返回非 0 就是放行失败（打的是 Win32
            // 错误码，5=拒绝访问 / 87=句柄无效），用户那边会继续表现为「拖进来毫无反应」。
            var dragDropFilterError = 0;
            if (shown)
            {
                try
                {
                    dragDropFilterError = Core.Native.AllowDragDropFromLowerIntegrity(
                        new System.Windows.Interop.WindowInteropHelper(window).Handle);
                }
                catch (Exception)
                {
                    dragDropFilterError = -1;
                }
            }

            var vm = (MainViewModel)window.DataContext;
            vm.Initialize();
            log.AppendLine($"  nav: groups={vm.NavGroups.Count} items={vm.NavItems.Count} expanded={vm.NavGroups.Count(g => g.IsExpanded)}");

            foreach (var nav in vm.NavItems)
            {
                vm.SelectedNav = nav;
                Pump();

                if (shown)
                {
                    try
                    {
                        UpdateLayoutSafe(window);
                        Pump();
                        Capture(window, outputDir, nav.Key, log);
                    }
                    catch (Exception ex)
                    {
                        log.AppendLine($"WARN 渲染 {nav.Key} 失败：{OneLine(ex.Message)}");
                    }
                }

                if (vm.CurrentViewModel is ContextMenuViewModel menuVm)
                {
                    menuVm.ShowNewMenu = false;
                    menuVm.OnlyThirdParty = false;
                    menuVm.SearchText = "";
                    menuVm.SelectedDomain = menuVm.Domains.FirstOrDefault(d => d.Kind == Services.MenuDomainKind.Drive);
                    Pump();

                    if (shown)
                    {
                        UpdateLayoutSafe(window);
                        Pump();
                        Capture(window, outputDir, "menu-classic", log);
                        Capture(window, outputDir, "menu-drive", log);
                    }

                    menuVm.ShowNewMenu = true;
                    menuVm.SelectedNewDomain = menuVm.NewDomains.FirstOrDefault(d => d.Kind == Services.MenuDomainKind.Drive);
                    Pump();

                    if (shown)
                    {
                        UpdateLayoutSafe(window);
                        Pump();
                        Capture(window, outputDir, "menu-new-drive", log);
                    }
                }

                if (vm.CurrentViewModel is FileLockViewModel lockVm)
                {
                    lockVm.PathText = typeof(App).Assembly.Location;
                    lockVm.Refresh();
                    Pump(2500);
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "filelock-scanned", log);
                    log.AppendLine($"  filelock: lockers={lockVm.Lockers.Count} result=\"{OneLine(lockVm.ResultText)}\"");
                }

                if (vm.CurrentViewModel is EventLogViewModel logVm)
                {
                    logVm.Refresh();
                    Pump(1500);
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "events-loaded", log);
                    log.AppendLine($"  events: groups={logVm.Groups.Count} summary=\"{OneLine(logVm.SummaryText)}\" error=\"{OneLine(logVm.ErrorText)}\"");
                }

                if (vm.CurrentViewModel is WindowsUpdateViewModel updateVm)
                {
                    updateVm.Refresh();
                    Pump(2500);
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "wupdate-loaded", log);
                    log.AppendLine($"  wupdate: installed={updateVm.Installed.Count} state=\"{OneLine(updateVm.StateText)}\" note=\"{OneLine(updateVm.NoteText)}\"");
                }

                if (vm.CurrentViewModel is NetworkViewModel networkVm)
                {
                    networkVm.Refresh();
                    Pump(6000);
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "network-loaded", log);
                    log.AppendLine($"  network: adapters={networkVm.Adapters.Count} pings={networkVm.Pings.Count} tcp={networkVm.TcpResults.Count} dns={networkVm.DnsResults.Count} summary=\"{OneLine(networkVm.SummaryText)}\" wifi=\"{OneLine(networkVm.Wifi.State)}\"");
                }

                if (vm.CurrentViewModel is OverviewViewModel overviewVm)
                {
                    overviewVm.Refresh();
                    Pump(1200);
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "overview-loaded", log);
                    var itemCount = overviewVm.Cards.Sum(c => c.Items.Count);
                    log.AppendLine($"  overview: cards={overviewVm.Cards.Count} items={itemCount} summary=\"{OneLine(overviewVm.SummaryText)}\"");
                }

                if (vm.CurrentViewModel is FileAssocViewModel assocVm)
                {
                    assocVm.Refresh();
                    Pump(1500);
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "assoc-loaded", log);
                    log.AppendLine($"  assoc: items={assocVm.Items.Count} summary=\"{OneLine(assocVm.SummaryText)}\"");
                }

                if (vm.CurrentViewModel is DiffViewModel diffVm)
                {
                    diffVm.LeftPath = Path.Combine(AppContext.BaseDirectory);
                    diffVm.RightPath = Path.Combine(AppContext.BaseDirectory);
                    diffVm.Refresh();
                    Pump(2500);
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "diff-loaded", log);
                    log.AppendLine($"  diff: entries={diffVm.Entries.Count} summary=\"{OneLine(diffVm.SummaryText)}\" error=\"{OneLine(diffVm.ErrorText)}\"");
                }

                if (vm.CurrentViewModel is DuplicateViewModel dupVm)
                {
                    dupVm.Roots.Clear();
                    dupVm.Roots.Add(Path.Combine(AppContext.BaseDirectory));
                    dupVm.MinSizeText = "1 B";
                    dupVm.Refresh();
                    Pump(4000);
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "duplicates-loaded", log);
                    log.AppendLine($"  duplicates: groups={dupVm.Groups.Count} summary=\"{OneLine(dupVm.SummaryText)}\"");
                }

                if (vm.CurrentViewModel is DiskHealthViewModel diskVm)
                {
                    diskVm.Refresh();
                    Pump(6000);
                    UpdateLayoutSafe(window);
                    Pump();
                    if (shown) Capture(window, outputDir, "diskhealth-loaded", log);
                    log.AppendLine($"  diskhealth: disks={diskVm.Disks.Count} volumes={diskVm.Volumes.Count} error=\"{OneLine(diskVm.ErrorText)}\" summary=\"{OneLine(diskVm.SummaryText)}\"");
                }

                if (vm.CurrentViewModel is SystemRestoreViewModel restoreVm)
                {
                    restoreVm.Refresh();
                    WaitWhileBusy(() => restoreVm.IsBusy, 40000);
                    UpdateLayoutSafe(window);
                    Pump();
                    if (shown) Capture(window, outputDir, "restore-loaded", log);

                    // 还原点要靠管理员权限才读得到。冒烟进程是 asInvoker，读不到属正常，
                    // 所以这里只要求「要么列出还原点，要么把原因说清楚」：有错误信息算说清楚，
                    // 系统保护没开也算说清楚，「保护开着但一个还原点都没有」则必须由摘要
                    // 明确告知（含「还没有」），只有列表空、错误空、摘要也是干巴巴一句时才判失败。
                    var restoreExplained = restoreVm.SummaryText.Contains("还没有", StringComparison.Ordinal);
                    var restoreOk = restoreVm.Points.Count > 0
                        || !string.IsNullOrWhiteSpace(restoreVm.ErrorText)
                        || restoreExplained;
                    log.AppendLine($"  restore: points={restoreVm.Points.Count} protection=\"{OneLine(restoreVm.ProtectionText)}\" "
                        + $"enabled={restoreVm.ProtectionEnabled} frequency=\"{OneLine(restoreVm.FrequencyText)}\" "
                        + $"summary=\"{OneLine(restoreVm.SummaryText)}\" error=\"{OneLine(restoreVm.ErrorText)}\" "
                        + $"result={(restoreOk ? "OK" : "FAILED")}");
                }

                if (vm.CurrentViewModel is ProcessManagerViewModel procVm)
                {
                    procVm.Refresh();
                    WaitWhileBusy(() => procVm.IsBusy, 40000);
                    UpdateLayoutSafe(window);
                    Pump();
                    if (shown) Capture(window, outputDir, "processes-loaded", log);

                    // 进程枚举不依赖权限，必须真的拿到进程；拿不到就是功能坏了。
                    var selfListed = procVm.Items.Any(p => p.Pid == Environment.ProcessId);
                    log.AppendLine($"  processes: count={procVm.Items.Count} self={selfListed} "
                        + $"summary=\"{OneLine(procVm.SummaryText)}\" error=\"{OneLine(procVm.ErrorText)}\" "
                        + $"result={(procVm.Items.Count > 0 && selfListed ? "OK" : "FAILED")}");
                }

                if (vm.CurrentViewModel is SmartAttributeViewModel smartVm)
                {
                    smartVm.Refresh();
                    WaitWhileBusy(() => smartVm.IsBusy, 40000);
                    UpdateLayoutSafe(window);
                    Pump();
                    if (shown) Capture(window, outputDir, "smart-loaded", log);

                    // SMART 原始属性同样需要管理员透传。asInvoker 下读不到很正常，
                    // 但服务层必须把原因说出来（读到磁盘但控制器不透传时，每块盘自己也有 Error）；
                    // 「一块盘都没有、也没有任何说明」才是真的坏。
                    var smartOk = smartVm.Attributes.Count > 0
                        || !string.IsNullOrWhiteSpace(smartVm.ErrorText)
                        || smartVm.Disks.Any(d => !string.IsNullOrWhiteSpace(d.Error));
                    log.AppendLine($"  smart: disks={smartVm.Disks.Count} attributes={smartVm.Attributes.Count} "
                        + $"summary=\"{OneLine(smartVm.SummaryText)}\" error=\"{OneLine(smartVm.ErrorText)}\" result={(smartOk ? "OK" : "FAILED")}");
                }

                if (vm.CurrentViewModel is EnvironmentViewModel envVm)
                {
                    envVm.Refresh();
                    Pump(1200);
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "env-loaded", log);
                    log.AppendLine($"  env: vars={envVm.Vars.Count} pathEntries={envVm.PathEntries.Count} summary=\"{OneLine(envVm.SummaryText)}\"");
                }

                if (vm.CurrentViewModel is ReportViewModel reportVm)
                {
                    reportVm.Refresh();
                    Pump(14000);
                    UpdateLayoutSafe(window);
                    Pump();
                    if (shown) Capture(window, outputDir, "report-loaded", log);
                    log.AppendLine($"  report: sections={reportVm.Sections.Count} score={reportVm.ScoreText} grade={reportVm.GradeText} summary=\"{OneLine(reportVm.SummaryText)}\" task=\"{OneLine(reportVm.MaintenanceText)}\"");
                }

                if (vm.CurrentViewModel is AppManagerViewModel appVm)
                {
                    // 默认停在「已安装应用」时，卸载清理面板必须是隐藏的
                    // （v1.8.0 的 bug 就是它一直可见，叠在应用列表上面）
                    Pump();
                    UpdateLayoutSafe(window);
                    Pump();
                    log.AppendLine($"  uninstall-hidden: {DescribeView(window, typeof(Views.UninstallCenterView))}");
                    log.AppendLine(ColumnResizeCheck(window));
                    log.AppendLine("  columns-apps: " + ColumnLayoutCheck(window));

                    appVm.ShowRuntimes = true;
                    Pump();
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "apps-runtimes", log);
                    log.AppendLine("  columns-runtimes: " + ColumnLayoutCheck(window));

                    appVm.ShowTools = true;
                    Pump();
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "apps-tools", log);
                    log.AppendLine("  columns-tools: " + ColumnLayoutCheck(window));
                    appVm.ShowTools = false;

                    // 卸载残留清理：喂一个真实安装目录，走完整的识别链路
                    var smokeTarget = SmokeUninstallTarget(outputDir);
                    appVm.ShowUninstallCenter = true;
                    appVm.UninstallCenter.AcceptPath(smokeTarget);
                    Pump(1500);
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "apps-uninstall", log);
                    log.AppendLine($"  uninstall: kind=\"{OneLine(appVm.UninstallCenter.TargetKindText)}\" matches={appVm.UninstallCenter.Matches.Count} candidates={appVm.UninstallCenter.Candidates.Count} residuals={appVm.UninstallCenter.Residuals.Count} summary=\"{OneLine(appVm.UninstallCenter.IdentifySummary)}\"");
                    log.AppendLine($"  uninstall-view: {DescribeView(window, typeof(Views.UninstallCenterView))}");

                    // 拖放这条路没法在离屏自检里模拟（要跨进程从资源管理器拖进来），
                    // 只能验证两件可查的事：视图确实允许放置；「粘贴路径」这条替代入口
                    // 能从剪贴板识别出同一个目录。剪贴板在无交互会话里可能不可用，
                    // 那就只打 WARN，不让它变成假失败。
                    var uninstallView = FindDescendant(window, typeof(Views.UninstallCenterView)) as Views.UninstallCenterView;
                    log.AppendLine($"  dragdrop: error={dragDropFilterError} viewAllowDrop={uninstallView?.AllowDrop}");
                    try
                    {
                        Clipboard.SetText(smokeTarget);
                        appVm.UninstallCenter.PastePathCommand.Execute(null);
                        Pump();
                        log.AppendLine($"  paste: ok=True target=\"{OneLine(appVm.UninstallCenter.TargetPath)}\" kind=\"{OneLine(appVm.UninstallCenter.TargetKindText)}\" same={string.Equals(appVm.UninstallCenter.TargetPath, smokeTarget, StringComparison.OrdinalIgnoreCase)}");
                    }
                    catch (Exception ex)
                    {
                        log.AppendLine($"WARN 剪贴板不可用，跳过「粘贴路径」验证：{ex.GetType().Name} {OneLine(ex.Message)}");
                    }

                    appVm.ShowUninstallCenter = false;

                    // 关掉之后必须真的消失，否则就是子视图叠在应用列表上（v1.8.0 的 bug）
                    Pump(600);
                    UpdateLayoutSafe(window);
                    Pump();
                    log.AppendLine($"  uninstall-view-off: {DescribeView(window, typeof(Views.UninstallCenterView))}");
                }

                if (vm.CurrentViewModel is NavPaneViewModel navPaneVm)
                {
                    navPaneVm.ShowTaskbar = true;
                    Pump(1500);
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "navpane-taskbar", log);
                    log.AppendLine($"  taskbar: pinned={navPaneVm.Taskbar.Pinned.Count} tray={navPaneVm.Taskbar.TrayIcons.Count} pinnedSummary=\"{OneLine(navPaneVm.Taskbar.PinnedSummary)}\" traySummary=\"{OneLine(navPaneVm.Taskbar.TraySummary)}\" error=\"{OneLine(navPaneVm.Taskbar.Error)}\"");
                    log.AppendLine($"  taskbar-view: {DescribeView(window, typeof(Views.TaskbarView))}");
                    navPaneVm.ShowTaskbar = false;
                }

                if (vm.CurrentViewModel is SystemTweakViewModel tweakVm)
                {
                    tweakVm.SubView = "ports";
                    Pump();
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "tweaks-ports", log);

                    tweakVm.SubView = "panels";
                    Pump();
                    UpdateLayoutSafe(window);
                    Pump();
                    Capture(window, outputDir, "tweaks-panels", log);
                    tweakVm.SubView = "tweaks";
                }
            }

            try { window.Close(); } catch (Exception) { }
            log.AppendLine("SMOKE OK");

            // 主题自检：切换主题后 Brush.Card 的颜色必须真的变化（防止 DynamicResource 失效）
            log.AppendLine(ThemeCheck());        }
        catch (Exception ex)
        {
            log.AppendLine("SMOKE FAILED");
            log.AppendLine(ex.ToString());
        }

        File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(true));
    }

    /// <summary>
    /// 给卸载残留清理面板造一个可预期的识别目标：一个名字像卸载器的 exe 放在独立子目录里。
    /// 不用机器上真实装的软件——那在不同机器上结果完全不同，CI 没法断言。
    ///
    /// 必须建在输出目录下：--smoke 进程是低完整性级别，写 %TEMP% 会被拒绝
    /// （UnauthorizedAccessException: Access to the path ... is denied），
    /// 而输出目录是它自己刚建出来写截图的，一定可写。
    /// </summary>
    private static string SmokeUninstallTarget(string outputDir)
    {
        var root = Path.Combine(outputDir, "WinKit-SmokeApp");
        try
        {
            Directory.CreateDirectory(root);
            var exe = Path.Combine(root, "uninstall.exe");
            if (!File.Exists(exe)) File.WriteAllBytes(exe, Array.Empty<byte>());
        }
        catch (Exception)
        {
            // 建不出来就退回程序目录，识别链路照样能跑完
            return AppContext.BaseDirectory;
        }

        return root;
    }

    private static void UpdateLayoutSafe(Window window)
    {
        // 无桌面会话时布局可能抛异常；数据 dump 不该因此中断
        try
        {
            window.UpdateLayout();
        }
        catch (Exception)
        {
            // 忽略：CI 只断言数据，不断言像素
        }
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

    /// <summary>
    /// 等到页面自己说「不忙了」。固定睡 N 秒靠不住：这些页面背后是 powershell，
    /// 冷启动就能花七八秒，机器一忙十几秒也正常，睡短了断言读到的是加载中的空数据，
    /// 睡长了每次冒烟都白等。
    /// </summary>
    private static void WaitWhileBusy(Func<bool> busy, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (busy() && Environment.TickCount64 < deadline)
        {
            Pump(200);
        }
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

    /// <summary>
    /// 在可视树里找第一个指定类型的后代。
    /// 子视图的显隐以前只能靠截图看，而截图在 CI 上未必渲染得出来；
    /// 直接读 Visibility/ActualWidth 才能断言「这个面板到底显示了没有」。
    /// </summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;

            var deeper = FindDescendant<T>(child);
            if (deeper is not null) return deeper;
        }

        return null;
    }

    /// <summary>
    /// 列宽布局自检：把每个列的实际宽度、表头文字宽度、DataGrid 视口宽度都打出来。
    /// 光看截图只能判断"右边被切了"，看不出是哪一列吃掉太多宽度，
    /// 也算不出"还要收多少才不出现横向滚动条"。
    /// </summary>
    /// <summary>
    /// 找到当前真正显示着的 DataGrid。子视图叠放时隐藏的表格还在可视树里，
    /// 用 FindDescendant 会拿到第一个（可能是隐藏的），量出来的列宽毫无意义。
    /// </summary>
    private static System.Windows.Controls.DataGrid? FindVisibleDataGrid(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is System.Windows.Controls.DataGrid dg && dg.IsVisible && dg.ActualWidth > 1)
                return dg;

            var deeper = FindVisibleDataGrid(child);
            if (deeper is not null) return deeper;
        }

        return null;
    }

    private static string ColumnLayoutCheck(DependencyObject root)
    {
        // 子视图是叠在一起的，隐藏的那些 DataGrid 仍然留在可视树里，
        // 所以必须挑当前真正可见的那个，否则量到的是别的表。
        var grid = FindVisibleDataGrid(root);
        if (grid is null) return "  columns: FAILED 没找到可见的 DataGrid";

        try
        {
            var parts = new List<string>();
            var total = 0.0;
            foreach (var col in grid.Columns)
            {
                var w = col.ActualWidth;
                total += w;
                var header = col.Header?.ToString() ?? "";
                // 期望值与实际值都打出来：DataGrid 在空间不够时会挤压列，
                // 只打实际值看不出"被挤掉了多少"。
                var spec = col.Width.IsAbsolute ? ((int)col.Width.Value).ToString()
                    : col.Width.IsStar ? $"{col.Width.Value:0.#}*"
                    : "auto";
                parts.Add($"{header}={spec}/{(int)Math.Round(w)}");
            }

            var viewer = FindDescendant(grid, typeof(System.Windows.Controls.ScrollViewer))
                         as System.Windows.Controls.ScrollViewer;
            var viewport = viewer?.ViewportWidth ?? 0;
            var scrollable = viewer?.ScrollableWidth ?? 0;
            var overflow = scrollable > 1;

            return $"viewport={(int)Math.Round(viewport)} total={(int)Math.Round(total)} " +
                   $"hscroll={(overflow ? "YES" : "no")} n={grid.Columns.Count} [{string.Join(" ", parts)}]";
        }
        catch (Exception ex)
        {
            return $"  columns: FAILED {ex.GetType().Name} {OneLine(ex.Message)}";
        }
    }

    private static string DescribeView(DependencyObject root, Type type)
    {
        var found = FindDescendant(root, type);
        if (found is not FrameworkElement fe) return "found=False";

        return $"found=True visibility={fe.Visibility} visible={fe.IsVisible} " +
               $"w={(int)fe.ActualWidth} h={(int)fe.ActualHeight}";
    }

    private static DependencyObject? FindDescendant(DependencyObject root, Type type)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (type.IsInstanceOfType(child)) return child;

            var deeper = FindDescendant(child, type);
            if (deeper is not null) return deeper;
        }

        return null;
    }

    /// <summary>
    /// 列宽拖拽自检：DataGridColumnHeader 的模板里必须有 PART_LeftHeaderGripper /
    /// PART_RightHeaderGripper 两个 Thumb，否则用户根本拖不动列宽（连光标都不变）。
    /// 这个只能查模板部件，截图看不出来。
    /// </summary>
    private static string ColumnResizeCheck(DependencyObject root)
    {
        var header = FindDescendant(root, typeof(System.Windows.Controls.Primitives.DataGridColumnHeader))
                     as System.Windows.Controls.Primitives.DataGridColumnHeader;

        if (header is null) return "  columnresize: FAILED 没找到列头";

        try
        {
            header.ApplyTemplate();
            var left = header.Template.FindName("PART_LeftHeaderGripper", header);
            var right = header.Template.FindName("PART_RightHeaderGripper", header);

            var grid = FindDescendant(root, typeof(System.Windows.Controls.DataGrid))
                       as System.Windows.Controls.DataGrid;
            var canResize = grid?.CanUserResizeColumns ?? false;

            var ok = left is not null && right is not null && canResize;
            return $"  columnresize: left={left is not null} right={right is not null} " +
                   $"canResize={canResize} result={(ok ? "OK" : "FAILED")}";
        }
        catch (Exception ex)
        {
            return $"  columnresize: FAILED {ex.GetType().Name} {OneLine(ex.Message)}";
        }
    }

    /// <summary>
    /// 主题自检：分别切到浅色/深色，读取 Brush.Card 的实际颜色。
    /// 这条能抓住"改了 Colors 字典但视图仍用 StaticResource"这类静默失效。
    /// </summary>
    private static string ThemeCheck()
    {
        try
        {
            string Read()
            {
                var brush = Current?.Resources["Brush.Card"] as System.Windows.Media.SolidColorBrush;
                return brush?.Color.ToString() ?? "null";
            }

            Services.ThemeService.Apply(Services.AppTheme.Light);
            var light = Read();

            Services.ThemeService.Apply(Services.AppTheme.Dark);
            var dark = Read();

            var ok = light != dark && light != "null" && dark != "null";
            return $"  theme: light={light} dark={dark} result={(ok ? "OK" : "FAILED")}";
        }
        catch (Exception ex)
        {
            return $"  theme: FAILED {ex.GetType().Name} {OneLine(ex.Message)}";
        }
    }

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
