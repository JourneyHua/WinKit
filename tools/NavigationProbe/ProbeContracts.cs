// Kept local so this isolated probe can also use the pre-change application assembly.
namespace WinKit.ViewModels;
public interface IModule { void Refresh(); }
public interface IModuleActivation { void OnActivated(); void OnDeactivated(); }
