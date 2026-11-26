using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using WinKit.Services;
using WinKit.ViewModels;

namespace WinKit;

public partial class App
{
    private static void RunUpdateUiChecks(string outputDir, StringBuilder log)
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("更新界面自检失败：" + message);
        }
        var fixture = Environment.GetEnvironmentVariable("WINKIT_UPDATE_SMOKE_FIXTURE");
        if (string.IsNullOrWhiteSpace(fixture)) fixture = Path.Combine(AppContext.BaseDirectory, "WinKit.exe");
        if (!File.Exists(fixture)) throw new IOException("无法定位自包含单 EXE 更新测试夹具");
        var info = FileVersionInfo.GetVersionInfo(fixture);
        var candidate = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        var previous = candidate.Build > 0 ? new Version(candidate.Major, candidate.Minor, candidate.Build - 1)
            : candidate.Minor > 0 ? new Version(candidate.Major, candidate.Minor - 1, 0)
            : new Version(Math.Max(0, candidate.Major - 1), 0, 0);
        using var handler = new UpdateSmokeHandler(fixture, candidate);
        using var client = new HttpClient(handler);
        var busy = false;
        var draft = true;
        var allowed = false;
        var closeRequested = false;
        var prepareCount = 0;
        using var model = new AppUpdateViewModel(new AppUpdateService(Path.Combine(outputDir, "update-ui"), client),
            () => busy, () => draft,
            (_, _) => { prepareCount++; return Task.FromResult(allowed); },
            () => closeRequested = true, previous);
        var window = new Window
        {
            Content = new Views.AppUpdateView { DataContext = model }, Width = 960, Height = 560,
            Left = -4000, Top = -4000, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false, Background = (System.Windows.Media.Brush)Current.FindResource("Brush.Window")
        };
        void Invoke(WinKit.Core.RelayCommand command)
        {
            Pump();
            var button = FindVisibleButton(window, b => ReferenceEquals(b.Command, command));
            Require(button is not null, "找不到界面操作");
            InvokeButton(button!);
        }
        void Finish()
        {
            WaitWhileBusy(() => model.IsWorking, 120000);
            Require(!model.IsWorking, "异步操作未完成");
            Pump();
        }
        try
        {
            window.Show();
            model.AutomaticChecksEnabled = false;
            model.CheckAsync(false).GetAwaiter().GetResult();
            Require(handler.CheckCount == 0 && !model.IsVisible, "关闭自动检查后仍发出请求或提示");
            handler.FailCheckOnce = true;
            model.EntryCommand.Execute(null);
            Finish();
            Require(model.HasError && model.IsPanelOpen && !model.HasUpdate, "手动检查失败未提供页内重试");
            Invoke(model.CheckCommand);
            Finish();
            Require(model.HasUpdate && !model.HasError && handler.DownloadCount == 0, "检查自动下载或未显示新版");
            Require(model.ReleaseNotes.Contains("隔离更新说明", StringComparison.Ordinal), "未展示发布说明");
            Capture(window, outputDir, "update-available", log);

            handler.HoldDownload = true;
            Invoke(model.DownloadCommand);
            Require(model.IsDownloading && model.CancelCommand.CanExecute(null), "下载无法取消");
            Invoke(model.CancelCommand);
            Finish();
            Require(!model.IsReady && !Directory.EnumerateFiles(Path.Combine(outputDir, "update-ui", "Downloads"), "*.partial").Any(), "取消后遗留就绪文件或临时内容");
            handler.HoldDownload = false;
            handler.FailDownloadOnce = true;
            Invoke(model.DownloadCommand);
            Finish();
            Require(model.HasError && model.CanDownload && !model.IsReady, "失败下载不能重试");
            Invoke(model.DownloadCommand);
            Finish();
            Require(model.IsReady && !model.HasError && model.DownloadPercent == 100 && model.ShowDraftWarning, "完整下载未就绪或缺草稿丢弃提示");
            Capture(window, outputDir, "update-ready", log);
            busy = true;
            model.NotifyHostStateChanged();
            Pump();
            Require(!model.RestartCommand.CanExecute(null), "系统操作期间仍可重启");
            model.RestartCommand.Execute(null);
            Require(prepareCount == 0 && !closeRequested, "绕过忙碌保护调用助手");
            busy = false;
            model.NotifyHostStateChanged();
            Invoke(model.RestartCommand);
            Finish();
            Require(prepareCount == 1 && !closeRequested && window.IsVisible, "助手取消后关闭了当前窗口");
            allowed = true;
            Invoke(model.RestartCommand);
            Finish();
            Require(prepareCount == 2 && closeRequested && model.IsRestartCommitted, "助手就绪后未提交重启");
            draft = false;
            model.NotifyHostStateChanged();
            Require(!model.ShowDraftWarning, "草稿提示未同步");
            Invoke(model.DismissCommand);
            Require(!model.IsVisible, "更新卡无法关闭");
            log.AppendLine("  ux-update: manual=retry notes=shown download=cancel/retry/verified busy=guarded restart=ready-first drafts=warned result=OK");
        }
        finally { window.Close(); }
    }

    private sealed class UpdateSmokeHandler : HttpMessageHandler
    {
        private readonly string _fixture;
        private readonly string _fileName;
        private readonly string _metadataName;
        private readonly byte[] _metadata;
        private readonly byte[] _release;
        internal bool FailCheckOnce { get; set; }
        internal bool FailDownloadOnce { get; set; }
        internal bool HoldDownload { get; set; }
        internal int CheckCount { get; private set; }
        internal int DownloadCount { get; private set; }
        internal UpdateSmokeHandler(string fixture, Version version)
        {
            _fixture = fixture;
            _fileName = $"WinKit-v{version}-win-x64.exe";
            _metadataName = $"WinKit-v{version}-win-x64.update.json";
            using var input = File.OpenRead(fixture);
            var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            var size = input.Length;
            _metadata = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, version = version.ToString(), rid = "win-x64", fileName = _fileName, sha256 = hash, size });
            var root = $"https://github.com/JourneyHua/WinKit/releases/download/v{version}/";
            _release = JsonSerializer.SerializeToUtf8Bytes(new
            {
                draft = false, prerelease = false, tag_name = $"v{version}",
                html_url = $"https://github.com/JourneyHua/WinKit/releases/tag/v{version}",
                published_at = DateTimeOffset.UtcNow, body = "隔离更新说明：修复与体验改进。",
                assets = new[] {
                    new { name = _fileName, size, browser_download_url = root + _fileName, digest = "sha256:" + hash },
                    new { name = _metadataName, size = (long)_metadata.Length, browser_download_url = root + _metadataName, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(_metadata)).ToLowerInvariant() }
                }
            });
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage response;
            var uri = request.RequestUri ?? throw new IOException("缺少请求地址");
            if (uri.AbsoluteUri == "https://api.github.com/repos/JourneyHua/WinKit/releases/latest")
            {
                CheckCount++;
                if (FailCheckOnce) { FailCheckOnce = false; response = new(HttpStatusCode.ServiceUnavailable); }
                else response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(_release) };
            }
            else if (uri.AbsolutePath.EndsWith("/" + _metadataName, StringComparison.Ordinal))
                response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(_metadata) };
            else if (uri.AbsolutePath.EndsWith("/" + _fileName, StringComparison.Ordinal))
            {
                DownloadCount++;
                if (FailDownloadOnce) { FailDownloadOnce = false; response = new(HttpStatusCode.ServiceUnavailable); }
                else
                {
                    var stream = new UpdateSmokeStream(_fixture, HoldDownload);
                    response = new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
                    response.Content.Headers.ContentLength = stream.Length;
                }
            }
            else throw new IOException("隔离测试拒绝未声明请求：" + uri);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class UpdateSmokeStream(string path, bool hold) : Stream
    {
        private readonly FileStream _inner = File.OpenRead(path);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (hold) await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
