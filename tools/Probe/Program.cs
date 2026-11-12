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
    /// 写入自测：只在 HKCU 下创建临时项，验证「改动 → 快照 → 撤销」是否精确还原。
    /// </summary>
    private static int SelfTest()
    {
        var failures = 0;
        var journal = new ChangeJournal();
        var testRoot = @"Software\Classes\*\shell\WinKitSelfTest";
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

        Console.WriteLine("=== WinKit 写入自测（仅 HKCU） ===");
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

        // 3. 屏蔽列表写入 → 撤销
        var blockedKey = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\BlockedSelfTest";
        var fakeClsid = "{00000000-WINKIT-SELFTEST-0000-0000000000}";
        journal.Record("自测-屏蔽", new[] { (HiveKind.CurrentUser, blockedKey) },
            () => Reg.SetString(RegPath.Hkcu(blockedKey), fakeClsid, ""));
        Check("写入屏蔽项", Reg.HasValue(RegPath.Hkcu(blockedKey), fakeClsid));

        journal.UndoLast();
        Check("撤销后屏蔽项消失", !Reg.HasValue(RegPath.Hkcu(blockedKey), fakeClsid));

        // 清理
        Reg.DeleteTree(testPath);
        Reg.DeleteTree(RegPath.Hkcu(blockedKey));
        Check("清理完成（测试键不存在）", !Reg.Exists(testPath));

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "全部通过" : $"{failures} 项失败");
        return failures == 0 ? 0 : 1;
    }
}
