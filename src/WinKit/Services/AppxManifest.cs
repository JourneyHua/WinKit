using System.IO;
using System.Xml.Linq;

namespace WinKit.Services;

public sealed class ManifestScopeInfo
{
    public bool Found { get; init; }
    public string Path { get; init; } = "";
    public bool DeclaresContextMenu { get; init; }
    public List<string> Clsids { get; init; } = new();
    public List<string> ScopeTypes { get; init; } = new();
}

/// <summary>
/// 读打包应用的 AppxManifest.xml，取出 windows.fileExplorerContextMenus 声明的 CLSID 与作用域。
/// 注册表里的 PackagedCom\Class 只是“包内所有 COM 类”，不等于右键菜单项，清单才是权威。
/// </summary>
public static class AppxManifest
{
    private static readonly string[] CandidateRoots =
    {
        @"C:\ProgramData\Microsoft\Windows\AppRepository\Packages",
        @"C:\Program Files\WindowsApps"
    };

    public static ManifestScopeInfo Read(string package, string? packageRootFolder)
    {
        foreach (var candidate in EnumerateCandidates(package, packageRootFolder))
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var info = Parse(candidate);
                if (info is not null) return info;
            }
            catch (Exception)
            {
                // 权限不足或文件损坏，继续尝试下一个路径
            }
        }

        return new ManifestScopeInfo { Found = false };
    }

    private static IEnumerable<string> EnumerateCandidates(string package, string? rootFolder)
    {
        foreach (var root in CandidateRoots)
            yield return System.IO.Path.Combine(root, package, "AppxManifest.xml");

        if (!string.IsNullOrWhiteSpace(rootFolder))
        {
            yield return System.IO.Path.Combine(rootFolder!, "AppxManifest.xml");

            var parent = Directory.GetParent(rootFolder!.TrimEnd('\\'));
            if (parent is not null)
                yield return System.IO.Path.Combine(parent.FullName, "AppxManifest.xml");
        }
    }

    private static ManifestScopeInfo? Parse(string path)
    {
        var doc = XDocument.Load(path, LoadOptions.None);
        var root = doc.Root;
        if (root is null) return null;

        var menus = root
            .Descendants()
            .Where(e => e.Name.LocalName == "FileExplorerContextMenus")
            .ToList();

        var clsids = new List<string>();
        var scopes = new List<string>();

        foreach (var menusNode in menus)
        {
            // 不同版本清单用 <desktop4:Item> 或 <desktop5:ItemType>，两种都要认
            foreach (var item in menusNode.Descendants()
                         .Where(e => e.Name.LocalName is "Item" or "ItemType"))
            {
                var type = (string?)item.Attribute("Type") ?? "*";
                if (!scopes.Contains(type, StringComparer.OrdinalIgnoreCase))
                    scopes.Add(type);

                foreach (var verb in item.Descendants().Where(e => e.Name.LocalName == "Verb"))
                {
                    var clsid = (string?)verb.Attribute("Clsid");
                    if (string.IsNullOrWhiteSpace(clsid)) continue;

                    // 清单里的 GUID 不带花括号
                    var normalized = clsid!.Trim().Trim('{', '}');
                    if (!clsids.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                        clsids.Add(normalized);
                }
            }
        }

        return new ManifestScopeInfo
        {
            Found = true,
            Path = path,
            DeclaresContextMenu = clsids.Count > 0 || menus.Count > 0,
            Clsids = clsids,
            ScopeTypes = scopes
        };
    }

    /// <summary>把清单里的 Type 字符串映射成界面上的域。</summary>
    public static MenuDomainKind? MapScope(string type) => type.ToLowerInvariant() switch
    {
        "*" or "file" or "files" => MenuDomainKind.File,
        "directory" or "folder" => MenuDomainKind.Folder,
        @"directory\background" or @"folder\background" => MenuDomainKind.FolderBackground,
        "drive" => MenuDomainKind.Drive,
        "desktopbackground" => MenuDomainKind.DesktopBackground,
        @"directory\library" or "libraryfolder" => MenuDomainKind.Library,
        _ => null
    };
}
