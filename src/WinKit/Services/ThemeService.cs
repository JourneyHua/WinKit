using System.IO;
using System.Text.Json;
using System.Windows;

namespace WinKit.Services;

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum TableDensity
{
    Compact,
    Comfortable
}

/// <summary>
/// 界面偏好：窗口位置/大小、上次停留模块、导航分组折叠状态、表格密度、主题。
/// 存 %LOCALAPPDATA%\WinKit\ui.json —— 与 Backups/Reports/Logs 放一起，可读可删，不污染注册表。
/// </summary>
public sealed class UiSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public TableDensity Density { get; set; } = TableDensity.Compact;

    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }

    public string LastModuleKey { get; set; } = "overview";

    /// <summary>被用户折叠的分组名；不在列表里 = 展开。</summary>
    public List<string> CollapsedGroups { get; set; } = new();

    /// <summary>用户手动展开过的分组（配合"默认只展开当前组"）。</summary>
    public List<string> ExpandedGroups { get; set; } = new();
}

public static class UiSettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinKit", "ui.json");

    public static UiSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new UiSettings();

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<UiSettings>(json, Options);
            return settings ?? new UiSettings();
        }
        catch (Exception)
        {
            // 损坏的配置不该阻止启动，回落到默认值
            return new UiSettings();
        }
    }

    public static void Save(UiSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception)
        {
            // 保存失败不影响使用
        }
    }
}

/// <summary>
/// 图标字体探测：Segoe Fluent Icons 在 Win10 1809+ / Win11 才有，
/// 缺失时界面会显示成方框（.notdef），所以启动时探测一次并决定是否降级。
/// </summary>
public static class IconFont
{
    private static bool? _available;

    /// <summary>图标字体是否可用；不可用时界面应隐藏图标列而不是显示豆腐块。</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available.HasValue) return _available.Value;

            try
            {
                var typeface = new System.Windows.Media.Typeface(
                    new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    System.Windows.FontStyles.Normal,
                    System.Windows.FontWeights.Normal,
                    System.Windows.FontStretches.Normal);

                var probe = new System.Windows.Media.FormattedText(
                    "\uE700",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    typeface, 15, System.Windows.Media.Brushes.Black, 96);

                // 缺字形时 WPF 渲染 .notdef，宽度会明显偏小
                _available = probe.Width > 8;
            }
            catch (Exception)
            {
                _available = false;
            }

            return _available.Value;
        }
    }
}

/// <summary>主题切换：整体替换资源字典（不能改画刷颜色——XAML 里的画刷是冻结的）。</summary>
public static class ThemeService
{
    private const string LightColors = "Themes/Colors.xaml";
    private const string DarkColors = "Themes/Colors.Dark.xaml";
    private const string Controls = "Themes/Controls.xaml";

    public static AppTheme Current { get; private set; } = AppTheme.System;

    public static bool IsDarkActive => Current switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => IsSystemDark()
    };

    public static bool IsSystemDark()
    {
        try
        {
            // AppsUseLightTheme = 0 → 系统处于深色模式
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 应用主题：只替换配色字典。
    /// 视图与样式里的所有 Brush 引用都是 DynamicResource，会随字典替换自动重解析，
    /// 因此不需要重建 Controls.xaml，也不会丢控件已绑定的样式实例。
    /// </summary>
    public static void Apply(AppTheme mode)
    {
        Current = mode;
        var app = Application.Current;
        if (app is null) return;

        var dark = IsDarkActive;
        var dicts = app.Resources.MergedDictionaries;

        var colorsIndex = -1;
        for (var i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Colors", StringComparison.OrdinalIgnoreCase))
            {
                colorsIndex = i;
                break;
            }
        }

        var newColors = new ResourceDictionary
        {
            Source = new Uri(dark ? DarkColors : LightColors, UriKind.Relative)
        };

        if (colorsIndex >= 0) dicts[colorsIndex] = newColors;
        else dicts.Insert(0, newColors);
    }
}
