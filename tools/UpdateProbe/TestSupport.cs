using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WinKit.Services;

namespace WinKit.UpdateProbe;

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}; expected {expected}, actual {actual}");

    public static T NotNull<T>(T? value, string message) where T : class =>
        value ?? throw new InvalidOperationException(message);

    public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}, but the operation succeeded.");
    }
}

internal sealed record Fixture(byte[] Bytes, Version Version, Version CurrentVersion, string Sha256)
{
    public string VersionText => Version.ToString(3);

    public static async Task<Fixture> LoadAsync(string path)
    {
        path = Path.GetFullPath(path);
        var bytes = await File.ReadAllBytesAsync(path);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new PEReader(stream);
        Assert.True(reader.PEHeaders.CoffHeader.Machine == Machine.Amd64
            && reader.PEHeaders.PEHeader?.Magic == PEMagic.PE32Plus
            && !reader.PEHeaders.IsDll, "--fixture must be a real AMD64 Windows executable.");
        var info = FileVersionInfo.GetVersionInfo(path);
        Assert.True(info.FilePrivatePart == 0 && info.ProductPrivatePart == 0,
            "--fixture must have a release version with a zero fourth component.");
        var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        Assert.True(info.ProductMajorPart == version.Major && info.ProductMinorPart == version.Minor
            && info.ProductBuildPart == version.Build && !string.IsNullOrWhiteSpace(info.ProductVersion),
            "--fixture FileVersion and ProductVersion must agree.");
        var product = info.ProductVersion!.Split('+')[0];
        Assert.True(System.Version.TryParse(product, out var parsed)
            && parsed.Major == version.Major && parsed.Minor == version.Minor
            && parsed.Build == version.Build && parsed.Revision is -1 or 0,
            "--fixture must be a stable versioned WinKit.exe, not a generic apphost.");
        var current = version.Build > 0 ? new Version(version.Major, version.Minor, version.Build - 1)
            : version.Minor > 0 ? new Version(version.Major, version.Minor - 1, 0)
            : version.Major > 0 ? new Version(version.Major - 1, 0, 0)
            : throw new ArgumentException("A 0.0.0 fixture has no older current version.");
        return new Fixture(bytes, version, current, Hash(bytes));
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public byte[] CopyWithByteChanged()
    {
        var copy = (byte[])Bytes.Clone();
        copy[^1] ^= 0x5a;
        return copy;
    }

    // Change the PE machine field only. The digest is recomputed by Feed, so this
    // exercises architecture validation independently from transport/hash failure.
    public byte[] AsWrongArchitecture()
    {
        var copy = (byte[])Bytes.Clone();
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(0x3c, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(peOffset + 4, 2), 0xAA64); // ARM64
        return copy;
    }

    public byte[] WithMismatchedFixedVersion(bool product)
    {
        var copy = (byte[])Bytes.Clone();
        using var stream = new MemoryStream(copy, writable: false);
        using var reader = new PEReader(stream);
        var resource = reader.PEHeaders.SectionHeaders.Single(section => section.Name == ".rsrc");
        var changed = 0;
        // VS_FIXEDFILEINFO is the public Windows VERSIONINFO format. Change only
        // one version's fourth component, retaining the other version and strings.
        for (var offset = resource.PointerToRawData; offset <= resource.PointerToRawData + resource.SizeOfRawData - 52; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(offset, 4)) != 0xFEEF04BD
                || BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(offset + 4, 4)) != 0x00010000) continue;
            var lowVersionOffset = offset + (product ? 20 : 12);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(lowVersionOffset, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(lowVersionOffset, 4), value | 1u);
            changed++;
        }
        Assert.True(changed > 0, "The fixture must contain a Windows VERSIONINFO resource.");
        return copy;
    }

    // Corrupt only public .NET bundle envelope data, then let Feed trust the new
    // hash/size. A rejection must therefore come from executable validation.
    public byte[] WithDamagedBundle(string mode)
    {
        var copy = (byte[])Bytes.Clone();
        byte[] marker = [0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
            0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32, 0x13, 0xf5, 0xb9, 0xe6,
            0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae];
        var position = copy.AsSpan().IndexOf(marker);
        Assert.True(position >= 8, "The successful fixture must be a real published .NET single-file bundle.");
        var header = BinaryPrimitives.ReadInt64LittleEndian(copy.AsSpan(position - 8, 8));
        Assert.True(header > position + marker.Length && header < copy.LongLength - 8,
            "The successful fixture must contain its bundle manifest.");
        if (mode == "bundle-missing-marker") copy[position] ^= 0x01;
        else if (mode == "bundle-offset-zero") BinaryPrimitives.WriteInt64LittleEndian(copy.AsSpan(position - 8, 8), 0);
        else if (mode == "bundle-offset-outside") BinaryPrimitives.WriteInt64LittleEndian(copy.AsSpan(position - 8, 8), long.MaxValue);
        else if (mode == "bundle-truncated-manifest") return copy[..checked((int)header + 8)];
        else throw new ArgumentOutOfRangeException(nameof(mode));
        return copy;
    }

    public byte[] AsDll()
    {
        var copy = (byte[])Bytes.Clone();
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(0x3c, 4));
        var characteristics = BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(peOffset + 22, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(peOffset + 22, 2), (ushort)(characteristics | 0x2000));
        return copy;
    }
}

internal sealed class Feed
{
    public const string Latest = "https://api.github.com/repos/JourneyHua/WinKit/releases/latest";
    public static readonly DateTimeOffset Published = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    public const string Notes = "## Update probe\n中文发行说明 — retained verbatim.";
    public string VersionText { get; }
    public string FileName => $"WinKit-v{VersionText}-win-x64.exe";
    public string ReleaseUri => $"https://github.com/JourneyHua/WinKit/releases/tag/v{VersionText}";
    public string ExecutableUri => $"https://github.com/JourneyHua/WinKit/releases/download/v{VersionText}/{FileName}";
    public string MetadataUri => $"https://github.com/JourneyHua/WinKit/releases/download/v{VersionText}/WinKit-v{VersionText}-win-x64.update.json";
    public byte[] Payload { get; }
    public string PayloadHash { get; }
    public JsonObject Release { get; }
    public JsonObject Metadata { get; }
    public JsonObject ExecutableAsset { get; }
    public JsonObject MetadataAsset { get; }
    public byte[] MetadataBytes { get; private set; } = [];

    public Feed(Fixture fixture, byte[]? payload = null, string? versionText = null)
    {
        Payload = payload ?? fixture.Bytes;
        PayloadHash = ReferenceEquals(Payload, fixture.Bytes) ? fixture.Sha256 : Fixture.Hash(Payload);
        VersionText = versionText ?? fixture.VersionText;
        ExecutableAsset = new JsonObject
        {
            ["name"] = FileName, ["size"] = Payload.LongLength, ["state"] = "uploaded",
            ["browser_download_url"] = ExecutableUri, ["digest"] = "sha256:" + PayloadHash
        };
        MetadataAsset = new JsonObject
        {
            ["name"] = $"WinKit-v{VersionText}-win-x64.update.json", ["state"] = "uploaded",
            ["browser_download_url"] = MetadataUri
        };
        Metadata = new JsonObject
        {
            ["schemaVersion"] = 1, ["version"] = VersionText, ["rid"] = "win-x64",
            ["fileName"] = FileName, ["sha256"] = PayloadHash, ["size"] = Payload.LongLength
        };
        Release = new JsonObject
        {
            ["tag_name"] = "v" + VersionText, ["draft"] = false, ["prerelease"] = false,
            ["html_url"] = ReleaseUri, ["published_at"] = Published.ToString("O"), ["body"] = Notes,
            ["assets"] = new JsonArray(ExecutableAsset, MetadataAsset)
        };
        RefreshMetadata();
    }

    public void RefreshMetadata() => SetMetadataBytes(Encoding.UTF8.GetBytes(Metadata.ToJsonString()));

    public void SetMetadataBytes(byte[] bytes)
    {
        MetadataBytes = bytes;
        MetadataAsset["size"] = bytes.LongLength;
        MetadataAsset["digest"] = "sha256:" + Fixture.Hash(bytes);
    }
}

internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _routes = new(StringComparer.Ordinal);
    public List<Uri> Requests { get; } = [];

    public void Route(string uri, Func<HttpResponseMessage> respond) =>
        RouteAsync(uri, (_, _) => Task.FromResult(respond()));

    public void RouteAsync(string uri, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        _routes[uri] = respond;

    public int Count(string uri) => Requests.Count(value => value.AbsoluteUri == uri);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri ?? throw new InvalidOperationException("A request had no URI.");
        Requests.Add(uri);
        Assert.Equal(HttpMethod.Get, request.Method, "Update requests must be read-only GETs");
        Assert.True(request.Headers.UserAgent.Count > 0, "GitHub API requests need a user agent.");
        if (!_routes.TryGetValue(uri.AbsoluteUri, out var respond))
            throw new InvalidOperationException("Unexpected HTTP request (real network is disabled): " + uri);
        var response = await respond(request, cancellationToken);
        response.RequestMessage ??= request;
        return response;
    }

    public static HttpResponseMessage Bytes(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new ByteArrayContent(bytes) };

    public static HttpResponseMessage Json(JsonObject value) => Bytes(Encoding.UTF8.GetBytes(value.ToJsonString()));

    public static HttpResponseMessage Redirect(string uri)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(uri, UriKind.RelativeOrAbsolute);
        return response;
    }

    public static HttpResponseMessage Stream(Stream stream, long? length = null)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentLength = length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

internal sealed class World : IDisposable
{
    private readonly string _parent;
    public string Storage { get; }
    public Feed Feed { get; }
    public FakeHttp Http { get; } = new();
    public HttpClient Client { get; }
    public AppUpdateService Service { get; }
    public DateTimeOffset Now { get; set; } = new(2026, 11, 26, 1, 0, 0, TimeSpan.Zero);
    public Version Current { get; }

    public World(string parent, Fixture fixture, Feed? feed = null)
    {
        _parent = Path.GetFullPath(parent);
        Storage = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Storage);
        Current = fixture.CurrentVersion;
        Feed = feed ?? new Feed(fixture);
        Http.Route(WinKit.UpdateProbe.Feed.Latest, () => FakeHttp.Json(Feed.Release));
        Http.Route(Feed.MetadataUri, () => FakeHttp.Bytes(Feed.MetadataBytes));
        Http.Route(Feed.ExecutableUri, () => FakeHttp.Bytes(Feed.Payload));
        Client = new HttpClient(Http) { Timeout = TimeSpan.FromSeconds(30) };
        Service = NewService();
    }

    public AppUpdateService NewService() => new(Storage, Client, () => Now);

    public async Task<AppUpdateRelease> CheckAsync(bool manual = true) =>
        Assert.NotNull(await Service.CheckAsync(Current, manual, CancellationToken.None), "Expected an available update.");

    public string Destination => Path.Combine(Service.DownloadDirectory, Feed.FileName);

    public void AssertNoIncompleteFiles()
    {
        if (!Directory.Exists(Service.DownloadDirectory)) return;
        Assert.True(!Directory.EnumerateFiles(Service.DownloadDirectory, "*", SearchOption.AllDirectories)
            .Any(path => !string.Equals(path, Destination, StringComparison.OrdinalIgnoreCase)),
            "A failed/cancelled download left an uncommitted file behind.");
    }

    public void Dispose()
    {
        Service.Dispose();
        Client.Dispose();
        // This exact GUID directory was created by this World, never a caller's fixture.
        Assert.True(string.Equals(Path.GetDirectoryName(Storage), _parent, StringComparison.OrdinalIgnoreCase),
            "Refusing cleanup outside the probe workspace.");
        Directory.Delete(Storage, recursive: true);
    }
}

// Non-seekable, bounded chunks expose real streaming rather than a prebuffered
// ByteArrayContent. A second-read hook makes cancellation/failure deterministic.
internal sealed class ChunkStream : Stream
{
    private readonly byte[] _bytes;
    private readonly int _chunkSize;
    private readonly int _firstDelayMs;
    private readonly Action? _beforeSecondRead;
    private int _position;
    public int ReadCalls { get; private set; }
    public bool Exhausted => _position == _bytes.Length;

    public ChunkStream(byte[] bytes, int chunkSize = 64 * 1024, int firstDelayMs = 0, Action? beforeSecondRead = null)
    {
        _bytes = bytes;
        _chunkSize = chunkSize;
        _firstDelayMs = firstDelayMs;
        _beforeSecondRead = beforeSecondRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ReadCalls++;
        if (ReadCalls == 2) _beforeSecondRead?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadCalls == 1 && _firstDelayMs > 0) await Task.Delay(_firstDelayMs, cancellationToken);
        var count = Math.Min(Math.Min(buffer.Length, _chunkSize), _bytes.Length - _position);
        _bytes.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class CallbackProgress(Action<AppUpdateProgress> callback) : IProgress<AppUpdateProgress>
{
    public void Report(AppUpdateProgress value) => callback(value);
}
