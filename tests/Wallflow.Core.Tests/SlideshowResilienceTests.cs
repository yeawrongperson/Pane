using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wallflow.Core;

namespace Wallflow.Core.Tests;

[TestClass]
public sealed class SlideshowResilienceTests
{
    private static readonly MonitorInfo Monitor = new("monitor", "DISPLAY1", "Display 1", 0, 0, 1920, 1080, true);

    [TestMethod]
    public void Zero_interval_uses_safe_default()
        => Assert.AreEqual(SlideshowPolicy.DefaultInterval, SlideshowPolicy.NormalizeInterval(TimeSpan.Zero));

    [TestMethod]
    public void Negative_interval_uses_safe_default()
        => Assert.AreEqual(SlideshowPolicy.DefaultInterval, SlideshowPolicy.NormalizeInterval(TimeSpan.FromSeconds(-1)));

    [TestMethod]
    public void Excessive_interval_is_clamped_to_maximum()
        => Assert.AreEqual(SlideshowPolicy.MaximumInterval, SlideshowPolicy.NormalizeInterval(TimeSpan.FromDays(30)));

    [TestMethod]
    public void Valid_interval_is_unchanged()
    {
        var interval = TimeSpan.FromMinutes(30);
        Assert.AreEqual(interval, SlideshowPolicy.NormalizeInterval(interval));
    }

    [TestMethod]
    public void Undefined_persisted_values_use_safe_fallbacks()
    {
        var profile = new MonitorWallpaperProfile
        {
            MonitorId = "monitor",
            Mode = (WallpaperMode)999,
            FitMode = (WallpaperFit)999,
            Transition = (TransitionKind)999,
            CurrentSlideshowIndex = -12
        };

        SlideshowPolicy.NormalizeProfile(profile);

        Assert.AreEqual(WallpaperMode.Static, profile.Mode);
        Assert.AreEqual(WallpaperFit.Fill, profile.FitMode);
        Assert.AreEqual(TransitionKind.SoftFade, profile.Transition);
        Assert.AreEqual(0, profile.CurrentSlideshowIndex);
    }

    [TestMethod]
    public async Task Recoverable_item_failure_does_not_stop_worker()
    {
        using var area = new SlideshowArea("one.jpg", "two.jpg");
        var attempts = new List<string>();
        var transition = new DelegateTransition((_, path, _, _) =>
        {
            attempts.Add(path);
            return path.EndsWith("one.jpg", StringComparison.OrdinalIgnoreCase)
                ? Task.FromException(new IOException("Unreadable test image."))
                : Task.CompletedTask;
        });
        await using var session = area.Session(transition, loop: false, (_, _) => Task.CompletedTask);

        session.Start(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(2, attempts.Count);
        Assert.IsTrue(session.Completion.IsCompletedSuccessfully);
        Assert.IsNull(session.Failure);
        Assert.IsTrue(session.Completion.IsCompleted);
    }

    [TestMethod]
    public async Task File_removed_after_enumeration_is_skipped()
    {
        using var area = new SlideshowArea("one.jpg", "two.jpg");
        var calls = 0;
        var transition = new DelegateTransition((_, _, _, _) =>
        {
            calls++;
            File.Delete(area.PathOf("two.jpg"));
            return Task.CompletedTask;
        });
        await using var session = area.Session(transition, loop: false, (_, _) => Task.CompletedTask);

        session.Start(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, calls);
        Assert.IsNull(session.Failure);
    }

    [TestMethod]
    public async Task Corrupt_item_failure_is_skipped_and_next_item_runs()
    {
        using var area = new SlideshowArea("one.jpg", "two.jpg");
        var successes = 0;
        var transition = new DelegateTransition((_, path, _, _) =>
        {
            if (path.EndsWith("one.jpg", StringComparison.OrdinalIgnoreCase))
                throw new WallpaperItemException("Corrupt test image.", new ArgumentException());
            successes++;
            return Task.CompletedTask;
        });
        await using var session = area.Session(transition, loop: false, (_, _) => Task.CompletedTask);

        session.Start(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, successes);
        Assert.IsNull(session.Failure);
    }

    [TestMethod]
    public async Task Cancellation_during_delay_completes_cleanly()
    {
        using var area = new SlideshowArea("one.jpg");
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transition = new DelegateTransition((_, _, _, _) => Task.CompletedTask);
        var session = area.Session(transition, loop: true, async (_, token) =>
        {
            delayEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        session.Start(); await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session.DisposeAsync();

        Assert.IsNull(session.Failure);
    }

    [TestMethod]
    public async Task Disposal_after_recoverable_failure_does_not_throw()
    {
        using var area = new SlideshowArea("one.jpg");
        var transition = new DelegateTransition((_, _, _, _) => Task.FromException(new UnauthorizedAccessException()));
        var session = area.Session(transition, loop: false, (_, _) => Task.CompletedTask);

        session.Start(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await session.DisposeAsync();

        Assert.IsNull(session.Failure);
    }

    [TestMethod]
    public async Task Repeated_failures_are_interval_gated()
    {
        using var area = new SlideshowArea("one.jpg");
        var attempts = 0;
        var delayEntered = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transition = new DelegateTransition((_, _, _, _) =>
        {
            attempts++;
            return Task.FromException(new IOException("Expected test failure."));
        });
        var session = area.Session(transition, loop: true, async (interval, token) =>
        {
            delayEntered.TrySetResult(interval);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        session.Start(); var interval = await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, attempts);
        Assert.AreEqual(SlideshowPolicy.MinimumInterval, interval);
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Unexpected_worker_failure_is_observed_and_recorded()
    {
        using var area = new SlideshowArea("one.jpg");
        var transition = new DelegateTransition((_, _, _, _) => Task.FromException(new NotSupportedException("Unexpected test failure.")));
        await using var session = area.Session(transition, loop: false, (_, _) => Task.CompletedTask);

        session.Start(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsInstanceOfType<NotSupportedException>(session.Failure);
        Assert.IsTrue(session.Completion.IsCompletedSuccessfully);
    }

    private sealed class SlideshowArea : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "WallflowTests", Guid.NewGuid().ToString("N"));

        public SlideshowArea(params string[] names)
        {
            Directory.CreateDirectory(_root);
            foreach (var name in names) File.WriteAllBytes(PathOf(name), [1]);
        }

        public string PathOf(string name) => Path.Combine(_root, name);

        public SlideshowSession Session(
            IWallpaperTransitionService transition,
            bool loop,
            Func<TimeSpan, CancellationToken, Task> delay)
        {
            var profile = new MonitorWallpaperProfile
            {
                MonitorId = Monitor.Id,
                Mode = WallpaperMode.Slideshow,
                SlideshowFolderPath = _root,
                SlideshowInterval = TimeSpan.FromMilliseconds(1),
                ShuffleEnabled = false,
                LoopEnabled = loop
            };
            return new SlideshowSession(Monitor, profile, transition, delay);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class DelegateTransition(
        Func<MonitorInfo, string, MonitorWallpaperProfile, CancellationToken, Task> apply) : IWallpaperTransitionService
    {
        public Task ApplyAsync(MonitorInfo monitor, string imagePath, MonitorWallpaperProfile profile, CancellationToken token = default)
            => apply(monitor, imagePath, profile, token);
    }
}
