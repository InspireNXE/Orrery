namespace Orrery.App.Connections;

/// <summary>A past time in friendly words, for after a verb: "Last connected 3 days ago".</summary>
static class RelativeTime
{
    /// <summary>"just now", "5 minutes ago", "2 hours ago", "yesterday", "3 days ago", "3 weeks ago", then "on 12 Mar 2026".</summary>
    public static string Describe(DateTimeOffset when, DateTimeOffset now)
    {
        var age = now - when;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now"; // A time slightly ahead of the clock counts too.
        }

        if (age < TimeSpan.FromHours(1))
        {
            return Plural((int)age.TotalMinutes, "minute");
        }

        if (age < TimeSpan.FromHours(24))
        {
            return Plural((int)age.TotalHours, "hour");
        }

        // From here, count calendar days in local time, so 23:50 yesterday is "yesterday" at any hour today.
        int days = now.LocalDateTime.Date.Subtract(when.LocalDateTime.Date).Days;
        return days switch
        {
            <= 1 => "yesterday",
            < 7 => $"{days} days ago",
            < 35 => Plural(days / 7, "week"),
            _ => "on " + when.LocalDateTime.ToString("d MMM yyyy"),
        };
    }

    /// <summary>The exact local date and time, for a tooltip.</summary>
    public static string Exact(DateTimeOffset when) => when.LocalDateTime.ToString("d MMMM yyyy, HH:mm:ss");

    static string Plural(int count, string unit) => count == 1 ? $"1 {unit} ago" : $"{count} {unit}s ago";
}
