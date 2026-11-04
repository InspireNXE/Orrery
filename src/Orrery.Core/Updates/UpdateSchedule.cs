using Orrery.Core.Settings;

namespace Orrery.Core.Updates;

/// <summary>
/// When automatic update checks run while Orrery stays open: every <see cref="AppSettings.UpdateCheckInterval"/> since
/// the last (<see cref="AppSettings.LastUpdateCheck"/>), and only while <see cref="AppSettings.ChecksForUpdates"/>. The
/// app also checks once at every start, whenever the last check was. Checking by hand doesn't wait for this.
/// </summary>
public static class UpdateSchedule
{
    /// <summary>
    /// Whether an automatic check should run now. A last check in the future (the clock was put back) counts as
    /// long ago, so checks don't stop for days.
    /// </summary>
    public static bool IsCheckDue(AppSettings settings, DateTimeOffset now) =>
        settings.ChecksForUpdates
        && (settings.LastUpdateCheck is not { } last || last > now || now - last >= settings.UpdateCheckInterval);

    /// <summary>How long until the next automatic check: zero when one is due now, null when checks are off.</summary>
    public static TimeSpan? TimeUntilDue(AppSettings settings, DateTimeOffset now) =>
        !settings.ChecksForUpdates ? null
        : IsCheckDue(settings, now) ? TimeSpan.Zero
        : settings.LastUpdateCheck!.Value + settings.UpdateCheckInterval - now;
}
