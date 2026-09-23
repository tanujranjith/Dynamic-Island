namespace DynamicIsland.Q.Core;

public sealed class QSessionController(IQProviderRegistry providers) : IQSessionController
{
    private readonly object _gate = new();
    private CancellationTokenSource? _activeCts;
    private long _generation;
    private readonly List<QMessage> _history = [];
    private QSessionSnapshot _snapshot = new(QRunState.Idle, QMode.Ask, string.Empty, string.Empty, "Ready", null, null, "", "");

    public QSessionSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public event Action<QSessionSnapshot>? Changed;

    public Task BeginAsync(QMode mode, string providerId, string model, QScreenContext? context, CancellationToken cancellationToken = default)
    {
        Cancel();
        Publish(_snapshot with
        {
            State = QRunState.Ready,
            Mode = mode,
            Prompt = string.Empty,
            Response = string.Empty,
            Status = context is null ? "Ready for a question" : $"Reading {context.WindowTitle}",
            Error = null,
            Context = context,
            ProviderId = providerId,
            Model = model
        });
        return Task.CompletedTask;
    }

    public async Task SubmitAsync(string prompt, QMode mode, string providerId, string model, string? credential, string? baseUrl,
        bool includeImage, Func<CancellationToken, Task<QScreenContext?>>? recapture = null,
        CancellationToken cancellationToken = default, int maxResponseTokens = 8192, string? customSystemPrompt = null,
        string reasoningEffort = "auto")
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        Cancel();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long generation;
        lock (_gate) { _activeCts = linked; generation = ++_generation; }
        void Emit(QSessionSnapshot value)
        {
            lock (_gate) { if (generation != _generation) return; _snapshot = value; }
            Changed?.Invoke(value);
        }
        var token = linked.Token;
        try
        {
            var provider = providers.Find(providerId);
            if (provider is null) throw new InvalidOperationException($"Provider '{providerId}' is not available.");

            var context = Snapshot.Context;
            if (recapture is not null)
            {
                Emit(Snapshot with { State = QRunState.Capturing, Prompt = prompt, Mode = mode, Error = null, Response = string.Empty, ProviderId = providerId, Model = model, Status = "Reading active window…" });
                context = await recapture(token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            Emit(Snapshot with { State = QRunState.Thinking, Prompt = prompt, Mode = mode, Context = context, Response = string.Empty, Error = null, ProviderId = providerId, Model = model, Status = "Thinking…" });
            QMessage[] history;
            lock (_gate) history = _history.ToArray();
            var request = new QRequest(mode, prompt, context, history, model,
                includeImage && context?.HasImage == true && provider.Info.Capabilities.HasFlag(QProviderCapabilities.Images),
                Math.Clamp(maxResponseTokens, 2048, 32768), customSystemPrompt, reasoningEffort);
            var response = new System.Text.StringBuilder();
            await foreach (var item in provider.StreamAsync(request, credential, baseUrl, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                switch (item)
                {
                    case QStreamEvent.Started:
                        Emit(Snapshot with { State = QRunState.Streaming, Status = "Q is responding…" });
                        break;
                    case QStreamEvent.Text text:
                        response.Append(text.Value);
                        Emit(Snapshot with { State = QRunState.Streaming, Response = response.ToString(), Status = "Q is responding…" });
                        break;
                    case QStreamEvent.Failed failed:
                        throw failed.Exception ?? new InvalidOperationException(failed.Message);
                    case QStreamEvent.Completed:
                        break;
                }
            }

            var answer = response.ToString().Trim();
            if (answer.Length == 0)
                throw new InvalidOperationException("The provider returned an empty response. Retry the question or switch models.");

            lock (_gate)
            {
                if (generation != _generation) return;
                _history.Add(new QMessage("user", prompt));
                _history.Add(new QMessage("assistant", answer));
                while (_history.Count > 8) _history.RemoveAt(0);
            }
            Emit(Snapshot with { State = QRunState.Complete, Response = answer, Status = "Complete", Error = null });
        }
        catch (OperationCanceledException)
        {
            Emit(Snapshot with { State = QRunState.Cancelled, Status = "Cancelled" });
        }
        catch (Exception ex)
        {
            Emit(Snapshot with { State = QRunState.Error, Status = "Q could not respond", Error = ex.Message });
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_activeCts, linked)) _activeCts = null;
            linked.Dispose();
        }
    }

    public void Cancel()
    {
        QSessionSnapshot? cancelled = null;
        lock (_gate)
        {
            ++_generation;
            if (_activeCts is { } active)
            {
                _activeCts = null;
                active.Cancel();
                _snapshot = cancelled = _snapshot with { State = QRunState.Cancelled, Status = "Cancelled" };
            }
        }
        if (cancelled is not null) Changed?.Invoke(cancelled);
    }

    public void Clear()
    {
        Cancel();
        lock (_gate) _history.Clear();
        Publish(new QSessionSnapshot(QRunState.Idle, QMode.Ask, string.Empty, string.Empty, "Ready", null, null, "", ""));
    }

    private void Publish(QSessionSnapshot snapshot)
    {
        lock (_gate) _snapshot = snapshot;
        Changed?.Invoke(snapshot);
    }

    public void Dispose() => Clear();
}
