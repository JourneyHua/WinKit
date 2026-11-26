using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinKit.Services;

/// <summary>固定官方仓库的正式版检查和校验下载；此类型永不启动下载的程序。</summary>
public sealed class AppUpdateService : IDisposable
{
    private const string RepositoryPath = "JourneyHua/WinKit";
    private const string ApiRoot = "https://api.github.com/repos/" + RepositoryPath + "/releases/";
    private const int MaximumReleaseJsonBytes = 2 * 1024 * 1024;
    private const int MaximumMetadataBytes = 64 * 1024;
    private const long MaximumDownloadBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(60);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly AppUpdatePreferences _preferences;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly object _trustedSync = new();
    private readonly HashSet<AppUpdateRelease> _trustedReleases = new();
    private Version? _currentVersion;
    private int _disposed;

    /// <param name="httpClient">测试可注入 fake handler 客户端。真实网络客户端必须关闭 AllowAutoRedirect；发生隐式重定向会被拒绝。</param>
    public AppUpdateService(string? storageDirectory = null, HttpClient? httpClient = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        var directory = Path.GetFullPath(storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinKit", "Updates"));
        DownloadDirectory = Path.Combine(directory, "Downloads");
        _preferences = new AppUpdatePreferences(directory);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _lifetimeToken = _lifetime.Token;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false
        }) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public string DownloadDirectory { get; }

    public bool AutomaticChecksEnabled
    {
        get { ThrowIfDisposed(); return _preferences.AutomaticChecksEnabled; }
        set { ThrowIfDisposed(); _preferences.AutomaticChecksEnabled = value; }
    }

    public bool IsAutomaticCheckDue()
    {
        ThrowIfDisposed();
        return _preferences.IsAutomaticCheckDue(_utcNow());
    }

    /// <summary>自动检查限 24 小时一次；手动检查不受开关、节流限制。仅下载小型元数据，不下载 EXE。</summary>
    public async Task<AppUpdateRelease?> CheckAsync(Version currentVersion, bool manual,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        var token = linked.Token;
        await _checkGate.WaitAsync(token).ConfigureAwait(false);
        linked.CancelAfter(CheckTimeout);
        try
        {
            token.ThrowIfCancellationRequested();
            lock (_trustedSync)
            {
                var normalized = AppUpdateVersion.Normalize(currentVersion);
                if (_currentVersion is null || normalized > _currentVersion) _currentVersion = normalized;
            }
            if (!manual && !_preferences.TryReserveAutomaticCheck(_utcNow())) return null;
            var release = await LoadReleaseAsync(new Uri(ApiRoot + "latest"), null, token, currentVersion).ConfigureAwait(false);
            if (release is null || AppUpdateVersion.Normalize(release.Version) <= AppUpdateVersion.Normalize(currentVersion))
                return null;
            RememberTrusted(release);
            return release;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && !_lifetimeToken.IsCancellationRequested)
        {
            throw new TimeoutException("检查更新超时，请稍后重试。", exception);
        }
        finally { _checkGate.Release(); }
    }

    /// <summary>安装助手重新查询指定正式版的可信元数据；不使用调用者提供的 URL、SHA256 或大小。</summary>
    public Task<AppUpdateRelease?> GetReleaseAsync(Version expectedVersion, CancellationToken cancellationToken = default) =>
        GetReleaseAsync(AppUpdateVersion.StableText(expectedVersion), cancellationToken);

    public async Task<AppUpdateRelease?> GetReleaseAsync(string versionText, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!AppUpdateVersion.TryParse(versionText, out _, out var prerelease) || prerelease)
            throw new ArgumentException("只允许有效的正式版 SemVer。", nameof(versionText));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        await _checkGate.WaitAsync(linked.Token).ConfigureAwait(false);
        linked.CancelAfter(CheckTimeout);
        try
        {
            var release = await LoadReleaseAsync(new Uri(ApiRoot + "tags/" + Uri.EscapeDataString("v" + versionText)),
                versionText, linked.Token).ConfigureAwait(false);
            if (release is not null) RememberTrusted(release);
            return release;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && !_lifetimeToken.IsCancellationRequested)
        {
            throw new TimeoutException("检查更新超时，请稍后重试。", exception);
        }
        finally { _checkGate.Release(); }
    }

    /// <summary>只接受本实例从可信 API 解析得到的版本。每次使用缓存仍完整重核；失败可直接重新调用重试。</summary>
    public async Task<AppUpdateDownload> DownloadAsync(AppUpdateRelease release, IProgress<AppUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateReleaseShape(release);
        lock (_trustedSync)
        {
            if (!_trustedReleases.Contains(release))
                throw new InvalidOperationException("请先检查正式更新，不能直接下载未经可信发布信息确认的版本。");
            if (_currentVersion is not null && AppUpdateVersion.Normalize(release.Version) <= _currentVersion)
                throw new InvalidOperationException("不能下载当前版本或更旧的版本作为更新。");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        var token = linked.Token;
        await _downloadGate.WaitAsync(token).ConfigureAwait(false);
        linked.CancelAfter(DownloadTimeout);
        string? temporaryPath = null;
        try
        {
            AppUpdateStorage.EnsureDirectory(DownloadDirectory);
            var destination = Path.Combine(DownloadDirectory, release.FileName);
            AppUpdateStorage.EnsureRegularFileIfPresent(destination);
            if (await VerifyFileAsync(destination, release, token).ConfigureAwait(false))
            {
                progress?.Report(new AppUpdateProgress(release.Size, release.Size));
                return new AppUpdateDownload(destination, release);
            }

            using var response = await SendTrustedAsync(release.DownloadUri, false, token).ConfigureAwait(false);
            RequireCompleteResponse(response, release.Size);
            temporaryPath = Path.Combine(DownloadDirectory, $".{Guid.NewGuid():N}.partial");
            progress?.Report(new AppUpdateProgress(0, release.Size));
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    long received = 0;
                    long lastReport = Environment.TickCount64;
                    while (true)
                    {
                        var read = await ReadWithIdleTimeoutAsync(input, buffer.AsMemory(), token).ConfigureAwait(false);
                        if (read == 0) break;
                        if (received > release.Size - read) throw new InvalidDataException("下载内容超出发布声明的大小。");
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        received += read;
                        if (Environment.TickCount64 - lastReport >= 100 || received == release.Size)
                        {
                            progress?.Report(new AppUpdateProgress(received, release.Size));
                            lastReport = Environment.TickCount64;
                        }
                    }
                    if (received != release.Size) throw new InvalidDataException("下载不完整，文件大小与正式发布信息不符。");
                    await output.FlushAsync(token).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            if (!await VerifyFileAsync(temporaryPath, release, token).ConfigureAwait(false))
                throw new InvalidDataException("下载未通过 SHA256、大小、自包含单文件、x64 EXE 或版本校验，请重新下载。");
            token.ThrowIfCancellationRequested();
            AppUpdateStorage.EnsureDirectory(DownloadDirectory);
            AppUpdateStorage.EnsureRegularFileIfPresent(destination);
            // 同一目录中完整校验后才替换缓存；旧缓存直到这一刻始终不受失败下载影响。
            File.Move(temporaryPath, destination, overwrite: true);
            temporaryPath = null;
            progress?.Report(new AppUpdateProgress(release.Size, release.Size));
            return new AppUpdateDownload(destination, release);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && !_lifetimeToken.IsCancellationRequested)
        {
            throw new TimeoutException("下载更新超时，请重新下载。", exception);
        }
        finally
        {
            try
            {
                if (temporaryPath is not null) File.Delete(temporaryPath);
            }
            finally { _downloadGate.Release(); }
        }
    }

    /// <summary>确认应用下载目录内的就绪缓存，每次重新读取全部内容，不信任上次的校验结果。</summary>
    public async Task<bool> ValidateDownloadAsync(AppUpdateDownload download, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (download is null || download.Release is null || string.IsNullOrWhiteSpace(download.FilePath)) return false;
        try
        {
            ValidateReleaseShape(download.Release);
            var expected = Path.Combine(DownloadDirectory, download.Release.FileName);
            if (!string.Equals(Path.GetFullPath(download.FilePath), expected, StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
        return await VerifyDownloadAsync(download.FilePath, download.Release, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>供助手验证任意暂存位置。release 必须先由 GetReleaseAsync 重新解析；本方法只执行本地校验。</summary>
    public async Task<bool> VerifyDownloadAsync(string filePath, AppUpdateRelease release,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        try
        {
            ValidateReleaseShape(release);
            return await VerifyFileAsync(filePath, release, linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private async Task<AppUpdateRelease?> LoadReleaseAsync(Uri apiUri, string? expectedVersionText, CancellationToken token,
        Version? newerThan = null)
    {
        using var response = await SendTrustedAsync(apiUri, true, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("GitHub 发布查询响应无效。");
        var bytes = await ReadBoundedAsync(response, MaximumReleaseJsonBytes, null, token).ConfigureAwait(false);
        using var document = ParseJson(bytes, 32);
        var root = document.RootElement;
        RequireObject(root);
        if (RequiredBoolean(root, "draft") || RequiredBoolean(root, "prerelease")) return null;
        var tag = RequiredString(root, "tag_name");
        if (!tag.StartsWith('v') || !AppUpdateVersion.TryParse(tag[1..], out var version, out var prerelease))
            throw new InvalidDataException("正式发布标签必须是 v<有效 SemVer>。");
        if (prerelease) return null;
        // 旧版本可能还没有新版更新协议；已是当前或更旧时不应再读取其资产。
        if (newerThan is not null && AppUpdateVersion.Normalize(version) <= AppUpdateVersion.Normalize(newerThan)) return null;
        var versionText = tag[1..];
        if (expectedVersionText is not null && versionText != expectedVersionText)
            throw new InvalidDataException("发布 API 返回的版本与请求版本不一致。");
        var releaseUri = BuildReleaseUri(versionText);
        if (!IsCanonicalUri(ParseUri(RequiredString(root, "html_url")), releaseUri))
            throw new InvalidDataException("发布页面不属于 WinKit 官方仓库或版本。");
        if (!root.TryGetProperty("published_at", out var published)
            || published.ValueKind != JsonValueKind.String || !published.TryGetDateTimeOffset(out var publishedAt))
            throw new InvalidDataException("正式发布时间无效。");
        var releaseNotes = "";
        if (root.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null)
        {
            if (body.ValueKind != JsonValueKind.String) throw new InvalidDataException("发布说明格式无效。");
            releaseNotes = body.GetString()!;
        }
        var fileName = BuildFileName(versionText);
        var metadataName = $"WinKit-v{versionText}-win-x64.update.json";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("正式发布缺少更新资产。");
        var executable = FindAsset(assets, tag, fileName, MaximumDownloadBytes);
        var metadata = FindAsset(assets, tag, metadataName, MaximumMetadataBytes);
        using var metadataResponse = await SendTrustedAsync(metadata.Uri, false, token).ConfigureAwait(false);
        RequireCompleteResponse(metadataResponse, metadata.Size);
        var metadataBytes = await ReadBoundedAsync(metadataResponse, MaximumMetadataBytes, metadata.Size, token).ConfigureAwait(false);
        if (metadata.Sha256 is not null
            && !HashEquals(SHA256.HashData(metadataBytes), metadata.Sha256))
            throw new InvalidDataException("更新元数据未通过 GitHub 资产 SHA256 校验。");
        using var metadataDocument = ParseJson(metadataBytes, 8);
        var update = metadataDocument.RootElement;
        RequireObject(update);
        if (!update.TryGetProperty("schemaVersion", out var schema)
            || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1
            || RequiredString(update, "version") != versionText
            || RequiredString(update, "rid") != "win-x64"
            || RequiredString(update, "fileName") != fileName
            || RequiredPositiveSize(update, "size", MaximumDownloadBytes) != executable.Size)
            throw new InvalidDataException("更新元数据的协议、版本、架构、文件名或大小与正式发布不一致。");
        var sha256 = RequiredString(update, "sha256");
        if (!IsSha256(sha256) || executable.Sha256 is not null
            && !string.Equals(executable.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新元数据的 SHA256 无效或与 GitHub 资产摘要不一致。");
        return new AppUpdateRelease(version, versionText, releaseNotes, publishedAt.ToUniversalTime(), releaseUri,
            executable.Uri, fileName, sha256.ToLowerInvariant(), executable.Size);
    }

    private static Asset FindAsset(JsonElement assets, string tag, string name, long maximumSize)
    {
        Asset? result = null;
        foreach (var item in assets.EnumerateArray())
        {
            RequireObject(item);
            if (RequiredString(item, "name") != name) continue;
            if (result is not null) throw new InvalidDataException("正式发布中存在重名更新资产。");
            if (item.TryGetProperty("state", out var state)
                && (state.ValueKind != JsonValueKind.String || state.GetString() != "uploaded"))
                throw new InvalidDataException("正式更新资产尚未上传完成。");
            var size = RequiredPositiveSize(item, "size", maximumSize);
            var expected = BuildAssetUri(tag, name);
            if (!IsCanonicalUri(ParseUri(RequiredString(item, "browser_download_url")), expected))
                throw new InvalidDataException("更新资产 URL 不属于 WinKit 官方仓库、版本或文件名。");
            string? digest = null;
            if (item.TryGetProperty("digest", out var digestValue) && digestValue.ValueKind != JsonValueKind.Null)
            {
                if (digestValue.ValueKind != JsonValueKind.String) throw new InvalidDataException("GitHub 资产摘要格式无效。");
                var value = digestValue.GetString()!;
                if (!value.StartsWith("sha256:", StringComparison.Ordinal) || !IsSha256(value[7..]))
                    throw new InvalidDataException("GitHub 资产摘要不是受支持的 SHA256。");
                digest = value[7..];
            }
            result = new Asset(expected, size, digest);
        }
        return result ?? throw new InvalidDataException($"正式发布缺少更新资产 {name}。");
    }

    private async Task<HttpResponseMessage> SendTrustedAsync(Uri canonicalUri, bool api, CancellationToken token)
    {
        var uri = canonicalUri;
        for (var redirects = 0; ; redirects++)
        {
            if (!IsAllowedRequestUri(uri, canonicalUri, api))
                throw new InvalidDataException("更新请求或重定向离开了受信任的 GitHub HTTPS 域名。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("WinKit-AppUpdate/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(api ? "application/vnd.github+json" : "application/octet-stream"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (api) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            // 注入的 HttpClient 也不能暗中跳过每一跳的校验。
            if (response.RequestMessage?.RequestUri is Uri actual && !IsSameAbsoluteUri(actual, uri))
            {
                response.Dispose();
                throw new InvalidDataException("更新 HTTP 客户端启用了隐式重定向，无法验证完整重定向链。");
            }
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirects >= MaximumRedirects)
                throw new InvalidDataException("更新下载的重定向无效或次数过多。");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    private static bool IsAllowedRequestUri(Uri uri, Uri canonicalUri, bool api)
    {
        if (!IsSafeHttps(uri)) return false;
        if (api || uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return IsCanonicalUri(uri, canonicalUri);
        // 仅允许 GitHub 正式 Release 实际使用的三个精确资产域名，不接受后缀通配。
        return uri.IdnHost.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximumSize, long? expectedSize,
        CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is long declared
            && (declared < 0 || declared > maximumSize || expectedSize is not null && declared != expectedSize.Value))
            throw new InvalidDataException("更新元数据的 HTTP 大小与发布声明不符或超过限制。");
        if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("更新响应使用了未声明的内容编码。");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await ReadWithIdleTimeoutAsync(input, buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length > maximumSize - read || expectedSize is not null && output.Length > expectedSize.Value - read)
                    throw new InvalidDataException("更新元数据超过声明大小或读取限制。");
                output.Write(buffer, 0, read);
            }
            if (expectedSize is not null && output.Length != expectedSize.Value)
                throw new InvalidDataException("更新元数据不完整。");
            return output.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task<int> ReadWithIdleTimeoutAsync(Stream input, Memory<byte> buffer, CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(ReadIdleTimeout);
        try { return await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("更新下载连续 60 秒未收到数据，请重试。", exception);
        }
    }

    private static void RequireCompleteResponse(HttpResponseMessage response, long expectedSize)
    {
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange is not null
            || response.Content.Headers.ContentLength is long length && length != expectedSize
            || response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("更新下载响应的状态、大小或内容编码不符合完整资产要求。");
    }

    private static async Task<bool> VerifyFileAsync(string filePath, AppUpdateRelease release, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            AppUpdateStorage.EnsureRegularFileIfPresent(filePath);
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != release.Size) return false;
            var actualHash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            if (!HashEquals(actualHash, release.Sha256) || stream.Length != release.Size) return false;
            token.ThrowIfCancellationRequested();
            stream.Position = 0;
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var headers = reader.PEHeaders;
            if (headers.PEHeader is null || headers.PEHeader.Magic != PEMagic.PE32Plus
                || headers.CoffHeader.Machine != Machine.Amd64
                || (headers.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0
                || (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0) return false;
            if (!await VerifySelfContainedBundleAsync(stream, headers, release.Version, token).ConfigureAwait(false)) return false;
            // 保持禁止写入/删除的读句柄，避免哈希后再读取版本资源时被其它进程替换。
            var info = FileVersionInfo.GetVersionInfo(filePath);
            var version = release.Version;
            if (string.IsNullOrWhiteSpace(info.FileVersion) || string.IsNullOrWhiteSpace(info.ProductVersion)
                || info.FileMajorPart != version.Major || info.FileMinorPart != version.Minor
                || info.FileBuildPart != version.Build || info.FilePrivatePart != 0
                || info.ProductMajorPart != version.Major || info.ProductMinorPart != version.Minor
                || info.ProductBuildPart != version.Build || info.ProductPrivatePart != 0) return false;
            var productVersion = info.ProductVersion.Split('+')[0];
            if (!Version.TryParse(productVersion, out var parsedProduct)
                || AppUpdateVersion.Normalize(parsedProduct) != AppUpdateVersion.Normalize(version)) return false;
            token.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException
            or ArgumentException or NotSupportedException or Win32Exception or FormatException)
        {
            return false;
        }
    }

    // .NET 6+ 的 bundle v6 格式；当前发行约定不启用压缩。只读解析，不加载或执行其中程序集。
    private static async Task<bool> VerifySelfContainedBundleAsync(FileStream stream, PEHeaders headers, Version expectedVersion,
        CancellationToken token)
    {
        long hostLength = headers.PEHeader!.SizeOfHeaders;
        foreach (var section in headers.SectionHeaders)
        {
            if (section.PointerToRawData < 0 || section.SizeOfRawData < 0) return false;
            hostLength = Math.Max(hostLength, (long)section.PointerToRawData + section.SizeOfRawData);
        }
        if (hostLength < 40 || hostLength > 16 * 1024 * 1024 || hostLength > stream.Length) return false;
        stream.Position = 0;
        var host = new byte[(int)hostLength];
        await stream.ReadExactlyAsync(host, token).ConfigureAwait(false);
        byte[] signature = [0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
            0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32, 0x13, 0xf5, 0xb9, 0xe6,
            0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae];
        var marker = host.AsSpan().IndexOf(signature);
        if (marker < sizeof(long) || host.AsSpan(marker + signature.Length).IndexOf(signature) >= 0) return false;
        var headerOffset = BinaryPrimitives.ReadInt64LittleEndian(host.AsSpan(marker - sizeof(long), sizeof(long)));
        if (headerOffset < hostLength || headerOffset > stream.Length - 64) return false;
        stream.Position = headerOffset;
        var manifest = new byte[(int)Math.Min(stream.Length - headerOffset, 1024 * 1024)];
        await stream.ReadExactlyAsync(manifest, token).ConfigureAwait(false);
        using var manifestStream = new MemoryStream(manifest, false);
        using var reader = new BinaryReader(manifestStream, new UTF8Encoding(false, true));
        if (reader.ReadUInt32() != 6 || reader.ReadUInt32() != 0) return false;
        var count = reader.ReadInt32();
        if (count is < 9 or > 4096) return false;
        var bundleId = ReadBundleString(reader, 128);
        if (bundleId.Length != 12 || bundleId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            return false;
        var depsOffset = reader.ReadInt64();
        var depsSize = reader.ReadInt64();
        var configOffset = reader.ReadInt64();
        var configSize = reader.ReadInt64();
        if ((reader.ReadUInt64() & ~1UL) != 0) return false;
        var entries = new Dictionary<string, BundleEntry>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            var offset = reader.ReadInt64();
            var size = reader.ReadInt64();
            var compressedSize = reader.ReadInt64();
            var type = reader.ReadByte();
            var name = ReadBundleString(reader, 4096);
            var entry = new BundleEntry(offset, size, type);
            if (compressedSize != 0 || type > 5 || offset < hostLength || size < 0 || offset > headerOffset - size
                || !IsSafeBundlePath(name) || !entries.TryAdd(name, entry)) return false;
        }
        long previousEnd = hostLength;
        foreach (var entry in entries.Values.OrderBy(value => value.Offset))
        {
            if (entry.Offset < previousEnd) return false;
            previousEnd = entry.Offset + entry.Size;
        }
        if (!TryBundleEntry(entries, "WinKit.dll", 1, out var application)
            || !TryBundleEntry(entries, "WinKit.deps.json", 3, out var deps)
            || !TryBundleEntry(entries, "WinKit.runtimeconfig.json", 4, out var config)
            || deps.Offset != depsOffset || deps.Size != depsSize || config.Offset != configOffset || config.Size != configSize)
            return false;
        // singlefilehost 可静态链接 CLR/JIT，不能强制清单含 coreclr/clrjit/hostfxr/hostpolicy。
        // 自包含证据由下方 includedFrameworks、CoreLib 和 WPF 程序集共同提供。
        foreach (var native in entries.Values.Where(entry => entry.Type == 2))
            if (!await IsBundledNativeX64Async(stream, native, token).ConfigureAwait(false)) return false;
        foreach (var name in new[] { "System.Private.CoreLib.dll", "PresentationFramework.dll", "PresentationCore.dll", "WindowsBase.dll" })
            if (!TryBundleEntry(entries, name, 1, out _)) return false;
        var appBytes = await ReadBundleEntryAsync(stream, application, 64 * 1024 * 1024, token).ConfigureAwait(false);
        using (var appStream = new MemoryStream(appBytes, false))
        using (var appReader = new PEReader(appStream))
        {
            if (!appReader.HasMetadata) return false;
            var metadata = appReader.GetMetadataReader();
            if (!metadata.IsAssembly) return false;
            var definition = metadata.GetAssemblyDefinition();
            if (metadata.GetString(definition.Name) != "WinKit"
                || AppUpdateVersion.Normalize(definition.Version) != AppUpdateVersion.Normalize(expectedVersion)) return false;
        }
        using (var configJson = ParseJson(await ReadBundleEntryAsync(stream, config, MaximumMetadataBytes, token).ConfigureAwait(false), 16))
        {
            var root = configJson.RootElement;
            RequireObject(root);
            if (!root.TryGetProperty("runtimeOptions", out var options)) return false;
            RequireObject(options);
            if (options.TryGetProperty("framework", out _) || options.TryGetProperty("frameworks", out _)
                || !options.TryGetProperty("includedFrameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Array)
                return false;
            var frameworkNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var framework in frameworks.EnumerateArray())
            {
                RequireObject(framework);
                var name = RequiredString(framework, "name");
                if (!frameworkNames.Add(name) || !Version.TryParse(RequiredString(framework, "version"), out var version)
                    || version.Major < 6 || version.Build < 0) return false;
            }
            if (!frameworkNames.Contains("Microsoft.NETCore.App") || !frameworkNames.Contains("Microsoft.WindowsDesktop.App")) return false;
        }
        using (var depsJson = ParseJson(await ReadBundleEntryAsync(stream, deps, MaximumReleaseJsonBytes, token).ConfigureAwait(false), 32))
        {
            var root = depsJson.RootElement;
            RequireObject(root);
            if (!root.TryGetProperty("runtimeTarget", out var target)) return false;
            RequireObject(target);
            var targetName = RequiredString(target, "name");
            if (!targetName.EndsWith("/win-x64", StringComparison.Ordinal)
                || !root.TryGetProperty("targets", out var targets)) return false;
            RequireObject(targets);
            if (!targets.TryGetProperty(targetName, out var selected)) return false;
            RequireObject(selected);
            var appName = FormattableString.Invariant($"WinKit/{expectedVersion.Major}.{expectedVersion.Minor}.{expectedVersion.Build}");
            if (!selected.TryGetProperty(appName, out var app)) return false;
            RequireObject(app);
            if (!app.TryGetProperty("runtime", out var runtime)) return false;
            RequireObject(runtime);
            if (!runtime.TryGetProperty("WinKit.dll", out _)) return false;
        }
        token.ThrowIfCancellationRequested();
        return true;
    }

    private static string ReadBundleString(BinaryReader reader, int maximumBytes)
    {
        var length = 0;
        for (var index = 0; index < 5; index++)
        {
            var value = reader.ReadByte();
            if (index == 4 && value > 7) throw new InvalidDataException("单文件清单字符串长度无效。");
            length |= (value & 0x7f) << (index * 7);
            if ((value & 0x80) != 0) continue;
            if (length <= 0 || length > maximumBytes) throw new InvalidDataException("单文件清单字符串超过限制。");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidDataException("单文件清单已截断。");
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        throw new InvalidDataException("单文件清单字符串长度无效。");
    }

    private static bool IsSafeBundlePath(string name) => name.Split('/').All(part => part.Length > 0
        && part is not "." and not ".." && !part.EndsWith('.') && !part.EndsWith(' ')
        && !part.Any(character => character < 32 || character is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'));

    private static bool TryBundleEntry(Dictionary<string, BundleEntry> entries, string name, byte type, out BundleEntry entry)
    {
        if (entries.TryGetValue(name, out var candidate) && candidate.Type == type && candidate.Size > 0)
        {
            entry = candidate;
            return true;
        }
        entry = new BundleEntry(0, 0, 0);
        return false;
    }

    private static async Task<byte[]> ReadBundleEntryAsync(FileStream stream, BundleEntry entry, int maximumBytes, CancellationToken token)
    {
        if (entry.Size <= 0 || entry.Size > maximumBytes) throw new InvalidDataException("单文件内嵌条目大小无效。");
        stream.Position = entry.Offset;
        var bytes = new byte[(int)entry.Size];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private static async Task<bool> IsBundledNativeX64Async(FileStream stream, BundleEntry entry, CancellationToken token)
    {
        if (entry.Size < 64) return false;
        stream.Position = entry.Offset;
        var dos = new byte[64];
        await stream.ReadExactlyAsync(dos, token).ConfigureAwait(false);
        if (dos[0] != 'M' || dos[1] != 'Z') return false;
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(60));
        if (peOffset < 64 || peOffset > entry.Size - 26) return false;
        stream.Position = entry.Offset + peOffset;
        var pe = new byte[26];
        await stream.ReadExactlyAsync(pe, token).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(pe) == 0x00004550
            && BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(4)) == 0x8664
            && (BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(22)) & 0x2002) == 0x2002
            && BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(24)) == 0x20b;
    }

    private sealed record BundleEntry(long Offset, long Size, byte Type);

    private static void ValidateReleaseShape(AppUpdateRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.Version is null || !AppUpdateVersion.TryParse(release.VersionText, out var version, out var prerelease)
            || prerelease || release.Version.Revision > 0
            || AppUpdateVersion.Normalize(release.Version) != AppUpdateVersion.Normalize(version)
            || release.FileName != BuildFileName(release.VersionText)
            || !IsSha256(release.Sha256) || release.Size is <= 0 or > MaximumDownloadBytes
            || !IsCanonicalUri(release.ReleaseUri, BuildReleaseUri(release.VersionText))
            || !IsCanonicalUri(release.DownloadUri, BuildAssetUri("v" + release.VersionText, release.FileName)))
            throw new InvalidDataException("更新版本、SHA256、大小、文件名或官方仓库 URL 无效。");
    }

    private void RememberTrusted(AppUpdateRelease release)
    {
        lock (_trustedSync) _trustedReleases.Add(release);
    }

    private static string BuildFileName(string versionText) => $"WinKit-v{versionText}-win-x64.exe";
    private static Uri BuildReleaseUri(string versionText) =>
        new($"https://github.com/{RepositoryPath}/releases/tag/{Uri.EscapeDataString("v" + versionText)}");
    private static Uri BuildAssetUri(string tag, string fileName) =>
        new($"https://github.com/{RepositoryPath}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(fileName)}");

    private static bool IsSafeHttps(Uri? uri) => uri is not null && uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static bool IsCanonicalUri(Uri? actual, Uri expected) => IsSafeHttps(actual)
        && actual!.Query.Length == 0 && actual.IdnHost.Equals(expected.IdnHost, StringComparison.OrdinalIgnoreCase)
        && actual.GetComponents(UriComponents.Path, UriFormat.Unescaped)
            .Equals(expected.GetComponents(UriComponents.Path, UriFormat.Unescaped), StringComparison.Ordinal);

    private static bool IsSameAbsoluteUri(Uri actual, Uri expected) =>
        Uri.Compare(actual, expected, UriComponents.AbsoluteUri, UriFormat.UriEscaped, StringComparison.Ordinal) == 0;

    private static Uri ParseUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        ? uri : throw new InvalidDataException("发布资产 URL 无效。");

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool HashEquals(byte[] actual, string expected) =>
        IsSha256(expected) && CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected));

    private static JsonDocument ParseJson(byte[] bytes, int maximumDepth)
    {
        try { return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = maximumDepth }); }
        catch (JsonException exception)
        {
            throw new InvalidDataException("更新发布信息不是有效的 JSON。", exception);
        }
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("更新发布 JSON 必须为对象。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("更新发布 JSON 含有重复字段。");
    }

    private static string RequiredString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is string result
            ? result : throw new InvalidDataException($"更新发布缺少有效字段 {name}。");

    private static bool RequiredBoolean(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw new InvalidDataException($"更新发布缺少有效字段 {name}。");

    private static long RequiredPositiveSize(JsonElement parent, string name, long maximum) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var size) && size > 0 && size <= maximum
            ? size : throw new InvalidDataException($"更新发布字段 {name} 不是允许范围内的文件大小。");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        if (_ownsHttpClient) _httpClient.Dispose();
        _lifetime.Dispose();
        // 正在退出的方法还会 Release；不能在此释放两个 gate。
    }

    private sealed record Asset(Uri Uri, long Size, string? Sha256);
}
