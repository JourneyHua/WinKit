using WinKit.Services;

var service = new SystemCleanService();
var root = Path.Combine(AppContext.BaseDirectory, $"WinKit.ResultsProbe.{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    for (var size = 1; size <= 350; size++)
        await File.WriteAllBytesAsync(Path.Combine(root, $"file-{size:D3}.dat"), new byte[size]);

    var topTen = await service.ScanLargeFilesAsync(root, minBytes: 1, maxResults: 10);
    Assert(topTen.Count == 10, $"expected 10 results, got {topTen.Count}");
    Assert(topTen.Select(file => file.SizeBytes).SequenceEqual(Enumerable.Range(341, 10).Reverse().Select(size => (long)size)),
        "Top-K should return descending sizes 350 through 341");

    var capped = await service.ScanLargeFilesAsync(root, minBytes: 1, maxResults: 500);
    Assert(capped.Count == SystemCleanService.MaxLargeFileResults, $"expected cap 300, got {capped.Count}");
    Assert(capped[0].SizeBytes == 350 && capped[^1].SizeBytes == 51, "cap should retain the largest 300 files");

    var zero = await service.ScanLargeFilesAsync(root, minBytes: 1, maxResults: 0);
    Assert(zero.Count == 0, "zero result limit should return no entries");

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    var cancellationObserved = false;
    try
    {
        await service.ScanLargeFilesAsync(root, minBytes: 1, cancellationToken: cancelled.Token);
    }
    catch (OperationCanceledException)
    {
        cancellationObserved = true;
    }
    Assert(cancellationObserved, "pre-cancelled scan should throw OperationCanceledException");

    Console.WriteLine("PASS: bounded Top-K, 300-item cap, zero limit, and cancellation propagation");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
