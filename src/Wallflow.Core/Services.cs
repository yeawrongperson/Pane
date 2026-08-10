using System.Text.Json;

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

public static class ImageCatalog
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));
    public static IReadOnlyList<string> Scan(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder).Where(IsSupported).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray()
        : [];
}

public sealed class ProfileStore(string settingsPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public async Task<List<MonitorWallpaperProfile>> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(settingsPath)) return [];
        await using var stream = File.OpenRead(settingsPath);
        return await JsonSerializer.DeserializeAsync<List<MonitorWallpaperProfile>>(stream, JsonOptions, token) ?? [];
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
    private readonly IWallpaperTransitionService _transition; private CancellationTokenSource? _cts; private Task? _worker;
    public SlideshowSession(MonitorInfo monitor, MonitorWallpaperProfile profile, IWallpaperTransitionService transition)
        => (_monitor, _profile, _transition) = (monitor, profile, transition);
    public event EventHandler<string>? WallpaperChanged;
    public void Start() { Stop(); _cts = new(); _worker = RunAsync(_cts.Token); }
    public void Stop() { _cts?.Cancel(); _cts?.Dispose(); _cts = null; }
    private async Task RunAsync(CancellationToken token)
    {
        var files = ImageCatalog.Scan(_profile.SlideshowFolderPath ?? "");
        if (files.Count == 0) return;
        var order = _profile.ShuffleEnabled ? files.OrderBy(_ => Random.Shared.Next()).ToArray() : files;
        while (!token.IsCancellationRequested)
        {
            var index = Math.Abs(_profile.CurrentSlideshowIndex++ % order.Count);
            await _transition.ApplyAsync(_monitor, order[index], _profile, token);
            _profile.LastWallpaperPath = order[index]; WallpaperChanged?.Invoke(this, order[index]);
            if (!_profile.LoopEnabled && _profile.CurrentSlideshowIndex >= order.Count) return;
            await Task.Delay(_profile.SlideshowInterval, token);
        }
    }
    public async ValueTask DisposeAsync() { Stop(); if (_worker is not null) try { await _worker; } catch (OperationCanceledException) { } }
}
