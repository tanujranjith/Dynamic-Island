namespace DynamicIsland.Q.Core;

public sealed record QComparisonTarget(string ProviderId, string Model, string? Credential,
    string ReasoningEffort = "auto", string? BaseUrl = null);

public sealed record QComparisonSnapshot(QSessionSnapshot Left, QSessionSnapshot Right)
{
    public static bool Busy(QRunState state) => state is QRunState.Capturing or QRunState.Listening or QRunState.Thinking or QRunState.Streaming;
    public bool IsBusy => Busy(Left.State) || Busy(Right.State);
    public QRunState State => IsBusy ? (Left.State == QRunState.Capturing || Right.State == QRunState.Capturing ? QRunState.Capturing : QRunState.Streaming)
        : Left.State == QRunState.Error || Right.State == QRunState.Error ? QRunState.Error
        : Left.State == QRunState.Complete || Right.State == QRunState.Complete ? QRunState.Complete
        : Left.State == QRunState.Cancelled || Right.State == QRunState.Cancelled ? QRunState.Cancelled
        : Left.State == QRunState.Ready || Right.State == QRunState.Ready ? QRunState.Ready : QRunState.Idle;
}

/// <summary>Two independent conversations sharing one capture per prompt, never each other's answers or credentials.</summary>
public sealed class QComparisonController : IDisposable
{
    private readonly QSessionController _left;
    private readonly QSessionController _right;
    private readonly object _gate = new();
    private CancellationTokenSource? _active;
    private long _generation;
    private string? _leftIdentity, _rightIdentity;
    private QComparisonSnapshot _snapshot;
    public QComparisonSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public event Action<QComparisonSnapshot>? Changed;

    public QComparisonController(IQProviderRegistry providers)
    {
        _left = new(providers); _right = new(providers);
        _snapshot = new(_left.Snapshot, _right.Snapshot);
        _left.Changed += LeftChanged; _right.Changed += RightChanged;
    }
    private void LeftChanged(QSessionSnapshot value) => Update(value, null);
    private void RightChanged(QSessionSnapshot value) => Update(null, value);
    private void Update(QSessionSnapshot? left, QSessionSnapshot? right)
    {
        QComparisonSnapshot snapshot;
        lock (_gate) snapshot = _snapshot = new(left ?? _snapshot.Left, right ?? _snapshot.Right);
        Changed?.Invoke(snapshot);
    }
    public async Task SubmitAsync(string prompt, QMode mode, QComparisonTarget left, QComparisonTarget right,
        Func<CancellationToken, Task<QScreenContext?>> capture, bool includeImage,
        CancellationToken cancellationToken = default, int maxTokens = 8192, string? systemPrompt = null)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        Cancel();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long generation;
        lock (_gate) { _active = linked; generation = ++_generation; }
        // Changing a slot's provider/model starts a fresh conversation for that slot.
        if (_leftIdentity != left.ProviderId + "/" + left.Model) { _left.Clear(); _leftIdentity = left.ProviderId + "/" + left.Model; }
        if (_rightIdentity != right.ProviderId + "/" + right.Model) { _right.Clear(); _rightIdentity = right.ProviderId + "/" + right.Model; }
        QSessionSnapshot Reading(QComparisonTarget target) => new(QRunState.Capturing, mode, prompt, "", "Reading…", null, null, target.ProviderId, target.Model);
        Update(Reading(left), Reading(right));
        try
        {
            var context = await capture(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            lock (_gate) if (generation != _generation) return;
            await _left.BeginAsync(mode, left.ProviderId, left.Model, context).ConfigureAwait(false);
            await _right.BeginAsync(mode, right.ProviderId, right.Model, context).ConfigureAwait(false);
            await Task.WhenAll(
                _left.SubmitAsync(prompt, mode, left.ProviderId, left.Model, left.Credential, left.BaseUrl, includeImage,
                    cancellationToken: linked.Token, maxResponseTokens: maxTokens, customSystemPrompt: systemPrompt, reasoningEffort: left.ReasoningEffort),
                _right.SubmitAsync(prompt, mode, right.ProviderId, right.Model, right.Credential, right.BaseUrl, includeImage,
                    cancellationToken: linked.Token, maxResponseTokens: maxTokens, customSystemPrompt: systemPrompt, reasoningEffort: right.ReasoningEffort)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate) if (generation != _generation) return;
            Update(Snapshot.Left with { State = QRunState.Cancelled, Status = "Cancelled" }, Snapshot.Right with { State = QRunState.Cancelled, Status = "Cancelled" });
        }
        catch (Exception ex)
        {
            lock (_gate) if (generation != _generation) return;
            Update(Snapshot.Left with { State = QRunState.Error, Error = ex.Message }, Snapshot.Right with { State = QRunState.Error, Error = ex.Message });
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_active, linked)) _active = null;
            linked.Dispose();
        }
    }
    public void Cancel()
    {
        lock (_gate) { ++_generation; _active?.Cancel(); _active = null; }
        _left.Cancel(); _right.Cancel();
        var state = Snapshot;
        if (state.IsBusy) Update(
            QComparisonSnapshot.Busy(state.Left.State) ? state.Left with { State = QRunState.Cancelled, Status = "Cancelled" } : state.Left,
            QComparisonSnapshot.Busy(state.Right.State) ? state.Right with { State = QRunState.Cancelled, Status = "Cancelled" } : state.Right);
    }
    public Task RetryAsync(bool right, QComparisonTarget target, CancellationToken token = default, int maxTokens = 8192, string? systemPrompt = null, bool includeImage = true)
    {
        if (Snapshot.IsBusy) return Task.CompletedTask;
        var session = right ? _right : _left;
        var previous = session.Snapshot;
        return session.SubmitAsync(previous.Prompt, previous.Mode, target.ProviderId, target.Model, target.Credential, target.BaseUrl,
            includeImage, cancellationToken: token, maxResponseTokens: maxTokens,
            customSystemPrompt: systemPrompt, reasoningEffort: target.ReasoningEffort);
    }
    public void Clear() { Cancel(); _left.Clear(); _right.Clear(); _leftIdentity = _rightIdentity = null; }
    public void Dispose() { Clear(); _left.Changed -= LeftChanged; _right.Changed -= RightChanged; _left.Dispose(); _right.Dispose(); }
}
