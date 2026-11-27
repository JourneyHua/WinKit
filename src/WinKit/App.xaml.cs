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
    internal static Services.AppUpdateInstallNotice? RestartNotice { get; set; }
    /// <summary>本程序认识的命令行开关。不在这张表里的开关一律当误用处理。</summary>
    private static readonly string[] KnownSwitches =
    [
        "--smoke",
        "--motion-smoke",
        "--manifest-probe",
        "--maintain",
        "--help",
        "-?"
    ];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var arguments = e.Args;
        if (Services.AppUpdateInstaller.IsHelperInvocation(arguments))
        {
            try { Shutdown(Services.AppUpdateInstaller.RunHelperAsync(arguments).GetAwaiter().GetResult()); }
            catch { Shutdown(1); }
            return;
        }
        if (Services.AppUpdateInstaller.TryReadRestartNotice(arguments, out var notice))
        {
            RestartNotice = notice;
            arguments = [];
        }
        if (Services.AppUpdateInstaller.IsUpdateInProgressForCurrentExecutable())
        {
            Shutdown(3);
            return;
        }

        // 这里以前没有 else 分支：随手传一个不认识的开关——最典型的是把 tools/Probe 的
        // --selftest 当成 WinKit.exe 的参数——不会报错，会静默启动完整 GUI 并弹 UAC，
        // 让人以为工具坏了或是自测跑起来了。宁可明确拒绝，也不要糊里糊涂跑起来。
        var badSwitches = arguments
            .Where(a => a.StartsWith("-", StringComparison.Ordinal) && !KnownSwitches.Contains(a))
            .ToList();

        if (badSwitches.Count > 0)
        {
            ShowUsage(badSwitches);
            Shutdown(2);
            return;
        }

        if (arguments.Contains("--help") || arguments.Contains("-?"))
        {
            ShowUsage([]);
            Shutdown(0);
            return;
        }

        var smokeIndex = Array.IndexOf(arguments, "--smoke");
        if (smokeIndex >= 0)
        {
            var output = smokeIndex + 1 < arguments.Length
                ? arguments[smokeIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "smoke");

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(RunSmoke(output));
            return;
        }

        var motionIndex = Array.IndexOf(arguments, "--motion-smoke");
        if (motionIndex >= 0)
        {
            var output = motionIndex + 1 < arguments.Length
                ? arguments[motionIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "smoke-motion");
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(RunMotionSmoke(output));
            return;
        }

        if (arguments.Contains("--manifest-probe"))
        {
            RunManifestProbe();
            Shutdown(0);
            return;
        }

        // 无人值守维护：由计划任务拉起，跑完即退，不建窗口
        if (arguments.Contains("--maintain"))
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
        // Only normal startup creates the main window; smoke, maintenance and update-helper modes never do.
        MainWindow = new MainWindow();
        MainWindow.Show();
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
        sb.AppendLine("  --motion-smoke <目录> 动效专项：长表滚动、减少动态效果、截图与渲染记录");
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
    private static int RunSmoke(string outputDir)
    {
        var log = new StringBuilder();
        var exitCode = 0;

        // 目录和日志必须在任何可能失败的操作之前就绪：
        // CI 的无交互桌面会话里窗口可能起不来，但数据 dump 仍要能落盘。
        outputDir = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(outputDir);
        var logPath = Path.Combine(outputDir, "smoke.log");

        try
        {
            Services.ThemeService.Apply(Services.AppTheme.Light);
            WinKit.MainWindow.ApplyDensity(Services.TableDensity.Compact);
            var journal = new ChangeJournal(Path.Combine(outputDir, "ux-backups"));
            var smokeVm = new MainViewModel(new Services.UiSettings(), journal, saveSettings: false);
            var window = new MainWindow(smokeVm)
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
                throw new InvalidOperationException("无法显示真实窗口，交互自检不能通过", ex);
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
            if (!shown) vm.Initialize();

            // 自检走与正常用户相同的导航路径；浏览模块不需要关闭任何模态提示开关。
            log.AppendLine($"  elevation: elevated={vm.IsElevated} banner={vm.ShowElevationBanner} navigation=nonmodal");
            RunUxInteractionChecks(window, vm, journal, outputDir, log);
            RunModuleStateChecks(outputDir, log);
            RunUpdateUiChecks(outputDir, log);
            RunMotionChecks(outputDir, log);
            RunShellMotionChecks(window, outputDir, log);
            RunDeferredCloseCheck(outputDir, log);
            log.AppendLine($"  nav: groups={vm.NavGroups.Count} items={vm.NavItems.Count} expanded={vm.NavGroups.Count(g => g.IsExpanded)}");

            var errorPagesChecked = 0;
            foreach (var nav in vm.NavItems)
            {
                NavigateSmoke(window, vm, nav);
                errorPagesChecked += CheckErrorLayout(window, vm.CurrentViewModel!, nav.Key, outputDir, log);

                if (shown)
                {
                    try
                    {
                        UpdateLayoutSafe(window);
                        Pump();
                        Capture(window, outputDir, nav.Key, log, "MODULE OK");
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
                    lockVm.PathText = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前程序");
                    lockVm.Refresh();
                    WaitWhileBusy(() => lockVm.IsBusy, 40000);
                    if (lockVm.IsBusy) throw new TimeoutException("文件占用查询超时");
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
                    WaitWhileBusy(() => reportVm.IsBusy, 45000);
                    InvokeButton(FindVisibleButton(window, b => ReferenceEquals(b.Command, reportVm.GenerateCommand))
                        ?? throw new InvalidOperationException("找不到体检操作"));
                    WaitWhileBusy(() => reportVm.IsBusy, 45000);
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

                    // 离屏窗口验证拖放消息放行和替代的粘贴动作；输入边界使用夹具，避免改动桌面剪贴板。
                    var uninstallView = FindDescendant(window, typeof(Views.UninstallCenterView)) as Views.UninstallCenterView;
                    log.AppendLine($"  dragdrop: error={dragDropFilterError} viewAllowDrop={uninstallView?.AllowDrop}");
                    var clipboardReader = appVm.UninstallCenter.ClipboardTextReader;
                    try
                    {
                        appVm.UninstallCenter.ClipboardTextReader = () => $"\"{smokeTarget}\"\r\n";
                        appVm.UninstallCenter.ClearCommand.Execute(null);
                        Pump();
                        if (FindVisibleButton(window, b => ReferenceEquals(b.Command, appVm.UninstallCenter.ScanCommand)) is not null)
                            throw new InvalidOperationException("没有有效目标时仍显示残留扫描");
                        InvokeButton(FindVisibleButton(window, b => ReferenceEquals(b.Command, appVm.UninstallCenter.PastePathCommand))
                            ?? throw new InvalidOperationException("没有找到粘贴路径按钮"));
                        var same = string.Equals(appVm.UninstallCenter.TargetPath, smokeTarget, StringComparison.OrdinalIgnoreCase);
                        if (!same) throw new InvalidOperationException("粘贴动作没有重新识别夹具路径");
                        log.AppendLine($"  paste: ok=True source=isolated-input target=\"{OneLine(appVm.UninstallCenter.TargetPath)}\" kind=\"{OneLine(appVm.UninstallCenter.TargetKindText)}\" same={same}");
                        InvokeButton(FindVisibleButton(window, b => ReferenceEquals(b.Command, appVm.UninstallCenter.ScanCommand))
                            ?? throw new InvalidOperationException("找不到有效目标的残留扫描操作"));
                        WaitWhileBusy(() => appVm.UninstallCenter.IsScanning, 45000);
                        if (appVm.UninstallCenter.IsScanning || appVm.UninstallCenter.ScanSummary.StartsWith("扫描失败", StringComparison.Ordinal)
                            || appVm.UninstallCenter.HasSelection
                            || FindVisibleButton(window, b => ReferenceEquals(b.Command, appVm.UninstallCenter.CleanSelectedCommand)) is not null)
                            throw new InvalidOperationException("残留扫描未完成，或结果未经用户选择即可清理");
                        Capture(window, outputDir, "ux-uninstall-scan", log);
                        log.AppendLine("  ux-uninstall: scan=invoked selection=opt-in cleanup=hidden result=OK");
                    }
                    finally
                    {
                        appVm.UninstallCenter.ClipboardTextReader = clipboardReader;
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

            if (errorPagesChecked != 14)
                throw new InvalidOperationException($"错误布局自检覆盖不完整：预期14页，实际{errorPagesChecked}页");
            log.AppendLine($"  ux-errors: pages={errorPagesChecked} buttons=unobstructed result=OK");
            NavigateSmoke(window, vm, vm.NavItems.First(n => n.Key == "env"));
            log.AppendLine(ThemeCheck(window, outputDir, log));
            window.Close();
            log.AppendLine("SMOKE OK");
            if (log.ToString().Contains("FAILED", StringComparison.Ordinal)) exitCode = 1;
        }
        catch (Exception ex)
        {
            exitCode = 1;
            log.AppendLine("SMOKE FAILED");
            log.AppendLine(ex.ToString());
        }

        File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(true));
        return exitCode;
    }

    /// <summary>
    /// 给卸载残留清理面板造一个可预期的识别目标：一个名字像卸载器的 exe 放在独立子目录里。
    /// 不用机器上真实装的软件——那在不同机器上结果完全不同，CI 没法断言。
    ///
    /// 夹具全部建在指定输出目录下，与真实安装目录及用户备份隔离。
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

    private static void RunUxInteractionChecks(
        MainWindow window, MainViewModel vm, ChangeJournal journal, string outputDir, StringBuilder log)
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("UX 自检失败：" + message);
        }

        void Invoke(System.Windows.Input.ICommand command)
        {
            Pump();
            var button = FindVisibleButton(window, b => b.Command == command);
            Require(button is not null && button.IsEnabled, "界面动作不可用");
            InvokeButton(button!);
            if (ReferenceEquals(command, vm.UndoCommand))
            {
                WaitWhileBusy(() => vm.IsGlobalBusy, 15000);
                Require(!vm.IsGlobalBusy, "撤销操作未结束");
            }
        }

        var file = Path.GetFullPath(Path.Combine(outputDir, "ux-check.txt"));
        File.WriteAllText(file, "before");
        journal.Record("界面撤销验证", [], () => File.WriteAllText(file, "after"), [file]);
        Pump();
        Require(vm.HasFeedback && vm.HasUndo && vm.UndoCommand.CanExecute(null), "修改后缺少反馈或撤销入口");
        Invoke(vm.UndoCommand);
        Require(File.ReadAllText(file) == "before" && !journal.CanUndo, "界面撤销未恢复原文件");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = journal.RecordAsync("并发保护验证", [], async () =>
        {
            File.WriteAllText(file, "pending");
            await release.Task;
        }, [file]);
        Pump();
        Require(vm.IsGlobalBusy && !vm.IsNavigationEnabled && !vm.UndoCommand.CanExecute(null)
            && !vm.ElevateCommand.CanExecute(null), "操作中仍允许导航、撤销或重启提权");
        var previous = vm.SelectedNav;
        var navigationButton = FindVisibleButton(window, b => ReferenceEquals(b.DataContext, previous));
        Require(navigationButton is not null && !navigationButton.IsEnabled, "导航按钮未显示禁用状态");
        var navigationPeer = new System.Windows.Automation.Peers.ButtonAutomationPeer(navigationButton!);
        try
        {
            ((System.Windows.Automation.Provider.IInvokeProvider)navigationPeer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
            Require(false, "禁用的导航按钮仍可执行");
        }
        catch (System.Windows.Automation.ElementNotEnabledException) { }
        vm.SelectedNav = vm.NavItems.First(item => item != previous);
        vm.UndoCommand.Execute(null);
        Require(vm.SelectedNav == previous && File.ReadAllText(file) == "pending", "忙碌时执行了冲突动作");
        window.Close();
        Pump();
        Require(window.IsVisible && vm.IsClosePending, "操作中关闭窗口丢失了未完成任务");
        Invoke(vm.CancelCloseCommand);
        Require(!vm.IsClosePending && window.IsVisible, "无法取消等待关闭");
        release.SetResult();
        WaitWhileBusy(() => !pending.IsCompleted, 5000);
        Require(pending.IsCompleted, "异步操作未结束");
        pending.GetAwaiter().GetResult();
        Pump();
        Require(!vm.IsGlobalBusy && vm.IsNavigationEnabled && navigationButton!.IsEnabled, "结束后交互未恢复");
        Invoke(vm.UndoCommand);
        Require(File.ReadAllText(file) == "before", "异步操作无法撤销");

        journal.Record("撤销失败重试验证", [], () => File.WriteAllText(file, "retry"), [file]);
        using (var locked = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Invoke(vm.UndoCommand);
            Require(vm.FeedbackIsError && journal.CanUndo, "撤销失败未解释原因或丢失了重试记录");
        }
        Invoke(vm.UndoCommand);
        Require(!vm.FeedbackIsError && !journal.CanUndo && File.ReadAllText(file) == "before", "解除占用后无法重试撤销");
        Invoke(vm.DismissFeedbackCommand);
        Require(!vm.HasFeedback, "提示无法关闭");
        log.AppendLine("  ux-interactions: undo=invoked busy=guarded close=deferred/cancelled retry=restored feedback=dismissed result=OK");

        journal.Record("备份界面恢复验证", [], () => File.WriteAllText(file, "after-backup"), [file]);
        NavigateSmoke(window, vm, vm.NavItems.First(n => n.Key == "backup"));
        var backups = vm.CurrentViewModel as BackupViewModel ?? throw new InvalidOperationException("备份页未加载");
        void WaitIdle()
        {
            WaitWhileBusy(() => vm.IsGlobalBusy, 15000);
            Require(!vm.IsGlobalBusy, "备份界面操作未结束");
            Pump();
        }
        void Preview(BackupRow row)
        {
            InvokeButton(FindVisibleButton(window, b => b.Command == backups.RestoreCommand && ReferenceEquals(b.CommandParameter, row))
                ?? throw new InvalidOperationException("找不到快照还原按钮"));
            WaitIdle();
        }
        var source = backups.Backups.Single(b => b.Title == "备份界面恢复验证");
        Preview(source);
        Require(backups.HasPreview && backups.PreviewText.Contains(file, StringComparison.OrdinalIgnoreCase), "恢复前未显示完整目标");
        Capture(window, outputDir, "ux-backup-preview", log);
        Invoke(backups.ClosePreviewCommand);
        Require(!backups.HasPreview, "备份预览无法关闭");
        Preview(source);
        File.AppendAllText(source.FilePath, " ");
        Invoke(backups.ConfirmRestoreCommand);
        WaitIdle();
        Require(backups.HasError && File.ReadAllText(file) == "after-backup" && journal.SessionCount == 1,
            "备份预览后内容变化未阻止恢复");
        var damaged = Path.Combine(journal.StorageDirectory, "ux-damaged.json");
        File.WriteAllText(damaged, "invalid json");
        backups.Refresh();
        WaitIdle();
        var invalid = backups.Backups.Single(b => b.FilePath == damaged);
        Preview(invalid);
        Require(backups.HasError && !backups.HasPreview && !backups.ConfirmRestoreCommand.CanExecute(null), "损坏快照仍保留旧恢复入口");
        File.Delete(damaged);
        backups.Refresh();
        WaitIdle();
        source = backups.Backups.Single(b => b.Title == "备份界面恢复验证");
        Preview(source);
        Invoke(backups.ConfirmRestoreCommand);
        WaitIdle();
        Require(!backups.HasPreview && !backups.HasError && File.ReadAllText(file) == "before" && journal.SessionCount == 2,
            "界面确认未恢复快照或没有生成撤销记录");
        Invoke(vm.UndoCommand);
        Require(File.ReadAllText(file) == "after-backup", "界面撤销恢复未回到恢复前状态");
        Invoke(vm.UndoCommand);
        Require(File.ReadAllText(file) == "before" && !journal.CanUndo, "备份夹具未还原初始状态");
        log.AppendLine("  ux-backup: preview=invoked targets=complete tampering=blocked invalid=cleared restore=invoked undo=restored result=OK");

    }

    private static void NavigateSmoke(MainWindow window, MainViewModel vm, NavItem nav)
    {
        WaitWhileBusy(() => vm.IsGlobalBusy, 60000);
        if (vm.IsGlobalBusy) throw new TimeoutException($"切换到 {nav.Key} 前仍有操作未完成");
        var group = vm.NavGroups.First(g => g.Items.Contains(nav));
        if (!group.IsExpanded)
            InvokeButton(FindVisibleButton(window, b => ReferenceEquals(b.DataContext, group))
                ?? throw new InvalidOperationException($"未找到导航分组：{group.Name}"));
        InvokeButton(FindVisibleButton(window, b => ReferenceEquals(b.DataContext, nav))
            ?? throw new InvalidOperationException($"未找到导航按钮：{nav.Key}"));
        if (!ReferenceEquals(vm.SelectedNav, nav)) throw new InvalidOperationException($"未能进入 {nav.Key}");
        WaitWhileBusy(() => vm.IsGlobalBusy, 60000);
        if (vm.IsGlobalBusy) throw new TimeoutException($"{nav.Key} 加载未完成");
        Pump();
    }

    private static void RunModuleStateChecks(string outputDir, StringBuilder log)
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("模块自检失败：" + message);
        }

        var paths = new Dictionary<Services.EnvScope, string>
        {
            [Services.EnvScope.User] = @"C:\WinKit-User-A;C:\WinKit-User-B",
            [Services.EnvScope.Machine] = @"C:\WinKit-Machine-A;C:\WinKit-Machine-B"
        };
        var readFailure = false;
        var pathVm = new EnvironmentViewModel(new ChangeJournal(Path.Combine(outputDir, "path-backups")), _ => { }, null,
            scope => readFailure ? throw new IOException("模拟读取失败") : paths[scope], () => []);
        pathVm.Refresh();
        var pathWindow = new Window
        {
            Content = new Views.EnvironmentView { DataContext = pathVm },
            Width = 1264, Height = 781, Left = -4000, Top = -4000, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        try
        {
            pathWindow.Show();
            Pump();
            var apply = FindVisibleButton(pathWindow, b => b.Command == pathVm.ApplyPathCommand);
            Require(apply is not null && !apply.IsEnabled && !pathVm.HasPathChanges, "未修改时仍可应用 PATH");
            var first = pathVm.PathEntries[0];
            InvokeButton(FindVisibleButton(pathWindow, b => b.Command == pathVm.MovePathDownCommand && ReferenceEquals(b.CommandParameter, first))
                ?? throw new InvalidOperationException("找不到 PATH 排序按钮"));
            Require(pathVm.HasPathChanges && pathVm.CanApplyPath && apply!.IsEnabled,
                $"排序后状态错误：dirty={pathVm.HasPathChanges} canApply={pathVm.CanApplyPath} button={apply!.IsEnabled}");
            InvokeButton(FindVisibleButton(pathWindow, b => b.Command == pathVm.MovePathUpCommand && ReferenceEquals(b.CommandParameter, first))
                ?? throw new InvalidOperationException("找不到 PATH 还原排序按钮"));
            Require(!pathVm.HasPathChanges && !apply!.IsEnabled, "恢复原顺序后仍标记为更改");
            pathVm.PathEntries.Move(0, 1);
            var userDraft = pathVm.PathEntries.Select(p => p.Raw).ToArray();
            pathVm.ShowMachinePathCommand.Execute(null);
            pathVm.PathEntries.Move(0, 1);
            var machineDraft = pathVm.PathEntries.Select(p => p.Raw).ToArray();
            pathVm.ShowUserPathCommand.Execute(null);
            pathVm.Refresh();
            Require(pathVm.PathEntries.Select(p => p.Raw).SequenceEqual(userDraft) && pathVm.HasPendingPathDrafts, "刷新或切换丢失用户草稿");
            paths[Services.EnvScope.User] = @"C:\WinKit-External";
            Require(!pathVm.ValidatePathBaseline() && pathVm.HasPathConflict && !pathVm.CanApplyPath
                && pathVm.PathEntries.Select(p => p.Raw).SequenceEqual(userDraft), "外部 PATH 冲突未阻止写入或丢失草稿");
            Pump();
            Capture(pathWindow, outputDir, "ux-path-conflict", log);
            pathVm.DiscardPathDraft();
            Require(!pathVm.HasPathChanges && pathVm.PathEntries.Single().Raw == paths[Services.EnvScope.User], "放弃草稿未读取最新值");
            pathVm.ShowMachinePathCommand.Execute(null);
            Require(pathVm.HasPathChanges && pathVm.PathEntries.Select(p => p.Raw).SequenceEqual(machineDraft), "放弃用户草稿影响了系统草稿");
            readFailure = true;
            pathVm.Refresh();
            Require(!pathVm.IsBusy && pathVm.HasError && pathVm.PathEntries.Select(p => p.Raw).SequenceEqual(machineDraft), "读取失败后丢失草稿或未释放忙碌状态");
        }
        finally { pathWindow.Close(); }
        log.AppendLine("  ux-path: reorder=invoked scopes=preserved refresh=preserved conflict=blocked discard=scoped read-failure=preserved result=OK");

        var left = Path.Combine(outputDir, "diff-fixture-left");
        var right = Path.Combine(outputDir, "diff-fixture-right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        File.WriteAllText(Path.Combine(left, "entry.txt"), "left");
        File.WriteAllText(Path.Combine(right, "entry.txt"), "right");
        var diff = new DiffViewModel(_ => { }) { LeftPath = left, RightPath = right, CompareHash = true };
        void Compare()
        {
            diff.CompareCommand.Execute(null);
            Require(diff.IsBusy && !diff.SwapCommand.CanExecute(null), "比较时仍可修改范围");
            var captured = diff.LeftPath;
            diff.LeftPath = right;
            diff.SwapCommand.Execute(null);
            Require(diff.LeftPath == captured, "比较中范围发生变化");
            WaitWhileBusy(() => diff.IsBusy, 15000);
            Require(!diff.IsBusy && !diff.HasError && diff.Entries.Count > 0, "隔离文件比较失败");
        }
        Compare();
        var oldEntry = diff.Entries[0];
        Require(diff.ResolveResultPath(oldEntry, true) == Path.Combine(left, "entry.txt"), "比较结果路径错误");
        diff.Recursive = !diff.Recursive;
        Require(diff.Entries.Count == 0 && diff.ResolveResultPath(oldEntry, true) is null
            && !diff.CopyLeftPathCommand.CanExecute(oldEntry), "条件变化后仍能操作旧结果");
        diff.LeftPath = Path.Combine(left, "entry.txt");
        diff.RightPath = Path.Combine(right, "entry.txt");
        Compare();
        var single = diff.Entries[0];
        Require(diff.ResolveResultPath(single, true) == diff.LeftPath && diff.ResolveResultPath(single, false) == diff.RightPath,
            "单文件路径被重复拼接");
        diff.SwapCommand.Execute(null);
        Require(diff.Entries.Count == 0 && diff.ResolveResultPath(single, false) is null, "交换范围后仍可操作旧行");
        log.AppendLine("  ux-diff: busy=guarded stale-result=invalidated single-file=resolved swap=invalidated result=OK");
    }

    private static void RunDeferredCloseCheck(string outputDir, StringBuilder log)
    {
        var journal = new ChangeJournal(Path.Combine(outputDir, "close-backups"));
        var vm = new MainViewModel(new Services.UiSettings(), journal, saveSettings: false);
        var window = new MainWindow(vm)
        {
            Left = -4000, Top = -4000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual
        };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Show();
        Pump();
        var operation = journal.RecordAsync("完成后关闭验证", [], async () => await release.Task);
        window.Close();
        Pump();
        if (!window.IsVisible || !vm.IsClosePending) throw new InvalidOperationException("忙碌窗口未等待操作完成");
        release.SetResult();
        WaitWhileBusy(() => !operation.IsCompleted || window.IsVisible, 15000);
        operation.GetAwaiter().GetResult();
        if (window.IsVisible || vm.IsClosePending) throw new InvalidOperationException("操作完成后窗口没有关闭");
        log.AppendLine("  ux-close: pending=visible completion=closed result=OK");
    }

    private static int CheckErrorLayout(Window window, object model, string key, string outputDir, StringBuilder log)
    {
        var errorStyle = (Style)window.FindResource("ErrorBar");
        var bar = VisualDescendants(window).OfType<System.Windows.Controls.Border>()
            .FirstOrDefault(b => ReferenceEquals(b.Style, errorStyle) && ReferenceEquals(b.DataContext, model));
        if (bar is null) return 0;
        var property = model.GetType().GetProperty("ErrorText")
            ?? throw new InvalidOperationException($"{key} 错误提示没有对应状态");
        var setter = property.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException($"{key} 错误状态无法注入自检");
        var original = property.GetValue(model);
        try
        {
            setter.Invoke(model, ["验证错误提示：操作未完成，当前内容已保留。请检查后重试；此提示应完整显示，且不遮住刷新、搜索或其它恢复操作。"]);
            Pump();
            window.UpdateLayout();
            if (!bar.IsVisible || bar.ActualWidth <= 0 || bar.ActualHeight <= 0)
                throw new InvalidOperationException($"{key} 出错后没有显示页内提示");
            var errorBounds = new Rect(bar.TranslatePoint(new Point(), window), bar.RenderSize);
            foreach (var button in VisualDescendants(window).OfType<System.Windows.Controls.Button>()
                         .Where(b => b.IsVisible && b.ActualWidth > 0 && b.ActualHeight > 0))
            {
                if (bar.IsAncestorOf(button)) continue;
                var buttonBounds = new Rect(button.TranslatePoint(new Point(), window), button.RenderSize);
                var overlap = Rect.Intersect(errorBounds, buttonBounds);
                if (!overlap.IsEmpty && overlap.Width > 1 && overlap.Height > 1)
                    throw new InvalidOperationException($"{key} 错误提示遮住按钮：{button.Content}");
            }
            Capture(window, outputDir, $"error-{key}", log);
            log.AppendLine($"  error-layout: page={key} buttons=unobstructed result=OK");
        }
        finally
        {
            setter.Invoke(model, [original]);
            Pump();
        }
        return 1;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static System.Windows.Controls.Button? FindVisibleButton(DependencyObject root,
        Func<System.Windows.Controls.Button, bool> predicate)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.Button button && button.IsVisible && predicate(button))
                return button;
            var found = FindVisibleButton(child, predicate);
            if (found is not null) return found;
        }
        return null;
    }

    private static void InvokeButton(System.Windows.Controls.Button button)
    {
        if (!button.IsEnabled) throw new InvalidOperationException("界面按钮不可用");
        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
        var invoke = peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)
            as System.Windows.Automation.Provider.IInvokeProvider
            ?? throw new InvalidOperationException("按钮缺少键盘/辅助功能调用入口");
        invoke.Invoke();
        Pump();
    }

    private static void UpdateLayoutSafe(Window window)
    {
        // 交互回归依赖真实布局；失败必须阻断发布。
        window.UpdateLayout();
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
        // Invoke 的命令可能再次排队刷新 CanExecute；排空到空闲再读取按钮状态。
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
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
    /// 主题自检：在同一个真实窗口中切换主题，验证已创建元素并保存两套截图。
    /// </summary>
    private static string ThemeCheck(Window window, string outputDir, StringBuilder log)
    {
        try
        {
            var label = FindDescendant<System.Windows.Controls.TextBlock>(window)
                ?? throw new InvalidOperationException("窗口缺少可验证的文字元素");
            static string ColorOf(Brush? brush) => (brush as SolidColorBrush)?.Color.ToString() ?? "null";
            Services.ThemeService.Apply(Services.AppTheme.Light);
            Pump();
            var light = ColorOf(window.Background);
            var lightText = ColorOf(label.Foreground);
            Capture(window, outputDir, "theme-light", log);

            Services.ThemeService.Apply(Services.AppTheme.Dark);
            Pump();
            var dark = ColorOf(window.Background);
            var darkText = ColorOf(label.Foreground);
            Capture(window, outputDir, "theme-dark", log);

            var ok = light != dark && lightText != darkText
                && light != "null" && dark != "null" && lightText != "null" && darkText != "null";
            Services.ThemeService.Apply(Services.AppTheme.Light);
            Pump();
            return $"  theme: light={light} dark={dark} lightText={lightText} darkText={darkText} visual=True result={(ok ? "OK" : "FAILED")}";
        }
        catch (Exception ex)
        {
            return $"  theme: FAILED {ex.GetType().Name} {OneLine(ex.Message)}";
        }
    }

    private static void Capture(Window window, string outputDir, string name, StringBuilder log, string marker = "OK")
    {
        var target = (FrameworkElement)window.Content;
        var width = Math.Max(1, (int)target.ActualWidth);
        var height = Math.Max(1, (int)target.ActualHeight);

        // 内容根可能透明，离屏截图也要包含正常窗口绘制的背景。
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background ?? (Brush)window.FindResource("Brush.Window"), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Render(target);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var file = Path.Combine(outputDir, $"{name}.png");
        using (var stream = File.Create(file)) encoder.Save(stream);

        log.AppendLine($"{marker} {name} -> {file} ({width}x{height})");
    }
}
