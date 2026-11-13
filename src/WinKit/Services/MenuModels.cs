using WinKit.Core;

namespace WinKit.Services;

public enum MenuDomainKind
{
    File,
    Folder,
    FolderBackground,
    Drive,
    DesktopBackground,
    Library,
    SendTo,
    New,
    Unknown
}

public enum MenuEntryKind
{
    Verb,
    Handler,
    SendToItem,
    ShellNew
}

public sealed class MenuDomain
{
    public MenuDomainKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string? ClassRoot { get; init; }
    public bool IsHighFrequency { get; init; }

    public static readonly IReadOnlyList<MenuDomain> All = new List<MenuDomain>
    {
        new() { Kind = MenuDomainKind.File, Title = "文件", Description = "右键任意文件时出现的菜单", ClassRoot = "*", IsHighFrequency = true },
        new() { Kind = MenuDomainKind.Folder, Title = "文件夹", Description = "右键文件夹时出现的菜单", ClassRoot = "Directory", IsHighFrequency = true },
        new() { Kind = MenuDomainKind.FolderBackground, Title = "文件夹空白处", Description = "在文件夹空白处右键时的菜单", ClassRoot = @"Directory\Background", IsHighFrequency = true },
        new() { Kind = MenuDomainKind.Drive, Title = "驱动器", Description = "右键 C:、D: 等磁盘时的菜单", ClassRoot = "Drive", IsHighFrequency = true },
        new() { Kind = MenuDomainKind.DesktopBackground, Title = "桌面空白处", Description = "在桌面空白处右键时的菜单", ClassRoot = "DesktopBackground" },
        new() { Kind = MenuDomainKind.Library, Title = "库", Description = "右键“库”节点时的菜单", ClassRoot = "LibraryFolder" },
        new() { Kind = MenuDomainKind.SendTo, Title = "发送到", Description = "“发送到”子菜单的内容（快捷方式文件）" },
        new() { Kind = MenuDomainKind.New, Title = "新建", Description = "右键“新建”子菜单里的文件类型" }
    };
}

public sealed class MenuEntry : ObservableObject
{
    private bool _isDisabled;

    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string KeyName { get; init; } = "";
    public MenuEntryKind Kind { get; init; }
    public MenuDomainKind Domain { get; init; }
    public string DomainTitle { get; init; } = "";
    public string HiveText { get; set; } = "";
    public string Publisher { get; set; } = "";
    public bool IsSystem { get; init; }
    public string RegistryPath { get; set; } = "";
    public string CommandPath { get; set; } = "";
    public string? Clsid { get; init; }
    public string Note { get; init; } = "";
    public bool CanToggle { get; init; } = true;
    public bool CanDelete { get; init; } = true;
    public bool CanRename { get; init; }

    public string SourceText => IsSystem ? "系统" : "第三方";

    public bool IsDisabled
    {
        get => _isDisabled;
        set
        {
            if (Set(ref _isDisabled, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsEnabledOn));
            }
        }
    }

    /// <summary>供界面复选框双向绑定，实际改动交给 ViewModel 处理。</summary>
    public bool IsEnabledOn
    {
        get => !IsDisabled;
        set
        {
            if (value == !IsDisabled) return;
            ToggleRequested?.Invoke(this, value);
        }
    }

    public Action<MenuEntry, bool>? ToggleRequested { get; set; }

    public string StatusText => IsDisabled ? "已禁用" : "启用中";

    public string SearchBlob =>
        $"{DisplayName} {KeyName} {Publisher} {CommandPath} {RegistryPath} {DomainTitle}".ToLowerInvariant();
}
