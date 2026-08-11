using System.Security;

namespace Wallflow.Core;

public sealed record ImageCatalogScanFailure(string Message, Exception Exception);

public sealed record ImageCatalogScanResult(
    IReadOnlyList<string> Files,
    bool WasTruncated,
    ImageCatalogScanFailure? Failure)
{
    public bool IsAvailable => Failure is null;
}

public static class ImageCatalog
{
    public const int MaximumSlideshowFiles = 10_000;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
    private static readonly ImageCatalogScanner DefaultScanner = new();

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    public static Task<ImageCatalogScanResult> ScanAsync(string folder, CancellationToken token = default)
        => DefaultScanner.ScanAsync(folder, token);
}

public sealed class ImageCatalogScanner
{
    // Scans share a small process-local pool. Cancellation removes obsolete queued work, while a
    // filesystem call already blocked inside Windows may not observe cancellation until it returns.
    private const int MaximumConcurrentScans = 4;
    private readonly int _maximumFiles;
    private readonly Func<string, IEnumerable<string>> _enumerateFiles;
    private readonly SemaphoreSlim _scanSlots = new(MaximumConcurrentScans, MaximumConcurrentScans);

    public ImageCatalogScanner(
        int maximumFiles = ImageCatalog.MaximumSlideshowFiles,
        Func<string, IEnumerable<string>>? enumerateFiles = null)
    {
        if (maximumFiles <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        _maximumFiles = maximumFiles;
        _enumerateFiles = enumerateFiles ?? (folder => Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly));
    }

    public async Task<ImageCatalogScanResult> ScanAsync(string folder, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(folder))
            return Failed("The slideshow folder is not available.", new DirectoryNotFoundException("No slideshow folder was selected."));

        await _scanSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Scan(folder, token), token).ConfigureAwait(false);
        }
        finally
        {
            _scanSlots.Release();
        }
    }

    private ImageCatalogScanResult Scan(string folder, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var files = new List<string>(Math.Min(_maximumFiles, 256));
            var wasTruncated = false;

            foreach (var path in _enumerateFiles(folder))
            {
                token.ThrowIfCancellationRequested();
                if (!ImageCatalog.IsSupported(path)) continue;
                files.Add(path);
                if (files.Count < _maximumFiles) continue;

                // Stop immediately at the safety bound. For oversized folders, the bounded subset
                // therefore follows filesystem enumeration order and only that subset is sorted.
                wasTruncated = true;
                break;
            }

            token.ThrowIfCancellationRequested();
            files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(left), Path.GetFileName(right)));
            token.ThrowIfCancellationRequested();
            return new ImageCatalogScanResult(files, wasTruncated, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (DirectoryNotFoundException ex) { return Failed("The slideshow folder could not be found.", ex); }
        catch (UnauthorizedAccessException ex) { return Failed("Pane does not have permission to read the slideshow folder.", ex); }
        catch (PathTooLongException ex) { return Failed("The slideshow folder contains a path that is too long.", ex); }
        catch (IOException ex) { return Failed("The slideshow folder is currently unavailable.", ex); }
        catch (NotSupportedException ex) { return Failed("The slideshow folder path is not supported.", ex); }
        catch (SecurityException ex) { return Failed("Pane is not allowed to read the slideshow folder.", ex); }
        catch (ArgumentException ex) { return Failed("The slideshow folder path is invalid.", ex); }
    }

    private static ImageCatalogScanResult Failed(string message, Exception exception)
        => new([], false, new ImageCatalogScanFailure(message, exception));
}

public sealed class LatestScanCoordinator<TKey> : IDisposable where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries = [];
    private long _nextGeneration;
    private bool _disposed;

    public Operation Begin(TKey key)
    {
        CancellationTokenSource? replaced = null;
        Operation operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.Remove(key, out var entry)) replaced = entry.Source;
            var source = new CancellationTokenSource();
            var generation = ++_nextGeneration;
            _entries[key] = new Entry(generation, source);
            operation = new Operation(this, key, generation, source);
        }
        CancelAndDispose(replaced);
        return operation;
    }

    public void Cancel(TKey key)
    {
        CancellationTokenSource? source = null;
        lock (_gate)
            if (_entries.Remove(key, out var entry)) source = entry.Source;
        CancelAndDispose(source);
    }

    public void CancelAll()
    {
        CancellationTokenSource[] sources;
        lock (_gate)
        {
            sources = _entries.Values.Select(entry => entry.Source).ToArray();
            _entries.Clear();
        }
        foreach (var source in sources) CancelAndDispose(source);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        CancelAll();
    }

    private bool IsCurrent(TKey key, long generation)
    {
        lock (_gate)
            return !_disposed && _entries.TryGetValue(key, out var entry) && entry.Generation == generation;
    }

    private void Complete(TKey key, long generation, CancellationTokenSource source)
    {
        var shouldDispose = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.Generation == generation)
            {
                _entries.Remove(key);
                shouldDispose = true;
            }
        }
        if (shouldDispose) source.Dispose();
    }

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Cancel(); }
        finally { source.Dispose(); }
    }

    private sealed record Entry(long Generation, CancellationTokenSource Source);

    public sealed class Operation : IDisposable
    {
        private LatestScanCoordinator<TKey>? _owner;
        private readonly TKey _key;
        private readonly long _generation;
        private readonly CancellationTokenSource _source;
        private readonly CancellationToken _token;

        internal Operation(LatestScanCoordinator<TKey> owner, TKey key, long generation, CancellationTokenSource source)
            => (_owner, _key, _generation, _source, _token) = (owner, key, generation, source, source.Token);

        public CancellationToken Token => _token;
        public bool IsCurrent => _owner?.IsCurrent(_key, _generation) == true;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Complete(_key, _generation, _source);
        }
    }
}
