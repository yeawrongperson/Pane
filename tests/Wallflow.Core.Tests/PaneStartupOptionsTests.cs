using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wallflow.Core;

namespace Wallflow.Core.Tests;

[TestClass]
public sealed class PaneStartupOptionsTests
{
    [TestMethod]
    public void No_arguments_select_normal_mode()
    {
        var options = PaneStartupOptions.Parse([]);

        Assert.AreEqual(PaneStartupMode.Normal, options.Mode);
        Assert.AreEqual(SingleInstanceActivationCoordinator.InstanceKey, options.InstanceKey);
        Assert.IsTrue(options.UsesPersistentProfileState);
        Assert.IsTrue(options.RunsLegacyProfileMigration);
        Assert.IsTrue(options.StartsPersistedSlideshows);
        Assert.IsTrue(options.AllowsWallpaperChanges);
        Assert.IsTrue(options.CreatesTrayIcon);
    }

    [TestMethod]
    public void Unknown_arguments_preserve_normal_mode()
        => Assert.AreEqual(PaneStartupMode.Normal, PaneStartupOptions.Parse(["--unknown"]).Mode);

    [TestMethod]
    public void Explicit_smoke_argument_selects_smoke_mode()
        => Assert.AreEqual(PaneStartupMode.SmokeTest, PaneStartupOptions.Parse([PaneStartupOptions.SmokeTestArgument]).Mode);

    [TestMethod]
    public void Smoke_mode_suppresses_all_persistent_and_wallpaper_behavior()
    {
        var options = PaneStartupOptions.Parse([PaneStartupOptions.SmokeTestArgument]);

        Assert.IsFalse(options.UsesPersistentProfileState);
        Assert.IsFalse(options.RunsLegacyProfileMigration);
        Assert.IsFalse(options.StartsPersistedSlideshows);
        Assert.IsFalse(options.AllowsWallpaperChanges);
        Assert.IsFalse(options.CreatesTrayIcon);
    }

    [TestMethod]
    public void Smoke_mode_uses_process_unique_key_distinct_from_normal_instance()
    {
        var options = PaneStartupOptions.Parse([PaneStartupOptions.SmokeTestArgument]);

        Assert.AreNotEqual(SingleInstanceActivationCoordinator.InstanceKey, options.InstanceKey);
        Assert.AreEqual(PaneStartupOptions.SmokeTestInstanceKeyPrefix + Environment.ProcessId, options.InstanceKey);
    }
}
