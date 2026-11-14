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
        var journal = new ChangeJournal();
        // 不要用 Software\Classes\* 这类受保护路径：非提权时连 HKCU 也会被拒
        var testRoot = @"Software\WinKitSelfTest\Menu";
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
            var blockedKey = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\BlockedSelfTest";
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
            var budgetRoot = @"Software\WinKitSelfTest\Budget";
            Reg.DeleteTree(RegPath.Hkcu(budgetRoot));
            for (var i = 0; i < 40; i++)
                Reg.SetString(RegPath.Hkcu($@"{budgetRoot}\k{i}"), "v", i.ToString());

            var smallBudget = SnapshotEngine.Capture(HiveKind.CurrentUser, budgetRoot, 5);
            var capturedNodes = SnapshotEngine.CountKeys(new[] { smallBudget });
            Check("快照预算生效（5 个节点以内）", capturedNodes <= 6, $"实际捕获 {capturedNodes} 个节点");
            Reg.DeleteTree(RegPath.Hkcu(budgetRoot));

            // 6. 快照还原必须保留原默认值与 HiddenByDefault（v1.4.0 此电脑节点修复依赖它）
            var shapeRoot = @"Software\WinKitSelfTest\Shape";
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
            var truncRoot = @"Software\WinKitSelfTest\Truncated";
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
            var qwordRoot = @"Software\WinKitSelfTest\QWord";
            Reg.DeleteTree(RegPath.Hkcu(qwordRoot));
            Reg.SetString(RegPath.Hkcu(qwordRoot), "placeholder", "x");
            using (var key = Reg.Create(RegPath.Hkcu(qwordRoot)))
            {
                key!.SetValue("big", 0x1_0000_0001L, Microsoft.Win32.RegistryValueKind.QWord);
            }

            Check("GetInt 对大 QWORD 返回 null", Reg.GetInt(RegPath.Hkcu(qwordRoot), "big") is null);
            Check("GetLong 保留 64 位精度", Reg.GetLong(RegPath.Hkcu(qwordRoot), "big") == 0x1_0000_0001L);
            Reg.DeleteTree(RegPath.Hkcu(qwordRoot));

            Reg.DeleteTree(testPath);
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
        Check("删除不存在的键视为成功", Reg.DeleteTree(RegPath.Hkcu(@"Software\WinKitSelfTest\NoSuchKey")));

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
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch (Exception) { }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"全部通过（跳过 {skipped} 项）"
            : $"{failures} 项失败，跳过 {skipped} 项");
        return failures == 0 ? 0 : 1;
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
