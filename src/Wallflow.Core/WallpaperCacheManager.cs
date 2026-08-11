namespace Wallflow.Core;

public sealed class WallpaperCacheManager
{
    public const long DefaultMaximumBytes = 1024L * 1024 * 1024;
    public const long DefaultTargetBytes = 750L * 1024 * 1024;
    public const long DefaultGrowthScanThresholdBytes = 64L * 1024 * 1024;

    private static readonly TimeSpan DefaultMaintenanceInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultStaleTemporaryAge = TimeSpan.FromHours(1);

    private readonly long _maximumBytes;
    private readonly long _targetBytes;
    private readonly long _growthScanThresholdBytes;
    private readonly TimeSpan _maintenanceInterval;
    private readonly TimeSpan _staleTemporaryAge;
    private readonly Func<DateTime> _utcNow;
    private readonly object _maintenanceGate = new();
    private long _nextMaintenanceUtcTicks;
    private long _bytesWrittenSinceMaintenance;

    public WallpaperCacheManager(
        string cacheRoot,
        long maximumBytes = DefaultMaximumBytes,
        long targetBytes = DefaultTargetBytes,
        TimeSpan? maintenanceInterval = null,
        TimeSpan? staleTemporaryAge = null,
        long growthScanThresholdBytes = DefaultGrowthScanThresholdBytes,
        Func<DateTime>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (targetBytes < 0 || targetBytes > maximumBytes) throw new ArgumentOutOfRangeException(nameof(targetBytes));
        if (growthScanThresholdBytes <= 0) throw new ArgumentOutOfRangeException(nameof(growthScanThresholdBytes));

        var interval = maintenanceInterval ?? DefaultMaintenanceInterval;
        var temporaryAge = staleTemporaryAge ?? DefaultStaleTemporaryAge;
        if (interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maintenanceInterval));
        if (temporaryAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(staleTemporaryAge));

        CacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        _maximumBytes = maximumBytes;
        _targetBytes = targetBytes;
        _growthScanThresholdBytes = growthScanThresholdBytes;
        _maintenanceInterval = interval;
        _staleTemporaryAge = temporaryAge;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string CacheRoot { get; }

    public void EnsureCacheDirectory()
    {
        Directory.CreateDirectory(CacheRoot);
        if (!IsSafeCacheRoot())
            throw new IOException("Pane's wallpaper cache directory cannot be a reparse point.");
    }

    public string GetCachePath(string generatedFileName)
    {
        if (!IsGeneratedCacheFileName(generatedFileName))
            throw new ArgumentException("The cache filename is not a Pane-generated wallpaper name.", nameof(generatedFileName));

        return Path.Combine(CacheRoot, generatedFileName);
    }

    public string CreateTemporaryPath(string generatedCachePath)
    {
        if (!IsOwnedPath(generatedCachePath, IsGeneratedCacheFileName))
            throw new ArgumentException("The destination is outside Pane's cache or has an unexpected name.", nameof(generatedCachePath));

        return generatedCachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    }

    public void Touch(string generatedCachePath)
    {
        if (!TryGetOwnedRegularFile(generatedCachePath, IsGeneratedCacheFileName, out _)) return;
        try { File.SetLastWriteTimeUtc(generatedCachePath, _utcNow()); }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    public void NotifyFileWritten(long bytes)
    {
        var pendingBytes = Interlocked.Add(ref _bytesWrittenSinceMaintenance, Math.Max(0, bytes));
        PruneIfNeeded(pendingBytes >= _growthScanThresholdBytes);
    }

    public void PruneIfNeeded(bool force = false)
    {
        var now = _utcNow();
        if (!force && now.Ticks < Volatile.Read(ref _nextMaintenanceUtcTicks)) return;
        if (!Monitor.TryEnter(_maintenanceGate)) return;

        try
        {
            now = _utcNow();
            if (!force && now.Ticks < Volatile.Read(ref _nextMaintenanceUtcTicks)) return;
            Volatile.Write(ref _nextMaintenanceUtcTicks, now.Add(_maintenanceInterval).Ticks);
            PruneCore(now);
            Interlocked.Exchange(ref _bytesWrittenSinceMaintenance, 0);
        }
        finally { Monitor.Exit(_maintenanceGate); }
    }

    public bool TryDeleteTemporaryFile(string temporaryPath)
        => TryDeleteOwnedFile(temporaryPath, IsGeneratedTemporaryFileName) == DeleteResult.Deleted;

    public static bool IsGeneratedCacheFileName(string? fileName)
    {
        if (fileName is null || fileName.Length != 68 || Path.GetFileName(fileName) != fileName) return false;
        return IsHex(fileName.AsSpan(0, 64)) && fileName.AsSpan(64).Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGeneratedTemporaryFileName(string? fileName)
    {
        if (fileName is null || fileName.Length != 105 || Path.GetFileName(fileName) != fileName) return false;
        return IsGeneratedCacheFileName(fileName[..68])
            && fileName[68] == '.'
            && IsHex(fileName.AsSpan(69, 32))
            && fileName.AsSpan(101).Equals(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    private void PruneCore(DateTime now)
    {
        if (!Directory.Exists(CacheRoot) || !IsSafeCacheRoot()) return;

        var generatedFiles = new List<CacheEntry>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(CacheRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                if (IsGeneratedTemporaryFileName(fileName))
                {
                    if (TryGetOwnedRegularFile(path, IsGeneratedTemporaryFileName, out var temporary)
                        && now >= temporary.LastWriteTimeUtc
                        && now - temporary.LastWriteTimeUtc >= _staleTemporaryAge)
                        TryDeleteOwnedFile(path, IsGeneratedTemporaryFileName);
                    continue;
                }

                if (IsGeneratedCacheFileName(fileName)
                    && TryGetOwnedRegularFile(path, IsGeneratedCacheFileName, out var generated))
                    generatedFiles.Add(new CacheEntry(path, generated.Length, generated.LastWriteTimeUtc));
            }
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        long totalBytes = 0;
        foreach (var entry in generatedFiles)
            totalBytes = entry.Length > long.MaxValue - totalBytes ? long.MaxValue : totalBytes + entry.Length;

        if (totalBytes <= _maximumBytes) return;

        foreach (var entry in generatedFiles.OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            var result = TryDeleteOwnedFile(entry.Path, IsGeneratedCacheFileName);
            if (result is DeleteResult.Deleted or DeleteResult.Missing)
                totalBytes = Math.Max(0, totalBytes - entry.Length);
            if (totalBytes <= _targetBytes) break;
        }
    }

    private bool TryGetOwnedRegularFile(string path, Func<string?, bool> expectedName, out FileInfo file)
    {
        file = null!;
        if (!IsSafeCacheRoot() || !IsOwnedPath(path, expectedName)) return false;

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
            file = new FileInfo(path);
            _ = file.Length;
            _ = file.LastWriteTimeUtc;
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    private DeleteResult TryDeleteOwnedFile(string path, Func<string?, bool> expectedName)
    {
        if (!TryGetOwnedRegularFile(path, expectedName, out _))
            return File.Exists(path) ? DeleteResult.Skipped : DeleteResult.Missing;

        try
        {
            File.Delete(path);
            return DeleteResult.Deleted;
        }
        catch (FileNotFoundException) { return DeleteResult.Missing; }
        catch (DirectoryNotFoundException) { return DeleteResult.Missing; }
        catch (UnauthorizedAccessException) { return DeleteResult.Skipped; }
        catch (IOException) { return DeleteResult.Skipped; }
    }

    private bool IsSafeCacheRoot()
    {
        try
        {
            var attributes = File.GetAttributes(CacheRoot);
            return (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    private bool IsOwnedPath(string path, Func<string?, bool> expectedName)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(fullPath);
            return parent is not null
                && string.Equals(Path.TrimEndingDirectorySeparator(parent), CacheRoot, StringComparison.OrdinalIgnoreCase)
                && expectedName(Path.GetFileName(fullPath));
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
        return true;
    }

    private sealed record CacheEntry(string Path, long Length, DateTime LastWriteTimeUtc);
    private enum DeleteResult { Deleted, Missing, Skipped }
}
