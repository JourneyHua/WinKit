# WinKit 解除锁定 / Unblock helper
#
# 由 解除锁定.cmd 调用，把本脚本所在目录（含子目录）里所有文件的
# 「来自 Internet」标记（Zone.Identifier 备用数据流，即 MOTW）去掉。
#
# 为什么需要它：从浏览器下载的 zip 解压后，每个文件都会带上这个流，
# Windows 因此把 WinKit.exe 当成「从网上来的程序」，每次启动都弹
# SmartScreen「Windows 已保护你的电脑」。去掉标记后就不再弹了。
#
# 注意：这个文件必须保存为 UTF-8 with BOM。Windows PowerShell 5.1 读
# 不带 BOM 的脚本时会按系统 ANSI 代码页解码，中文会变成乱码。

$ErrorActionPreference = 'Continue'

$dir = Split-Path -Parent $PSCommandPath
if (-not $dir) {
    Write-Host '读不到本脚本所在目录，已中止。' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host '   WinKit 解除锁定'
Write-Host '   ----------------'
Write-Host "   目录：$dir"
Write-Host ''

$files = @(Get-ChildItem -LiteralPath $dir -Recurse -File -Force -ErrorAction SilentlyContinue)
if ($files.Count -eq 0) {
    Write-Host '   这个目录里没找到文件。' -ForegroundColor Yellow
    exit 1
}

# 只挑真的带标记的文件，这样报告里的数字才有意义（否则会把「本来就干净的文件」
# 也数进去，用户会以为脚本做了很多事）。
$marked = @()
foreach ($f in $files) {
    $stream = Get-Item -LiteralPath $f.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue
    if ($stream) { $marked += $f }
}

if ($marked.Count -eq 0) {
    Write-Host "   检查了 $($files.Count) 个文件，都没有「来自 Internet」标记（可能之前已经解锁过）。"
    Write-Host '   现在可以直接双击 WinKit.exe。'
    exit 0
}

$ok = 0
$failed = @()
foreach ($f in $marked) {
    try {
        Unblock-File -LiteralPath $f.FullName -ErrorAction Stop
        $ok++
    } catch {
        $failed += $f.Name
    }
}

Write-Host "   共 $($files.Count) 个文件，其中 $($marked.Count) 个带「来自 Internet」标记，已去掉 $ok 个。" -ForegroundColor Green
if ($failed.Count -gt 0) {
    Write-Host "   以下 $($failed.Count) 个文件去不掉（可能被占用）：" -ForegroundColor Yellow
    foreach ($name in $failed) { Write-Host "     - $name" }
    Write-Host '   可以手动处理：右键文件 → 属性 → 勾选「解除锁定」→ 确定。'
    exit 1
}

Write-Host '   现在可以直接双击 WinKit.exe，不会再弹 SmartScreen 拦截窗口。'
exit 0
