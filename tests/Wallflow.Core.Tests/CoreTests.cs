using Wallflow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Wallflow.Core.Tests;
[TestClass] public class CoreTests
{
    [TestMethod] public void Supported_images_are_filtered_case_insensitively() { Assert.IsTrue(ImageCatalog.IsSupported("photo.JPEG")); Assert.IsFalse(ImageCatalog.IsSupported("notes.txt")); }
    [TestMethod] public async Task Profiles_round_trip()
    {
        var root = Path.Combine(Path.GetTempPath(), "WallflowTests", Guid.NewGuid().ToString("N")); var path = Path.Combine(root, "profiles.json");
        try { var store = new ProfileStore(path); await store.SaveAsync([new() { MonitorId = "display-1", Mode = WallpaperMode.Slideshow }]); var result = await store.LoadAsync(); Assert.AreEqual(WallpaperMode.Slideshow, result.Single().Mode); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod] public async Task Slideshow_targets_only_its_monitor_and_advances()
    {
        var root = Path.Combine(Path.GetTempPath(), "WallflowTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "one.jpg"), [1]); await File.WriteAllBytesAsync(Path.Combine(root, "two.png"), [1]);
            var transition = new RecordingTransition(); var monitor = new MonitorInfo("target-monitor", "DISPLAY1", "Display 1", 0, 0, 1920, 1080, true);
            var profile = new MonitorWallpaperProfile { MonitorId = monitor.Id, Mode = WallpaperMode.Slideshow, SlideshowFolderPath = root, SlideshowInterval = TimeSpan.FromMinutes(1), ShuffleEnabled = false, LoopEnabled = false };
            await using var session = new SlideshowSession(monitor, profile, transition, (_, _) => Task.CompletedTask); session.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await transition.TwoChanges.Task.WaitAsync(timeout.Token);
            Assert.IsTrue(transition.MonitorIds.All(id => id == "target-monitor")); Assert.AreEqual(2, transition.Paths.Distinct().Count());
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class RecordingTransition : IWallpaperTransitionService
    {
        public List<string> MonitorIds { get; } = []; public List<string> Paths { get; } = []; public TaskCompletionSource TwoChanges { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ApplyAsync(MonitorInfo monitor, string imagePath, MonitorWallpaperProfile profile, CancellationToken token = default)
        { lock (Paths) { MonitorIds.Add(monitor.Id); Paths.Add(imagePath); if (Paths.Count >= 2) TwoChanges.TrySetResult(); } return Task.CompletedTask; }
    }
}
