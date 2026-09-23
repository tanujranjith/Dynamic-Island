using System.Runtime.CompilerServices;
using DynamicIsland.Q.Core;
using Xunit;

namespace DynamicIsland.Windows.Tests;

public sealed class QComparisonTests
{
    private static QComparisonTarget Target(string id) => new(id, id + "-model", id + "-key");
    private static Task<QScreenContext?> NoCapture(CancellationToken token) => Task.FromResult<QScreenContext?>(null);

    [Fact]
    public async Task StartsBothRequestsWithOneIdenticalCaptureAndIndependentCredentials()
    {
        var leftGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rightGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var left = new Provider("gemini", (_, t) => leftGate.Task.WaitAsync(t));
        var right = new Provider("openai", (_, t) => rightGate.Task.WaitAsync(t));
        using var comparison = new QComparisonController(new QProviderRegistry([left, right]));
        var context = new QScreenContext("Fixture", "fixture", 1, 1, "same screen", [1], DateTimeOffset.UtcNow);
        var captures = 0;
        var run = comparison.SubmitAsync("same question", QMode.Ask, Target("gemini"), Target("openai"),
            _ => { captures++; return Task.FromResult<QScreenContext?>(context); }, true);
        await Task.WhenAll(left.Started.Task, right.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, captures);
        Assert.Same(context, left.Requests[0].ScreenContext); Assert.Same(context, right.Requests[0].ScreenContext);
        Assert.Equal("gemini-key", left.Credentials[0]); Assert.Equal("openai-key", right.Credentials[0]);
        Assert.Equal(left.Requests[0].Prompt, right.Requests[0].Prompt);
        Assert.True(left.Requests[0].IncludeImage && right.Requests[0].IncludeImage);
        leftGate.SetResult("left answer");
        await left.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(comparison.Snapshot.IsBusy);
        rightGate.SetResult("right answer"); await run;
        Assert.Equal("left answer", comparison.Snapshot.Left.Response);
        Assert.Equal("right answer", comparison.Snapshot.Right.Response);
        Assert.False(comparison.Snapshot.IsBusy);
    }

    [Fact]
    public async Task FailedProviderDoesNotDiscardTheOtherAnswer()
    {
        var failed = new Provider("gemini", (_, _) => Task.FromException<string>(new InvalidOperationException("Missing key")));
        var good = new Provider("openai", (_, _) => Task.FromResult("success"));
        using var comparison = new QComparisonController(new QProviderRegistry([failed, good]));
        await comparison.SubmitAsync("hello", QMode.Ask, Target("gemini"), Target("openai"), NoCapture, false);
        Assert.Equal(QRunState.Error, comparison.Snapshot.Left.State);
        Assert.Equal("success", comparison.Snapshot.Right.Response);
        Assert.Equal(QRunState.Complete, comparison.Snapshot.Right.State);
    }

    [Fact]
    public async Task FollowUpsHaveSeparateHistoriesAndChangedSlotStartsFresh()
    {
        var left = new Provider("gemini", (_, _) => Task.FromResult("gemini answer"));
        var right = new Provider("openai", (_, _) => Task.FromResult("openai answer"));
        using var comparison = new QComparisonController(new QProviderRegistry([left, right]));
        await comparison.SubmitAsync("first", QMode.Ask, Target("gemini"), Target("openai"), NoCapture, false);
        await comparison.SubmitAsync("follow-up", QMode.Ask, Target("gemini"), Target("openai"), NoCapture, false);
        Assert.Contains(left.Requests[1].History, h => h.Content == "gemini answer");
        Assert.DoesNotContain(left.Requests[1].History, h => h.Content == "openai answer");
        Assert.Contains(right.Requests[1].History, h => h.Content == "openai answer");
        await comparison.SubmitAsync("new model", QMode.Ask, Target("gemini") with { Model = "other" }, Target("openai"), NoCapture, false);
        Assert.Empty(left.Requests[2].History); Assert.NotEmpty(right.Requests[2].History);
    }

    [Fact]
    public async Task ClearDuringCaptureDoesNotSendRequestsOrReopenResults()
    {
        var gate = new TaskCompletionSource<QScreenContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider("gemini", (_, _) => Task.FromResult("late"));
        using var comparison = new QComparisonController(new QProviderRegistry([provider]));
        var run = comparison.SubmitAsync("hello", QMode.Ask, Target("gemini"), Target("openai"), _ => gate.Task, false);
        comparison.Clear(); gate.SetResult(null); await run;
        Assert.Empty(provider.Requests); Assert.Equal(QRunState.Idle, comparison.Snapshot.State);
    }

    [Fact]
    public async Task ClearSuppressesLateProviderChunks()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var left = new Provider("gemini", (_, _) => gate.Task);
        var right = new Provider("openai", (_, _) => gate.Task);
        using var comparison = new QComparisonController(new QProviderRegistry([left, right]));
        var run = comparison.SubmitAsync("old", QMode.Ask, Target("gemini"), Target("openai"), NoCapture, false);
        await Task.WhenAll(left.Started.Task, right.Started.Task);
        comparison.Clear(); gate.SetResult("old answer"); await run;
        Assert.Equal(QRunState.Idle, comparison.Snapshot.State);
        Assert.Empty(comparison.Snapshot.Left.Response); Assert.Empty(comparison.Snapshot.Right.Response);
    }

    [Fact]
    public async Task RetryOnlyRunsSelectedSlotAndHonorsDisabledImages()
    {
        var left = new Provider("gemini", (_, _) => Task.FromResult("left"));
        var right = new Provider("openai", (_, _) => Task.FromResult("right"));
        using var comparison = new QComparisonController(new QProviderRegistry([left, right]));
        await comparison.SubmitAsync("hello", QMode.Ask, Target("gemini"), Target("openai"),
            _ => Task.FromResult<QScreenContext?>(new("fixture", "fixture", 1, 1, "", [1], DateTimeOffset.UtcNow)), false);
        await comparison.RetryAsync(true, Target("openai"), includeImage: false);
        Assert.Single(left.Requests); Assert.Equal(2, right.Requests.Count);
        Assert.All(right.Requests, r => Assert.False(r.IncludeImage));
        Assert.Equal("left", comparison.Snapshot.Left.Response);
    }

    [Fact]
    public async Task CancelStopsBothProviders()
    {
        static async Task<string> Slow(QRequest request, CancellationToken token) { await Task.Delay(Timeout.Infinite, token); return "never"; }
        var left = new Provider("gemini", Slow); var right = new Provider("openai", Slow);
        using var comparison = new QComparisonController(new QProviderRegistry([left, right]));
        var run = comparison.SubmitAsync("hello", QMode.Ask, Target("gemini"), Target("openai"), NoCapture, false);
        await Task.WhenAll(left.Started.Task, right.Started.Task);
        comparison.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(comparison.Snapshot.IsBusy);
        Assert.Equal(QRunState.Cancelled, comparison.Snapshot.Left.State);
        Assert.Equal(QRunState.Cancelled, comparison.Snapshot.Right.State);
    }

    private sealed class Provider(string id, Func<QRequest, CancellationToken, Task<string>> respond) : IQProvider
    {
        public QProviderInfo Info { get; } = new(id, id, QProviderCapabilities.Text | QProviderCapabilities.Images, id + "-model");
        public List<QRequest> Requests { get; } = [];
        public List<string?> Credentials { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<QModelInfo>> GetModelsAsync(string? credential, CancellationToken cancellationToken, string? baseUrl = null) => Task.FromResult<IReadOnlyList<QModelInfo>>([]);
        public async IAsyncEnumerable<QStreamEvent> StreamAsync(QRequest request, string? credential, string? baseUrl,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request); Credentials.Add(credential); Started.TrySetResult();
            yield return new QStreamEvent.Started();
            yield return new QStreamEvent.Text(await respond(request, cancellationToken));
            Finished.TrySetResult();
        }
    }
}
