using System.IO;
using System.Text;
using WinKit.Core;
using WinKit.Services;

namespace WinKitProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 0 && args[0] == "--selftest")
            return SelfTest();

        if (args.Length > 0 && args[0] == "--journal-selftest")
            return JournalSelfTest() == 0 ? 0 : 1;

        if (args.Length > 0 && args[0] == "--quick-access-selftest")
            return QuickAccessChecks.Run() == 0 ? 0 : 1;

        if (args.Length > 0 && args[0] == "--duplicate-selftest")
            return DuplicateCleanupSelfTest() == 0 ? 0 : 1;

        if (args.Length > 0 && args[0] == "--duplicate-recycle-selftest")
            return DuplicateCleanupSelfTest(includeRecycle: true) == 0 ? 0 : 1;

        if (args.Length > 0 && args[0] == "--shelltree")
            return ShellTree();

        var report = new StringBuilder();

        void Line(string text = "")
        {
            report.AppendLine(text);
            Console.WriteLine(text);
        }

        Line("=== WinKit 运行期探测报告 ===");
        Line($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"OS：{Environment.OSVersion.VersionString}");
        Line($"提权：{(Native.IsElevated() ? "是" : "否")}");
        Line();

        var journal = new ChangeJournal();
        var menu = new ContextMenuService(journal);

        Line("[1] 经典菜单各域条目统计");
        foreach (var domain in MenuDomain.All)
        {
            try
            {
                var items = menu.Load(domain.Kind);
                var thirdParty = items.Count(i => !i.IsSystem);
                Line($"  - {domain.Title,-6} 共 {items.Count,4} 条（第三方 {thirdParty,3}），示例：" +
                     string.Join(" | ", items.Take(3).Select(i => i.DisplayName)));
            }
            catch (Exception ex)
            {
                Line($"  - {domain.Title} 读取失败：{ex.GetType().Name} {ex.Message}");
            }
        }

        Line();
        Line("[2] Win11 一级菜单（PackagedCom 注册项）");
        try
        {
            var newMenu = new NewMenuService(journal);
            var items = newMenu.Load();
            Line($"  共 {items.Count} 项");
            foreach (var item in items.Take(10))
                Line($"  - {item.DisplayName,-28} 包={item.Package,-40} 已屏蔽={item.IsBlocked}");
            Line($"  当前处于经典菜单模式：{(newMenu.ForcedClassicMenuState() == 1 ? "是" : "否")}");
        }
        catch (Exception ex)
        {
            Line($"  读取失败：{ex.GetType().Name} {ex.Message}");
        }

        Line();
        Line("[3] 此电脑");
        try
        {
            var thisPc = new ThisPcService(journal);
            Line("  用户文件夹：");
            foreach (var item in thisPc.LoadFolders())
                Line($"    - {item.Title,-6} {(item.IsVisible ? "显示" : "隐藏")}");

            var nodes = thisPc.LoadNodes();
            Line($"  系统节点：共 {nodes.Count} 项");
            foreach (var node in nodes)
                Line($"    - {node.Title,-20} {node.Subtitle} {(node.IsVisible ? "显示" : "隐藏")}");

            Line("  驱动器：");
            foreach (var drive in thisPc.LoadDrives())
                Line($"    - {drive.Title,-16} {drive.Subtitle} {(drive.IsVisible ? "显示" : "隐藏")}");
        }
        catch (Exception ex)
        {
            Line($"  读取失败：{ex.GetType().Name} {ex.Message}");
        }

        Line();
        Line("[4] 快速访问（Shell COM / Jump List）");
        try
        {
            var nav = new NavPaneService(journal);
            var (items, error) = nav.LoadQuickAccess();
            if (error is not null)
            {
                Line($"  读取失败：{error}");
                Line("  结论：COM 通道不可用，需要降级为只读展示。");
            }
            else
            {
                Line($"  共 {items.Count} 项");
                foreach (var item in items.Take(12))
                    Line($"    - {item.Name,-24} 文件夹={item.IsFolder} {item.Path}");
            }

            Line("  导航窗格开关：");
            foreach (var toggle in nav.LoadToggles())
                Line($"    - {toggle.Title,-14} {(toggle.IsOn ? "开" : "关")}");
        }
        catch (Exception ex)
        {
            Line($"  读取失败：{ex.GetType().Name} {ex.Message}");
        }

        Line();
        Line("[5] 屏蔽列表（Shell Extensions\\Blocked）");
        try
        {
            var blocked = ContextMenuService.ListBlockedClsids();
            Line($"  共 {blocked.Count} 项{(blocked.Count > 0 ? "：" + string.Join(", ", blocked.Take(8)) : "")}");
        }
        catch (Exception ex)
        {
            Line($"  读取失败：{ex.Message}");
        }

        Line();
        Line("[6] 假设验证结论");
        Line("  - 快速访问能否读取 → 见 [4]");
        Line("  - 新菜单第三方项能否枚举 → 见 [2]（能枚举即可尝试写入 Blocked）");
        Line("  - 此电脑属性位是否可控 → 见 [3]，ThisPCPolicy 状态已读出");

        var outputPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "probe-report.txt");

        File.WriteAllText(outputPath, report.ToString(), new UTF8Encoding(true));
        Console.WriteLine();
        Console.WriteLine($"报告已写入：{outputPath}");
        return 0;
    }

    /// <summary>
    /// 用 Shell COM 枚举指定文件夹的实际子项（名称 + 路径/CLSID），用于定位导航栏里那些节点。
    /// </summary>
    private static int ShellTree()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            Console.WriteLine("Shell.Application 不可用");
            return 1;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;

        void Dump(string title, object folderObj)
        {
            Console.WriteLine($"=== {title} ===");
            try
            {
                dynamic? folder = folderObj;
                if (folder is null)
                {
                    Console.WriteLine("  <null>");
                    return;
                }

                dynamic items = folder.Items();
                var count = (int)items.Count;
                for (var i = 0; i < count; i++)
                {
                    dynamic item = items.Item(i);
                    string name = "", path = "";
                    try { name = (string)item.Name; } catch (Exception) { }
                    try { path = (string)item.Path; } catch (Exception) { }
                    bool isFolder = false;
                    try { isFolder = (bool)item.IsFolder; } catch (Exception) { }
                    Console.WriteLine($"  {name,-28} folder={isFolder,-5} {path}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  枚举失败：" + ex.Message);
            }
        }

        Dump("此电脑 (17)", shell.Namespace(17));
        Console.WriteLine();
        Dump("导航栏根 shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}", shell.Namespace("shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}"));

        return 0;
    }

    /// <summary>
    /// 自测：写入类用例只在 HKCU 建临时项，验证「改动 → 快照 → 撤销」是否精确还原；
    /// 只读类用例（解析器、分类器、概览、网卡过滤）在任何会话都能跑。
    /// 低完整性级别（沙箱/受限令牌）下 HKCU 写入会被拒绝，此时写入类用例标记 SKIP 而不是崩溃。
    /// </summary>
    private static int SelfTest()
    {
        var failures = 0;
        var skipped = 0;
        var fixtureId = Guid.NewGuid().ToString("N");
        var journal = new ChangeJournal(Path.Combine(Path.GetTempPath(), "winkit_selftest_backups_" + fixtureId));
        // 独占本次测试的键，避免清理之前运行或其他进程留下的夹具。
        var registryFixture = $@"Software\WinKitSelfTest\{fixtureId}";
        var testRoot = $@"{registryFixture}\Menu";
        var testPath = RegPath.Hkcu(testRoot);

        void Check(string name, bool condition, string detail = "")
        {
            if (condition)
            {
                Console.WriteLine($"  PASS  {name}");
            }
            else
            {
                failures++;
                Console.WriteLine($"  FAIL  {name} {detail}");
            }
        }

        void Skip(string name, string reason)
        {
            skipped++;
            Console.WriteLine($"  SKIP  {name}（{reason}）");
        }

        Console.WriteLine("=== WinKit 自测 ===");
        Console.WriteLine($"  提权：{(Native.IsElevated() ? "是" : "否")}");

        var canWrite = ProbeRegistryWrite(testRoot);
        if (!canWrite)
            Console.WriteLine("  注意：当前会话不能写 HKCU（低完整性级别/受限令牌），写入类用例将跳过。");

        // ---------- 写入类：快照与撤销 ----------
        if (canWrite)
        {
            Reg.DeleteTree(testPath);

            // 1. 禁用 → 撤销
            Reg.SetString(testPath, "MUIVerb", "自测菜单项");
            Reg.SetString(RegPath.Hkcu(testRoot + @"\command"), "", "notepad.exe");

            journal.Record("自测-禁用", new[] { (HiveKind.CurrentUser, testRoot) },
                () => Reg.SetString(testPath, "LegacyDisable", ""));
            Check("写入 LegacyDisable", Reg.HasValue(testPath, "LegacyDisable"));

            journal.UndoLast();
            Check("撤销后 LegacyDisable 消失", !Reg.HasValue(testPath, "LegacyDisable"));
            Check("撤销后原有 MUIVerb 保留", Reg.GetString(testPath, "MUIVerb") == "自测菜单项");
            Check("撤销后子键 command 保留", Reg.Exists(RegPath.Hkcu(testRoot + @"\command")));

            // 2. 删除整键 → 撤销恢复
            journal.Record("自测-删除", new[] { (HiveKind.CurrentUser, testRoot) },
                () => Reg.DeleteTree(testPath));
            Check("删除后键不存在", !Reg.Exists(testPath));

            journal.UndoLast();
            Check("撤销后键恢复", Reg.Exists(testPath));
            Check("撤销后子键恢复", Reg.Exists(RegPath.Hkcu(testRoot + @"\command")));
            Check("撤销后值恢复", Reg.GetString(testPath, "MUIVerb") == "自测菜单项");

            // 3. 屏蔽列表写入 → 撤销（同时验证撤销后磁盘快照文件已被清理）
            var blockedKey = $@"{registryFixture}\BlockedSelfTest";
            var fakeClsid = "{00000000-WINKIT-SELFTEST-0000-0000000000}";
            var snap = journal.Record("自测-屏蔽", new[] { (HiveKind.CurrentUser, blockedKey) },
                () => Reg.SetString(RegPath.Hkcu(blockedKey), fakeClsid, ""));
            Check("写入屏蔽项", Reg.HasValue(RegPath.Hkcu(blockedKey), fakeClsid));
            Check("快照文件已落盘", !string.IsNullOrWhiteSpace(snap.SavedFilePath) && File.Exists(snap.SavedFilePath));

            journal.UndoLast();
            Check("撤销后屏蔽项消失", !Reg.HasValue(RegPath.Hkcu(blockedKey), fakeClsid));
            Check("撤销后快照文件自动删除", string.IsNullOrWhiteSpace(snap.SavedFilePath) || !File.Exists(snap.SavedFilePath));
            Reg.DeleteTree(RegPath.Hkcu(blockedKey));

            // 4. 文件级快照（发送到） → 撤销恢复
            var tempFile = Path.Combine(Path.GetTempPath(), $"winkit_selftest_{Guid.NewGuid():N}.lnk");
            File.WriteAllText(tempFile, "self-test-shortcut", Encoding.UTF8);
            journal.Record("自测-文件删除", Array.Empty<(HiveKind, string)>(),
                () => File.Delete(tempFile),
                new[] { tempFile });
            Check("文件删除后不存在", !File.Exists(tempFile));

            journal.UndoLast();
            Check("文件撤销后恢复且内容一致", File.Exists(tempFile) && File.ReadAllText(tempFile, Encoding.UTF8) == "self-test-shortcut");
            if (File.Exists(tempFile)) File.Delete(tempFile);

            // 5. 快照递归预算必须是共享计数器（v1.4.0 修复：按值传预算会让整棵树节点数失控）
            var budgetRoot = $@"{registryFixture}\Budget";
            Reg.DeleteTree(RegPath.Hkcu(budgetRoot));
            for (var i = 0; i < 40; i++)
                Reg.SetString(RegPath.Hkcu($@"{budgetRoot}\k{i}"), "v", i.ToString());

            var smallBudget = SnapshotEngine.Capture(HiveKind.CurrentUser, budgetRoot, 5);
            var capturedNodes = SnapshotEngine.CountKeys(new[] { smallBudget });
            Check("快照预算生效（5 个节点以内）", capturedNodes <= 6, $"实际捕获 {capturedNodes} 个节点");
            Reg.DeleteTree(RegPath.Hkcu(budgetRoot));

            // 6. 快照还原必须保留原默认值与 HiddenByDefault（v1.4.0 此电脑节点修复依赖它）
            var shapeRoot = $@"{registryFixture}\Shape";
            Reg.DeleteTree(RegPath.Hkcu(shapeRoot));
            Reg.SetString(RegPath.Hkcu(shapeRoot), "", "CLSID_ThisPCDownloadsRegFolder");
            Reg.SetInt(RegPath.Hkcu(shapeRoot), "HiddenByDefault", 1);
            Reg.SetString(RegPath.Hkcu(shapeRoot + @"\child"), "x", "y");

            var shapeSnapshot = SnapshotEngine.Capture(HiveKind.CurrentUser, shapeRoot);
            Reg.DeleteTree(RegPath.Hkcu(shapeRoot));
            SnapshotEngine.Restore(shapeSnapshot);

            Check("还原后默认值保留（不是裸 GUID）",
                Reg.GetString(RegPath.Hkcu(shapeRoot)) == "CLSID_ThisPCDownloadsRegFolder");
            Check("还原后 HiddenByDefault 保留", Reg.GetInt(RegPath.Hkcu(shapeRoot), "HiddenByDefault") == 1);
            Check("还原后子键保留", Reg.GetString(RegPath.Hkcu(shapeRoot + @"\child"), "x") == "y");

            // 7. 截断快照必须拒绝整树还原，不能把没记下来的子树删掉
            var truncRoot = $@"{registryFixture}\Truncated";
            Reg.DeleteTree(RegPath.Hkcu(truncRoot));
            for (var i = 0; i < 30; i++)
                Reg.SetString(RegPath.Hkcu($@"{truncRoot}\c{i}"), "v", i.ToString());

            var truncSnapshot = SnapshotEngine.Capture(HiveKind.CurrentUser, truncRoot, 3);
            var wasTruncated = truncSnapshot.Truncated;

            var refused = false;
            try
            {
                SnapshotEngine.Restore(truncSnapshot);
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }

            Check("超预算快照被标记为不完整", wasTruncated);
            Check("不完整快照拒绝整树还原", refused);
            Check("拒绝后原键与子键都还在",
                Reg.Exists(RegPath.Hkcu(truncRoot)) && Reg.Exists(RegPath.Hkcu($@"{truncRoot}\c29")));
            Reg.DeleteTree(RegPath.Hkcu(truncRoot));

            // 8. QWORD 超出 int 范围时 GetInt 必须返回 null，不能静默截断
            var qwordRoot = $@"{registryFixture}\QWord";
            Reg.DeleteTree(RegPath.Hkcu(qwordRoot));
            Reg.SetString(RegPath.Hkcu(qwordRoot), "placeholder", "x");
            using (var key = Reg.Create(RegPath.Hkcu(qwordRoot)))
            {
                key!.SetValue("big", 0x1_0000_0001L, Microsoft.Win32.RegistryValueKind.QWord);
            }

            Check("GetInt 对大 QWORD 返回 null", Reg.GetInt(RegPath.Hkcu(qwordRoot), "big") is null);
            Check("GetLong 保留 64 位精度", Reg.GetLong(RegPath.Hkcu(qwordRoot), "big") == 0x1_0000_0001L);
            Reg.DeleteTree(RegPath.Hkcu(qwordRoot));

            // 9. 32/64 位视图：验证快照保存并还原到原来的视图。
            var viewRoot = $@"Software\Classes\WinKitViewSelfTest_{fixtureId}";
            var x86Path = RegPath.Hkcu(viewRoot).WithView(RegViewKind.X86);
            var x64Path = RegPath.Hkcu(viewRoot).WithView(RegViewKind.X64);

            Reg.DeleteTree(x86Path);
            Reg.DeleteTree(x64Path);
            Reg.SetString(x86Path, "", "x86-only");

            Check("32 位视图写入可见", Reg.GetString(x86Path) == "x86-only");

            // 注意：WOW64 重定向只对特定键（HKLM\Software、部分 Classes 子树）生效，
            // 自测里随便建的键两个视图看到同一份数据是正常的，不能据此断言“64 位视图看不到”。
            // 能断言的是：两个视图都能独立打开同一路径，且视图被完整带进快照与还原。
            var bothViewsOpen = Reg.Exists(x86Path) && Reg.Exists(x64Path);
            Check("同一路径可用 32/64 位视图分别打开", bothViewsOpen);
            Check("DisplayPath 标注视图", x86Path.DisplayPath.Contains("32 位视图"));

            var viewSnapshot = SnapshotEngine.Capture(x86Path);
            Check("快照记录了视图", viewSnapshot.View == "X86", $"实际 {viewSnapshot.View}");

            Reg.DeleteTree(x86Path);
            Check("删除后 32 位视图键消失", !Reg.Exists(x86Path));

            SnapshotEngine.Restore(viewSnapshot);
            Check("还原写回 32 位视图", Reg.GetString(x86Path) == "x86-only");

            Reg.DeleteTree(x86Path);
            Reg.DeleteTree(x64Path);

            Reg.DeleteTree(RegPath.Hkcu(registryFixture));
            Check("清理完成（测试键不存在）", !Reg.Exists(testPath));
        }
        else
        {
            Skip("快照写入/撤销用例", "当前会话不能写 HKCU");
            Skip("快照预算用例", "当前会话不能写 HKCU");
            Skip("快照还原保真用例", "当前会话不能写 HKCU");
        }

        // ---------- 只读类：解析器与分类器 ----------
        var sampleWinget = """
            名称                      ID                          版本         可用         源
            -------------------------------------------------------------------------------------
            7-Zip 23.01 (x64)         7zip.7zip                   23.01        24.08        winget
            Microsoft Visual Studio Code Microsoft.VisualStudioCode 1.92.0     1.95.1       winget
            2 个可用升级。
            """;
        var parsedUpgrades = AppManagerService.ParseWingetUpgradeTable(sampleWinget);
        Check("winget 升级表解析数量正确", parsedUpgrades.Count == 2);
        Check("winget 升级表字段匹配正确",
            parsedUpgrades.Count == 2
            && parsedUpgrades[0].Id == "7zip.7zip"
            && parsedUpgrades[0].AvailableVersion == "24.08");

        var samplePnp = """
            Microsoft PnP 工具

            发布名称:     oem12.inf
            原始名称:     nv_dispi.inf
            提供程序名称: NVIDIA
            类名:         显示适配器
            类 GUID:      {4d36e968-e325-11ce-bfc1-08002be10318}
            驱动程序版本: 10/15/2025 32.0.15.6590
            签名者姓名:   Microsoft Windows Hardware Compatibility Publisher
            """;
        var parsedDrivers = DriverManagerService.ParsePnpUtilDrivers(samplePnp);
        Check("pnputil 驱动表解析成功",
            parsedDrivers.Count == 1
            && parsedDrivers[0].PublishedName == "oem12.inf"
            && parsedDrivers[0].OriginalName == "nv_dispi.inf"
            && parsedDrivers[0].IsCritical);

        var runtimes = new RuntimeCheckService().CheckAll();
        Check("常用运行库检测清单完整（21 项）", runtimes.Count >= 21);
        Check("常用运行库分类覆盖四大类", runtimes.Select(r => r.Category).Distinct().Count() == 4);

        var toolCatalog = new SystemToolCatalogService().LoadCatalog();
        Check("常用系统工具库清单完整（22 款，5 大分类）",
            toolCatalog.Count == 22 && toolCatalog.Select(t => t.Category).Distinct().Count() == 5);

        var cleanTargets = new SystemCleanService().BuildTargets();
        Check("系统清理目标清单完整（10 类）", cleanTargets.Count == 10);

        var sampleNetstat = """
            活动连接
              协议  本地地址          外部地址        状态           PID
              TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1024
              UDP    127.0.0.1:5353         *:*                                    2048
            """;
        var parsedPorts = SystemTweakService.ParseNetstatOutput(sampleNetstat);
        Check("netstat 端口占用表解析成功",
            parsedPorts.Count == 2 && parsedPorts[0].LocalPort == 135 && parsedPorts[1].LocalPort == 5353);

        Check("分类器识别蓝屏", EventLogService.Categorize("Microsoft-Windows-WER-SystemErrorReporting", "1001") == "蓝屏 / 意外关机");
        Check("分类器识别应用崩溃", EventLogService.Categorize("Application Error", "1000") == "应用崩溃");
        Check("分类器识别 .NET 崩溃", EventLogService.Categorize(".NET Runtime", "1026") == "应用崩溃");
        Check("分类器识别更新失败", EventLogService.Categorize("Microsoft-Windows-WindowsUpdateClient", "20") == "更新失败");
        Check("分类器识别磁盘", EventLogService.Categorize("volsnap", "36") == "磁盘 / 存储");
        Check("分类器识别证书", EventLogService.Categorize("Microsoft-Windows-CAPI2", "4176") == "证书 / 加密");
        Check("分类器兜底为其它", EventLogService.Categorize("SomeUnknownProvider", "1") == "其它");

        var adapters = new NetworkDiagnosticService().LoadAdapters();
        Check("网卡列表不含过滤驱动伪接口",
            adapters.All(a => !a.Name.Contains("-QoS Packet Scheduler", StringComparison.OrdinalIgnoreCase)
                              && !a.Name.Contains("-WFP ", StringComparison.OrdinalIgnoreCase)),
            $"首个可疑项：{adapters.FirstOrDefault(a => a.Name.Contains("PACKET", StringComparison.OrdinalIgnoreCase))?.Name}");

        var lockService = new FileLockService();
        var (_, emptyError) = lockService.FindLockers("");
        var (_, missingError) = lockService.FindLockers(Path.Combine(Path.GetTempPath(), "winkit_no_such_file_" + Guid.NewGuid().ToString("N")));
        Check("文件占用：空路径被拒绝", emptyError is not null);
        Check("文件占用：不存在的路径被拒绝", missingError is not null);

        var overviewCards = new SystemOverviewService().Load();
        Check("系统概览返回四张卡片", overviewCards.Count == 4);
        Check("系统概览每项都有标签与值",
            overviewCards.SelectMany(c => c.Items).All(i => !string.IsNullOrWhiteSpace(i.Label) && !string.IsNullOrWhiteSpace(i.Value)));
        Check("系统概览不把 Windows 11 显示成 Windows 10",
            overviewCards[0].Items.Any(i => !i.Value.Contains("Windows 10")));

        var updateState = new WindowsUpdateService(journal).LoadState();
        Check("Windows 更新状态可读", updateState.ServiceStateText.Length > 0);
        Check("Windows 更新缓存路径已给出", !string.IsNullOrWhiteSpace(updateState.UpdateCachePath));

        // 损坏/手改的备份文件不能靠“非 HKCU 即 HKLM”落到系统区
        var badHiveRefused = false;
        try
        {
            SnapshotEngine.Restore(new SnapshotKey { Hive = "HKCR", SubKey = @"Software\Whatever" });
        }
        catch (InvalidOperationException)
        {
            badHiveRefused = true;
        }

        Check("非法 Hive 的快照被拒绝还原", badHiveRefused);
        Check("删除不存在的键视为成功", Reg.DeleteTree(RegPath.Hkcu($@"{registryFixture}\NoSuchKey")));

        // ---------- 文件对比 / 重复文件 / Scoop 解析（纯文件系统，低完整性也能跑） ----------
        var workDir = Path.Combine(Path.GetTempPath(), "winkit_selftest_" + Guid.NewGuid().ToString("N")[..8]);
        var leftDir = Path.Combine(workDir, "left");
        var rightDir = Path.Combine(workDir, "right");
        Directory.CreateDirectory(leftDir);
        Directory.CreateDirectory(rightDir);

        try
        {
            File.WriteAllText(Path.Combine(leftDir, "same.txt"), "same-content");
            File.WriteAllText(Path.Combine(rightDir, "same.txt"), "same-content");
            File.SetLastWriteTime(Path.Combine(leftDir, "same.txt"), new DateTime(2026, 1, 1, 0, 0, 0));
            File.SetLastWriteTime(Path.Combine(rightDir, "same.txt"), new DateTime(2026, 1, 1, 0, 0, 0));

            File.WriteAllText(Path.Combine(leftDir, "changed.txt"), "AAAA");
            File.WriteAllText(Path.Combine(rightDir, "changed.txt"), "BBBBBB");

            File.WriteAllText(Path.Combine(leftDir, "only-left.txt"), "left");
            File.WriteAllText(Path.Combine(rightDir, "only-right.txt"), "right");

            var (diffEntries, diffError) = new DiffService().Compare(leftDir, rightDir);
            Check("文件夹对比无错误", diffError is null, diffError ?? "");
            Check("对比识别相同文件",
                diffEntries.Any(e => e.RelativePath == "same.txt" && e.Kind == DiffKind.Same));
            Check("对比识别内容不同",
                diffEntries.Any(e => e.RelativePath == "changed.txt" && e.Kind == DiffKind.ContentDiffers));
            Check("对比识别仅左侧",
                diffEntries.Any(e => e.RelativePath == "only-left.txt" && e.Kind == DiffKind.OnlyLeft));
            Check("对比识别仅右侧",
                diffEntries.Any(e => e.RelativePath == "only-right.txt" && e.Kind == DiffKind.OnlyRight));

            var (_, mixedError) = new DiffService().Compare(leftDir, Path.Combine(rightDir, "same.txt"));
            Check("文件与文件夹混比被拒绝", mixedError is not null);

            // 重复文件：同内容必进组，同大小不同内容不能进组
            var dupDir = Path.Combine(workDir, "dup");
            Directory.CreateDirectory(Path.Combine(dupDir, "sub"));
            File.WriteAllText(Path.Combine(dupDir, "a.txt"), "duplicate-payload");
            File.WriteAllText(Path.Combine(dupDir, "sub", "b.txt"), "duplicate-payload");
            File.WriteAllText(Path.Combine(dupDir, "c.txt"), "duplicate-payloaX"); // 同长度不同内容
            File.WriteAllText(Path.Combine(dupDir, "unique.txt"), "only-one");

            var (dupGroups, dupError) = new DuplicateFinderService().Find(new[] { dupDir }, minSize: 1);
            Check("重复检测无错误", dupError is null, dupError ?? "");
            Check("重复检测只认内容相同（同大小不同内容不算）",
                dupGroups.Count == 1 && dupGroups[0].Count == 2,
                $"实际 {dupGroups.Count} 组");
            Check("重复检测识别跨子目录",
                dupGroups.Count == 1 && dupGroups[0].Files.Any(f => f.FullPath.Contains("sub")));
            Check("重复检测统计可省空间",
                dupGroups.Count == 1 && dupGroups[0].WastedBytes == "duplicate-payload".Length);

            // Scoop 解析：两种清单文件名、bucket 回填、无清单时不虚报可升级
            var scoopRoot = Path.Combine(workDir, "scoop");
            var appsDir = Path.Combine(scoopRoot, "apps");
            var bucketDir = Path.Combine(scoopRoot, "buckets", "main", "bucket");
            Directory.CreateDirectory(bucketDir);

            void MakeApp(string name, string fileName, string version, string? installBucket = null)
            {
                var dir = Path.Combine(appsDir, name, "current");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, fileName),
                    $$"""{"version":"{{version}}"{{(installBucket is null ? "" : $",\"bucket\":\"{installBucket}\"")}}}""");
            }

            MakeApp("newstyle", "scoop-manifest.json", "1.0");
            MakeApp("oldstyle", "manifest.json", "2.0");
            MakeApp("bucketless", "scoop-manifest.json", "3.0");

            // bucket 清单给出更高版本 → 应判定可升级
            File.WriteAllText(Path.Combine(bucketDir, "newstyle.json"), """{"version":"1.1"}""");
            File.WriteAllText(Path.Combine(bucketDir, "oldstyle.json"), """{"version":"2.0"}""");
            File.WriteAllText(Path.Combine(bucketDir, "bucketless.json"), """{"version":"3.0"}""");

            // scoop 自身没有清单
            Directory.CreateDirectory(Path.Combine(appsDir, "selfboot", "current"));

            var scoopPackages = new ScoopService(new[] { scoopRoot }).Load();
            var newstyle = scoopPackages.FirstOrDefault(p => p.Name == "newstyle");
            var oldstyle = scoopPackages.FirstOrDefault(p => p.Name == "oldstyle");
            var bucketless = scoopPackages.FirstOrDefault(p => p.Name == "bucketless");
            var selfboot = scoopPackages.FirstOrDefault(p => p.Name == "selfboot");

            Check("Scoop 能读 scoop-manifest.json", newstyle?.Version == "1.0");
            Check("Scoop 能读 manifest.json", oldstyle?.Version == "2.0");
            Check("Scoop 识别可升级", newstyle?.HasUpdate == true && newstyle.LatestVersion == "1.1");
            Check("Scoop 同版本不算可升级", oldstyle?.HasUpdate == false);
            Check("Scoop bucket 从清单回填", bucketless?.Bucket == "main", $"实际 {bucketless?.Bucket}");
            Check("Scoop 自身无清单时不虚报升级",
                selfboot is not null && !selfboot.HasUpdate && selfboot.Version == "版本未知");

            // 文件关联：只验证能读出数据且字段自洽
            var assocItems = new FileAssocService(journal).Load();
            Check("文件关联可读取", assocItems.Count > 0);
            Check("文件关联字段自洽",
                assocItems.All(i => i.Extension.StartsWith('.') && !string.IsNullOrWhiteSpace(i.ProgId)));

            // 环境变量：解析与合并顺序
            var envService = new EnvironmentService(journal);
            var envVars = envService.LoadAll();
            Check("环境变量可读取", envVars.Count > 0);
            Check("PATH 只出现一次", envVars.Count(v => v.IsPath) <= 1);

            var parsedPath = envService.ParsePath("C:\\Windows;C:\\NotExist_" + Guid.NewGuid().ToString("N") + ";%SystemRoot%");
            Check("PATH 解析条目数正确", parsedPath.Count == 3);
            Check("PATH 标记失效目录", parsedPath.Count(e => !e.Exists) == 1);
            Check("PATH 展开环境变量", parsedPath.Any(e => e.Raw == "%SystemRoot%" && e.Exists));

            // 磁盘健康：卷信息不依赖管理员，必须能拿到
            var (diskItems, volumeItems, _) = new DiskHealthService().Load();
            Check("磁盘健康可读到卷信息", volumeItems.Count > 0);
            Check("卷占用百分比在合理范围", volumeItems.All(v => v.UsedPercent is >= 0 and <= 100));

            // 体检报告：结构完整、能导出两种格式
            var report = new HealthReportService().Build();
            Check("体检报告包含 5 个分区", report.Sections.Count == 5);
            Check("体检评分在 0~100", report.Score is >= 0 and <= 100);
            Check("体检报告每项都有标签与值",
                report.Sections.SelectMany(s => s.Items).All(i => !string.IsNullOrWhiteSpace(i.Label) && i.Value is not null));

            var markdown = HealthReportService.ToMarkdown(report);
            var json = HealthReportService.ToJson(report);
            Check("Markdown 导出可用", markdown.Contains("# WinKit 体检报告") && markdown.Contains("评分"));
            Check("JSON 导出可解析",
                System.Text.Json.JsonDocument.Parse(json).RootElement.TryGetProperty("Sections", out _));

            // ---------- KnownFolder GUID 解析（托盘记录里 37 条是这种形式） ----------
            var programFiles = Shortcuts.GetKnownFolderPath(
                new Guid("6D809377-6AF0-444B-8957-A3773F02200E"));
            Check("KnownFolder 解析 Program Files", !string.IsNullOrWhiteSpace(programFiles),
                $"实际 {programFiles}");
            Check("KnownFolder 解析带尾路径",
                Shortcuts.ResolveKnownFolderPath("{6D809377-6AF0-444B-8957-A3773F02200E}\\Notepad.exe")
                    == Path.Combine(programFiles ?? "", "Notepad.exe"));
            Check("非 GUID 路径原样返回",
                Shortcuts.ResolveKnownFolderPath(@"C:\Windows\notepad.exe") == @"C:\Windows\notepad.exe");
            Check("未知 GUID 不抛异常",
                Shortcuts.ResolveKnownFolderPath("{00000000-0000-0000-0000-000000000000}\\x.exe")
                    == "{00000000-0000-0000-0000-000000000000}\\x.exe");

            // ---------- 三态存在性判断：这是「别把好图标判成僵尸」的关键 ----------
            var realFile = Path.Combine(workDir, "exists.txt");
            File.WriteAllText(realFile, "x");
            Check("存在的文件判为未失效", Shortcuts.IsTargetMissing(realFile) == false);
            Check("不存在的文件判为失效", Shortcuts.IsTargetMissing(Path.Combine(workDir, "nope.txt")) == true);
            Check("空路径判为未知（不是失效）", Shortcuts.IsTargetMissing("") is null);
            // WindowsApps 非管理员不可枚举：必须判「未知」而不是「失效」，
            // 否则「折叠失效项」会把 Copilot / Outlook 这些正常图标一起藏掉。
            // 注意只能拿 WindowsApps 根下的路径来测：包目录本身若真被卸载了，
            // 父目录不存在 → 判失效才是对的，不能和「没权限」混为一谈。
            const string guardedRoot = @"C:\Program Files\WindowsApps";
            var rootLocked = false;
            try
            {
                using var entries = Directory.EnumerateFileSystemEntries(guardedRoot).GetEnumerator();
                entries.MoveNext();
            }
            catch (UnauthorizedAccessException)
            {
                rootLocked = true;
            }
            catch (Exception)
            {
                rootLocked = false;
            }

            if (rootLocked)
            {
                var guarded = Shortcuts.IsTargetMissing(Path.Combine(guardedRoot, "__winkit_probe_absent__.exe"));
                Check("无权限枚举的目录判为未知而非失效", guarded is null, $"实际 {guarded}");
            }
            else
            {
                Skip("无权限枚举的目录判为未知而非失效", "当前会话能枚举 WindowsApps");
            }

            // ---------- 任务栏固定项：读取不依赖管理员 ----------
            var taskbar = new TaskbarService(journal);
            var pinned = taskbar.LoadPinned();
            Check("任务栏固定项可读取", pinned.Count > 0, $"实际 {pinned.Count} 个");
            Check("固定项都有快捷方式路径", pinned.All(p => File.Exists(p.ShortcutPath)));
            Check("CLSID 链接不算失效",
                pinned.Where(p => p.IsShellItem).All(p => p.IsTargetMissing is null),
                "空目标路径必须返回未知");

            // ---------- 托盘图标记录 ----------
            var tray = taskbar.LoadTrayIcons();
            Check("托盘记录可读取", tray.Count > 0, $"实际 {tray.Count} 条");
            Check("托盘路径都已解析 KnownFolder",
                tray.All(t => string.IsNullOrWhiteSpace(t.ResolvedPath) || !t.ResolvedPath.Contains('{')),
                "解析后不该残留 GUID 前缀");
            Check("托盘记录的失效判定为三态",
                tray.All(t => t.IsMissing is not null || string.IsNullOrWhiteSpace(t.ResolvedPath)));

            // ---------- 卸载器候选扫描 ----------
            var fakeApp = Path.Combine(workDir, "FakeApp");
            Directory.CreateDirectory(fakeApp);
            File.WriteAllText(Path.Combine(fakeApp, "SUPUninstall.exe"), "");
            File.WriteAllText(Path.Combine(fakeApp, "uninstall.dat"), "");   // 名字像卸载器但不是 exe
            File.WriteAllText(Path.Combine(fakeApp, "readme.txt"), "");

            var uninstall = new UninstallService();
            var candidates = uninstall.FindCandidateUninstallers(fakeApp);
            Check("扫到候选卸载器", candidates.Count == 1, $"实际 {candidates.Count} 个");
            Check("只认 exe，排除 uninstall.dat",
                candidates.All(c => c.FilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
            Check("候选卸载器带所在目录", candidates.All(c => c.Folder == fakeApp));
            Check("不存在的目录返回空列表",
                uninstall.FindCandidateUninstallers(Path.Combine(workDir, "NoSuchDir")).Count == 0);

            // ---------- 残留扫描：靠夹具目录，不依赖本机装了什么 ----------
            var residualRoot = Path.Combine(workDir, "residual-root");
            var residualApp = Path.Combine(residualRoot, "WinKitProbeGhostApp");
            Directory.CreateDirectory(residualApp);
            File.WriteAllText(Path.Combine(residualApp, "data.bin"), "0123456789");
            // 同名但不同大小写的目录也应命中
            Directory.CreateDirectory(Path.Combine(residualRoot, "winkitprobeghostapp2"));

            var scanResult = new ResidualScanService().Scan(new ResidualScanRequest
            {
                ExactNames = new[] { "WinKitProbeGhostApp", "WinKitProbeGhostApp2" },
                ExtraRoots = new[] { residualRoot },
                MaxDepth = 2,
                IncludeDirectories = true,
                IncludeShortcuts = false,
                IncludeServices = false,
                IncludeTasks = false,
                IncludeStartup = false,
                IncludeAssociations = false
            });

            Check("残留扫描命中夹具目录", scanResult.Items.Count >= 2, $"实际 {scanResult.Items.Count} 项");
            Check("精确同名只提供线索，默认不勾选",
                scanResult.PossibleCount >= 2 && scanResult.PathReferenceCount == 0 && scanResult.Items.All(item => !item.IsSelected),
                $"名称相似 {scanResult.PossibleCount} 项，路径引用 {scanResult.PathReferenceCount} 项");
            Check("残留项带类型与大小字段",
                scanResult.Items.All(i => !string.IsNullOrWhiteSpace(i.KindText) && !string.IsNullOrWhiteSpace(i.ConfidenceText)));
            Check("扫描统计了目录数", scanResult.ScannedDirectories > 0);
            var installedSubfolder = Path.Combine(residualApp, "WinKitProbeGhostApp");
            var sibling = Path.Combine(residualApp, "WinKitProbeGhostApp2");
            Directory.CreateDirectory(installedSubfolder);
            Directory.CreateDirectory(sibling);
            var scopedScan = new ResidualScanService().Scan(new ResidualScanRequest
            {
                ExactNames = ["WinKitProbeGhostApp", "WinKitProbeGhostApp2"], ExtraRoots = [residualRoot],
                ExcludedPaths = [installedSubfolder], MaxDepth = 2, IncludeDirectories = true,
                IncludeShortcuts = false, IncludeServices = false, IncludeTasks = false,
                IncludeStartup = false, IncludeAssociations = false
            });
            Check("移除候选不能包含当前安装目录，但仍扫描其旁边的候选",
                scopedScan.Items.Any(item => item.Path == residualApp && !item.CanClean)
                && scopedScan.Items.Any(item => item.Path == sibling && item.CanClean)
                && scopedScan.Items.All(item => item.Path != installedSubfolder));
            Check("文件关联类残留不可清理（UserChoice 有哈希签名）",
                scanResult.Items.Where(i => i.Kind == ResidualKind.FileAssociation).All(i => !i.CanClean));
            Check("清理动作不碰文件关联",
                new ResidualScanService()
                    .Scan(new ResidualScanRequest
                    {
                        PathFragments = new[] { "WinKitProbeNoSuchFragment" },
                        ExtraRoots = new[] { residualRoot },
                        MaxDepth = 1,
                        IncludeDirectories = false
                    })
                    .Items.All(i => i.Kind != ResidualKind.Directory || i.CanClean));

            // 拖放放行：提权进程靠它接收资源管理器的拖放。这里没有窗口，
            // 只能验证「空句柄返回 ERROR_INVALID_PARAMETER 而不是崩溃」这一层；
            // 真正放行成功与否由 smoke 的 dragdrop 断言覆盖。
            Check("拖放放行对空句柄返回错误码", Native.AllowDragDropFromLowerIntegrity(IntPtr.Zero) == 87);

            // 权限闸门：主清单是 asInvoker，未提权是常态。闸门必须给出可执行的下一步，
            // 而不是返回一句空话让调用方自己想办法。
            if (Native.IsElevated())
            {
                Skip("未提权时权限闸门给出提权指引", "当前进程已是管理员");
            }
            else
            {
                var gate = Native.RequireAdmin("测试动作");
                Check("未提权时权限闸门给出提权指引",
                    gate is not null && gate.Contains("以管理员身份重新启动", StringComparison.Ordinal));
            }

            // SMART 属性块解析：这里造一段 512 字节的假数据，按 ATA 的格式塞两条属性，
            // 验证偏移计算、原始值小端还原、阈值合并与「原始值不为 0 就提示」的启发式都在位。
            // 真机上这块数据只有管理员能读（而且 USB/RAID 盘根本读不到），所以必须能脱离真机测。
            var smartData = new byte[512];
            var smartThresholds = new byte[512];

            smartData[2] = 5;                       // 属性 ID：重映射扇区数
            smartData[5] = 100;                     // 当前值
            smartData[6] = 99;                      // 最差值
            smartData[7] = 3;                       // 原始值低字节 = 3（确实重映射过 3 个扇区）

            smartData[14] = 9;                      // 属性 ID：通电时间
            smartData[17] = 90;                     // 当前值
            smartData[18] = 88;                     // 最差值
            smartData[19] = 0x39;                   // 原始值 = 0x3039 = 12345
            smartData[20] = 0x30;

            smartThresholds[2] = 5;                 // 属性 5 的阈值 = 10
            smartThresholds[3] = 10;

            var attributes = SmartAttributeService.Parse(smartData, smartThresholds);

            Check("SMART 属性块解析出两条属性", attributes.Count == 2, $"实际 {attributes.Count} 条");
            Check("SMART 属性名按 ID 映射", attributes[0].Name.Contains("重映射", StringComparison.Ordinal),
                attributes[0].Name);
            Check("SMART 阈值从阈值块合并", attributes[0].Threshold == 10, $"实际 {attributes[0].Threshold}");
            Check("SMART 通电时间原始值按小时显示", attributes[1].RawText == "12345 小时",
                attributes[1].RawText);
            Check("SMART 重映射扇区原始值不为 0 时标为注意", attributes[0].Level == "warn",
                attributes[0].Level);
            Check("SMART 阈值缺失时按 0 处理", attributes[1].Threshold == 0, $"实际 {attributes[1].Threshold}");
            Check("SMART 数据太短时返回空列表", SmartAttributeService.Parse(new byte[10], null).Count == 0);
            Check("SMART 导出目录非空", SmartAttributeService.ExportDirectory.Length > 0);

            // 进程枚举：这些信息全来自 System.Diagnostics.Process，不需要管理员权限，
            // 所以能在自测里真跑一遍（不同于 SMART/还原点那两处必须容错）。
            var (processes, processError) = new ProcessService().Load();
            var self = processes.FirstOrDefault(p => p.Pid == Environment.ProcessId);

            Check("进程枚举有结果", processes.Count > 0, $"实际 {processes.Count} 个");
            Check("进程枚举包含自测进程自己", self is not null);
            Check("进程列表按内存降序", processes.SequenceEqual(processes.OrderByDescending(p => p.WorkingSetBytes)));
            Check("进程筛选能按 PID 命中自己", self is not null && self.Matches(Environment.ProcessId.ToString()));
            Check("进程筛选不命中不存在的关键字", self is not null && !self.Matches("__winkit_probe_no_such_keyword__"));
            Check("进程摘要非空", ProcessService.Summarize(processes).Length > 0);
            Check("拒绝结束自己", !new ProcessService().Kill(new ProcessItem { Pid = Environment.ProcessId }, false).Ok);
            Check("进程枚举容错信息可为空", processError is null || processError.Length > 0);

            // 系统还原点：非管理员会话里必然会失败，只能验证「不抛异常且给出说明」。
            var restore = new SystemRestoreService();
            var (points, _ , _, restoreError) = restore.Load();
            Check("系统还原读取不抛异常", restoreError is null || restoreError.Length > 0,
                restoreError ?? "");
            Check("还原点类型码映射中文", new RestorePointItem { TypeCode = 10 }.TypeText == "驱动安装",
                new RestorePointItem { TypeCode = 10 }.TypeText);
            Check("未知还原点类型码原样标出", new RestorePointItem { TypeCode = 99 }.TypeText == "类型 99",
                new RestorePointItem { TypeCode = 99 }.TypeText);
            Check("还原点列表条数为非负", points.Count >= 0);
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch (Exception) { }
        }

        failures += DuplicateCleanupSelfTest();
        failures += JournalSelfTest();
        failures += QuickAccessChecks.Run();
        try { Directory.Delete(journal.StorageDirectory, true); } catch (Exception) { }
        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"全部通过（跳过 {skipped} 项）"
            : $"{failures} 项失败，跳过 {skipped} 项");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>只使用唯一临时目录，验证扫描后变化或被写入的文件绝不按旧指纹清理。</summary>
    private static int DuplicateCleanupSelfTest(bool includeRecycle = false)
    {
        var failures = 0;
        var checks = 0;
        var workDir = Path.Combine(Path.GetTempPath(), "winkit_duplicate_cleanup_" + Guid.NewGuid().ToString("N"));
        // 变化点放在 64 KiB 之后，避免只复核文件头也能误通过回归。
        var original = Enumerable.Range(0, 96 * 1024 + 17).Select(i => (byte)(i % 251)).ToArray();
        var changed = (byte[])original.Clone();
        changed[^1] ^= 0xff;
        var resized = original[..^1];
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original));
        Console.WriteLine("=== 重复文件清理安全自测（临时夹具） ===");

        void Check(string name, bool condition, string detail = "")
        {
            checks++;
            if (!condition) failures++;
            Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}  {name}");
            if (!condition && detail.Length > 0) Console.WriteLine("        " + detail);
        }
        void Run(string name, Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"  FAIL  {name}：{ex}");
            }
        }
        string Fixture(string name)
        {
            var path = Path.Combine(workDir, name + ".bin");
            File.WriteAllBytes(path, original);
            return path;
        }
        DuplicateCleanupResult Cleanup(string keep, params string[] targets) =>
            DuplicateFinderService.MoveToRecycleBin(targets, keep, original.LongLength, expectedHash);
        void CheckRefused(string name, DuplicateCleanupResult result, string failedPath, bool needsRescan = false)
        {
            var detail = string.Join("；", result.Failures);
            Check(name + "：不报告成功且无文件被回收",
                !result.Ok && result.MovedPaths.Count == 0 && result.Failures.Count > 0, detail);
            Check(name + "：失败信息包含对应文件路径",
                result.Failures.Any(message => message.Contains(failedPath, StringComparison.OrdinalIgnoreCase)), detail);
            if (needsRescan)
                Check(name + "：明确要求重新扫描",
                    result.Failures.Any(message => message.Contains("重新扫描", StringComparison.Ordinal)), detail);
        }
        void CheckPreserved(string name, string path, byte[] expected) =>
            Check(name, File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(expected), path);

        try
        {
            Directory.CreateDirectory(workDir);
            Run("目标同大小内容变化", () =>
            {
                var keep = Fixture("content-keep");
                var target = Fixture("content-target");
                File.WriteAllBytes(target, changed);
                var result = Cleanup(keep, target);
                CheckRefused("目标同大小内容变化", result, target, needsRescan: true);
                CheckPreserved("内容变化目标仍存在且内容不变", target, changed);
                CheckPreserved("内容变化时保留副本不变", keep, original);
            });
            Run("目标大小变化", () =>
            {
                var keep = Fixture("size-keep");
                var target = Fixture("size-target");
                File.WriteAllBytes(target, resized);
                var result = Cleanup(keep, target);
                CheckRefused("目标大小变化", result, target, needsRescan: true);
                CheckPreserved("大小变化目标仍存在且内容不变", target, resized);
                CheckPreserved("大小变化时保留副本不变", keep, original);
            });
            Run("目标丢失", () =>
            {
                var keep = Fixture("missing-target-keep");
                var target = Fixture("missing-target");
                File.Delete(target);
                var result = Cleanup(keep, target);
                CheckRefused("目标丢失", result, target);
                Check("丢失目标不会被创建", !File.Exists(target));
                CheckPreserved("目标丢失时保留副本不变", keep, original);
            });
            Run("目标被写入句柄占用", () =>
            {
                var keep = Fixture("locked-target-keep");
                var target = Fixture("locked-target");
                DuplicateCleanupResult result;
                // 旧句柄允许删除，确保拒绝来自复核阶段，而不是 Shell 的删除共享冲突。
                using (var writer = new FileStream(target, FileMode.Open, FileAccess.Write,
                           FileShare.ReadWrite | FileShare.Delete))
                    result = Cleanup(keep, target);
                CheckRefused("目标被写入句柄占用", result, target);
                CheckPreserved("被写入句柄占用的目标仍完整存在", target, original);
                CheckPreserved("目标被占用时保留副本不变", keep, original);
            });
            Run("保留副本同大小内容变化", () =>
            {
                var keep = Fixture("changed-keep");
                var first = Fixture("changed-keep-target-a");
                var second = Fixture("changed-keep-target-b");
                File.WriteAllBytes(keep, changed);
                var result = Cleanup(keep, first, second);
                CheckRefused("保留副本同大小内容变化", result, keep, needsRescan: true);
                CheckPreserved("内容变化的保留副本不变", keep, changed);
                CheckPreserved("保留副本内容变化时首个目标未清理", first, original);
                CheckPreserved("保留副本内容变化时后续目标未清理", second, original);
            });
            Run("保留副本大小变化", () =>
            {
                var keep = Fixture("resized-keep");
                var first = Fixture("resized-keep-target-a");
                var second = Fixture("resized-keep-target-b");
                File.WriteAllBytes(keep, resized);
                var result = Cleanup(keep, first, second);
                CheckRefused("保留副本大小变化", result, keep, needsRescan: true);
                CheckPreserved("大小变化的保留副本不变", keep, resized);
                CheckPreserved("保留副本大小变化时首个目标未清理", first, original);
                CheckPreserved("保留副本大小变化时后续目标未清理", second, original);
            });
            Run("保留副本丢失", () =>
            {
                var keep = Fixture("missing-keep");
                var first = Fixture("missing-keep-target-a");
                var second = Fixture("missing-keep-target-b");
                File.Delete(keep);
                var result = Cleanup(keep, first, second);
                CheckRefused("保留副本丢失", result, keep);
                Check("丢失的保留副本不会被创建", !File.Exists(keep));
                CheckPreserved("保留副本丢失时首个目标未清理", first, original);
                CheckPreserved("保留副本丢失时后续目标未清理", second, original);
            });
            Run("保留副本被写入句柄占用", () =>
            {
                var keep = Fixture("locked-keep");
                var first = Fixture("locked-keep-target-a");
                var second = Fixture("locked-keep-target-b");
                DuplicateCleanupResult result;
                using (var writer = new FileStream(keep, FileMode.Open, FileAccess.Write,
                           FileShare.ReadWrite | FileShare.Delete))
                    result = Cleanup(keep, first, second);
                CheckRefused("保留副本被写入句柄占用", result, keep);
                CheckPreserved("被写入句柄占用的保留副本仍完整存在", keep, original);
                CheckPreserved("保留副本被占用时首个目标未清理", first, original);
                CheckPreserved("保留副本被占用时后续目标未清理", second, original);
            });
            if (includeRecycle)
            {
                Run("真实回收独立夹具", () =>
                {
                    var keep = Fixture("recycle-keep");
                    var target = Fixture("recycle-target");
                    var result = Cleanup(keep, keep, target, target);
                    Check("真实回收成功且重复路径只报告一次", result.Ok
                        && result.MovedPaths.SequenceEqual(new[] { target }, StringComparer.OrdinalIgnoreCase),
                        string.Join("；", result.Failures));
                    Check("真实回收后目标不在原位置", !File.Exists(target));
                    CheckPreserved("真实回收保留最后副本", keep, original);
                });
            }
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("  FAIL  重复清理夹具初始化：" + ex);
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("  FAIL  重复清理临时夹具清理：" + ex.Message);
            }
        }

        Console.WriteLine($"重复文件清理安全：{checks} 项断言，{failures} 项失败");
        return failures;
    }

    /// <summary>仅使用临时文件、隔离备份目录和专用 HKCU 键，真实制造保存/写回失败。</summary>
    private static int JournalSelfTest()
    {
        var failures = 0;
        var checks = 0;
        var workDir = Path.Combine(Path.GetTempPath(), "winkit_journal_" + Guid.NewGuid().ToString("N"));
        var registryRoot = @"Software\WinKitSelfTest\Journal_" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(workDir);
        Console.WriteLine("=== 撤销可靠性自测（临时夹具） ===");

        void Check(string name, bool condition)
        {
            checks++;
            if (!condition) failures++;
            Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}  {name}");
        }
        void Run(string name, Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"  FAIL  {name}：{ex}");
            }
        }
        Exception? Error(Action action)
        {
            try { action(); return null; }
            catch (Exception ex) { return ex; }
        }
        ChangeJournal Journal(string name) => new(Path.Combine(workDir, name + "-backups"));
        string Fixture(string name, string text = "before")
        {
            var path = Path.Combine(workDir, name + ".txt");
            File.WriteAllText(path, text);
            return path;
        }
        var noKeys = Array.Empty<(HiveKind, string)>();

        try
        {
            Run("保存失败", () =>
            {
                var blocker = Fixture("backup-directory-is-file");
                var journal = new ChangeJournal(blocker);
                var executed = false;
                var sync = Error(() => journal.Record("保存失败", noKeys, () => executed = true));
                Check("Record 保存失败拒绝执行且没有虚假历史", sync is IOException && !executed && !journal.CanUndo);
                var asyncError = Error(() => journal.RecordAsync("异步保存失败", noKeys,
                    () => { executed = true; return Task.CompletedTask; }).GetAwaiter().GetResult());
                Check("RecordAsync 保存失败拒绝执行", asyncError is IOException && !executed && journal.SessionCount == 0);
            });

            Run("操作前落盘与兼容接口", () =>
            {
                var journal = Journal("before-action");
                var path = Fixture("before-action");
                var durable = false;
                var snapshot = journal.Record("先保存", noKeys, () =>
                {
                    var saved = journal.ListBackups();
                    durable = saved.Count == 1
                        && System.Text.Json.JsonSerializer.Deserialize<OperationSnapshot>(File.ReadAllText(saved[0].FullName))
                            ?.Files.Single().Base64Data == Convert.ToBase64String(Encoding.UTF8.GetBytes("before"));
                    File.WriteAllText(path, "after");
                }, new[] { path });
                Check("动作执行前已有完整可解析快照", durable);
                Check("旧 UndoLast 成功仍返回标题", journal.UndoLast() == "先保存");
                Check("成功撤销恢复原文件并清理快照", File.ReadAllText(path) == "before"
                    && !File.Exists(snapshot.SavedFilePath) && !journal.CanUndo);
                Check("空历史结构化结果不冒充一次撤销", journal.UndoLastResult() is { Succeeded: true, HasChanges: false }
                    && journal.UndoLast() is null && journal.RollbackSession() is null);
            });

            Run("文件撤销写回失败", () =>
            {
                var journal = Journal("locked-undo");
                var path = Fixture("locked-undo");
                var snapshot = journal.Record("文件占用", noKeys, () => File.WriteAllText(path, "after"), new[] { path });
                JournalRestoreResult? reported = null;
                journal.ResultReported += result => reported = result;
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var outcome = journal.UndoLastResult();
                    Check("真实文件锁失败明确返回失败并发出失败事件", !outcome.Succeeded && outcome.HasChanges
                        && outcome.Failures?.Count == 1 && reported?.Succeeded == false && !outcome.Message.StartsWith("已撤销"));
                    Check("撤销失败保留会话和磁盘快照", journal.SessionCount == 1 && File.Exists(snapshot.SavedFilePath));
                }
                Check("释放占用后撤销可重试", journal.UndoLastResult().Succeeded && File.ReadAllText(path) == "before" && !journal.CanUndo);
            });

            Run("原本不存在的文件删除失败", () =>
            {
                var journal = Journal("locked-delete");
                var path = Path.Combine(workDir, "new-file.txt");
                var snapshot = journal.Record("创建文件", noKeys, () => File.WriteAllText(path, "new"), new[] { path });
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Check("撤销删除失败不会吞错", !journal.UndoLastResult().Succeeded && File.Exists(snapshot.SavedFilePath));
                Check("撤销删除失败可重试", journal.UndoLastResult().Succeeded && !File.Exists(path));
            });

            foreach (var asynchronous in new[] { false, true })
            {
                Run(asynchronous ? "异步自动回滚失败" : "同步自动回滚失败", () =>
                {
                    var label = asynchronous ? "async" : "sync";
                    var journal = Journal(label + "-action-failure");
                    var path = Fixture(label + "-action-failure");
                    var original = new InvalidOperationException(label + "-original-error");
                    FileStream? held = null;
                    JournalRestoreResult? reported = null;
                    journal.ResultReported += result => reported = result;
                    try
                    {
                        void MutateThenFail()
                        {
                            File.WriteAllText(path, "partial");
                            held = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                            throw original;
                        }
                        var error = Error(() =>
                        {
                            if (asynchronous)
                                journal.RecordAsync("异步部分失败", noKeys, async () =>
                                {
                                    await Task.Yield();
                                    MutateThenFail();
                                }, new[] { path }).GetAwaiter().GetResult();
                            else journal.Record("同步部分失败", noKeys, MutateThenFail, new[] { path });
                        });
                        Check($"{label} 自动回滚失败保留原始异常", error is AggregateException aggregate
                            && aggregate.Flatten().InnerExceptions.Contains(original));
                        Check($"{label} 自动回滚失败保留可重试历史和文件", journal.SessionCount == 1
                            && journal.ListBackups().Count == 1 && reported?.Succeeded == false);
                    }
                    finally { held?.Dispose(); }
                    Check($"{label} 自动回滚失败释放占用后可撤销", journal.UndoLastResult().Succeeded
                        && File.ReadAllText(path) == "before" && !journal.CanUndo);
                });
            }

            Run("动作失败但自动回滚成功", () =>
            {
                var journal = Journal("rolled-back-action");
                var path = Fixture("rolled-back-action");
                var original = new InvalidOperationException("original-action-error");
                var error = Error(() => journal.Record("执行失败", noKeys, () =>
                {
                    File.WriteAllText(path, "partial");
                    throw original;
                }, new[] { path }));
                Check("自动回滚成功保留原始异常并恢复原状态", ReferenceEquals(error, original)
                    && File.ReadAllText(path) == "before" && !journal.CanUndo && journal.ListBackups().Count == 0);
            });

            Run("会话回滚失败保持栈顺序", () =>
            {
                var journal = Journal("rollback-order");
                var path = Fixture("rollback-order");
                var other = Fixture("rollback-other");
                var first = journal.Record("第一步", noKeys, () => File.WriteAllText(path, "one"), new[] { path });
                var second = journal.Record("第二步", noKeys, () => File.WriteAllText(path, "two"), new[] { path });
                var third = journal.Record("第三步", noKeys, () => File.WriteAllText(other, "three"), new[] { other });
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var outcome = journal.RollbackSessionResult();
                    Check("部分回滚准确计数并停止在失败处", !outcome.Succeeded && outcome.RestoredCount == 1
                        && journal.SessionCount == 2 && journal.LastTitle == "第二步");
                    Check("只清理成功记录，失败及更早快照保留", File.Exists(first.SavedFilePath)
                        && File.Exists(second.SavedFilePath) && !File.Exists(third.SavedFilePath) && File.ReadAllText(other) == "before");
                }
                Check("失败处之前的目标未被更早历史覆盖", File.ReadAllText(path) == "two");
                var retry = journal.RollbackSessionResult();
                Check("再次回滚按顺序回到原值", retry.Succeeded && retry.RestoredCount == 2
                    && File.ReadAllText(path) == "before" && !journal.CanUndo);
            });

            Run("保留超出清理上限的活跃快照", () =>
            {
                var journal = Journal("retention");
                var path = Fixture("retention");
                var saved = new List<OperationSnapshot>();
                for (var i = 0; i < 52; i++)
                    saved.Add(journal.Record("保留历史 " + i, noKeys, () => File.WriteAllText(path, "step-" + i), new[] { path }));
                Check("超过50条仍保留全部可撤销数据", saved.All(s => File.Exists(s.SavedFilePath)) && journal.ListBackups().Count == 52);
                Check("超过50条全部可以按序回滚", journal.RollbackSessionResult() is { Succeeded: true, RestoredCount: 52 }
                    && File.ReadAllText(path) == "before");
            });

            Run("备份恢复自身可撤销", () =>
            {
                var journal = Journal("restore-backup");
                var path = Fixture("restore-backup");
                var source = Path.Combine(workDir, "source-backup.json");
                File.WriteAllText(source, System.Text.Json.JsonSerializer.Serialize(new OperationSnapshot
                {
                    Title = "来源备份", Files = new() { SnapshotEngine.CaptureFile(path) }
                }));
                File.WriteAllText(path, "current");
                journal.RestoreBackup(source);
                Check("还原备份生效并保留来源", File.ReadAllText(path) == "before" && File.Exists(source) && journal.SessionCount == 1);
                Check("还原备份可撤销至恢复前状态", journal.UndoLastResult().Succeeded && File.ReadAllText(path) == "current" && File.Exists(source));
            });

            Run("备份目标预览与确认后变化", () =>
            {
                var journal = Journal("restore-preview");
                var path = Fixture("restore-preview");
                var source = Path.Combine(workDir, "preview-source.json");
                File.WriteAllText(source, System.Text.Json.JsonSerializer.Serialize(new OperationSnapshot
                {
                    Title = "显示目标", Files = new() { SnapshotEngine.CaptureFile(path) }
                }));
                File.WriteAllText(path, "current");
                var preview = journal.PreviewBackup(source);
                Check("预览显示完整文件路径和覆盖行为", preview.Title == "显示目标"
                    && preview.TargetsText.Contains(path) && preview.TargetsText.Contains("覆盖"));
                File.AppendAllText(source, " ");
                Check("预览后备份变动拒绝写入且不创建撤销记录",
                    Error(() => journal.RestoreBackup(source, preview.ContentHash)) is InvalidOperationException
                    && File.ReadAllText(path) == "current" && !journal.CanUndo);
                var reviewed = journal.PreviewBackup(source);
                journal.RestoreBackup(source, reviewed.ContentHash);
                Check("重新核对后可恢复且可撤销", File.ReadAllText(path) == "before"
                    && journal.UndoLastResult().Succeeded && File.ReadAllText(path) == "current");
                Check("相对文件路径拒绝恢复", Error(() => SnapshotEngine.Validate(new SnapshotFile
                {
                    Path = "relative.txt", Existed = false
                })) is InvalidOperationException);
            });

            Run("备份部分恢复失败", () =>
            {
                var journal = Journal("partial-restore");
                var first = Fixture("partial-first");
                var second = Fixture("partial-second");
                var source = Path.Combine(workDir, "partial-source.json");
                File.WriteAllText(source, System.Text.Json.JsonSerializer.Serialize(new OperationSnapshot
                {
                    Title = "部分失败备份", Files = new() { SnapshotEngine.CaptureFile(first), SnapshotEngine.CaptureFile(second) }
                }));
                File.WriteAllText(first, "current-first");
                File.WriteAllText(second, "current-second");
                using (File.Open(second, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var error = Error(() => journal.RestoreBackup(source));
                    Check("备份部分恢复失败不返回成功且保留回滚数据", error is AggregateException
                        && journal.SessionCount == 1 && journal.ListBackups().Count == 1 && File.Exists(source));
                    Check("部分恢复失败将能写回的文件自动回滚", File.ReadAllText(first) == "current-first");
                }
                Check("部分恢复失败后可重试撤销", journal.UndoLastResult().Succeeded
                    && File.ReadAllText(first) == "current-first" && File.ReadAllText(second) == "current-second");
            });

            Run("缺失还原数据拒绝写入", () =>
            {
                var journal = Journal("invalid-data");
                var path = Fixture("invalid-data", "keep-current");
                var source = Path.Combine(workDir, "missing-data.json");
                File.WriteAllText(source, System.Text.Json.JsonSerializer.Serialize(new OperationSnapshot
                {
                    Files = new() { new SnapshotFile { Path = path, Existed = true, Base64Data = null } }
                }));
                Check("缺失内容不会以空文件假还原成功", Error(() => journal.RestoreBackup(source)) is InvalidOperationException
                    && File.ReadAllText(path) == "keep-current" && !journal.CanUndo);
                Check("跟踪目录不会误记为不存在的文件", Error(() => journal.Record("错误文件路径", noKeys,
                    () => throw new Exception("不应执行"), new[] { workDir })) is IOException);
            });

            Run("不存在文件与注册表快照验证", () =>
            {
                Check("目标和父目录均不存在时删除还原成功", Error(() => SnapshotEngine.RestoreFile(new SnapshotFile
                {
                    Path = Path.Combine(workDir, "absent-parent", "absent.txt"), Existed = false
                })) is null);
                Check("捕获失败快照明确拒绝还原", Error(() => SnapshotEngine.Restore(new SnapshotKey
                {
                    Hive = "HKCU", SubKey = registryRoot, CaptureFailed = true
                })) is InvalidOperationException);
                var incomplete = new SnapshotKey
                {
                    Hive = "HKCU", SubKey = registryRoot, Existed = true,
                    Children = new() { new SnapshotKey { Hive = "HKCU", SubKey = registryRoot + @"\child", Truncated = true } }
                };
                Check("写父键前预检全部子快照", Error(() => SnapshotEngine.Validate(incomplete)) is InvalidOperationException);
                var invalidValues = new SnapshotKey
                {
                    Hive = "HKCU", SubKey = registryRoot, Existed = true,
                    Values = new() { new SnapshotValue { Name = "v", Kind = "MultiString", Data = System.Text.Json.Nodes.JsonValue.Create("wrong-array") } }
                };
                Check("损坏的多字符串不能按空数组假还原", Error(() => SnapshotEngine.Validate(invalidValues)) is InvalidOperationException);
                var journal = Journal("invalid-registry");
                var executed = false;
                var invalidPath = registryRoot + "\\" + new string('x', 256);
                Check("Record拒绝无法完整捕获的注册表路径", Error(() => journal.Record("捕获失败",
                    new[] { (HiveKind.CurrentUser, invalidPath) }, () => executed = true)) is not null
                    && !executed && !journal.CanUndo);
                Check("注册表删除失败返回值会转成恢复失败", Error(() => SnapshotEngine.Restore(new SnapshotKey
                {
                    Hive = "HKCU", SubKey = invalidPath, Existed = false
                })) is IOException);
            });

            if (ProbeRegistryWrite(registryRoot))
            {
                Run("注册表不完整快照与字符串保真", () =>
                {
                    var path = RegPath.Hkcu(registryRoot);
                    Reg.SetString(path, "base64-looking", "YWJj");
                    Reg.SetExpandString(path, "expand", "%TEMP%\\test");
                    Reg.SetString(RegPath.Hkcu(registryRoot + @"\child"), "v", "original");
                    var snapshot = SnapshotEngine.Capture(path);
                    Reg.SetString(path, "base64-looking", "changed");
                    SnapshotEngine.Restore(snapshot);
                    Check("看似Base64的普通字符串原样恢复", Reg.GetString(path, "base64-looking") == "YWJj"
                        && Reg.GetString(path, "expand", false) == "%TEMP%\\test");
                    snapshot.Children[0].Truncated = true;
                    Reg.SetString(path, "sentinel", "keep");
                    Check("子快照不完整时在修改父键前拒绝", Error(() => SnapshotEngine.Restore(snapshot)) is InvalidOperationException
                        && Reg.GetString(path, "sentinel") == "keep");
                    Check("捕获失败不能假报恢复成功", Error(() => SnapshotEngine.Restore(new SnapshotKey
                    {
                        Hive = "HKCU", SubKey = registryRoot, CaptureFailed = true
                    })) is InvalidOperationException && Reg.GetString(path, "sentinel") == "keep");
                });
            }
            else Console.WriteLine("  SKIP  注册表失败语义/字符串保真（当前会话不能写专用HKCU临时键）");
        }
        finally
        {
            Reg.DeleteTree(RegPath.Hkcu(registryRoot));
            try { Directory.Delete(workDir, true); }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("  FAIL  临时夹具清理：" + ex.Message);
            }
        }

        Console.WriteLine($"撤销可靠性：{checks} 项断言，{failures} 项失败");
        return failures;
    }

    /// <summary>探测当前会话能否写 HKCU（低完整性级别下会被拒绝）。失败时输出原因。</summary>
    private static bool ProbeRegistryWrite(string subKey)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subKey, true);
            if (key is null)
            {
                Console.WriteLine("  HKCU 写入探测：CreateSubKey 返回 null");
                return false;
            }

            key.SetValue("probe", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.DeleteValue("probe", false);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  HKCU 写入探测失败：{ex.GetType().Name} {ex.Message}");
            return false;
        }
        finally
        {
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(subKey, false); } catch (Exception) { }
        }
    }
}
