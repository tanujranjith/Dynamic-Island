namespace DynamicIsland.Windows.Models;

public sealed class RecurrenceRule
{
    public AlarmRepeat Repeat { get; set; } = AlarmRepeat.Once;
    public int WeekdayMask { get; set; }
    public int IntervalDays { get; set; } = 1;
    public DateTime? EndDate { get; set; }
}

public static class RecurrenceCalculator
{
    public static DateTimeOffset? Next(
        DateTimeOffset after,
        int hour,
        int minute,
        AlarmRepeat repeat,
        int? anchorDayOfWeek = null,
        DateTime? anchorDate = null,
        int weekdayMask = 0,
        int intervalDays = 1,
        DateTime? endDate = null,
        TimeZoneInfo? timeZone = null)
    {
        if (repeat == AlarmRepeat.Once)
        {
            var once = LocalCandidate(after.Date, hour, minute, after.Offset, timeZone);
            if (once <= after) once = LocalCandidate(after.Date.AddDays(1), hour, minute, after.Offset, timeZone);
            return IsBeforeEnd(once, endDate) ? once : null;
        }

        var startDate = anchorDate?.Date ?? after.Date;
        var candidate = Candidate(after, hour, minute, 0);
        for (var offset = 0; offset <= 3660; offset++)
        {
            var date = candidate.Date.AddDays(offset);
            if (endDate is not null && date.Date > endDate.Value.Date) return null;
            var matches = repeat switch
            {
                AlarmRepeat.Daily => true,
                AlarmRepeat.Weekdays => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
                AlarmRepeat.Weekends => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                AlarmRepeat.Weekly => anchorDayOfWeek is null || (int)date.DayOfWeek == anchorDayOfWeek.Value,
                AlarmRepeat.SelectedWeekdays => weekdayMask != 0 && (weekdayMask & (1 << (int)date.DayOfWeek)) != 0,
                AlarmRepeat.EveryNDays => (date.Date - startDate).Days >= 0 &&
                    (date.Date - startDate).Days % Math.Max(1, intervalDays) == 0,
                _ => false
            };
            if (!matches) continue;
            var value = LocalCandidate(date, hour, minute, after.Offset, timeZone);
            if (value > after && IsBeforeEnd(value, endDate)) return value;
        }
        return null;
    }

    private static DateTimeOffset Candidate(DateTimeOffset after, int hour, int minute, int dayOffset) =>
        new DateTimeOffset(after.Year, after.Month, after.Day, Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), 0, after.Offset).AddDays(dayOffset);

    private static bool IsBeforeEnd(DateTimeOffset value, DateTime? endDate) =>
        endDate is null || value.Date <= endDate.Value.Date;

    private static DateTimeOffset LocalCandidate(DateTime date, int hour, int minute, TimeSpan fallbackOffset, TimeZoneInfo? zone)
    {
        var local = DateTime.SpecifyKind(date.Date.AddHours(Math.Clamp(hour, 0, 23)).AddMinutes(Math.Clamp(minute, 0, 59)), DateTimeKind.Unspecified);
        if (zone is null) return new(local, fallbackOffset);
        // Spring gap: first valid minute. Fall overlap: first occurrence, never ring twice.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new(local, offset);
    }
}
