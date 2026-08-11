namespace Wallflow.Core;

public sealed class WallpaperSetup
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public List<MonitorWallpaperProfile> MonitorProfiles { get; set; } = [];
}

public sealed class PaneSetupState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public required string ActiveSetupId { get; set; }
    public List<WallpaperSetup> Setups { get; set; } = [];
    public List<MonitorAlias> MonitorAliases { get; set; } = [];
}

public sealed class MonitorAlias
{
    public required string MonitorId { get; set; }
    public string? MonitorDevicePath { get; set; }
    public required string Name { get; set; }
}

public enum MonitorMatchKind
{
    ExactId,
    DevicePath,
    Created
}

public sealed record SetupMonitorMatch(
    MonitorInfo Monitor,
    MonitorWallpaperProfile Profile,
    MonitorMatchKind Kind);

public sealed record SetupMonitorResolution(
    IReadOnlyList<SetupMonitorMatch> Matches,
    int SavedDisplayCount)
{
    public int ConnectedDisplayCount => Matches.Count;
}

public sealed record SetupDeleteResult(string DeletedSetupId, string ActiveSetupId, bool ActiveSetupChanged);

public sealed class SetupManager
{
    public const int MaximumSetupNameLength = 64;
    public const int MaximumMonitorAliasLength = 48;
    public const string DefaultSetupName = "My Setup";

    public SetupManager(PaneSetupState state)
    {
        SetupStateValidator.Validate(state);
        State = state;
    }

    public PaneSetupState State { get; }
    public WallpaperSetup ActiveSetup => Find(State.ActiveSetupId);

    public static PaneSetupState CreateInitialState(
        string? name = null,
        IEnumerable<MonitorWallpaperProfile>? profiles = null)
    {
        var setup = new WallpaperSetup
        {
            Id = CreateId(),
            Name = string.IsNullOrWhiteSpace(name) ? DefaultSetupName : name.Trim(),
            MonitorProfiles = profiles?.Select(CloneProfile).ToList() ?? []
        };
        return new PaneSetupState { ActiveSetupId = setup.Id, Setups = [setup] };
    }

    public WallpaperSetup Activate(string setupId)
    {
        var setup = Find(setupId);
        State.ActiveSetupId = setup.Id;
        return setup;
    }

    public WallpaperSetup CreateFromCurrent(string name)
    {
        var setup = new WallpaperSetup
        {
            Id = CreateId(),
            Name = NormalizeNewName(name),
            MonitorProfiles = ActiveSetup.MonitorProfiles.Select(CloneProfile).ToList()
        };
        State.Setups.Add(setup);
        State.ActiveSetupId = setup.Id;
        return setup;
    }

    public WallpaperSetup CreateFresh(string name, IEnumerable<MonitorInfo> connectedMonitors)
    {
        ArgumentNullException.ThrowIfNull(connectedMonitors);
        var setup = new WallpaperSetup
        {
            Id = CreateId(),
            Name = NormalizeNewName(name),
            MonitorProfiles = connectedMonitors.Select(CreateProfile).ToList()
        };
        State.Setups.Add(setup);
        State.ActiveSetupId = setup.Id;
        return setup;
    }

    public WallpaperSetup Duplicate(string setupId)
    {
        var source = Find(setupId);
        var setup = new WallpaperSetup
        {
            Id = CreateId(),
            Name = NextCopyName(source.Name),
            MonitorProfiles = source.MonitorProfiles.Select(CloneProfile).ToList()
        };
        var sourceIndex = State.Setups.IndexOf(source);
        State.Setups.Insert(sourceIndex + 1, setup);
        return setup;
    }

    public bool Rename(string setupId, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = name.Trim();
        if (normalized.Length > MaximumSetupNameLength) return false;
        Find(setupId).Name = normalized;
        return true;
    }

    public SetupDeleteResult Delete(string setupId)
    {
        if (State.Setups.Count == 1)
            throw new InvalidOperationException("Pane must always have at least one setup.");

        var setup = Find(setupId);
        var wasActive = string.Equals(State.ActiveSetupId, setup.Id, StringComparison.Ordinal);
        State.Setups.Remove(setup);
        if (wasActive) State.ActiveSetupId = State.Setups[0].Id;
        return new(setup.Id, State.ActiveSetupId, wasActive);
    }

    public SetupMonitorResolution ReconcileActiveMonitors(IEnumerable<MonitorInfo> connectedMonitors)
        => SetupMonitorMatcher.Reconcile(ActiveSetup, connectedMonitors);

    public void ReconcileMonitorAliases(IEnumerable<MonitorInfo> connectedMonitors)
    {
        ArgumentNullException.ThrowIfNull(connectedMonitors);
        var connected = connectedMonitors.ToArray();
        var claimed = new HashSet<MonitorAlias>();
        foreach (var monitor in connected)
        {
            var alias = State.MonitorAliases.FirstOrDefault(candidate =>
                !claimed.Contains(candidate) &&
                string.Equals(candidate.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase));
            if (alias is null && IsUniqueConnectedDevicePath(monitor, connected))
                alias = UniqueDevicePathAlias(monitor.DeviceName, claimed);
            if (alias is null) continue;

            alias.MonitorId = monitor.Id;
            alias.MonitorDevicePath = monitor.DeviceName;
            claimed.Add(alias);
        }
    }

    public string GetMonitorDisplayName(MonitorInfo monitor, IEnumerable<MonitorInfo>? connectedMonitors = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var alias = State.MonitorAliases.FirstOrDefault(candidate =>
            string.Equals(candidate.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase));
        if (alias is null &&
            (connectedMonitors is null || IsUniqueConnectedDevicePath(monitor, connectedMonitors)) &&
            !string.IsNullOrWhiteSpace(monitor.DeviceName))
            alias = UniqueDevicePathAlias(monitor.DeviceName);
        return alias?.Name ?? monitor.FriendlyName;
    }

    public bool SetMonitorAlias(MonitorInfo monitor, string? name)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length > MaximumMonitorAliasLength) return false;

        var alias = State.MonitorAliases.FirstOrDefault(candidate =>
            string.Equals(candidate.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase));

        if (normalized.Length == 0)
        {
            if (alias is not null) State.MonitorAliases.Remove(alias);
            return true;
        }

        if (alias is null)
        {
            State.MonitorAliases.Add(new MonitorAlias
            {
                MonitorId = monitor.Id,
                MonitorDevicePath = monitor.DeviceName,
                Name = normalized
            });
            return true;
        }

        alias.MonitorId = monitor.Id;
        alias.MonitorDevicePath = monitor.DeviceName;
        alias.Name = normalized;
        return true;
    }

    public WallpaperSetup Find(string setupId)
        => State.Setups.FirstOrDefault(setup => string.Equals(setup.Id, setupId, StringComparison.Ordinal))
           ?? throw new KeyNotFoundException($"Setup '{setupId}' was not found.");

    public static MonitorWallpaperProfile CloneProfile(MonitorWallpaperProfile source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new MonitorWallpaperProfile
        {
            MonitorId = source.MonitorId,
            MonitorDevicePath = source.MonitorDevicePath,
            DisplayX = source.DisplayX,
            DisplayY = source.DisplayY,
            DisplayWidth = source.DisplayWidth,
            DisplayHeight = source.DisplayHeight,
            Mode = source.Mode,
            StaticImagePath = source.StaticImagePath,
            SlideshowFolderPath = source.SlideshowFolderPath,
            SlideshowInterval = source.SlideshowInterval,
            ShuffleEnabled = source.ShuffleEnabled,
            LoopEnabled = source.LoopEnabled,
            FitMode = source.FitMode,
            Transition = source.Transition,
            TransitionDurationMs = source.TransitionDurationMs,
            CurrentSlideshowIndex = source.CurrentSlideshowIndex,
            LastWallpaperPath = source.LastWallpaperPath,
            Enabled = source.Enabled
        };
    }

    public static MonitorWallpaperProfile CreateProfile(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return new MonitorWallpaperProfile
        {
            MonitorId = monitor.Id,
            MonitorDevicePath = monitor.DeviceName,
            DisplayX = monitor.X,
            DisplayY = monitor.Y,
            DisplayWidth = monitor.Width,
            DisplayHeight = monitor.Height
        };
    }

    private static string CreateId() => Guid.NewGuid().ToString("N");

    private MonitorAlias? UniqueDevicePathAlias(string? devicePath, HashSet<MonitorAlias>? claimed = null)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return null;
        var matches = State.MonitorAliases.Where(candidate =>
            (claimed is null || !claimed.Contains(candidate)) &&
            !string.IsNullOrWhiteSpace(candidate.MonitorDevicePath) &&
            string.Equals(candidate.MonitorDevicePath, devicePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool IsUniqueConnectedDevicePath(MonitorInfo monitor, IEnumerable<MonitorInfo> connectedMonitors)
        => !string.IsNullOrWhiteSpace(monitor.DeviceName) && connectedMonitors.Count(candidate =>
            string.Equals(candidate.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase)) == 1;

    private string NextCopyName(string sourceName)
    {
        var baseName = sourceName.Length + 5 <= MaximumSetupNameLength
            ? sourceName + " Copy"
            : sourceName[..(MaximumSetupNameLength - 5)].TrimEnd() + " Copy";
        if (State.Setups.All(setup => !string.Equals(setup.Name, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;

        for (var suffix = 2; ; suffix++)
        {
            var suffixText = $" {suffix}";
            var candidate = baseName.Length + suffixText.Length <= MaximumSetupNameLength
                ? baseName + suffixText
                : baseName[..(MaximumSetupNameLength - suffixText.Length)].TrimEnd() + suffixText;
            if (State.Setups.All(setup => !string.Equals(setup.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    private static string NormalizeNewName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Setup name cannot be empty.", nameof(name));
        var normalized = name.Trim();
        if (normalized.Length > MaximumSetupNameLength)
            throw new ArgumentException($"Setup names can contain at most {MaximumSetupNameLength} characters.", nameof(name));
        return normalized;
    }
}

public static class SetupMonitorMatcher
{
    public static SetupMonitorResolution Reconcile(WallpaperSetup setup, IEnumerable<MonitorInfo> connectedMonitors)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(connectedMonitors);
        var claimed = new HashSet<MonitorWallpaperProfile>();
        var matches = new List<SetupMonitorMatch>();

        foreach (var monitor in connectedMonitors)
        {
            var profile = setup.MonitorProfiles.FirstOrDefault(candidate =>
                !claimed.Contains(candidate) &&
                string.Equals(candidate.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase));
            var kind = MonitorMatchKind.ExactId;

            if (profile is null && !string.IsNullOrWhiteSpace(monitor.DeviceName))
            {
                var fallback = setup.MonitorProfiles.Where(candidate =>
                    !claimed.Contains(candidate) &&
                    !string.IsNullOrWhiteSpace(candidate.MonitorDevicePath) &&
                    string.Equals(candidate.MonitorDevicePath, monitor.DeviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (fallback.Length == 1)
                {
                    profile = fallback[0];
                    profile.MonitorId = monitor.Id;
                    kind = MonitorMatchKind.DevicePath;
                }
            }

            if (profile is null)
            {
                profile = SetupManager.CreateProfile(monitor);
                setup.MonitorProfiles.Add(profile);
                kind = MonitorMatchKind.Created;
            }

            profile.MonitorDevicePath = monitor.DeviceName;
            profile.DisplayX = monitor.X;
            profile.DisplayY = monitor.Y;
            profile.DisplayWidth = monitor.Width;
            profile.DisplayHeight = monitor.Height;
            claimed.Add(profile);
            matches.Add(new(monitor, profile, kind));
        }

        return new(matches, setup.MonitorProfiles.Count);
    }
}

public static class SetupStateValidator
{
    public static void Validate(PaneSetupState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != PaneSetupState.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported Pane setup schema version {state.SchemaVersion}.");
        if (state.Setups is null || state.Setups.Count == 0)
            throw new InvalidDataException("Pane setup state must contain at least one setup.");

        state.MonitorAliases ??= [];
        var aliasMonitorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in state.MonitorAliases)
        {
            if (alias is null || string.IsNullOrWhiteSpace(alias.MonitorId) || !aliasMonitorIds.Add(alias.MonitorId))
                throw new InvalidDataException("Monitor alias IDs must be non-empty and unique.");
            alias.Name = alias.Name?.Trim() ?? string.Empty;
            if (alias.Name.Length == 0 || alias.Name.Length > SetupManager.MaximumMonitorAliasLength)
                throw new InvalidDataException($"Monitor aliases must contain between 1 and {SetupManager.MaximumMonitorAliasLength} characters.");
        }

        var setupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setup in state.Setups)
        {
            if (string.IsNullOrWhiteSpace(setup.Id) || !setupIds.Add(setup.Id))
                throw new InvalidDataException("Pane setup IDs must be non-empty and unique.");
            if (string.IsNullOrWhiteSpace(setup.Name))
                throw new InvalidDataException("Pane setup names cannot be empty.");
            if (setup.MonitorProfiles is null)
                throw new InvalidDataException("Pane setup monitor profiles cannot be null.");

            var monitorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in setup.MonitorProfiles)
            {
                if (profile is null || string.IsNullOrWhiteSpace(profile.MonitorId) || !monitorIds.Add(profile.MonitorId))
                    throw new InvalidDataException("Monitor profile IDs must be non-empty and unique within a setup.");
                SlideshowPolicy.NormalizeProfile(profile);
            }
        }

        if (!setupIds.Contains(state.ActiveSetupId))
            throw new InvalidDataException("The active setup ID does not identify a saved setup.");
    }
}

public readonly record struct FloatingPanelPosition(double X, double Y);

public static class FloatingPanelPlacement
{
    public static FloatingPanelPosition Clamp(
        double x,
        double y,
        double panelWidth,
        double panelHeight,
        double availableWidth,
        double availableHeight,
        double margin = 8)
    {
        var maxX = Math.Max(margin, availableWidth - panelWidth - margin);
        var maxY = Math.Max(margin, availableHeight - panelHeight - margin);
        return new(
            Math.Clamp(x, margin, maxX),
            Math.Clamp(y, margin, maxY));
    }
}

public sealed class SetupUndoTracker
{
    private string? _targetSetupId;

    public void Offer(string previousSetupId, string activeSetupId)
        => _targetSetupId = string.Equals(previousSetupId, activeSetupId, StringComparison.Ordinal) ? null : previousSetupId;

    public bool TryTake(out string setupId)
    {
        setupId = Interlocked.Exchange(ref _targetSetupId, null) ?? string.Empty;
        return setupId.Length > 0;
    }

    public void Clear() => Interlocked.Exchange(ref _targetSetupId, null);
}

public sealed class LatestSetupSwitchCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private CancellationTokenSource? _current;
    private bool _disposed;

    public async Task<bool> RunLatestAsync(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CancellationTokenSource source;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            source = new CancellationTokenSource();
            try { _current?.Cancel(); }
            catch (ObjectDisposedException) { }
            _current = source;
        }

        await _serial.WaitAsync();
        try
        {
            if (source.IsCancellationRequested) return false;
            await operation(source.Token);
            return !source.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            _serial.Release();
            lock (_gate)
            {
                if (ReferenceEquals(_current, source)) _current = null;
                source.Dispose();
            }
        }
    }

    public void CancelCurrent()
    {
        lock (_gate)
        {
            try { _current?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _current?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        await _serial.WaitAsync().ConfigureAwait(false);
        _serial.Release();
        _serial.Dispose();
    }
}
