using System.IO;
using System.Text;

namespace WinKit.Services;

/// <summary>
/// 快速访问里「固定文件夹」的顺序存档。
///
/// 只保存在 WinKit 内尚未应用的顺序（每行一个路径），恢复草稿不会写入 Explorer。
/// 资源管理器没有公开的重排接口；只有明确应用时才会移动必要的固定项。
/// </summary>
public sealed class QuickAccessOrderStore
{
    private readonly string _filePath;

    /// <param name="filePath">测试时可注入临时路径；默认落在 %LOCALAPPDATA%\WinKit\quickaccess-order.txt。</param>
    public QuickAccessOrderStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinKit",
            "quickaccess-order.txt");
    }

    public string FilePath => _filePath;

    /// <summary>
    /// 读存档。文件不存在、读失败、内容空都返回空列表——存档丢了就当没排过序，
    /// 不该因此让整个页面报错。
    /// </summary>
    public List<string> Load()
    {
        var result = new List<string>();
        try
        {
            if (!File.Exists(_filePath)) return result;

            foreach (var line in File.ReadAllLines(_filePath, Encoding.UTF8))
            {
                var path = line.Trim();
                if (path.Length == 0) continue;
                if (result.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                result.Add(path);
            }
        }
        catch (Exception)
        {
            // 存档读不出来不影响使用，静默当空
        }

        return result;
    }

    /// <summary>
    /// 写存档。写失败要抛出去——用户排了半天顺序却以为存好了，比直接报错更糟。
    /// </summary>
    public void Save(IEnumerable<string> orderedPaths)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var lines = orderedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        File.WriteAllLines(_filePath, lines, new UTF8Encoding(false));
    }
}
