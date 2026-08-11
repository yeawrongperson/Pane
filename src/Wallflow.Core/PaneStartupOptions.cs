namespace Wallflow.Core;

public enum PaneStartupMode
{
    Normal,
    SmokeTest
}

public sealed record PaneStartupOptions(PaneStartupMode Mode)
{
    public const string SmokeTestArgument = "--smoke-test";
    public const string SmokeTestInstanceKeyPrefix = "Pane.SmokeTest.";

    public static PaneStartupOptions Parse(IEnumerable<string>? arguments)
    {
        var isSmokeTest = arguments?.Any(argument =>
            string.Equals(argument, SmokeTestArgument, StringComparison.Ordinal)) == true;
        return new(isSmokeTest ? PaneStartupMode.SmokeTest : PaneStartupMode.Normal);
    }

    public bool IsSmokeTest => Mode == PaneStartupMode.SmokeTest;
    public string InstanceKey => IsSmokeTest
        ? SmokeTestInstanceKeyPrefix + Environment.ProcessId
        : SingleInstanceActivationCoordinator.InstanceKey;
    public bool UsesPersistentProfileState => !IsSmokeTest;
    public bool RunsLegacyProfileMigration => !IsSmokeTest;
    public bool StartsPersistedSlideshows => !IsSmokeTest;
    public bool AllowsWallpaperChanges => !IsSmokeTest;
    public bool CreatesTrayIcon => !IsSmokeTest;
}
