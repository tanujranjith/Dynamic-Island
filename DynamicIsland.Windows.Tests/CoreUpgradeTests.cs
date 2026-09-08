using System.Text.Json;
using DynamicIsland.Windows.Infrastructure;
using DynamicIsland.Windows.Models;
using Xunit;

namespace DynamicIsland.Windows.Tests;

public sealed class CoreUpgradeTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        private long _stamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _stamp;
        public void Advance(int seconds) { Now = Now.AddSeconds(seconds); _stamp += TimeSpan.FromSeconds(seconds).Ticks; }
    }
    [Fact]
    public void IndependentTimersPauseResumeRestartAndChooseSoonest()
    {
        var clock = new Clock(); var engine = new TimerEngine(new(), clock);
        var longId = engine.Add(TimeSpan.FromMinutes(5), "Tea"); var shortId = engine.Add(TimeSpan.FromMinutes(1), "Break");
        Assert.Equal(shortId, engine.DisplayTimer()!.Id);
        Assert.Equal(longId, engine.DisplayTimer(longId)!.Id);
        clock.Advance(10); engine.Pause(shortId); clock.Advance(20);
        Assert.Equal(50, TimerEngine.Remaining(engine.State.Timers[1], clock.Now).TotalSeconds);
        Assert.Equal(longId, engine.DisplayTimer()!.Id);
        engine.Resume(shortId); clock.Advance(50);
        Assert.Equal(new[] { shortId }, engine.Tick().Completed);
        Assert.Equal(TimerPhase.Running, engine.State.Timers[0].Phase);
        engine.Restart(shortId); Assert.Equal(2, engine.State.Timers.Count);
        Assert.Equal(60, TimerEngine.Remaining(engine.State.Timers[1], clock.Now).TotalSeconds);
    }
    [Fact]
    public void SimultaneousCompletionsAreOneBatchAndStayInDoneList()
    {
        var clock = new Clock(); var engine = new TimerEngine(new(), clock);
        engine.Add(TimeSpan.FromSeconds(1), "A"); engine.Add(TimeSpan.FromSeconds(1), "B"); clock.Advance(1);
        Assert.Equal(2, engine.Tick().Completed.Count); Assert.Empty(engine.Tick().Completed);
        clock.Advance(5); engine.Tick();
        Assert.All(engine.State.Timers, t => { Assert.Equal(TimerPhase.Completed, t.Phase); Assert.True(t.CompletionAcknowledged); });
    }
    [Fact]
    public void RecoveryIsQuietAndIncludesSnoozedAndRepeatingAlarms()
    {
        var clock = new Clock(); var engine = new TimerEngine(new(), clock);
        engine.Add(TimeSpan.FromSeconds(1), "Missed");
        engine.State.Alarms.Add(new() { Phase = AlarmPhase.Snoozed, SnoozeUntil = clock.Now.AddSeconds(1), Label = "Nap" });
        engine.State.Alarms.Add(new() { Phase = AlarmPhase.Scheduled, TargetAt = clock.Now.AddSeconds(1), Repeat = AlarmRepeat.Daily, Hour = 12 });
        clock.Advance(120); var result = engine.Tick(true);
        Assert.Empty(result.Completed); Assert.Empty(result.Ringing); Assert.Equal(3, engine.State.MissedAlerts.Count);
        Assert.Equal(AlarmPhase.Dismissed, engine.State.Alarms[0].Phase);
        Assert.True(engine.State.Alarms[1].TargetAt > clock.Now);
        Assert.True(engine.State.Timers[0].CompletionAcknowledged);
        engine.Tick(true); Assert.Equal(3, engine.State.MissedAlerts.Count);
    }
    [Fact]
    public void SnoozingOneAlarmDoesNotChangeAnother()
    {
        var clock = new Clock(); var engine = new TimerEngine(new(), clock);
        var a = new AlarmState { Phase = AlarmPhase.Scheduled, TargetAt = clock.Now };
        var b = new AlarmState { Phase = AlarmPhase.Scheduled, TargetAt = clock.Now };
        engine.State.Alarms.AddRange([a, b]); Assert.Equal(2, engine.Tick().Ringing.Count);
        engine.Snooze(a.Id, 5); Assert.Equal(AlarmPhase.Ringing, b.Phase);
        clock.Advance(60); engine.Tick(); Assert.Equal(AlarmPhase.Dismissed, b.Phase); Assert.Equal(AlarmPhase.Snoozed, a.Phase);
        clock.Advance(240); Assert.Equal(new[] { a.Id }, engine.Tick().Ringing);
    }
    [Fact]
    public void LegacyStateMigratesAndRoundTripsIdsAndRecurrence()
    {
        var state = TimerEngine.Deserialize("""{"Timer":{"Label":"Tea","Phase":2,"TotalSeconds":300,"PausedRemainingSeconds":90},"Alarm":{"Phase":3,"Label":"Wake","Hour":8,"Repeat":6,"RepeatWeekdayMask":62,"SnoozeCount":2}}""");
        Assert.Single(state.Timers); Assert.Single(state.Alarms); Assert.Equal(90, state.Timer.PausedRemainingSeconds);
        Assert.Equal(62, state.Alarm.RepeatWeekdayMask); Assert.Equal(2, state.Alarm.SnoozeCount);
        var copy = TimerEngine.Deserialize(JsonSerializer.Serialize(state));
        Assert.Equal(state.Timer.Id, copy.Timer.Id); Assert.Equal(state.Alarm.Id, copy.Alarm.Id);
    }
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Version\":2,\"Timers\":null}")]
    [InlineData("{\"Version\":2,\"Timers\":[{\"TotalSeconds\":-1}]}")]
    public void InvalidStorageIsRejected(string json) => Assert.ThrowsAny<JsonException>(() => TimerEngine.Deserialize(json));
    [Fact]
    public void FutureStorageIsExplicitlyRejected() => Assert.Throws<NotSupportedException>(() => TimerEngine.Deserialize("{\"Version\":99}"));
    [Fact]
    public void ClockCorrectionsDoNotChangeRunningCountdown()
    {
        var clock = new Clock(); var engine = new TimerEngine(new(), clock);
        engine.Add(TimeSpan.FromMinutes(5), "Tea"); clock.Advance(10); clock.Now = clock.Now.AddHours(-1);
        Assert.Equal(290, engine.GetRemaining(engine.State.Timer).TotalSeconds);
        clock.Now = clock.Now.AddHours(2); Assert.Equal(290, engine.GetRemaining(engine.State.Timer).TotalSeconds);
        engine.PrepareForSave();
        var restored = new TimerEngine(TimerEngine.Deserialize(JsonSerializer.Serialize(engine.State)), clock);
        Assert.Equal(290, restored.GetRemaining(restored.State.Timer).TotalSeconds);
    }
    [Fact]
    public void CalendarRecurrenceHandlesDstGapAndOverlap()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var spring = RecurrenceCalculator.Next(new(2026, 3, 7, 12, 0, 0, TimeSpan.FromHours(-5)), 2, 30, AlarmRepeat.Daily, timeZone: zone)!.Value;
        Assert.Equal(3, spring.Hour); Assert.Equal(0, spring.Minute); Assert.Equal(TimeSpan.FromHours(-4), spring.Offset);
        var fall = RecurrenceCalculator.Next(new(2026, 10, 31, 12, 0, 0, TimeSpan.FromHours(-4)), 1, 30, AlarmRepeat.Daily, timeZone: zone)!.Value;
        Assert.Equal(TimeSpan.FromHours(-4), fall.Offset);
        Assert.Equal(2, RecurrenceCalculator.Next(fall, 1, 30, AlarmRepeat.Daily, timeZone: zone)!.Value.Day);
    }
    [Theory]
    [InlineData(true, true, true, IslandActivity.Alarm)]
    [InlineData(false, true, true, IslandActivity.Timer)]
    [InlineData(false, false, true, IslandActivity.Q)]
    [InlineData(false, false, false, IslandActivity.Media)]
    public void UrgentActivitiesAndQOverridePin(bool alarm, bool done, bool q, IslandActivity expected) =>
        Assert.Equal(expected, ActivityPolicy.Select(alarm, done, q, IslandActivity.Media, true, true, true, true, true));
    [Fact]
    public void UnavailablePinFallsBack() => Assert.Equal(IslandActivity.Media, ActivityPolicy.Select(false, false, false, IslandActivity.Timer, false, false, true, false, false));
    [Fact]
    public void NotificationsBaselineThenDeliverEveryNewIdentityInOrder()
    {
        var tracker = new NotificationSnapshotTracker(); var now = DateTimeOffset.UtcNow;
        var existing = new NotificationInfo("A", "old", "", 100, now, "app-a");
        Assert.Empty(tracker.Observe([existing]));
        var first = existing with { Id = 3, Title = "first", CreatedAt = now.AddSeconds(1) };
        var second = existing with { Id = 2, Title = "second", CreatedAt = now.AddSeconds(2) };
        Assert.Equal(new[] { first, second }, tracker.Observe([second, existing, first, second]));
        Assert.Empty(tracker.Observe([first, second, existing]));
        var reused = first with { CreatedAt = now.AddSeconds(3) };
        Assert.Equal(new[] { reused }, tracker.Observe([reused, second]));
        tracker.Reset(); Assert.Empty(tracker.Observe([reused]));
    }
    [Fact]
    public void QueueGroupsByStableAppAndPreservesIndividualTargets()
    {
        var clock = new Clock(); var queue = new NotificationQueue(clock);
        var a = new NotificationHistoryItem(Guid.NewGuid(), "Same name", "A", "", clock.Now, AppId: "a");
        var b = a with { Id = Guid.NewGuid(), Title = "B", CreatedAt = clock.Now.AddSeconds(1) };
        var c = a with { Id = Guid.NewGuid(), AppId = "c" };
        queue.Enqueue([a, b, c]); Assert.Equal(2, queue.Count); Assert.Null(queue.Take(true));
        var group = queue.Take(false)!; Assert.Equal(new[] { a, b }, group.Items); Assert.Equal(b.Id, group.Latest.Id);
        Assert.Contains("(+1)", group.Title); Assert.Equal(c.Id, queue.Take(false)!.Latest.Id);
    }
    [Fact]
    public void DeferredGroupsCollapseToSummaryAndQueueIsBounded()
    {
        var clock = new Clock(); var queue = new NotificationQueue(clock);
        for (var i = 0; i < 55; i++) queue.Enqueue([new(Guid.NewGuid(), "A", i.ToString(), "", clock.Now)]);
        Assert.Equal(50, queue.Count); clock.Advance(31);
        var group = queue.Take(false)!; Assert.True(group.Summary); Assert.Equal(50, group.Items.Count); Assert.Equal(0, queue.Count);
        queue.Enqueue(group.Items); queue.Clear(); Assert.Null(queue.Take(false));
    }
    [Theory]
    [InlineData(1920, 1)]
    [InlineData(1366, 1.5)]
    [InlineData(1280, 2)]
    public void ViewportNeverExceedsWorkingArea(double pixels, double dpi)
    {
        var available = pixels / dpi;
        Assert.InRange(WindowSizingPolicy.BoundedDimension(1000, available), 1, available - 24);
        Assert.InRange(WindowSizingPolicy.WidgetViewport(available, true), 1, available);
        Assert.True(WindowSizingPolicy.WidgetWidth(available, 10, 132) >= 132);
    }
}
