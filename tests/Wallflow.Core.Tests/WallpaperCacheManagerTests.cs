using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wallflow.Core;

namespace Wallflow.Core.Tests;

[TestClass]
public sealed class WallpaperCacheManagerTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Cache_below_limit_removes_nothing()
    {
        using var area = new TestArea();
        var file = area.WriteGenerated('A', 50, Now.AddHours(-2));
        area.Manager(maximum: 100, target: 60).PruneIfNeeded(force: true);
        Assert.IsTrue(File.Exists(file));
    }

    [TestMethod]
    public void Cache_above_limit_prunes_oldest_files_to_target()
    {
        using var area = new TestArea();
        var oldest = area.WriteGenerated('A', 40, Now.AddHours(-3));
        var middle = area.WriteGenerated('B', 40, Now.AddHours(-2));
        var newest = area.WriteGenerated('C', 40, Now.AddHours(-1));

        area.Manager(maximum: 100, target: 60).PruneIfNeeded(force: true);

        Assert.IsFalse(File.Exists(oldest));
        Assert.IsFalse(File.Exists(middle));
        Assert.IsTrue(File.Exists(newest));
    }

    [TestMethod]
    public void Generated_file_outside_cache_is_never_removed()
    {
        using var area = new TestArea();
        var outside = area.WriteOutsideGenerated('D', 200);
        area.WriteGenerated('A', 60, Now.AddHours(-2));
        area.WriteGenerated('B', 60, Now.AddHours(-1));

        area.Manager(maximum: 100, target: 50).PruneIfNeeded(force: true);

        Assert.IsTrue(File.Exists(outside));
    }

    [TestMethod]
    public void Unknown_file_inside_cache_is_never_removed()
    {
        using var area = new TestArea();
        var unknown = Path.Combine(area.CacheRoot, "user-file.png");
        File.WriteAllBytes(unknown, new byte[200]);
        area.WriteGenerated('A', 60, Now.AddHours(-2));
        area.WriteGenerated('B', 60, Now.AddHours(-1));

        area.Manager(maximum: 100, target: 50).PruneIfNeeded(force: true);

        Assert.IsTrue(File.Exists(unknown));
    }

    [TestMethod]
    public void Only_sha256_png_names_are_eligible_for_pruning()
    {
        Assert.IsTrue(WallpaperCacheManager.IsGeneratedCacheFileName(new string('A', 64) + ".png"));
        Assert.IsTrue(WallpaperCacheManager.IsGeneratedCacheFileName(new string('f', 64) + ".PNG"));
        Assert.IsFalse(WallpaperCacheManager.IsGeneratedCacheFileName(new string('G', 64) + ".png"));
        Assert.IsFalse(WallpaperCacheManager.IsGeneratedCacheFileName(new string('A', 63) + ".png"));
        Assert.IsFalse(WallpaperCacheManager.IsGeneratedCacheFileName("..\\" + new string('A', 64) + ".png"));
    }

    [TestMethod]
    public void Reparse_file_is_not_followed_or_deleted()
    {
        using var area = new TestArea();
        var outside = area.WriteOutsideGenerated('D', 200);
        var link = Path.Combine(area.CacheRoot, new string('A', 64) + ".png");

        try { File.CreateSymbolicLink(link, outside); }
        catch (UnauthorizedAccessException) { Assert.Inconclusive("File symlink creation is not permitted on this machine."); return; }
        catch (PlatformNotSupportedException) { Assert.Inconclusive("File symlinks are not supported on this machine."); return; }
        catch (IOException) { Assert.Inconclusive("File symlink creation is unavailable on this machine."); return; }

        area.Manager(maximum: 1, target: 0).PruneIfNeeded(force: true);

        Assert.IsTrue(File.Exists(outside));
        Assert.IsTrue(File.Exists(link));
    }

    [TestMethod]
    public void Missing_owned_temp_does_not_fail_maintenance()
    {
        using var area = new TestArea();
        var manager = area.Manager(maximum: 100, target: 60);
        var destination = manager.GetCachePath(new string('A', 64) + ".png");
        var missingTemporary = manager.CreateTemporaryPath(destination);

        Assert.IsFalse(manager.TryDeleteTemporaryFile(missingTemporary));
        manager.PruneIfNeeded(force: true);
    }

    [TestMethod]
    public void Stale_owned_temps_are_removed_but_fresh_temps_are_preserved()
    {
        using var area = new TestArea();
        var manager = area.Manager(maximum: 100, target: 60, staleTemporaryAge: TimeSpan.FromHours(1));
        var destination = manager.GetCachePath(new string('A', 64) + ".png");
        var stale = manager.CreateTemporaryPath(destination);
        var fresh = manager.CreateTemporaryPath(destination);
        File.WriteAllBytes(stale, [1]); File.SetLastWriteTimeUtc(stale, Now.AddHours(-2));
        File.WriteAllBytes(fresh, [1]); File.SetLastWriteTimeUtc(fresh, Now);

        manager.PruneIfNeeded(force: true);

        Assert.IsFalse(File.Exists(stale));
        Assert.IsTrue(File.Exists(fresh));
    }

    [TestMethod]
    public void Newest_cache_entry_is_preserved_preferentially()
    {
        using var area = new TestArea();
        var old = area.WriteGenerated('A', 60, Now.AddDays(-1));
        var active = area.WriteGenerated('B', 60, Now);

        area.Manager(maximum: 100, target: 80).PruneIfNeeded(force: true);

        Assert.IsFalse(File.Exists(old));
        Assert.IsTrue(File.Exists(active));
    }

    [TestMethod]
    public void Unique_temp_names_are_recognizable_and_do_not_collide()
    {
        using var area = new TestArea();
        var manager = area.Manager(maximum: 100, target: 60);
        var destination = manager.GetCachePath(new string('A', 64) + ".png");
        var first = manager.CreateTemporaryPath(destination);
        var second = manager.CreateTemporaryPath(destination);

        Assert.AreNotEqual(first, second);
        Assert.IsTrue(WallpaperCacheManager.IsGeneratedTemporaryFileName(Path.GetFileName(first)));
        Assert.IsTrue(WallpaperCacheManager.IsGeneratedTemporaryFileName(Path.GetFileName(second)));
    }

    [TestMethod]
    public void Concurrent_maintenance_calls_do_not_race_or_throw()
    {
        using var area = new TestArea();
        area.WriteGenerated('A', 40, Now.AddHours(-3));
        area.WriteGenerated('B', 40, Now.AddHours(-2));
        area.WriteGenerated('C', 40, Now.AddHours(-1));
        var manager = area.Manager(maximum: 100, target: 60);

        Parallel.For(0, 16, _ => manager.PruneIfNeeded(force: true));

        var remainingBytes = Directory.EnumerateFiles(area.CacheRoot).Sum(path => new FileInfo(path).Length);
        Assert.IsTrue(remainingBytes <= 60, $"Expected at most 60 bytes, but found {remainingBytes}.");
    }

    private sealed class TestArea : IDisposable
    {
        private readonly string _testParent = Path.Combine(Path.GetTempPath(), "WallflowTests", "CacheManager");
        private readonly string _root;

        public TestArea()
        {
            _root = Path.Combine(_testParent, Guid.NewGuid().ToString("N"));
            CacheRoot = Path.Combine(_root, "cache");
            OutsideRoot = Path.Combine(_root, "outside");
            Directory.CreateDirectory(CacheRoot);
            Directory.CreateDirectory(OutsideRoot);
        }

        public string CacheRoot { get; }
        public string OutsideRoot { get; }

        public WallpaperCacheManager Manager(long maximum, long target, TimeSpan? staleTemporaryAge = null)
            => new(CacheRoot, maximum, target, TimeSpan.Zero, staleTemporaryAge, 1, () => Now);

        public string WriteGenerated(char hex, int size, DateTime lastWrite)
        {
            var path = Path.Combine(CacheRoot, new string(hex, 64) + ".png");
            File.WriteAllBytes(path, new byte[size]);
            File.SetLastWriteTimeUtc(path, lastWrite);
            return path;
        }

        public string WriteOutsideGenerated(char hex, int size)
        {
            var path = Path.Combine(OutsideRoot, new string(hex, 64) + ".png");
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            try
            {
                if (Directory.Exists(_testParent) && !Directory.EnumerateFileSystemEntries(_testParent).Any())
                    Directory.Delete(_testParent);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }
}
