using System.Text.Json;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Wallflow.Core;

public interface IMonitorService { Task<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken token = default); }
public interface IWallpaperService
{
    Task SetWallpaperAsync(string monitorId, string imagePath, WallpaperFit fit, CancellationToken token = default);
    Task<string?> GetWallpaperAsync(string monitorId, CancellationToken token = default);
}
public interface IWallpaperTransitionService
{
    Task ApplyAsync(MonitorInfo monitor, string imagePath, MonitorWallpaperProfile profile, CancellationToken token = default);
}

public sealed class WallpaperItemException(string message, Exception innerException) : Exception(message, innerException);

public sealed class ProfileStore(string settingsPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public async Task<List<MonitorWallpaperProfile>> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(settingsPath)) return [];
        await using var stream = File.OpenRead(settingsPath);
        var profiles = await JsonSerializer.DeserializeAsync<List<MonitorWallpaperProfile>>(stream, JsonOptions, token) ?? [];
        foreach (var profile in profiles) SlideshowPolicy.NormalizeProfile(profile);
        return profiles;
    }
    public async Task SaveAsync(IEnumerable<MonitorWallpaperProfile> profiles, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temporary = settingsPath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, profiles, JsonOptions, token);
        File.Move(temporary, settingsPath, true);
    }
}

public sealed class SlideshowSession : IAsyncDisposable
{
    private readonly MonitorInfo _monitor; private readonly MonitorWallpaperProfile _profile;
    private readonly IWallpaperTransitionService _transition; private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IReadOnlyList<string>? _initialFiles;
    private CancellationTokenSource? _cts; private Task? _worker;
    public SlideshowSession(
        MonitorInfo monitor,
        MonitorWallpaperProfile profile,
        IWallpaperTransitionService transition,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : this(monitor, profile, transition, null, delay) { }

    public SlideshowSession(
        MonitorInfo monitor,
        MonitorWallpaperProfile profile,
        IWallpaperTransitionService transition,
        IReadOnlyList<string> initialFiles)
        : this(monitor, profile, transition, initialFiles, null) { }

    private SlideshowSession(
        MonitorInfo monitor,
        MonitorWallpaperProfile profile,
        IWallpaperTransitionService transition,
        IReadOnlyList<string>? initialFiles,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        (_monitor, _profile, _transition, _initialFiles) = (monitor, profile, transition, initialFiles);
        _delay = delay ?? Task.Delay;
    }
    public event EventHandler<string>? WallpaperChanged;
    public Exception? Failure { get; private set; }
    public Task Completion => _worker ?? Task.CompletedTask;
    public void Start()
    {
        if (_worker is { IsCompleted: false }) return;
        _cts?.Dispose(); SlideshowPolicy.NormalizeProfile(_profile); Failure = null;
        _cts = new(); _worker = ObserveWorkerAsync(_cts.Token);
    }
    public void Stop() => _cts?.Cancel();
    private async Task ObserveWorkerAsync(CancellationToken token)
    {
        try { await RunAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Failure = ex;
            Debug.WriteLine($"Pane slideshow worker stopped after an unexpected {ex.GetType().Name}: {ex.Message}");
        }
    }
    private async Task RunAsync(CancellationToken token)
    {
        var files = _initialFiles ?? (await ImageCatalog.ScanAsync(_profile.SlideshowFolderPath ?? "", token)).Files;
        if (files.Count == 0) return;
        var order = _profile.ShuffleEnabled ? files.OrderBy(_ => Random.Shared.Next()).ToArray() : files;
        var attempts = 0;
        while (!token.IsCancellationRequested)
        {
            var index = _profile.CurrentSlideshowIndex % order.Count;
            _profile.CurrentSlideshowIndex = _profile.CurrentSlideshowIndex == int.MaxValue ? 0 : _profile.CurrentSlideshowIndex + 1;
            var imagePath = order[index]; attempts++;
            try
            {
                if (!File.Exists(imagePath)) throw new FileNotFoundException("Slideshow image was not found.", imagePath);
                await _transition.ApplyAsync(_monitor, imagePath, _profile, token);
                _profile.LastWallpaperPath = imagePath; WallpaperChanged?.Invoke(this, imagePath);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsRecoverableItemFailure(ex)) { }

            if (!_profile.LoopEnabled && attempts >= order.Count) return;
            await _delay(_profile.SlideshowInterval, token);
        }
    }

    private static bool IsRecoverableItemFailure(Exception exception)
        => exception is WallpaperItemException or IOException or UnauthorizedAccessException or ExternalException or ArgumentException;

    public async ValueTask DisposeAsync()
    {
        var cts = _cts; var worker = _worker;
        _cts = null; _worker = null;
        cts?.Cancel();
        try { if (worker is not null) await worker; }
        finally { cts?.Dispose(); }
    }
}
