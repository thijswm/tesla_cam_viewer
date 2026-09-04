namespace TeslaCamViewer.Shared;

public static class EventLocalTime
{
    public static (DateTime StartUtc, DateTime EndUtc) UtcRangeForLocalDate(DateTime localDate)
    {
        var startLocal = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Local);
        return (startLocal.ToUniversalTime(), startLocal.AddDays(1).ToUniversalTime());
    }

    public static (DateTime StartUtc, DateTime EndUtc) UtcRangeForLocalMonth(DateTime localMonth)
    {
        var startLocal = new DateTime(localMonth.Year, localMonth.Month, 1, 0, 0, 0, DateTimeKind.Local);
        return (startLocal.ToUniversalTime(), startLocal.AddMonths(1).ToUniversalTime());
    }

    public static DateTime ToLocalDate(DateTime utcTimestamp)
    {
        var utc = utcTimestamp.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utcTimestamp, DateTimeKind.Utc)
            : utcTimestamp;
        return utc.ToLocalTime().Date;
    }
}
