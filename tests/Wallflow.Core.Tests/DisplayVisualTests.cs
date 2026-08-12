using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wallflow.Core;

namespace Wallflow.Core.Tests;

[TestClass]
public sealed class DisplayStyleClassifierTests
{
    [DataTestMethod]
    [DataRow(1920, 1080, DisplayShellStyle.StandardFlat, DisplayOrientation.Landscape)]
    [DataRow(2560, 1440, DisplayShellStyle.StandardFlat, DisplayOrientation.Landscape)]
    [DataRow(1080, 1920, DisplayShellStyle.StandardFlat, DisplayOrientation.Portrait)]
    [DataRow(3440, 1440, DisplayShellStyle.UltrawideFlat, DisplayOrientation.Landscape)]
    [DataRow(3840, 1600, DisplayShellStyle.UltrawideFlat, DisplayOrientation.Landscape)]
    [DataRow(5120, 1440, DisplayShellStyle.UltrawideFlat, DisplayOrientation.Landscape)]
    public void Common_resolutions_classify_conservatively(
        int width,
        int height,
        DisplayShellStyle expectedStyle,
        DisplayOrientation expectedOrientation)
    {
        var monitor = Monitor(width, height);

        var classification = DisplayStyleClassifier.Classify(monitor);

        Assert.AreEqual(expectedStyle, classification.ShellStyle);
        Assert.AreEqual(expectedOrientation, monitor.Orientation);
    }

    [TestMethod]
    public void DisplayConfig_rotation_is_preserved_when_reported()
    {
        var monitor = Monitor(1920, 1080) with { ReportedOrientation = DisplayOrientation.LandscapeFlipped };

        Assert.AreEqual(DisplayOrientation.LandscapeFlipped, monitor.Orientation);
    }

    [DataTestMethod]
    [DataRow(null, 1920, 1080, DisplayOrientation.Landscape)]
    [DataRow(null, 1080, 1920, DisplayOrientation.Portrait)]
    [DataRow(0, 1080, 1920, DisplayOrientation.Portrait)]
    [DataRow(90, 1920, 1080, DisplayOrientation.Portrait)]
    [DataRow(180, 1920, 1080, DisplayOrientation.LandscapeFlipped)]
    [DataRow(270, 1080, 1920, DisplayOrientation.PortraitFlipped)]
    public void Rotation_resolution_supports_all_orientations_and_dimension_fallback(
        int? rotation,
        int width,
        int height,
        DisplayOrientation expected)
    {
        Assert.AreEqual(expected, DisplayOrientationResolver.Resolve(rotation, width, height));
    }

    [TestMethod]
    public void Internal_display_is_laptop()
    {
        var monitor = Monitor(2560, 1600) with { IsInternal = true };

        Assert.AreEqual(DisplayShellStyle.Laptop, DisplayStyleClassifier.Classify(monitor).ShellStyle);
    }

    [TestMethod]
    public void Trustworthy_large_diagonal_is_large_display()
    {
        var monitor = Monitor(3840, 2160) with
        {
            PhysicalWidthMillimeters = 1_218,
            PhysicalHeightMillimeters = 685,
            PhysicalSizeSource = PhysicalSizeSource.EdidReported
        };

        var classification = DisplayStyleClassifier.Classify(monitor);

        Assert.AreEqual(DisplayShellStyle.LargeDisplay, classification.ShellStyle);
        Assert.IsTrue(classification.PhysicalSize!.DiagonalInches > 50);
        Assert.AreEqual(PhysicalSizeConfidence.EdidReported, classification.PhysicalSizeConfidence);
    }

    [TestMethod]
    public void Gdi_estimated_plausible_fifty_inch_display_does_not_auto_classify_as_large()
    {
        var monitor = Monitor(3840, 2160) with
        {
            PhysicalWidthMillimeters = 1_107,
            PhysicalHeightMillimeters = 623,
            PhysicalSizeSource = PhysicalSizeSource.GdiEstimated
        };

        var classification = DisplayStyleClassifier.Classify(monitor);

        Assert.AreEqual(DisplayShellStyle.StandardFlat, classification.ShellStyle);
        Assert.AreEqual(PhysicalSizeConfidence.Estimated, classification.PhysicalSizeConfidence);
        Assert.IsTrue(classification.PhysicalSize!.DiagonalInches > 49);
    }

    [TestMethod]
    public void Missing_physical_metadata_is_safe()
    {
        var classification = DisplayStyleClassifier.Classify(Monitor(1920, 1080));

        Assert.AreEqual(DisplayShellStyle.StandardFlat, classification.ShellStyle);
        Assert.IsNull(classification.PhysicalSize);
        Assert.AreEqual(PhysicalSizeConfidence.Unavailable, classification.PhysicalSizeConfidence);
    }

    [DataTestMethod]
    [DataRow(0, 0)]
    [DataRow(10_000, 8_000)]
    [DataRow(49, 300)]
    public void Invalid_physical_dimensions_are_ignored(int widthMillimeters, int heightMillimeters)
    {
        var monitor = Monitor(1920, 1080) with
        {
            PhysicalWidthMillimeters = widthMillimeters,
            PhysicalHeightMillimeters = heightMillimeters
        };

        var classification = DisplayStyleClassifier.Classify(monitor);

        Assert.AreEqual(DisplayShellStyle.StandardFlat, classification.ShellStyle);
        Assert.IsNull(classification.PhysicalSize);
    }

    [TestMethod]
    public void Plausible_dimensions_with_bogus_aspect_ratio_are_not_used_for_large_display_classification()
    {
        var monitor = Monitor(1920, 1080) with
        {
            PhysicalWidthMillimeters = 700,
            PhysicalHeightMillimeters = 700
        };

        var classification = DisplayStyleClassifier.Classify(monitor);

        Assert.AreEqual(DisplayShellStyle.StandardFlat, classification.ShellStyle);
        Assert.IsNull(classification.PhysicalSize);
    }

    [DataTestMethod]
    [DataRow(DisplayOrientation.Portrait)]
    [DataRow(DisplayOrientation.PortraitFlipped)]
    public void Portrait_rotation_accepts_native_landscape_edid_dimensions(DisplayOrientation orientation)
    {
        var monitor = Monitor(1080, 1920) with
        {
            ReportedOrientation = orientation,
            PhysicalWidthMillimeters = 530,
            PhysicalHeightMillimeters = 300,
            PhysicalSizeSource = PhysicalSizeSource.EdidReported
        };

        var classification = DisplayStyleClassifier.Classify(monitor);

        Assert.IsNotNull(classification.PhysicalSize);
        Assert.AreEqual(PhysicalSizeConfidence.EdidReported, classification.PhysicalSizeConfidence);
    }

    [TestMethod]
    public void Portrait_dimensions_without_rotation_metadata_accept_native_landscape_edid_dimensions()
    {
        var monitor = Monitor(1080, 1920) with
        {
            PhysicalWidthMillimeters = 530,
            PhysicalHeightMillimeters = 300,
            PhysicalSizeSource = PhysicalSizeSource.EdidReported
        };

        Assert.IsNotNull(DisplayStyleClassifier.Classify(monitor).PhysicalSize);
    }

    [TestMethod]
    public void Manual_standard_override_wins_over_ultrawide_detection()
    {
        var monitor = Monitor(3440, 1440);
        var preference = Preference(DisplayShellStyle.StandardFlat);

        var descriptor = MonitorVisualResolver.Resolve(monitor, preference);

        Assert.AreEqual(DisplayShellStyle.StandardFlat, descriptor.ResolvedShellStyle);
        Assert.AreEqual(DisplayStyleSource.ManualOverride, descriptor.StyleSource);
    }

    [TestMethod]
    public void Manual_curved_override_wins()
    {
        var descriptor = MonitorVisualResolver.Resolve(
            Monitor(3440, 1440),
            Preference(DisplayShellStyle.UltrawideCurved));

        Assert.AreEqual(DisplayShellStyle.UltrawideCurved, descriptor.ResolvedShellStyle);
        Assert.AreEqual(DisplayStyleSource.ManualOverride, descriptor.StyleSource);
    }

    [TestMethod]
    public void Auto_preference_uses_classifier()
    {
        var descriptor = MonitorVisualResolver.Resolve(Monitor(3440, 1440), Preference(DisplayShellStyle.Auto));

        Assert.AreEqual(DisplayShellStyle.UltrawideFlat, descriptor.ResolvedShellStyle);
        Assert.AreEqual(DisplayStyleSource.Automatic, descriptor.StyleSource);
    }

    [TestMethod]
    public void Diagnostic_formatter_contains_detection_and_resolution_fields()
    {
        var monitor = Monitor(3440, 1440) with
        {
            MonitorDevicePath = @"\\?\DISPLAY#MODEL",
            ManufacturerId = "10AC",
            ProductCode = "A123",
            IsInternal = false
        };
        var text = MonitorDiagnosticFormatter.Format(monitor, MonitorVisualResolver.Resolve(monitor));

        StringAssert.Contains(text, "Resolution: 3440 x 1440");
        StringAssert.Contains(text, "Manufacturer: 10AC");
        StringAssert.Contains(text, "Auto style: UltrawideFlat");
        StringAssert.Contains(text, "Style source: Automatic");
    }

    private static MonitorInfo Monitor(int width, int height)
        => new("monitor", "DISPLAY1", "Display", 0, 0, width, height, true);

    private static MonitorVisualPreference Preference(DisplayShellStyle style)
        => new() { MonitorId = "monitor", StyleOverride = style };
}

[TestClass]
public sealed class MonitorPhysicalSizeIdentityMatcherTests
{
    private const string DisplayPath = @"\\?\DISPLAY#DEL40A9#5&10a58c1d&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string WmiInstance = @"DISPLAY\DEL40A9\5&10a58c1d&0&UID4352_0";

    [TestMethod]
    public void Unique_active_wmi_instance_matches_displayconfig_path_and_converts_centimeters()
    {
        var matches = MonitorPhysicalSizeIdentityMatcher.Match(
            [DisplayPath],
            [new(WmiInstance, true, 60, 34)]);

        var size = matches[DisplayPath];
        Assert.AreEqual(600, size.WidthMillimeters);
        Assert.AreEqual(340, size.HeightMillimeters);
    }

    [TestMethod]
    public void Zero_edid_dimension_is_unavailable()
    {
        var matches = MonitorPhysicalSizeIdentityMatcher.Match(
            [DisplayPath],
            [new(WmiInstance, true, 0, 34)]);

        Assert.AreEqual(0, matches.Count);
    }

    [TestMethod]
    public void Ambiguous_wmi_identity_is_omitted_instead_of_guessed()
    {
        var matches = MonitorPhysicalSizeIdentityMatcher.Match(
            [DisplayPath],
            [new(WmiInstance, true, 60, 34), new(WmiInstance, true, 70, 39)]);

        Assert.AreEqual(0, matches.Count);
    }

    [TestMethod]
    public void Missing_wmi_metadata_is_safe()
    {
        var matches = MonitorPhysicalSizeIdentityMatcher.Match([DisplayPath], []);

        Assert.AreEqual(0, matches.Count);
    }

    [TestMethod]
    public void Multiple_identical_models_match_only_by_their_unique_instance_identity()
    {
        const string secondDisplayPath = @"\\?\DISPLAY#DEL40A9#5&10a58c1d&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        const string secondWmiInstance = @"DISPLAY\DEL40A9\5&10a58c1d&0&UID4353_0";
        var matches = MonitorPhysicalSizeIdentityMatcher.Match(
            [DisplayPath, secondDisplayPath],
            [new(WmiInstance, true, 60, 34), new(secondWmiInstance, true, 70, 39)]);

        Assert.AreEqual(2, matches.Count);
        Assert.AreEqual(600, matches[DisplayPath].WidthMillimeters);
        Assert.AreEqual(700, matches[secondDisplayPath].WidthMillimeters);
    }
}

[TestClass]
public sealed class MonitorVisualPreferenceTests
{
    private static readonly MonitorInfo MonitorA = new(
        "monitor-a", "DISPLAY1", "Display A", 0, 0, 3440, 1440, true,
        MonitorDevicePath: @"\\?\DISPLAY#ACME#A");

    [TestMethod]
    public void Visual_preference_is_global_across_setup_switching_and_cloning()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState("First"));
        manager.SetMonitorVisualStyle(MonitorA, DisplayShellStyle.UltrawideCurved, [MonitorA]);
        var second = manager.CreateFromCurrent("Second");
        var duplicate = manager.Duplicate(second.Id);

        manager.Activate(duplicate.Id);
        var descriptor = manager.GetMonitorVisualDescriptor(MonitorA, [MonitorA]);

        Assert.AreEqual(DisplayShellStyle.UltrawideCurved, descriptor.ResolvedShellStyle);
        Assert.AreEqual(1, manager.State.MonitorVisualPreferences.Count);
    }

    [TestMethod]
    public void Exact_monitor_reconnect_preserves_visual_preference()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState());
        manager.SetMonitorVisualStyle(MonitorA, DisplayShellStyle.StandardFlat, [MonitorA]);
        var reconnected = MonitorA with { X = 100, Width = 2560, Height = 1440 };

        manager.ReconcileMonitorVisualPreferences([reconnected]);

        Assert.AreEqual(DisplayShellStyle.StandardFlat,
            manager.GetMonitorVisualDescriptor(reconnected, [reconnected]).ResolvedShellStyle);
        Assert.AreEqual(1, manager.State.MonitorVisualPreferences.Count);
    }

    [TestMethod]
    public void Unique_device_path_reconnect_reassigns_visual_preference()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState());
        manager.SetMonitorVisualStyle(MonitorA, DisplayShellStyle.UltrawideCurved, [MonitorA]);
        var reconnected = MonitorA with { Id = "monitor-a-new" };

        manager.ReconcileMonitorVisualPreferences([reconnected]);

        Assert.AreEqual("monitor-a-new", manager.State.MonitorVisualPreferences.Single().MonitorId);
        Assert.AreEqual(DisplayShellStyle.UltrawideCurved,
            manager.GetMonitorVisualDescriptor(reconnected, [reconnected]).ResolvedShellStyle);
    }

    [TestMethod]
    public void Ambiguous_device_path_does_not_reassign_visual_preference()
    {
        var state = SetupManager.CreateInitialState();
        state.MonitorVisualPreferences.Add(new()
        {
            MonitorId = "old-a",
            MonitorDevicePath = MonitorA.MonitorDevicePath,
            StyleOverride = DisplayShellStyle.UltrawideCurved
        });
        state.MonitorVisualPreferences.Add(new()
        {
            MonitorId = "old-b",
            MonitorDevicePath = MonitorA.MonitorDevicePath,
            StyleOverride = DisplayShellStyle.LargeDisplay
        });
        var manager = new SetupManager(state);
        var reconnected = MonitorA with { Id = "new" };

        manager.ReconcileMonitorVisualPreferences([reconnected]);

        Assert.AreEqual(3, manager.State.MonitorVisualPreferences.Count);
        Assert.AreEqual(DisplayShellStyle.UltrawideFlat,
            manager.GetMonitorVisualDescriptor(reconnected, [reconnected]).ResolvedShellStyle);
    }

    [TestMethod]
    public void Disconnected_visual_preference_is_retained()
    {
        var manager = new SetupManager(SetupManager.CreateInitialState());
        manager.SetMonitorVisualStyle(MonitorA, DisplayShellStyle.UltrawideCurved, [MonitorA]);

        manager.ReconcileMonitorVisualPreferences([]);

        Assert.AreEqual(1, manager.State.MonitorVisualPreferences.Count);
        Assert.AreEqual(DisplayShellStyle.UltrawideCurved, manager.State.MonitorVisualPreferences.Single().StyleOverride);
    }
}
