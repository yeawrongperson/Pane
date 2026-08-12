using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wallflow.Core;

namespace Wallflow.Core.Tests;

[TestClass]
public sealed class SetupTests
{
    private static readonly MonitorInfo MonitorA = new("monitor-a", "DISPLAY1", "Display 1", 0, 0, 1920, 1080, true);
    private static readonly MonitorInfo MonitorB = new("monitor-b", "DISPLAY2", "Display 2", 1920, 0, 2560, 1440, false);

    [TestMethod]
    public void Fresh_state_contains_exactly_one_named_setup()
    {
        var state = SetupManager.CreateInitialState();

        Assert.AreEqual(1, state.Setups.Count);
        Assert.AreEqual(SetupManager.DefaultSetupName, state.Setups[0].Name);
        Assert.AreEqual(state.Setups[0].Id, state.ActiveSetupId);
    }

    [TestMethod]
    public void Setup_id_is_unique_and_stable_across_rename()
    {
        var first = SetupManager.CreateInitialState();
        var second = SetupManager.CreateInitialState();
        var manager = new SetupManager(first);
        var id = manager.ActiveSetup.Id;

        Assert.AreNotEqual(first.ActiveSetupId, second.ActiveSetupId);
        Assert.IsTrue(manager.Rename(id, "Renamed"));
        Assert.AreEqual(id, manager.ActiveSetup.Id);
    }

    [TestMethod]
    public void Create_from_current_deep_clones_monitor_profiles()
    {
        var state = SetupManager.CreateInitialState("Default", [Profile(MonitorA, "one.jpg")]);
        var manager = new SetupManager(state);
        var source = manager.ActiveSetup;

        var created = manager.CreateFromCurrent("Photography");

        Assert.AreNotSame(source.MonitorProfiles[0], created.MonitorProfiles[0]);
        Assert.AreEqual("one.jpg", created.MonitorProfiles[0].StaticImagePath);
        Assert.AreEqual(created.Id, state.ActiveSetupId);
    }

    [TestMethod]
    public void Duplicate_deep_clones_without_activating_copy()
    {
        var state = SetupManager.CreateInitialState("Photography", [Profile(MonitorA, "one.jpg")]);
        var manager = new SetupManager(state);
        var activeId = state.ActiveSetupId;

        var duplicate = manager.Duplicate(activeId);

        Assert.AreEqual(activeId, state.ActiveSetupId);
        Assert.AreEqual("Photography Copy", duplicate.Name);
        Assert.AreNotSame(manager.ActiveSetup.MonitorProfiles[0], duplicate.MonitorProfiles[0]);
    }

    [TestMethod]
    public void Editing_duplicate_cannot_mutate_source_setup()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("Source", [Profile(MonitorA, "source.jpg")]));
        var duplicate = manager.Duplicate(manager.ActiveSetup.Id);

        duplicate.MonitorProfiles[0].StaticImagePath = "copy.jpg";

        Assert.AreEqual("source.jpg", manager.ActiveSetup.MonitorProfiles[0].StaticImagePath);
    }

    [TestMethod]
    public void Empty_or_overlong_rename_is_rejected()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState());

        Assert.IsFalse(manager.Rename(manager.ActiveSetup.Id, "   "));
        Assert.IsFalse(manager.Rename(manager.ActiveSetup.Id, new string('x', SetupManager.MaximumSetupNameLength + 1)));
        Assert.AreEqual(SetupManager.DefaultSetupName, manager.ActiveSetup.Name);
    }

    [TestMethod]
    public void Rename_trims_whitespace()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState());

        Assert.IsTrue(manager.Rename(manager.ActiveSetup.Id, "  Photography  "));

        Assert.AreEqual("Photography", manager.ActiveSetup.Name);
    }

    [TestMethod]
    public void Inactive_setup_can_be_deleted_without_changing_active_setup()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("Default"));
        var activeId = manager.ActiveSetup.Id;
        var copy = manager.Duplicate(activeId);

        var result = manager.Delete(copy.Id);

        Assert.IsFalse(result.ActiveSetupChanged);
        Assert.AreEqual(activeId, manager.State.ActiveSetupId);
        Assert.AreEqual(1, manager.State.Setups.Count);
    }

    [TestMethod]
    public void Final_setup_cannot_be_deleted()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState());

        Assert.ThrowsException<InvalidOperationException>(() => manager.Delete(manager.ActiveSetup.Id));
    }

    [TestMethod]
    public void Deleting_active_setup_selects_first_remaining_setup_deterministically()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("First"));
        var firstId = manager.ActiveSetup.Id;
        var second = manager.CreateFromCurrent("Second");
        manager.CreateFromCurrent("Third");
        manager.Activate(second.Id);

        var result = manager.Delete(second.Id);

        Assert.IsTrue(result.ActiveSetupChanged);
        Assert.AreEqual(firstId, manager.State.ActiveSetupId);
    }

    [TestMethod]
    public void Disconnected_monitor_profile_is_retained()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [Profile(MonitorA), Profile(MonitorB)]));

        var resolution = manager.ReconcileActiveMonitors([MonitorA]);

        Assert.AreEqual(2, manager.ActiveSetup.MonitorProfiles.Count);
        Assert.AreEqual(2, resolution.SavedDisplayCount);
        Assert.AreEqual(1, resolution.ConnectedDisplayCount);
        Assert.IsTrue(manager.ActiveSetup.MonitorProfiles.Any(profile => profile.MonitorId == MonitorB.Id));
    }

    [TestMethod]
    public void Unknown_connected_monitor_gets_profile_without_deleting_dormant_monitor()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [Profile(MonitorA)]));

        var resolution = manager.ReconcileActiveMonitors([MonitorB]);

        Assert.AreEqual(2, manager.ActiveSetup.MonitorProfiles.Count);
        Assert.AreEqual(MonitorMatchKind.Created, resolution.Matches.Single().Kind);
        Assert.IsTrue(manager.ActiveSetup.MonitorProfiles.Any(profile => profile.MonitorId == MonitorA.Id));
    }

    [TestMethod]
    public void Exact_monitor_id_match_is_preferred()
    {
        var exact = Profile(MonitorA);
        var fallback = Profile(new MonitorInfo("other", MonitorA.DeviceName, "Other", 0, 0, 800, 600, false));
        var setup = new WallpaperSetup { Id = "setup", Name = "Setup", MonitorProfiles = [fallback, exact] };

        var result = SetupMonitorMatcher.Reconcile(setup, [MonitorA]);

        Assert.AreSame(exact, result.Matches.Single().Profile);
        Assert.AreEqual(MonitorMatchKind.ExactId, result.Matches.Single().Kind);
    }

    [TestMethod]
    public void Unique_device_path_is_approved_fallback()
    {
        var saved = Profile(new MonitorInfo("old-id", MonitorA.DeviceName, "Old", 0, 0, 1920, 1080, false), "saved.jpg");
        var setup = new WallpaperSetup { Id = "setup", Name = "Setup", MonitorProfiles = [saved] };

        var result = SetupMonitorMatcher.Reconcile(setup, [MonitorA]);

        Assert.AreSame(saved, result.Matches.Single().Profile);
        Assert.AreEqual(MonitorMatchKind.DevicePath, result.Matches.Single().Kind);
        Assert.AreEqual(MonitorA.Id, saved.MonitorId);
        Assert.AreEqual("saved.jpg", saved.StaticImagePath);
    }

    [TestMethod]
    public void Ambiguous_device_path_does_not_reassign_saved_profiles()
    {
        var first = Profile(new MonitorInfo("old-1", MonitorA.DeviceName, "Old 1", 0, 0, 1920, 1080, false));
        var second = Profile(new MonitorInfo("old-2", MonitorA.DeviceName, "Old 2", 0, 0, 1920, 1080, false));
        var setup = new WallpaperSetup { Id = "setup", Name = "Setup", MonitorProfiles = [first, second] };

        var result = SetupMonitorMatcher.Reconcile(setup, [MonitorA]);

        Assert.AreEqual(MonitorMatchKind.Created, result.Matches.Single().Kind);
        Assert.AreEqual("old-1", first.MonitorId);
        Assert.AreEqual("old-2", second.MonitorId);
    }

    [TestMethod]
    public void Reconciliation_updates_saved_topology()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [Profile(MonitorA)]));
        var moved = MonitorA with { X = -1080, Y = 120, Width = 1080, Height = 1920 };

        manager.ReconcileActiveMonitors([moved]);
        var profile = manager.ActiveSetup.MonitorProfiles.Single();

        Assert.AreEqual(-1080, profile.DisplayX);
        Assert.AreEqual(120, profile.DisplayY);
        Assert.AreEqual(1080, profile.DisplayWidth);
        Assert.AreEqual(1920, profile.DisplayHeight);
    }

    [TestMethod]
    public void Twenty_setups_remain_supported_without_fixed_tiny_limit()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState());
        for (var index = 1; index < 20; index++) manager.CreateFromCurrent($"Setup {index + 1}");

        Assert.AreEqual(20, manager.State.Setups.Count);
    }

    [TestMethod]
    public void Undo_restores_previous_selection_once()
    {
        var undo = new SetupUndoTracker();
        undo.Offer("setup-a", "setup-b");

        Assert.IsTrue(undo.TryTake(out var target));
        Assert.AreEqual("setup-a", target);
        Assert.IsFalse(undo.TryTake(out _));
    }

    [TestMethod]
    public void Second_switch_replaces_previous_undo_target()
    {
        var undo = new SetupUndoTracker();
        undo.Offer("setup-a", "setup-b");
        undo.Offer("setup-b", "setup-c");

        Assert.IsTrue(undo.TryTake(out var target));
        Assert.AreEqual("setup-b", target);
    }

    [TestMethod]
    public async Task Rapid_switching_allows_only_latest_operation_to_complete()
    {
        await using var coordinator = new LatestSetupSwitchCoordinator();
        var completed = new List<string>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.RunLatestAsync(async token =>
        {
            firstStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            completed.Add("A");
        });
        await firstStarted.Task;
        var second = coordinator.RunLatestAsync(async token =>
        {
            await Task.Delay(50, token);
            completed.Add("B");
        });
        var third = coordinator.RunLatestAsync(_ =>
        {
            completed.Add("C");
            return Task.CompletedTask;
        });

        await Task.WhenAll(first, second, third);

        CollectionAssert.AreEqual(new[] { "C" }, completed);
        Assert.IsFalse(await first);
        Assert.IsFalse(await second);
        Assert.IsTrue(await third);
    }

    [TestMethod]
    public void Smoke_startup_does_not_enable_persistent_setup_state()
    {
        var options = PaneStartupOptions.Parse([PaneStartupOptions.SmokeTestArgument]);

        Assert.IsFalse(options.UsesPersistentProfileState);
        Assert.IsFalse(options.RunsLegacyProfileMigration);
        Assert.IsFalse(options.StartsPersistedSlideshows);
    }

    [TestMethod]
    public void Monitor_alias_can_be_assigned_and_is_trimmed()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState());

        Assert.IsTrue(manager.SetMonitorAlias(MonitorA, "  Main Monitor  "));

        Assert.AreEqual("Main Monitor", manager.GetMonitorDisplayName(MonitorA, [MonitorA, MonitorB]));
        Assert.AreEqual(1, manager.State.MonitorAliases.Count);
    }

    [TestMethod]
    public void Monitor_alias_is_global_across_multiple_setups()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("First", [Profile(MonitorA)]));
        manager.SetMonitorAlias(MonitorA, "Main Monitor");
        var second = manager.CreateFromCurrent("Second");

        manager.Activate(second.Id);

        Assert.AreEqual("Main Monitor", manager.GetMonitorDisplayName(MonitorA, [MonitorA]));
        Assert.AreEqual(1, manager.State.MonitorAliases.Count);
    }

    [TestMethod]
    public void Monitor_alias_survives_disconnected_monitor_retention()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [Profile(MonitorA), Profile(MonitorB)]));
        manager.SetMonitorAlias(MonitorB, "Vertical");

        manager.ReconcileActiveMonitors([MonitorA]);
        manager.ReconcileMonitorAliases([MonitorA]);

        Assert.AreEqual("Vertical", manager.GetMonitorDisplayName(MonitorB, [MonitorB]));
    }

    [TestMethod]
    public void Monitor_alias_resolves_on_exact_monitor_id()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState());
        manager.State.MonitorAliases.Add(new MonitorAlias { MonitorId = MonitorA.Id, MonitorDevicePath = "OLD", Name = "Exact" });

        Assert.AreEqual("Exact", manager.GetMonitorDisplayName(MonitorA, [MonitorA]));
    }

    [TestMethod]
    public void Unique_device_path_fallback_reassigns_monitor_alias()
    {
        var reconnected = MonitorA with { Id = "monitor-a-new" };
        var manager = new SetupManager(SetupManager.CreateInitialState());
        manager.State.MonitorAliases.Add(new MonitorAlias { MonitorId = "monitor-a-old", MonitorDevicePath = MonitorA.DeviceName, Name = "Main Monitor" });

        manager.ReconcileMonitorAliases([reconnected]);

        Assert.AreEqual("Main Monitor", manager.GetMonitorDisplayName(reconnected, [reconnected]));
        Assert.AreEqual(reconnected.Id, manager.State.MonitorAliases.Single().MonitorId);
    }

    [TestMethod]
    public void Ambiguous_device_path_fallback_does_not_assign_alias()
    {
        var reconnected = MonitorA with { Id = "monitor-a-new" };
        var manager = new SetupManager(SetupManager.CreateInitialState());
        manager.State.MonitorAliases.Add(new MonitorAlias { MonitorId = "old-1", MonitorDevicePath = MonitorA.DeviceName, Name = "First" });
        manager.State.MonitorAliases.Add(new MonitorAlias { MonitorId = "old-2", MonitorDevicePath = MonitorA.DeviceName, Name = "Second" });

        manager.ReconcileMonitorAliases([reconnected]);

        Assert.AreEqual(reconnected.FriendlyName, manager.GetMonitorDisplayName(reconnected, [reconnected]));
        Assert.IsFalse(manager.State.MonitorAliases.Any(alias => alias.MonitorId == reconnected.Id));
    }

    [TestMethod]
    public void Clearing_monitor_alias_returns_default_friendly_name()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState());
        manager.SetMonitorAlias(MonitorA, "Main Monitor");

        Assert.IsTrue(manager.SetMonitorAlias(MonitorA, "   "));

        Assert.AreEqual(MonitorA.FriendlyName, manager.GetMonitorDisplayName(MonitorA, [MonitorA]));
        Assert.AreEqual(0, manager.State.MonitorAliases.Count);
    }

    [TestMethod]
    public void Duplicating_setup_does_not_duplicate_monitor_alias_metadata()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [Profile(MonitorA)]));
        manager.SetMonitorAlias(MonitorA, "Main Monitor");

        manager.Duplicate(manager.ActiveSetup.Id);

        Assert.AreEqual(1, manager.State.MonitorAliases.Count);
        Assert.AreEqual("Main Monitor", manager.State.MonitorAliases.Single().Name);
    }

    [TestMethod]
    public void One_definitively_disconnected_profile_can_be_removed()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [Profile(MonitorA), Profile(MonitorB)]));

        var result = manager.RemoveDisconnectedMonitorProfile(manager.ActiveSetup.Id, MonitorB.Id, [MonitorA]);

        Assert.AreEqual(SetupMonitorProfileRemovalOutcome.Removed, result.Outcome);
        CollectionAssert.AreEqual(new[] { MonitorA.Id }, manager.ActiveSetup.MonitorProfiles.Select(profile => profile.MonitorId).ToArray());
    }

    [TestMethod]
    public void Connected_profile_removal_is_refused()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [Profile(MonitorA)]));

        var result = manager.RemoveDisconnectedMonitorProfile(manager.ActiveSetup.Id, MonitorA.Id, [MonitorA]);

        Assert.AreEqual(SetupMonitorProfileRemovalOutcome.RefusedConnected, result.Outcome);
        Assert.AreEqual(1, manager.ActiveSetup.MonitorProfiles.Count);
    }

    [TestMethod]
    public void Ambiguous_legacy_fallback_is_indeterminate_and_cannot_be_removed()
    {
        var first = Profile(new("old-1", "DISPLAY9", "Old 1", 0, 0, 1920, 1080, false));
        var second = Profile(new("old-2", "DISPLAY9", "Old 2", 0, 0, 1920, 1080, false));
        var connected = new MonitorInfo("current", "DISPLAY9", "Current", 0, 0, 1920, 1080, true);
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [first, second]));

        var inventory = manager.AnalyzeMonitorProfiles(manager.ActiveSetup.Id, [connected]);
        var result = manager.RemoveDisconnectedMonitorProfile(manager.ActiveSetup.Id, first.MonitorId, [connected]);

        Assert.IsTrue(inventory.Profiles.All(status => status.Connection == SetupMonitorProfileConnection.Indeterminate));
        Assert.AreEqual(SetupMonitorProfileRemovalOutcome.RefusedIndeterminate, result.Outcome);
        Assert.AreEqual(2, manager.ActiveSetup.MonitorProfiles.Count);
    }

    [TestMethod]
    public void Unique_legacy_gdi_fallback_protects_profile_as_connected()
    {
        var saved = Profile(new("old", MonitorA.DeviceName, "Old", 0, 0, 1920, 1080, false));
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [saved]));
        var reconnected = MonitorA with { Id = "new-id" };

        var inventory = manager.AnalyzeMonitorProfiles(manager.ActiveSetup.Id, [reconnected]);

        Assert.AreEqual(SetupMonitorProfileConnection.Connected, inventory.Profiles.Single().Connection);
        Assert.AreEqual(0, manager.RemoveDisconnectedMonitorProfiles(manager.ActiveSetup.Id, [reconnected]).RemovedCount);
    }

    [TestMethod]
    public void Exact_ids_are_claimed_before_fallback_candidates()
    {
        var exact = Profile(new("exact", "DISPLAY9", "Exact", 0, 0, 1920, 1080, false));
        var fallback = Profile(new("old", "DISPLAY9", "Fallback", 1920, 0, 1920, 1080, false));
        var exactMonitor = new MonitorInfo("exact", "DISPLAY9", "Exact current", 0, 0, 1920, 1080, false);
        var fallbackMonitor = new MonitorInfo("new", "DISPLAY9", "Fallback current", 1920, 0, 1920, 1080, false);
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [fallback, exact]));

        var inventory = manager.AnalyzeMonitorProfiles(manager.ActiveSetup.Id, [fallbackMonitor, exactMonitor]);

        Assert.AreSame(exactMonitor, inventory.Profiles.Single(status => status.Profile.MonitorId == exact.MonitorId).Monitor);
        Assert.AreSame(fallbackMonitor, inventory.Profiles.Single(status => status.Profile.MonitorId == fallback.MonitorId).Monitor);
        Assert.AreEqual(2, inventory.ConnectedDisplayCount);
    }

    [TestMethod]
    public void Bulk_removal_preserves_connected_ambiguous_and_other_setup_profiles()
    {
        var connected = Profile(MonitorA);
        var stale = Profile(MonitorB);
        var ambiguousOne = Profile(new("old-1", "DISPLAY9", "Old 1", 0, 0, 1920, 1080, false));
        var ambiguousTwo = Profile(new("old-2", "DISPLAY9", "Old 2", 0, 0, 1920, 1080, false));
        var manager = new SetupManager(SetupManager.CreateInitialState("First", [connected, stale, ambiguousOne, ambiguousTwo]));
        var targetSetup = manager.ActiveSetup;
        var other = manager.Duplicate(targetSetup.Id);
        var ambiguousMonitor = new MonitorInfo("current-vdd", "DISPLAY9", "VDD", 0, 0, 1920, 1080, false);

        var result = manager.RemoveDisconnectedMonitorProfiles(targetSetup.Id, [MonitorA, ambiguousMonitor]);

        CollectionAssert.AreEqual(new[] { MonitorB.Id }, result.RemovedMonitorIds.ToArray());
        CollectionAssert.AreEquivalent(new[] { MonitorA.Id, "old-1", "old-2" }, targetSetup.MonitorProfiles.Select(profile => profile.MonitorId).ToArray());
        Assert.AreEqual(4, other.MonitorProfiles.Count);
    }

    [TestMethod]
    public void Bulk_removal_preserves_configuration_aliases_and_visual_preferences()
    {
        var remaining = Profile(MonitorA, "remaining.jpg");
        remaining.Mode = WallpaperMode.Slideshow;
        remaining.SlideshowFolderPath = "remaining-folder";
        remaining.SlideshowInterval = TimeSpan.FromMinutes(30);
        remaining.ShuffleEnabled = false;
        remaining.LoopEnabled = false;
        remaining.FitMode = WallpaperFit.Center;
        remaining.Transition = TransitionKind.SlideLeft;
        remaining.TransitionDurationMs = 1234;
        remaining.CurrentSlideshowIndex = 7;
        remaining.LastWallpaperPath = "last.jpg";
        remaining.Enabled = false;
        var stale = Profile(MonitorB, "stale.jpg");
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [remaining, stale]));
        manager.State.MonitorAliases.Add(new() { MonitorId = MonitorB.Id, Name = "Old VDD" });
        manager.State.MonitorVisualPreferences.Add(new() { MonitorId = MonitorB.Id, LastKnownModelName = "VDD" });

        manager.RemoveDisconnectedMonitorProfiles(manager.ActiveSetup.Id, [MonitorA]);

        var actual = manager.ActiveSetup.MonitorProfiles.Single();
        Assert.AreEqual("remaining.jpg", actual.StaticImagePath);
        Assert.AreEqual("remaining-folder", actual.SlideshowFolderPath);
        Assert.AreEqual(TimeSpan.FromMinutes(30), actual.SlideshowInterval);
        Assert.IsFalse(actual.ShuffleEnabled);
        Assert.IsFalse(actual.LoopEnabled);
        Assert.AreEqual(WallpaperFit.Center, actual.FitMode);
        Assert.AreEqual(TransitionKind.SlideLeft, actual.Transition);
        Assert.AreEqual(1234, actual.TransitionDurationMs);
        Assert.AreEqual(7, actual.CurrentSlideshowIndex);
        Assert.AreEqual("last.jpg", actual.LastWallpaperPath);
        Assert.IsFalse(actual.Enabled);
        Assert.AreEqual(1, manager.State.MonitorAliases.Count);
        Assert.AreEqual(1, manager.State.MonitorVisualPreferences.Count);
    }

    [TestMethod]
    public void Disconnected_profile_can_reconnect_normally_when_not_removed()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [Profile(MonitorA), Profile(MonitorB)]));
        Assert.AreEqual(SetupMonitorProfileConnection.Disconnected,
            manager.AnalyzeMonitorProfiles(manager.ActiveSetup.Id, [MonitorA]).Profiles.Single(status => status.Profile.MonitorId == MonitorB.Id).Connection);

        var resolution = manager.ReconcileActiveMonitors([MonitorA, MonitorB]);

        Assert.AreEqual(MonitorMatchKind.ExactId, resolution.Matches.Single(match => match.Monitor.Id == MonitorB.Id).Kind);
        Assert.AreEqual(2, manager.ActiveSetup.MonitorProfiles.Count);
    }

    [TestMethod]
    public void Manually_removed_monitor_reconnects_as_new_profile()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [Profile(MonitorB)]));
        Assert.IsTrue(manager.RemoveDisconnectedMonitorProfile(manager.ActiveSetup.Id, MonitorB.Id, []).WasRemoved);

        var resolution = manager.ReconcileActiveMonitors([MonitorB]);

        Assert.AreEqual(MonitorMatchKind.Created, resolution.Matches.Single().Kind);
        Assert.AreEqual(MonitorB.Id, manager.ActiveSetup.MonitorProfiles.Single().MonitorId);
    }

    [TestMethod]
    public void Cleanup_can_leave_a_valid_zero_profile_setup()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [Profile(MonitorA)]));

        manager.RemoveDisconnectedMonitorProfiles(manager.ActiveSetup.Id, []);
        SetupStateValidator.Validate(manager.State);

        Assert.AreEqual(0, manager.ActiveSetup.MonitorProfiles.Count);
    }

    [TestMethod]
    public void Vdd_cleanup_keeps_two_exact_current_profiles_and_removes_safe_stale_profiles()
    {
        var currentOne = new MonitorInfo("vdd-current-1", "DISPLAY15", "VDD 1", 0, 0, 1920, 1080, false);
        var currentTwo = new MonitorInfo("vdd-current-2", "DISPLAY16", "VDD 2", 1920, 0, 1920, 1080, false);
        var profiles = new[]
        {
            Profile(new("vdd-stale-1", "DISPLAY10", "Old VDD", 0, 0, 1920, 1080, false)),
            Profile(new("vdd-stale-2", "DISPLAY11", "Old VDD", 0, 0, 1920, 1080, false)),
            Profile(new("vdd-stale-3", "DISPLAY12", "Old VDD", 0, 0, 1920, 1080, false)),
            Profile(currentOne),
            Profile(currentTwo)
        };
        var manager = new SetupManager(SetupManager.CreateInitialState("VDD", profiles));

        var result = manager.RemoveDisconnectedMonitorProfiles(manager.ActiveSetup.Id, [currentOne, currentTwo]);

        Assert.AreEqual(3, result.RemovedCount);
        CollectionAssert.AreEquivalent(new[] { currentOne.Id, currentTwo.Id }, manager.ActiveSetup.MonitorProfiles.Select(profile => profile.MonitorId).ToArray());
    }

    [TestMethod]
    public void Two_connected_monitors_cannot_claim_or_delete_the_same_profile()
    {
        var saved = Profile(new("old", "DISPLAY9", "Old", 0, 0, 1920, 1080, false));
        var first = new MonitorInfo("new-1", "DISPLAY9", "First", 0, 0, 1920, 1080, false);
        var second = new MonitorInfo("new-2", "DISPLAY9", "Second", 1920, 0, 1920, 1080, false);
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [saved]));

        var inventory = manager.AnalyzeMonitorProfiles(manager.ActiveSetup.Id, [first, second]);
        var result = manager.RemoveDisconnectedMonitorProfiles(manager.ActiveSetup.Id, [first, second]);

        Assert.AreEqual(1, inventory.ConnectedDisplayCount);
        Assert.AreEqual(0, result.RemovedCount);
        Assert.AreEqual(1, manager.ActiveSetup.MonitorProfiles.Count);
    }

    [TestMethod]
    public void Nonexistent_setup_or_profile_is_non_mutating()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [Profile(MonitorA)]));

        var missingSetup = manager.RemoveDisconnectedMonitorProfiles("missing", []);
        var missingProfile = manager.RemoveDisconnectedMonitorProfile(manager.ActiveSetup.Id, "missing", []);

        Assert.IsFalse(missingSetup.SetupFound);
        Assert.AreEqual(SetupMonitorProfileRemovalOutcome.NotFound, missingProfile.Outcome);
        Assert.AreEqual(1, manager.ActiveSetup.MonitorProfiles.Count);
    }

    [TestMethod]
    public void Repeated_bulk_cleanup_is_idempotent()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("Default", [Profile(MonitorA), Profile(MonitorB)]));

        var first = manager.RemoveDisconnectedMonitorProfiles(manager.ActiveSetup.Id, [MonitorA]);
        var second = manager.RemoveDisconnectedMonitorProfiles(manager.ActiveSetup.Id, [MonitorA]);

        Assert.AreEqual(1, first.RemovedCount);
        Assert.AreEqual(0, second.RemovedCount);
    }

    [DataTestMethod]
    [DataRow(4, 0, 0, "4 connected")]
    [DataRow(4, 7, 0, "4 connected · 7 disconnected")]
    [DataRow(0, 7, 0, "0 connected · 7 disconnected")]
    [DataRow(4, 5, 2, "4 connected · 5 disconnected · 2 uncertain")]
    [DataRow(1, 1, 1, "1 connected · 1 disconnected · 1 uncertain")]
    public void Connection_summary_formats_counts(int connected, int disconnected, int uncertain, string expected)
        => Assert.AreEqual(expected, SetupConnectionSummaryFormatter.Format(Inventory(connected, disconnected, uncertain)));

    [TestMethod]
    public void Connection_summary_handles_zero_saved_profiles()
        => Assert.AreEqual("No saved displays", SetupConnectionSummaryFormatter.Format(new([])));

    [TestMethod]
    public void Floating_panel_position_is_clamped_to_available_bounds()
    {
        var position = FloatingPanelPlacement.Clamp(-30, 900, 420, 560, 1100, 800);

        Assert.AreEqual(8, position.X);
        Assert.AreEqual(232, position.Y);
    }

    [TestMethod]
    public void Oversized_floating_panel_keeps_its_header_origin_reachable()
    {
        var position = FloatingPanelPlacement.Clamp(500, 500, 420, 560, 300, 220);

        Assert.AreEqual(8, position.X);
        Assert.AreEqual(8, position.Y);
    }

    private static MonitorWallpaperProfile Profile(MonitorInfo monitor, string? image = null)
    {
        var profile = SetupManager.CreateProfile(monitor);
        profile.StaticImagePath = image;
        return profile;
    }

    private static SetupMonitorInventory Inventory(int connected, int disconnected, int uncertain)
    {
        var statuses = new List<SetupMonitorProfileStatus>();
        void Add(int count, SetupMonitorProfileConnection connection)
        {
            for (var index = 0; index < count; index++)
                statuses.Add(new(new MonitorWallpaperProfile { MonitorId = $"{connection}-{statuses.Count}" }, connection));
        }
        Add(connected, SetupMonitorProfileConnection.Connected);
        Add(disconnected, SetupMonitorProfileConnection.Disconnected);
        Add(uncertain, SetupMonitorProfileConnection.Indeterminate);
        return new(statuses);
    }
}

[TestClass]
public sealed class SetupStateStoreTests
{
    [TestMethod]
    public async Task Schema_two_state_with_zero_profile_setup_remains_compatible()
    {
        using var area = new SetupStoreArea();
        Directory.CreateDirectory(area.Root);
        await File.WriteAllTextAsync(area.SetupStatePath, """
            {
              "SchemaVersion": 2,
              "ActiveSetupId": "empty",
              "Setups": [
                { "Id": "empty", "Name": "Empty", "MonitorProfiles": [] }
              ],
              "MonitorAliases": [],
              "MonitorVisualPreferences": []
            }
            """);

        var loaded = await area.Store.LoadOrCreateAsync();

        Assert.IsTrue(loaded.CanSave);
        Assert.IsFalse(loaded.WasMigrated);
        Assert.AreEqual(0, loaded.State.Setups.Single().MonitorProfiles.Count);
    }

    [TestMethod]
    public async Task Removed_disconnected_profile_stays_removed_after_reload()
    {
        using var area = new SetupStoreArea();
        var connected = new MonitorInfo("connected", "DISPLAY1", "Connected", 0, 0, 1920, 1080, true);
        var stale = new MonitorInfo("stale", "DISPLAY2", "Stale", 1920, 0, 1920, 1080, false);
        var manager = new SetupManager(SetupManager.CreateInitialState("Default",
            [SetupManager.CreateProfile(connected), SetupManager.CreateProfile(stale)]));
        manager.RemoveDisconnectedMonitorProfiles(manager.ActiveSetup.Id, [connected]);

        await area.Store.SaveAsync(manager.State);
        var loaded = await area.Store.LoadOrCreateAsync();

        CollectionAssert.AreEqual(new[] { connected.Id }, loaded.State.Setups.Single().MonitorProfiles.Select(profile => profile.MonitorId).ToArray());
    }

    [TestMethod]
    public async Task Active_setup_and_rename_survive_restart()
    {
        using var area = new SetupStoreArea();
        var manager = new SetupManager(SetupManager.CreateInitialState("Default"));
        var second = manager.CreateFromCurrent("Photography");
        Assert.IsTrue(manager.Rename(second.Id, "Night"));
        await area.Store.SaveAsync(manager.State);

        var loaded = await area.Store.LoadOrCreateAsync();

        Assert.AreEqual(second.Id, loaded.State.ActiveSetupId);
        Assert.AreEqual("Night", loaded.State.Setups.Single(setup => setup.Id == second.Id).Name);
    }

    [TestMethod]
    public async Task Legacy_profiles_and_setup_name_migrate_losslessly()
    {
        using var area = new SetupStoreArea();
        var legacyProfile = new MonitorWallpaperProfile
        {
            MonitorId = "legacy-monitor",
            StaticImagePath = "legacy.jpg",
            SlideshowFolderPath = "legacy-folder",
            Mode = WallpaperMode.Slideshow
        };
        await new ProfileStore(area.LegacyProfilesPath).SaveAsync([legacyProfile]);
        await File.WriteAllTextAsync(area.LegacyNamePath, "  Photography  ");

        var result = await area.Store.LoadOrCreateAsync();

        Assert.IsTrue(result.WasMigrated);
        Assert.IsTrue(result.CanSave);
        Assert.AreEqual("Photography", result.State.Setups.Single().Name);
        var migrated = result.State.Setups.Single().MonitorProfiles.Single();
        Assert.AreEqual("legacy-monitor", migrated.MonitorId);
        Assert.AreEqual("legacy.jpg", migrated.StaticImagePath);
        Assert.AreEqual("legacy-folder", migrated.SlideshowFolderPath);
    }

    [TestMethod]
    public async Task Migration_is_idempotent_and_new_state_becomes_authoritative()
    {
        using var area = new SetupStoreArea();
        await new ProfileStore(area.LegacyProfilesPath).SaveAsync([new() { MonitorId = "legacy" }]);
        var first = await area.Store.LoadOrCreateAsync();
        first.State.Setups.Single().Name = "Authoritative";
        await area.Store.SaveAsync(first.State);
        await File.WriteAllTextAsync(area.LegacyNamePath, "Ignored Legacy Name");

        var second = await area.Store.LoadOrCreateAsync();

        Assert.IsFalse(second.WasMigrated);
        Assert.AreEqual(first.State.ActiveSetupId, second.State.ActiveSetupId);
        Assert.AreEqual("Authoritative", second.State.Setups.Single().Name);
    }

    [TestMethod]
    public async Task Migration_never_deletes_original_legacy_files()
    {
        using var area = new SetupStoreArea();
        await new ProfileStore(area.LegacyProfilesPath).SaveAsync([new() { MonitorId = "legacy" }]);
        await File.WriteAllTextAsync(area.LegacyNamePath, "Legacy");
        var profileContents = await File.ReadAllTextAsync(area.LegacyProfilesPath);
        var nameContents = await File.ReadAllTextAsync(area.LegacyNamePath);

        await area.Store.LoadOrCreateAsync();

        Assert.IsTrue(File.Exists(area.LegacyProfilesPath));
        Assert.IsTrue(File.Exists(area.LegacyNamePath));
        Assert.AreEqual(profileContents, await File.ReadAllTextAsync(area.LegacyProfilesPath));
        Assert.AreEqual(nameContents, await File.ReadAllTextAsync(area.LegacyNamePath));
    }

    [TestMethod]
    public async Task Malformed_new_state_is_not_destructively_overwritten()
    {
        using var area = new SetupStoreArea();
        const string malformed = "{ definitely not valid json";
        Directory.CreateDirectory(area.Root);
        await File.WriteAllTextAsync(area.SetupStatePath, malformed);

        var result = await area.Store.LoadOrCreateAsync();

        Assert.IsFalse(result.CanSave);
        Assert.IsNotNull(result.Failure);
        Assert.AreEqual(malformed, await File.ReadAllTextAsync(area.SetupStatePath));
        Assert.AreEqual(1, result.State.Setups.Count);
    }

    [TestMethod]
    public async Task Atomic_save_leaves_no_temporary_files()
    {
        using var area = new SetupStoreArea();

        await area.Store.SaveAsync(SetupManager.CreateInitialState());

        Assert.AreEqual(0, Directory.GetFiles(area.Root, "*.tmp").Length);
        Assert.IsTrue(File.Exists(area.SetupStatePath));
    }

    [TestMethod]
    public async Task Monitor_alias_persists_through_serialization_and_reload()
    {
        using var area = new SetupStoreArea();
        var manager = new SetupManager(SetupManager.CreateInitialState());
        manager.SetMonitorAlias(new MonitorInfo("monitor-a", "DISPLAY1", "Display 1", 0, 0, 1920, 1080, true), "Main Monitor");
        await area.Store.SaveAsync(manager.State);

        var loaded = await area.Store.LoadOrCreateAsync();

        Assert.AreEqual("Main Monitor", loaded.State.MonitorAliases.Single().Name);
        Assert.AreEqual("monitor-a", loaded.State.MonitorAliases.Single().MonitorId);
    }

    [TestMethod]
    public async Task Legacy_setup_state_without_monitor_aliases_loads_normally()
    {
        using var area = new SetupStoreArea();
        Directory.CreateDirectory(area.Root);
        await File.WriteAllTextAsync(area.SetupStatePath, """
            {
              "SchemaVersion": 1,
              "ActiveSetupId": "legacy",
              "Setups": [
                { "Id": "legacy", "Name": "Legacy", "MonitorProfiles": [] }
              ]
            }
            """);

        var loaded = await area.Store.LoadOrCreateAsync();

        Assert.IsTrue(loaded.CanSave);
        Assert.IsTrue(loaded.WasMigrated);
        Assert.AreEqual(PaneSetupState.CurrentSchemaVersion, loaded.State.SchemaVersion);
        Assert.AreEqual(0, loaded.State.MonitorAliases.Count);
        Assert.AreEqual(0, loaded.State.MonitorVisualPreferences.Count);
        Assert.AreEqual("Legacy", loaded.State.Setups.Single().Name);
    }

    [TestMethod]
    public async Task Schema_one_state_migrates_without_losing_setup_profile_alias_or_active_selection()
    {
        using var area = new SetupStoreArea();
        Directory.CreateDirectory(area.Root);
        await File.WriteAllTextAsync(area.SetupStatePath, """
            {
              "SchemaVersion": 1,
              "ActiveSetupId": "second",
              "Setups": [
                {
                  "Id": "first",
                  "Name": "First",
                  "MonitorProfiles": [
                    { "MonitorId": "monitor-a", "StaticImagePath": "wallpaper.jpg" }
                  ]
                },
                { "Id": "second", "Name": "Second", "MonitorProfiles": [] }
              ],
              "MonitorAliases": [
                { "MonitorId": "monitor-a", "MonitorDevicePath": "DISPLAY1", "Name": "Main" }
              ]
            }
            """);

        var loaded = await area.Store.LoadOrCreateAsync();

        Assert.IsTrue(loaded.CanSave);
        Assert.IsTrue(loaded.WasMigrated);
        Assert.AreEqual(2, loaded.State.Setups.Count);
        Assert.AreEqual("second", loaded.State.ActiveSetupId);
        Assert.AreEqual("wallpaper.jpg", loaded.State.Setups[0].MonitorProfiles.Single().StaticImagePath);
        Assert.AreEqual("Main", loaded.State.MonitorAliases.Single().Name);
        Assert.AreEqual(0, loaded.State.MonitorVisualPreferences.Count);
    }

    [TestMethod]
    public async Task Monitor_visual_preference_persists_through_serialization_and_reload()
    {
        using var area = new SetupStoreArea();
        var monitor = new MonitorInfo(
            "monitor-a", "DISPLAY1", "Display A", 0, 0, 3440, 1440, true,
            MonitorDevicePath: @"\\?\DISPLAY#ACME#A");
        var manager = new SetupManager(SetupManager.CreateInitialState());
        manager.SetMonitorVisualStyle(monitor, DisplayShellStyle.UltrawideCurved, [monitor]);
        await area.Store.SaveAsync(manager.State);

        var loaded = await area.Store.LoadOrCreateAsync();

        var preference = loaded.State.MonitorVisualPreferences.Single();
        Assert.AreEqual(DisplayShellStyle.UltrawideCurved, preference.StyleOverride);
        Assert.AreEqual(monitor.MonitorDevicePath, preference.MonitorDevicePath);
        Assert.AreEqual(3440, preference.LastKnownWidth);
    }

    private sealed class SetupStoreArea : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "WallflowTests", Guid.NewGuid().ToString("N"));
        public string SetupStatePath => Path.Combine(Root, "setups.json");
        public string LegacyProfilesPath => Path.Combine(Root, "profiles.json");
        public string LegacyNamePath => Path.Combine(Root, "setup-name.txt");
        public SetupStateStore Store => new(SetupStatePath, LegacyProfilesPath, LegacyNamePath);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
