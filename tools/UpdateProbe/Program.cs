using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WinKit.Services;

namespace WinKit.UpdateProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length is not (2 or 4) || args[0] != "--fixture"
            || (args.Length == 4 && args[2] != "--framework-dependent-fixture"))
        {
            Console.Error.WriteLine("Usage: dotnet run --project tools/UpdateProbe/UpdateProbe.csproj -- --fixture <published self-contained WinKit.exe> [--framework-dependent-fixture <FDD WinKit.exe>]");
            Console.Error.WriteLine("The fixture is read only; all downloads use a fake HTTP handler and are never executed.");
            return 2;
        }

        string? root = null;
        var failures = 0;
        try
        {
            var fixture = await Fixture.LoadAsync(args[1]);
            var frameworkDependentFixture = args.Length == 4 ? await Fixture.LoadAsync(args[3]) : null;
            root = Path.Combine(Path.GetTempPath(), "WinKit-UpdateProbe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Console.WriteLine($"Fixture version {fixture.VersionText}; simulated installed version {fixture.CurrentVersion}; {fixture.Bytes.LongLength:N0} bytes.");
            var cases = new List<(string Name, Func<Task> Run)>
            {
                ("official release contract and exact asset selection", () => OfficialReleaseAsync(root, fixture)),
                ("stable release filtering and version comparison", () => ReleaseFilteringAsync(root, fixture)),
                ("strict invalid release tags", () => InvalidTagsAsync(root, fixture)),
                ("metadata and GitHub digest agreement", () => MetadataFailuresAsync(root, fixture)),
                ("official repository and hostile asset URLs", () => UnsafeUrlsAsync(root, fixture)),
                ("trusted redirects and rejected redirect escapes", () => RedirectsAsync(root, fixture)),
                ("malformed and incomplete metadata responses", () => MetadataResponsesAsync(root, fixture)),
                ("24-hour boundary, persistence, disabled and manual checks", () => PreferencesAsync(root, fixture)),
                ("failed or cancelled automatic attempts throttle and manual retry works", () => FailedAutomaticCheckAsync(root, fixture)),
                ("caller cannot invent or modify a trusted download", () => TrustBoundaryAsync(root, fixture)),
                ("valid EXE download and verified cache reuse", () => ValidDownloadAsync(root, fixture)),
                ("streamed progress precedes EOF and atomic publication", () => StreamingAsync(root, fixture)),
                ("cancelled stream removes partial file and can retry", () => CancellationAsync(root, fixture)),
                ("failed stream preserves existing cache and can retry", () => InterruptedDownloadAsync(root, fixture)),
                ("complete download HTTP response constraints", () => DownloadResponsesAsync(root, fixture)),
                ("size and SHA256 failures leave no ready download", () => PayloadIntegrityAsync(root, fixture)),
                ("PE format, AMD64 architecture and executable image", () => ExecutableIntegrityAsync(root, fixture)),
                ("candidate and embedded version mismatch", () => VersionMismatchAsync(root, fixture)),
                ("tampered and missing caches are revalidated", () => TamperedCacheAsync(root, fixture)),
                ("pre-cancelled operations and disposed service", () => LifetimeAsync(root, fixture))
            };
            if (frameworkDependentFixture is not null)
                cases.Add(("trusted framework-dependent EXE cannot be downloaded or reused",
                    () => FrameworkDependentAsync(root, frameworkDependentFixture)));
            foreach (var (name, run) in cases)
            {
                try
                {
                    await run();
                    Console.WriteLine("PASS " + name);
                }
                catch (Exception exception)
                {
                    failures++;
                    Console.Error.WriteLine("FAIL " + name + Environment.NewLine + exception);
                }
            }
            Console.WriteLine($"UpdateProbe: {cases.Count - failures}/{cases.Count} groups passed.");
        }
        catch (Exception exception)
        {
            failures++;
            Console.Error.WriteLine("Probe setup failed: " + exception);
        }
        finally
        {
            if (root is not null)
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception exception)
                {
                    failures++;
                    Console.Error.WriteLine("Temporary-directory cleanup failed: " + exception);
                }
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static async Task OfficialReleaseAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        // Unrelated installer/architecture assets must not influence the fixed x64 selection.
        ((JsonArray)w.Feed.Release["assets"]!).Insert(0, new JsonObject
        {
            ["name"] = $"WinKit-v{fixture.VersionText}-win-arm64.exe",
            ["browser_download_url"] = "https://untrusted.example/WinKit.exe"
        });
        var release = await w.CheckAsync();
        Assert.Equal(fixture.Version, release.Version, "Candidate version");
        Assert.Equal(fixture.VersionText, release.VersionText, "Candidate SemVer text");
        Assert.Equal(Feed.Notes, release.ReleaseNotes, "Release notes must remain intact");
        Assert.Equal(Feed.Published, release.PublishedAt, "Release timestamp");
        Assert.Equal(w.Feed.ReleaseUri, release.ReleaseUri.AbsoluteUri, "Official release page");
        Assert.Equal(w.Feed.ExecutableUri, release.DownloadUri.AbsoluteUri, "Official executable URL");
        Assert.Equal(w.Feed.FileName, release.FileName, "Fixed single-EXE asset name");
        Assert.Equal(fixture.Sha256, release.Sha256, "Verified release digest");
        Assert.Equal(fixture.Bytes.LongLength, release.Size, "Verified release size");
        Assert.Equal(1, w.Http.Count(Feed.Latest), "Only the fixed official latest API is queried");
        Assert.Equal(1, w.Http.Count(w.Feed.MetadataUri), "Check must fetch verification metadata");
        Assert.Equal(0, w.Http.Count(w.Feed.ExecutableUri), "Check must not download the executable");
        Assert.True(!File.Exists(w.Destination), "Check alone must not produce an executable cache.");
    }

    private static async Task ReleaseFilteringAsync(string root, Fixture fixture)
    {
        foreach (var filter in new[] { "draft", "prerelease", "semver-prerelease" })
        {
            using var w = new World(root, fixture);
            if (filter == "semver-prerelease") w.Feed.Release["tag_name"] = "v" + fixture.VersionText + "-rc.1";
            else w.Feed.Release[filter] = true;
            Assert.True(await w.Service.CheckAsync(w.Current, true) is null, filter + " must not be offered.");
            Assert.Equal(1, w.Http.Requests.Count, "Filtered releases must not fetch metadata or EXE");
        }
        foreach (var current in new[]
        {
            fixture.Version,
            new Version(fixture.Version.Major, fixture.Version.Minor, fixture.Version.Build, 0),
            new Version(fixture.Version.Major, fixture.Version.Minor, fixture.Version.Build + 1)
        })
        {
            using var w = new World(root, fixture);
            // Existing releases predate the metadata protocol; no newer update must not be an error.
            w.Feed.Release.Remove("assets");
            Assert.True(await w.Service.CheckAsync(current, true) is null, "Equal/older releases must not be offered.");
            Assert.Equal(1, w.Http.Requests.Count, "No metadata request for equal/older versions");
        }
        using var missing = new World(root, fixture);
        missing.Http.Route(Feed.Latest, () => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.True(await missing.Service.CheckAsync(missing.Current, true) is null, "No published release is a normal no-update result.");
    }

    private static async Task InvalidTagsAsync(string root, Fixture fixture)
    {
        foreach (var tag in new[] { "1.2.3", "V1.2.3", "v1.2", "v1.2.3.4", "v01.2.3", "v1.02.3", "v1.2.03", "v1.2.3-01", "v2147483648.0.0", "v1.2.3 " })
        {
            using var w = new World(root, fixture);
            w.Feed.Release["tag_name"] = tag;
            await Assert.ThrowsAsync<InvalidDataException>(() => w.Service.CheckAsync(w.Current, true));
            Assert.Equal(1, w.Http.Requests.Count, "Invalid tags must fail before fetching assets: " + tag);
        }
    }

    private static async Task MetadataFailuresAsync(string root, Fixture fixture)
    {
        var changes = new (string Name, Action<Feed> Change)[]
        {
            ("schema", f => f.Metadata["schemaVersion"] = 2),
            ("version", f => f.Metadata["version"] = "0.0.0"),
            ("rid", f => f.Metadata["rid"] = "win-arm64"),
            ("filename", f => f.Metadata["fileName"] = "WinKit.exe"),
            ("size", f => f.Metadata["size"] = f.Payload.LongLength + 1),
            ("zero-size", f => f.Metadata["size"] = 0),
            ("malformed-hash", f => f.Metadata["sha256"] = "not-a-sha256"),
            ("API-digest-disagrees", f => f.Metadata["sha256"] = new string('0', 64)),
            ("missing-required-version", f => f.Metadata.Remove("version")),
            ("API-unsupported-digest", f => f.ExecutableAsset["digest"] = "sha512:" + new string('0', 128)),
            ("asset-not-uploaded", f => f.ExecutableAsset["state"] = "new"),
            ("duplicate-executable", f => ((JsonArray)f.Release["assets"]!).Add(f.ExecutableAsset.DeepClone())),
            ("missing-executable", f => ((JsonArray)f.Release["assets"]!).Remove(f.ExecutableAsset)),
            ("missing-metadata", f => ((JsonArray)f.Release["assets"]!).Remove(f.MetadataAsset))
        };
        foreach (var (name, change) in changes)
        {
            using var w = new World(root, fixture);
            change(w.Feed);
            w.Feed.RefreshMetadata();
            await Assert.ThrowsAsync<InvalidDataException>(() => w.Service.CheckAsync(w.Current, true));
            Assert.Equal(0, w.Http.Count(w.Feed.ExecutableUri), name + " must not fetch the EXE");
            Assert.True(!File.Exists(w.Destination), name + " must not publish a cache.");
        }
        using var duplicate = new World(root, fixture);
        duplicate.Feed.SetMetadataBytes(Encoding.UTF8.GetBytes(duplicate.Feed.Metadata.ToJsonString().Insert(1, "\"rid\":\"win-x64\",")));
        await Assert.ThrowsAsync<InvalidDataException>(() => duplicate.Service.CheckAsync(duplicate.Current, true));

        using var legacyDigest = new World(root, fixture);
        legacyDigest.Feed.ExecutableAsset.Remove("digest");
        legacyDigest.Feed.MetadataAsset.Remove("digest");
        Assert.Equal(fixture.Sha256, (await legacyDigest.CheckAsync()).Sha256, "Metadata remains usable when GitHub omits its optional digest");
    }

    private static async Task UnsafeUrlsAsync(string root, Fixture fixture)
    {
        foreach (var target in new[] { "release", "executable", "metadata" })
        foreach (var mutation in new[] { "other-repo", "http", "lookalike", "userinfo", "port", "query", "fragment", "other-version" })
        {
            using var w = new World(root, fixture);
            var canonical = target == "release" ? w.Feed.ReleaseUri : target == "executable" ? w.Feed.ExecutableUri : w.Feed.MetadataUri;
            var hostile = mutation switch
            {
                "other-repo" => canonical.Replace("/JourneyHua/WinKit/", "/OtherOwner/WinKit/", StringComparison.Ordinal),
                "http" => canonical.Replace("https:", "http:", StringComparison.Ordinal),
                "lookalike" => canonical.Replace("github.com/", "github.com.evil.example/", StringComparison.Ordinal),
                "userinfo" => canonical.Replace("github.com/", "github.com@evil.example/", StringComparison.Ordinal),
                "port" => canonical.Replace("github.com/", "github.com:444/", StringComparison.Ordinal),
                "query" => canonical + "?download=1",
                "fragment" => canonical + "#other",
                _ => canonical.Replace("/v" + fixture.VersionText, "/v0.0.0", StringComparison.Ordinal)
            };
            if (target == "release") w.Feed.Release["html_url"] = hostile;
            else if (target == "executable") w.Feed.ExecutableAsset["browser_download_url"] = hostile;
            else w.Feed.MetadataAsset["browser_download_url"] = hostile;
            await Assert.ThrowsAsync<InvalidDataException>(() => w.Service.CheckAsync(w.Current, true));
            Assert.Equal(1, w.Http.Requests.Count, target + "/" + mutation + " must fail before following the asset URL");
        }
    }

    private static async Task RedirectsAsync(string root, Fixture fixture)
    {
        foreach (var location in new[]
        {
            "https://evil.example/payload", "http://release-assets.githubusercontent.com/payload",
            "https://release-assets.githubusercontent.com.evil.example/payload", "https://user@objects.githubusercontent.com/payload",
            "https://objects.githubusercontent.com:444/payload", "https://objects.githubusercontent.com/payload#fragment",
            "https://github.com/OtherOwner/WinKit/releases/download/v1.0.0/WinKit.exe"
        })
        {
            using var w = new World(root, fixture);
            w.Http.Route(w.Feed.MetadataUri, () => FakeHttp.Redirect(location));
            await Assert.ThrowsAsync<InvalidDataException>(() => w.Service.CheckAsync(w.Current, true));
            Assert.Equal(2, w.Http.Requests.Count, "Untrusted redirects must be rejected before making their request");
        }
        using (var apiEscape = new World(root, fixture))
        {
            apiEscape.Http.Route(Feed.Latest, () => FakeHttp.Redirect("https://api.github.com/repos/OtherOwner/WinKit/releases/latest"));
            await Assert.ThrowsAsync<InvalidDataException>(() => apiEscape.CheckAsync());
            Assert.Equal(1, apiEscape.Http.Requests.Count, "API redirects cannot switch repositories");
        }
        using (var implicitRedirect = new World(root, fixture))
        {
            implicitRedirect.Http.Route(Feed.Latest, () =>
            {
                var response = FakeHttp.Json(implicitRedirect.Feed.Release);
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/release");
                return response;
            });
            await Assert.ThrowsAsync<InvalidDataException>(() => implicitRedirect.CheckAsync());
        }
        using (var loop = new World(root, fixture))
        {
            loop.Http.Route(loop.Feed.MetadataUri, () => FakeHttp.Redirect(loop.Feed.MetadataUri));
            await Assert.ThrowsAsync<InvalidDataException>(() => loop.CheckAsync());
            Assert.True(loop.Http.Requests.Count < 10, "Redirect loops must terminate promptly.");
        }
        using var allowed = new World(root, fixture);
        const string metadataCdn = "https://release-assets.githubusercontent.com/probe/metadata?signature=fixture";
        const string executableCdn = "https://objects.githubusercontent.com/probe/exe?signature=fixture";
        allowed.Http.Route(allowed.Feed.MetadataUri, () => FakeHttp.Redirect(metadataCdn));
        allowed.Http.Route(metadataCdn, () => FakeHttp.Bytes(allowed.Feed.MetadataBytes));
        var release = await allowed.CheckAsync();
        allowed.Http.Route(allowed.Feed.ExecutableUri, () => FakeHttp.Redirect(executableCdn));
        allowed.Http.Route(executableCdn, () => FakeHttp.Bytes(allowed.Feed.Payload));
        var download = await allowed.Service.DownloadAsync(release);
        Assert.True(await allowed.Service.ValidateDownloadAsync(download), "Canonical GitHub asset redirects must remain supported.");
        Assert.Equal(1, allowed.Http.Count(executableCdn), "Allowed CDN is reached exactly once");
    }

    private static async Task MetadataResponsesAsync(string root, Fixture fixture)
    {
        foreach (var mode in new[] { "invalid-json", "duplicate-release-field", "metadata-hash", "short-body", "oversized-body", "content-encoding", "partial-content" })
        {
            using var w = new World(root, fixture);
            if (mode == "invalid-json") w.Http.Route(Feed.Latest, () => FakeHttp.Bytes(Encoding.UTF8.GetBytes("not JSON")));
            else if (mode == "duplicate-release-field") w.Http.Route(Feed.Latest, () => FakeHttp.Bytes(Encoding.UTF8.GetBytes(w.Feed.Release.ToJsonString().Insert(1, "\"draft\":false,"))));
            else if (mode == "metadata-hash") w.Feed.MetadataAsset["digest"] = "sha256:" + new string('0', 64);
            else if (mode == "short-body") w.Http.Route(w.Feed.MetadataUri, () => FakeHttp.Stream(new ChunkStream(w.Feed.MetadataBytes[..^1])));
            else if (mode == "oversized-body") w.Http.Route(w.Feed.MetadataUri, () => FakeHttp.Stream(new ChunkStream([.. w.Feed.MetadataBytes, (byte)' '])));
            else w.Http.Route(w.Feed.MetadataUri, () =>
            {
                var response = FakeHttp.Bytes(w.Feed.MetadataBytes, mode == "partial-content" ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
                if (mode == "content-encoding") response.Content.Headers.ContentEncoding.Add("gzip");
                return response;
            });
            await Assert.ThrowsAsync<InvalidDataException>(() => w.Service.CheckAsync(w.Current, true));
            Assert.Equal(0, w.Http.Count(w.Feed.ExecutableUri), mode + " must not download EXE");
        }
    }

    private static async Task PreferencesAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        Assert.True(w.Service.AutomaticChecksEnabled && w.Service.IsAutomaticCheckDue(), "Fresh preferences must allow an automatic check.");
        await w.CheckAsync(manual: false);
        Assert.True(!w.Service.IsAutomaticCheckDue(), "A completed automatic check reserves the interval.");
        using var reopened = w.NewService();
        w.Now += TimeSpan.FromHours(24) - TimeSpan.FromTicks(1);
        Assert.True(!reopened.IsAutomaticCheckDue(), "24 hours minus one tick must remain throttled after restart.");
        Assert.True(await reopened.CheckAsync(w.Current, false) is null, "Throttled automatic check returns no release.");
        Assert.Equal(1, w.Http.Count(Feed.Latest), "Throttled check must not access HTTP");
        w.Now += TimeSpan.FromTicks(1);
        Assert.True(reopened.IsAutomaticCheckDue(), "At exactly 24 hours a check must be due.");
        Assert.NotNull(await reopened.CheckAsync(w.Current, false), "Boundary automatic check must run.");
        reopened.AutomaticChecksEnabled = false;
        using var disabled = w.NewService();
        Assert.True(!disabled.AutomaticChecksEnabled && !disabled.IsAutomaticCheckDue(), "Disabled preference survives a new service instance.");
        var before = w.Http.Count(Feed.Latest);
        Assert.True(await disabled.CheckAsync(w.Current, false) is null, "Disabled automatic check must be skipped.");
        Assert.Equal(before, w.Http.Count(Feed.Latest), "Disabled check must not access HTTP");
        Assert.NotNull(await disabled.CheckAsync(w.Current, true), "Manual check must work while automatic checks are disabled.");
        Assert.NotNull(await disabled.CheckAsync(w.Current, true), "Manual check must not be throttled.");
        Assert.Equal(before + 2, w.Http.Count(Feed.Latest), "Each manual check reaches the API");
        disabled.AutomaticChecksEnabled = true;
        using var enabledAgain = w.NewService();
        Assert.True(enabledAgain.AutomaticChecksEnabled, "Re-enabling automatic checks persists.");
        w.Now -= TimeSpan.FromHours(1);
        Assert.True(!enabledAgain.IsAutomaticCheckDue(), "A backwards clock must not cause repeated automatic checks.");

        using var manualOnly = new World(root, fixture);
        await manualOnly.CheckAsync();
        Assert.True(manualOnly.Service.IsAutomaticCheckDue(), "Manual checks must not consume the automatic interval.");
    }

    private static async Task FailedAutomaticCheckAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        w.Http.Route(Feed.Latest, () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(() => w.Service.CheckAsync(w.Current, false));
        using var reopened = w.NewService();
        Assert.True(!reopened.IsAutomaticCheckDue(), "Failed automatic attempts must persist the throttle.");
        Assert.True(await reopened.CheckAsync(w.Current, false) is null, "Background failure must not create an immediate retry storm.");
        Assert.Equal(1, w.Http.Count(Feed.Latest), "Failed automatic request is not repeated");
        w.Http.Route(Feed.Latest, () => FakeHttp.Json(w.Feed.Release));
        Assert.NotNull(await reopened.CheckAsync(w.Current, true), "Manual retry after failure must recover.");
        Assert.Equal(2, w.Http.Count(Feed.Latest), "Manual retry reaches HTTP");

        using var cancelled = new World(root, fixture);
        using var cancellation = new CancellationTokenSource();
        cancelled.Http.RouteAsync(Feed.Latest, (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.Service.CheckAsync(cancelled.Current, false, cancellation.Token));
        using var afterCancellation = cancelled.NewService();
        Assert.True(!afterCancellation.IsAutomaticCheckDue(), "An automatic request cancelled after reaching HTTP must retain its persisted interval.");
        Assert.True(await afterCancellation.CheckAsync(cancelled.Current, false) is null, "A cancelled background request must not cause an immediate retry storm.");
        Assert.Equal(1, cancelled.Http.Count(Feed.Latest), "Automatic cancellation must not repeat the request");
        cancelled.Http.Route(Feed.Latest, () => FakeHttp.Json(cancelled.Feed.Release));
        Assert.NotNull(await afterCancellation.CheckAsync(cancelled.Current, true), "Manual retry must recover after an automatic request is cancelled.");
    }

    private static async Task TrustBoundaryAsync(string root, Fixture fixture)
    {
        using var checkedWorld = new World(root, fixture);
        var release = await checkedWorld.CheckAsync();
        using var fresh = new World(root, fixture);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fresh.Service.DownloadAsync(release));
        Assert.Equal(0, fresh.Http.Requests.Count, "A record from another instance cannot initiate HTTP without a check");
        await Assert.ThrowsAsync<InvalidOperationException>(() => checkedWorld.Service.DownloadAsync(release with { Sha256 = new string('0', 64) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => checkedWorld.Service.DownloadAsync(release with { DownloadUri = new Uri("https://evil.example/WinKit.exe") }));
        Assert.Equal(0, checkedWorld.Http.Count(checkedWorld.Feed.ExecutableUri), "Caller-modified records cannot start downloads");
        Assert.True(!File.Exists(checkedWorld.Destination), "Rejected download must not write a cache.");
        await checkedWorld.Service.CheckAsync(fixture.Version, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => checkedWorld.Service.DownloadAsync(release));
        Assert.Equal(0, checkedWorld.Http.Count(checkedWorld.Feed.ExecutableUri), "A trusted record cannot later downgrade an updated current version");
    }

    private static async Task ValidDownloadAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        var release = await w.CheckAsync();
        var download = await w.Service.DownloadAsync(release);
        Assert.Equal(w.Destination, download.FilePath, "Download must use the dedicated cache and canonical filename");
        Assert.Equal(release, download.Release, "Downloaded record retains the verified release");
        Assert.True(await w.Service.ValidateDownloadAsync(download), "Valid AMD64 executable must validate.");
        await AssertFileAsync(w.Destination, fixture.Bytes.LongLength, fixture.Sha256);
        var cached = await w.Service.DownloadAsync(release);
        Assert.Equal(download.FilePath, cached.FilePath, "A verified cache is reusable");
        Assert.Equal(1, w.Http.Count(w.Feed.ExecutableUri), "Valid cache reuse must not redownload");
        var outside = Path.Combine(w.Storage, "outside.exe");
        File.Copy(download.FilePath, outside);
        Assert.True(!await w.Service.ValidateDownloadAsync(new AppUpdateDownload(outside, release)), "A valid EXE outside the owned download directory must not be install-ready.");
        w.AssertNoIncompleteFiles();
    }

    private static async Task StreamingAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        var release = await w.CheckAsync();
        var stream = new ChunkStream(fixture.Bytes, Math.Min(64 * 1024, fixture.Bytes.Length / 2), firstDelayMs: 150);
        w.Http.Route(w.Feed.ExecutableUri, () => FakeHttp.Stream(stream, release.Size));
        var samples = new List<AppUpdateProgress>();
        var sawIntermediateBeforeEof = false;
        var progress = new CallbackProgress(value =>
        {
            Assert.Equal(release.Size, value.TotalBytes, "Progress must use the trusted total size");
            Assert.True(value.BytesReceived >= 0 && value.BytesReceived <= value.TotalBytes, "Progress byte count is bounded.");
            if (samples.Count > 0) Assert.True(value.BytesReceived >= samples[^1].BytesReceived, "Progress is monotonic.");
            if (value.BytesReceived > 0 && value.BytesReceived < value.TotalBytes)
            {
                sawIntermediateBeforeEof |= !stream.Exhausted;
                Assert.True(!File.Exists(w.Destination), "A streamed file must not be published before complete validation.");
            }
            samples.Add(value);
        });
        var download = await w.Service.DownloadAsync(release, progress);
        Assert.True(sawIntermediateBeforeEof, "Nonterminal progress must arrive before the stream reaches EOF.");
        Assert.Equal(0L, samples[0].BytesReceived, "A new download starts at zero");
        Assert.Equal(release.Size, samples[^1].BytesReceived, "A completed download reports all bytes");
        Assert.Equal(100d, samples[^1].Percent, "A completed download reports 100 percent");
        Assert.True(stream.ReadCalls > 2, "The response must be consumed as multiple reads.");
        Assert.True(await w.Service.ValidateDownloadAsync(download), "Streamed download must produce a valid cache.");
    }

    private static async Task CancellationAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        var release = await w.CheckAsync();
        using var cancellation = new CancellationTokenSource();
        var sawTemporaryFile = false;
        var stream = new ChunkStream(fixture.Bytes, beforeSecondRead: () =>
        {
            sawTemporaryFile = Directory.EnumerateFiles(w.Service.DownloadDirectory).Any();
            cancellation.Cancel();
        });
        w.Http.Route(w.Feed.ExecutableUri, () => FakeHttp.Stream(stream, release.Size));
        await Assert.ThrowsAsync<OperationCanceledException>(() => w.Service.DownloadAsync(release, cancellationToken: cancellation.Token));
        Assert.True(sawTemporaryFile, "Cancellation must happen after an actual partial write.");
        Assert.True(!File.Exists(w.Destination), "Cancellation must not publish a cache.");
        w.AssertNoIncompleteFiles();
        w.Http.Route(w.Feed.ExecutableUri, () => FakeHttp.Bytes(fixture.Bytes));
        Assert.True(await w.Service.ValidateDownloadAsync(await w.Service.DownloadAsync(release)), "The same instance must be able to retry after cancellation.");
        Assert.Equal(2, w.Http.Count(w.Feed.ExecutableUri), "Retry issues a fresh download");
    }

    private static async Task InterruptedDownloadAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        var release = await w.CheckAsync();
        Directory.CreateDirectory(w.Service.DownloadDirectory);
        byte[] originalCache = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(w.Destination, originalCache);
        w.Http.Route(w.Feed.ExecutableUri, () => FakeHttp.Stream(new ChunkStream(fixture.Bytes,
            beforeSecondRead: () => throw new IOException("Injected network stream interruption.")), release.Size));
        await Assert.ThrowsAsync<IOException>(() => w.Service.DownloadAsync(release));
        Assert.True((await File.ReadAllBytesAsync(w.Destination)).SequenceEqual(originalCache), "A failed replacement must leave the previous cache byte-for-byte intact.");
        w.AssertNoIncompleteFiles();
        w.Http.Route(w.Feed.ExecutableUri, () => FakeHttp.Bytes(fixture.Bytes));
        var recovered = await w.Service.DownloadAsync(release);
        Assert.True(await w.Service.ValidateDownloadAsync(recovered), "Retry after a transport interruption must replace the invalid old cache.");
        await AssertFileAsync(w.Destination, release.Size, fixture.Sha256);
    }

    private static async Task DownloadResponsesAsync(string root, Fixture fixture)
    {
        foreach (var mode in new[] { "length", "partial-content", "range-header", "encoding", "status" })
        {
            using var w = new World(root, fixture);
            var release = await w.CheckAsync();
            w.Http.Route(w.Feed.ExecutableUri, () =>
            {
                var response = FakeHttp.Bytes(fixture.Bytes, mode switch
                {
                    "partial-content" => HttpStatusCode.PartialContent,
                    "status" => HttpStatusCode.ServiceUnavailable,
                    _ => HttpStatusCode.OK
                });
                if (mode == "length") response.Content.Headers.ContentLength = release.Size - 1;
                if (mode == "range-header") response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, release.Size - 1, release.Size);
                if (mode == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
                return response;
            });
            if (mode == "status") await Assert.ThrowsAsync<HttpRequestException>(() => w.Service.DownloadAsync(release));
            else await Assert.ThrowsAsync<InvalidDataException>(() => w.Service.DownloadAsync(release));
            Assert.True(!File.Exists(w.Destination), mode + " must not publish a cache.");
            w.AssertNoIncompleteFiles();
        }
    }

    private static async Task PayloadIntegrityAsync(string root, Fixture fixture)
    {
        foreach (var mode in new[] { "short", "overrun", "hash" })
        {
            using var w = new World(root, fixture);
            var release = await w.CheckAsync();
            byte[] payload = mode switch
            {
                "short" => fixture.Bytes[..^1],
                "overrun" => [.. fixture.Bytes, 0x42],
                _ => fixture.CopyWithByteChanged()
            };
            w.Http.Route(w.Feed.ExecutableUri, () => FakeHttp.Stream(new ChunkStream(payload)));
            await Assert.ThrowsAsync<InvalidDataException>(() => w.Service.DownloadAsync(release));
            Assert.True(!File.Exists(w.Destination), mode + " payload must never become ready.");
            w.AssertNoIncompleteFiles();
            w.Http.Route(w.Feed.ExecutableUri, () => FakeHttp.Bytes(fixture.Bytes));
            Assert.True(await w.Service.ValidateDownloadAsync(await w.Service.DownloadAsync(release)), mode + " failure must allow a clean retry.");
        }
    }

    private static async Task ExecutableIntegrityAsync(string root, Fixture fixture)
    {
        foreach (var mode in new[] { "not-PE", "wrong-architecture", "DLL", "file-version", "product-version",
            "bundle-missing-marker", "bundle-offset-zero", "bundle-offset-outside", "bundle-truncated-manifest" })
        {
            var payload = mode switch
            {
                "not-PE" => Encoding.UTF8.GetBytes("This matches its trusted digest but is not an executable."),
                "wrong-architecture" => fixture.AsWrongArchitecture(),
                "file-version" => fixture.WithMismatchedFixedVersion(product: false),
                "product-version" => fixture.WithMismatchedFixedVersion(product: true),
                var value when value.StartsWith("bundle-", StringComparison.Ordinal) => fixture.WithDamagedBundle(value),
                _ => fixture.AsDll()
            };
            using var w = new World(root, fixture, new Feed(fixture, payload));
            var release = await w.CheckAsync();
            Assert.Equal(Fixture.Hash(payload), release.Sha256, "The PE test payload must have a valid trusted hash");
            await Assert.ThrowsAsync<InvalidDataException>(() => w.Service.DownloadAsync(release));
            Assert.True(!File.Exists(w.Destination), mode + " must fail even when size and SHA256 match.");
            w.AssertNoIncompleteFiles();
        }
    }

    private static async Task VersionMismatchAsync(string root, Fixture fixture)
    {
        var claimedVersion = new Version(fixture.Version.Major, fixture.Version.Minor, fixture.Version.Build + 1).ToString(3);
        using var w = new World(root, fixture, new Feed(fixture, versionText: claimedVersion));
        var release = await w.CheckAsync();
        Assert.Equal(fixture.Sha256, release.Sha256, "Wrong-version fixture still has a valid trusted digest");
        await Assert.ThrowsAsync<InvalidDataException>(() => w.Service.DownloadAsync(release));
        Assert.True(!File.Exists(w.Destination), "A real AMD64 EXE with the wrong embedded version must not be published.");
        w.AssertNoIncompleteFiles();
    }

    private static async Task TamperedCacheAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        var release = await w.CheckAsync();
        var download = await w.Service.DownloadAsync(release);
        await using (var file = new FileStream(download.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            file.Position = file.Length - 1;
            var original = file.ReadByte();
            file.Position--;
            file.WriteByte((byte)(original ^ 0x5a));
        }
        Assert.True(!await w.Service.ValidateDownloadAsync(download), "Same-size cache tampering must be detected on every validation.");
        download = await w.Service.DownloadAsync(release);
        Assert.Equal(2, w.Http.Count(w.Feed.ExecutableUri), "A corrupt cache must be downloaded again");
        Assert.True(await w.Service.ValidateDownloadAsync(download), "Redownload must repair the cache.");
        await using (var file = new FileStream(download.FilePath, FileMode.Open, FileAccess.Write, FileShare.None)) file.SetLength(file.Length - 1);
        Assert.True(!await w.Service.ValidateDownloadAsync(download), "Truncated cache must fail validation.");
        File.Delete(download.FilePath);
        Assert.True(!await w.Service.ValidateDownloadAsync(download), "Missing cache must fail validation.");
        download = await w.Service.DownloadAsync(release);
        Assert.Equal(3, w.Http.Count(w.Feed.ExecutableUri), "A missing cache must be downloaded again");
        Assert.True(await w.Service.ValidateDownloadAsync(download), "Missing-cache recovery must produce a valid executable.");
    }

    private static async Task LifetimeAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => w.Service.CheckAsync(w.Current, false, cancellation.Token));
        Assert.Equal(0, w.Http.Requests.Count, "Pre-cancelled checks must not access HTTP");
        Assert.True(w.Service.IsAutomaticCheckDue(), "Pre-cancelled checks must not reserve an automatic interval.");
        var release = await w.CheckAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => w.Service.DownloadAsync(release, cancellationToken: cancellation.Token));
        Assert.Equal(0, w.Http.Count(w.Feed.ExecutableUri), "Pre-cancelled downloads must not access HTTP");
        w.AssertNoIncompleteFiles();
        w.Service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => w.Service.CheckAsync(w.Current, true));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => w.Service.DownloadAsync(release));
    }

    private static async Task FrameworkDependentAsync(string root, Fixture fixture)
    {
        using var w = new World(root, fixture);
        var release = await w.CheckAsync();
        Assert.Equal(fixture.Sha256, release.Sha256, "FDD fixture has the independently trusted hash");
        await Assert.ThrowsAsync<InvalidDataException>(() => w.Service.DownloadAsync(release));
        Assert.True(!File.Exists(w.Destination), "An FDD apphost must not be published even when size, hash, AMD64 PE and versions match.");
        w.AssertNoIncompleteFiles();
        Directory.CreateDirectory(w.Service.DownloadDirectory);
        await File.WriteAllBytesAsync(w.Destination, fixture.Bytes);
        Assert.True(!await w.Service.ValidateDownloadAsync(new AppUpdateDownload(w.Destination, release)),
            "An FDD apphost manually present at the canonical cache path must not become install-ready.");
        await Assert.ThrowsAsync<InvalidDataException>(() => w.Service.DownloadAsync(release));
        Assert.Equal(2, w.Http.Count(w.Feed.ExecutableUri), "An invalid FDD cache must be rejected and fetched again rather than reused");
        w.AssertNoIncompleteFiles();
    }

    private static async Task AssertFileAsync(string path, long size, string hash)
    {
        await using var file = File.OpenRead(path);
        Assert.Equal(size, file.Length, "Published cache size");
        Assert.Equal(hash, Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant(), "Published cache digest");
    }
}
