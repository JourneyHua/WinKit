using System.IO;
using System.Text;

namespace WinKit.Services;

/// <summary>
/// 快速访问里「固定文件夹」的顺序存档。
///
/// 为什么要自己存：资源管理器的固定顺序由 Shell 内部维护（固定动作的时间戳决定先后），
/// 没有公开的写入接口——所以「在程序里排好序 → 关掉程序 → 再打开」本来会退回原样。
/// 这里把用户排好的顺序记成本地文件（每行一个路径，按顺序），
/// 每次加载时按存档重排显示；要让资源管理器本身也跟上，仍走「取消全部固定 + 按序重新固定」。
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
