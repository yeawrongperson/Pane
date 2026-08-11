namespace Wallflow.Core;

public static class SlideshowPolicy
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromHours(24);

    public static TimeSpan NormalizeInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) return DefaultInterval;
        if (interval < MinimumInterval) return MinimumInterval;
        return interval > MaximumInterval ? MaximumInterval : interval;
    }

    public static void NormalizeProfile(MonitorWallpaperProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.SlideshowInterval = NormalizeInterval(profile.SlideshowInterval);
        if (!Enum.IsDefined(profile.Mode)) profile.Mode = WallpaperMode.Static;
        if (!Enum.IsDefined(profile.FitMode)) profile.FitMode = WallpaperFit.Fill;
        if (!Enum.IsDefined(profile.Transition)) profile.Transition = TransitionKind.SoftFade;
        if (profile.CurrentSlideshowIndex < 0) profile.CurrentSlideshowIndex = 0;
    }
}
