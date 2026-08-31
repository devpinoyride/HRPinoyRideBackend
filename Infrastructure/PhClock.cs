namespace PinoyRideHrApi.Infrastructure;

/// <summary>
/// Time helper that keeps the portal anchored to the Philippines time zone
/// (Asia/Manila on Linux containers; Singapore Standard Time alias on Windows).
/// All "today" / work-date decisions and display conversions go through here.
/// </summary>
public static class PhClock
{
    private static readonly TimeZoneInfo TZ = Resolve();

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Asia/Manila", "Singapore Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // keep trying the next alias
            }
            catch (InvalidTimeZoneException)
            {
                // keep trying the next alias
            }
        }
        return TimeZoneInfo.Utc;
    }

    /// <summary>Current wall-clock time in the Philippines.</summary>
    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TZ);

    /// <summary>Current date in the Philippines.</summary>
    public static DateOnly Today => DateOnly.FromDateTime(Now);

    /// <summary>Converts a stored UTC instant to the Philippines wall-clock time.</summary>
    public static DateTime? ToLocal(DateTime? utc)
    {
        if (!utc.HasValue) return null;
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), TZ);
    }

    /// <summary>
    /// Combines a Philippines work date + requested time into the exact UTC instant
    /// that should be stored in a timestamptz column so that it renders back as the
    /// requested wall-clock time in Manila.
    /// </summary>
    public static DateTime Combine(DateOnly date, TimeOnly time)
    {
        var local = new DateTime(date.Year, date.Month, date.Day, time.Hour, time.Minute, time.Second, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, TZ);
    }
}