using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wallflow.Core;

namespace Wallflow.Core.Tests;

[TestClass]
public sealed class ImageCatalogTests
{
    [TestMethod]
    public async Task Normal_folder_returns_supported_images_only_in_deterministic_order()
    {
        using var area = new CatalogArea("z.PNG", "ignore.txt", "A.jpg", "m.webp");

        var result = await ImageCatalog.ScanAsync(area.Root);

        Assert.IsTrue(result.IsAvailable);
        CollectionAssert.AreEqual(new[] { "A.jpg", "m.webp", "z.PNG" }, result.Files.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public async Task Empty_folder_is_a_valid_empty_result()
    {
        using var area = new CatalogArea();
        var result = await ImageCatalog.ScanAsync(area.Root);

        Assert.IsTrue(result.IsAvailable);
        Assert.AreEqual(0, result.Files.Count);
    }

    [TestMethod]
    public async Task Missing_directory_is_recoverable()
    {
        using var area = new CatalogArea();
        var missing = Path.Combine(area.Root, "missing");

        var result = await ImageCatalog.ScanAsync(missing);

        Assert.IsFalse(result.IsAvailable);
        Assert.IsInstanceOfType<DirectoryNotFoundException>(result.Failure!.Exception);
        Assert.AreEqual(0, result.Files.Count);
    }

    [TestMethod]
    public async Task Inaccessible_directory_failure_is_recoverable()
    {
        var scanner = new ImageCatalogScanner(enumerateFiles: _ => throw new UnauthorizedAccessException("Test-only denial."));

        var result = await scanner.ScanAsync("test-fixture");

        Assert.IsFalse(result.IsAvailable);
        Assert.IsInstanceOfType<UnauthorizedAccessException>(result.Failure!.Exception);
    }

    [TestMethod]
    public async Task Item_limit_stops_enumeration_and_returns_bounded_result()
    {
        var yielded = 0;
        IEnumerable<string> Files(string _)
        {
            foreach (var name in new[] { "c.jpg", "b.jpg", "a.jpg", "never-reached.jpg" })
            {
                yielded++;
                yield return name;
            }
        }
        var scanner = new ImageCatalogScanner(2, Files);

        var result = await scanner.ScanAsync("test-fixture");

        Assert.AreEqual(2, yielded);
        Assert.AreEqual(2, result.Files.Count);
        Assert.IsTrue(result.WasTruncated);
        CollectionAssert.AreEqual(new[] { "b.jpg", "c.jpg" }, result.Files.ToArray());
    }

    [TestMethod]
    public async Task Cancellation_before_scan_is_reported_as_cancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => ImageCatalog.ScanAsync("test-fixture", cancellation.Token));
    }

    [TestMethod]
    public async Task Cancellation_during_scan_returns_no_final_result()
    {
        using var cancellation = new CancellationTokenSource();
        IEnumerable<string> Files(string _)
        {
            yield return "one.jpg";
            cancellation.Cancel();
            yield return "two.jpg";
        }
        var scanner = new ImageCatalogScanner(enumerateFiles: Files);

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => scanner.ScanAsync("test-fixture", cancellation.Token));
    }

    [TestMethod]
    public void New_scan_cancels_and_invalidates_older_scan_for_same_monitor()
    {
        using var coordinator = new LatestScanCoordinator<string>();
        using var scanA = coordinator.Begin("monitor-1");
        using var scanB = coordinator.Begin("monitor-1");

        Assert.IsTrue(scanA.Token.IsCancellationRequested);
        Assert.IsFalse(scanA.IsCurrent);
        Assert.IsTrue(scanB.IsCurrent);
    }

    [TestMethod]
    public async Task File_disappearing_during_scan_does_not_crash_catalog()
    {
        using var area = new CatalogArea("one.jpg");
        IEnumerable<string> Files(string _)
        {
            var path = Path.Combine(area.Root, "one.jpg");
            yield return path;
            File.Delete(path);
        }
        var scanner = new ImageCatalogScanner(enumerateFiles: Files);

        var result = await scanner.ScanAsync(area.Root);

        Assert.IsTrue(result.IsAvailable);
        Assert.AreEqual(1, result.Files.Count);
    }

    [TestMethod]
    public async Task Bad_folder_does_not_prevent_independent_good_scan()
    {
        using var area = new CatalogArea("good.jpg");
        var badTask = ImageCatalog.ScanAsync(Path.Combine(area.Root, "missing"));
        var goodTask = ImageCatalog.ScanAsync(area.Root);

        await Task.WhenAll(badTask, goodTask);

        Assert.IsFalse(badTask.Result.IsAvailable);
        Assert.IsTrue(goodTask.Result.IsAvailable);
        Assert.AreEqual(1, goodTask.Result.Files.Count);
    }

    private sealed class CatalogArea : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "WallflowTests", Guid.NewGuid().ToString("N"));

        public CatalogArea(params string[] files)
        {
            Directory.CreateDirectory(Root);
            foreach (var file in files) File.WriteAllBytes(Path.Combine(Root, file), []);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
