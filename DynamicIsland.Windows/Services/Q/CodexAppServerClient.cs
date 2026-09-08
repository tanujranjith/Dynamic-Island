using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
namespace DynamicIsland.Windows.Services.Q;

/// <summary>JSON-RPC client for the official Codex app-server. OAuth credentials remain owned by Codex.</summary>
public sealed class CodexAppServerClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly string _clientVersion;
    private readonly CodexRuntimeResolver _runtimeResolver;
    private readonly CodexThreadLedger _threadLedger;
    private readonly LoggingService? _log;
    private Process? _process;
    private StreamWriter? _writer;
    private Task? _readerTask;
    private int _nextId;
    private bool _initialized;
    private bool _disposed;
    private Action<JsonElement>? _notification;

    public CodexAppServerClient(string clientVersion = "1.0.6", CodexRuntimeResolver? runtimeResolver = null,
        LoggingService? log = null, CodexThreadLedger? threadLedger = null)
    {
        _clientVersion = clientVersion;
        _runtimeResolver = runtimeResolver ?? new CodexRuntimeResolver();
        _threadLedger = threadLedger ?? new CodexThreadLedger();
        _log = log;
    }

    public bool IsRunning => _process is { HasExited: false };
    public CodexRuntimeInfo? RuntimeInfo { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Infrastructure.AppDataPaths.IsPreview) throw new InvalidOperationException("Codex is disabled in the isolated preview.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized && IsRunning) return;
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized && IsRunning) return;
            StopProcess();
            var runtime = await ValidateRuntimeAsync(_runtimeResolver.Resolve(), cancellationToken).ConfigureAwait(false);
            RuntimeInfo = runtime;
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = runtime.ExecutablePath,
                    Arguments = "app-server --listen stdio://",
                    WorkingDirectory = Path.GetDirectoryName(runtime.ExecutablePath) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };
            if (!process.Start()) throw new CodexAppServerException(CodexFailureKind.ServerExited, "Unable to start the Codex service.");
            process.Exited += (_, _) => OnProcessExited();
            lock (_gate)
            {
                _process = process;
                _writer = process.StandardInput;
                _readerTask = ReadLoopAsync(process.StandardOutput, process.StandardError);
            }
            try
            {
                await SendRequestCoreAsync("initialize", new
                {
                    clientInfo = new { name = "dynamic_island", title = "Dynamic Island", version = _clientVersion }
                }, cancellationToken).ConfigureAwait(false);
                await SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
                _initialized = true;
                _log?.Info($"Codex service started: source={runtime.Source}, version={runtime.Version ?? "unknown"}");
                await CleanupOwnedThreadsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch { StopProcess(); throw; }
        }
        finally { _startGate.Release(); }
    }

    public async Task<CodexAccount?> ReadAccountAsync(bool refreshToken = false, CancellationToken cancellationToken = default)
    {
        var result = await SendRequestAsync("account/read", new { refreshToken }, cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object) return null;
        return new CodexAccount(String(account, "email"), String(account, "planType"), String(account, "type") ?? "unknown");
    }

    public async Task<CodexDeviceLogin> StartDeviceLoginAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendRequestAsync("account/login/start", new { type = "chatgptDeviceCode" }, cancellationToken).ConfigureAwait(false);
        return new CodexDeviceLogin(
            String(result, "loginId") ?? throw new CodexAppServerException(CodexFailureKind.Protocol, "Codex did not return a login id."),
            String(result, "verificationUrl") ?? throw new CodexAppServerException(CodexFailureKind.Protocol, "Codex did not return a verification URL."),
            String(result, "userCode") ?? throw new CodexAppServerException(CodexFailureKind.Protocol, "Codex did not return a device code."));
    }

    public async Task<CodexAccount> WaitForLoginAsync(string loginId, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<CodexAccount>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<JsonElement>? handler = null;
        handler = message =>
        {
            if (!string.Equals(String(message, "method"), "account/login/completed", StringComparison.Ordinal)) return;
            var parameters = message.TryGetProperty("params", out var value) ? value : default;
            if (!string.Equals(String(parameters, "loginId"), loginId, StringComparison.Ordinal)) return;
            if (parameters.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
                _ = CompleteAccountAsync(completion, cancellationToken);
            else completion.TrySetException(new CodexAppServerException(CodexFailureKind.SignedOut,
                String(parameters, "error") ?? "ChatGPT sign-in was not completed."));
        };
        AddNotificationHandler(handler);
        try
        {
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.ConfigureAwait(false);
        }
        finally { RemoveNotificationHandler(handler); }
    }

    public async Task CancelLoginAsync(string loginId, CancellationToken cancellationToken = default) =>
        _ = await SendRequestAsync("account/login/cancel", new { loginId }, cancellationToken).ConfigureAwait(false);

    public async Task LogoutAsync(CancellationToken cancellationToken = default) =>
        _ = await SendRequestAsync("account/logout", new { }, cancellationToken).ConfigureAwait(false);

    private async Task CompleteAccountAsync(TaskCompletionSource<CodexAccount> completion, CancellationToken cancellationToken)
    {
        try
        {
            completion.TrySetResult(await ReadAccountAsync(true, cancellationToken).ConfigureAwait(false)
                ?? throw new CodexAppServerException(CodexFailureKind.SignedOut, "Sign-in completed without an account."));
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    public async Task<IReadOnlyList<CodexModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendRequestAsync("model/list", new { limit = 100, includeHidden = false }, cancellationToken).ConfigureAwait(false);
        return CodexModelCatalogParser.Parse(result);
    }

    public async Task<CodexRateLimit?> ReadRateLimitAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendRequestAsync("account/rateLimits/read", new { }, cancellationToken).ConfigureAwait(false);
        var limits = result.TryGetProperty("rateLimits", out var value) ? value : default;
        if (limits.ValueKind != JsonValueKind.Object) return null;
        var primary = limits.TryGetProperty("primary", out var item) ? item : default;
        return new CodexRateLimit(String(limits, "limitId"),
            primary.TryGetProperty("usedPercent", out var used) && used.TryGetInt32(out var percent) ? percent : null,
            primary.TryGetProperty("resetsAt", out var reset) && reset.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null);
    }

    public async IAsyncEnumerable<string> StreamTurnAsync(string model, string? reasoningEffort, string prompt, byte[]? image,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var workspace = Path.Combine(DynamicIsland.Windows.Infrastructure.AppDataPaths.Root, "CodexWorkspace");
        Directory.CreateDirectory(workspace);
        var thread = await SendRequestAsync("thread/start",
            CodexTurnStartRequest.CreateThread(model, workspace), cancellationToken).ConfigureAwait(false);
        var threadId = String(thread.GetProperty("thread"), "id")
            ?? throw new CodexAppServerException(CodexFailureKind.Protocol, "Codex did not return a thread id.");
        _threadLedger.Add(threadId);
        var input = new List<object> { new { type = "text", text = prompt } };
        string? tempImage = null;
        if (image is { Length: > 0 })
        {
            tempImage = Path.Combine(Path.GetTempPath(), $"dynamic-island-codex-{Guid.NewGuid():N}.png");
            await File.WriteAllBytesAsync(tempImage, image, cancellationToken).ConfigureAwait(false);
            input.Add(new { type = "localImage", path = tempImage });
        }

        var channel = Channel.CreateUnbounded<string>();
        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accumulator = new CodexTurnNotificationAccumulator(threadId);
        string? turnId = null;
        Action<JsonElement>? handler = message =>
        {
            try
            {
                var update = accumulator.Process(message);
                if (update is null) return;
                if (update.Kind == CodexTurnUpdateKind.Text && !string.IsNullOrEmpty(update.Value)) channel.Writer.TryWrite(update.Value);
                else if (update.Kind == CodexTurnUpdateKind.Completed) { channel.Writer.TryComplete(); completed.TrySetResult(null); }
                else if (update.Kind == CodexTurnUpdateKind.Failed)
                {
                    channel.Writer.TryComplete();
                    var kind = CodexErrorClassifier.Classify(update.Value);
                    completed.TrySetResult(new CodexAppServerException(kind, CodexErrorClassifier.UserMessage(kind, update.Value)));
                }
            }
            catch (Exception ex) { channel.Writer.TryComplete(); completed.TrySetResult(ex); }
        };
        AddNotificationHandler(handler);
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            var activeTurnId = turnId;
            if (activeTurnId is not null) _ = TryInterruptAsync(threadId, activeTurnId);
        });
        try
        {
            var started = await SendRequestAsync("turn/start", CodexTurnStartRequest.Create(threadId, input, model, reasoningEffort), cancellationToken).ConfigureAwait(false);
            turnId = started.TryGetProperty("turn", out var turn) ? String(turn, "id") : null;
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                while (channel.Reader.TryRead(out var text)) yield return text;
            var failure = await completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (failure is not null) throw failure;
            _log?.Info($"Codex turn completed: model={model}, effort={reasoningEffort ?? "auto"}, outcome=completed");
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
                _log?.Info($"Codex turn completed: model={model}, effort={reasoningEffort ?? "auto"}, outcome=cancelled");
            RemoveNotificationHandler(handler);
            if (tempImage is not null) try { File.Delete(tempImage); } catch { }
            await TryDeleteThreadAsync(threadId).ConfigureAwait(false);
        }
    }

    public async Task InterruptTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default) =>
        _ = await SendRequestAsync("turn/interrupt", new { threadId, turnId }, cancellationToken).ConfigureAwait(false);

    public async Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        _ = await SendRequestAsync("thread/delete", new { threadId }, cancellationToken).ConfigureAwait(false);
        _threadLedger.Remove(threadId);
    }

    private async Task TryInterruptAsync(string threadId, string turnId)
    {
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); _ = await SendRequestAsync("turn/interrupt", new { threadId, turnId }, timeout.Token).ConfigureAwait(false); }
        catch { }
    }

    private async Task TryDeleteThreadAsync(string threadId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _ = await SendRequestAsync("thread/delete", new { threadId }, timeout.Token).ConfigureAwait(false);
            _threadLedger.Remove(threadId);
        }
        catch { }
    }

    private async Task CleanupOwnedThreadsAsync(CancellationToken cancellationToken)
    {
        foreach (var threadId in _threadLedger.Snapshot())
        {
            try { _ = await SendRequestCoreAsync("thread/delete", new { threadId }, cancellationToken).ConfigureAwait(false); _threadLedger.Remove(threadId); }
            catch { }
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return await SendRequestCoreAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendRequestCoreAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            await WriteAsync(JsonSerializer.Serialize(new { method, id, @params = parameters }), timeout.Token).ConfigureAwait(false);
            using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
            var response = await completion.Task.ConfigureAwait(false);
            if (response.TryGetProperty("error", out var error)) throw CodexErrorClassifier.FromProtocol(method, error);
            return response.TryGetProperty("result", out var value) ? value : response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexAppServerException(CodexFailureKind.Network, "Codex did not respond in time.", method);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken) =>
        WriteAsync(JsonSerializer.Serialize(new { method, @params = parameters }), cancellationToken);

    private async Task WriteAsync(string line, CancellationToken cancellationToken)
    {
        StreamWriter writer;
        lock (_gate) writer = _writer ?? throw new CodexAppServerException(CodexFailureKind.ServerExited, "The Codex service is not running.");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false); await writer.FlushAsync(cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private async Task ReadLoopAsync(StreamReader output, StreamReader error)
    {
        _ = Task.Run(async () => { while (await error.ReadLineAsync().ConfigureAwait(false) is not null) { } });
        try
        {
            while (await output.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement.Clone();
                if (root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var serverRequestId))
                {
                    await DeclineServerRequestAsync(serverRequestId, String(root, "method") ?? string.Empty).ConfigureAwait(false);
                    continue;
                }
                if (root.TryGetProperty("id", out var id) && id.TryGetInt32(out var requestId) && _pending.TryRemove(requestId, out var completion)) completion.TrySetResult(root);
                else _notification?.Invoke(root);
            }
            throw new CodexAppServerException(CodexFailureKind.ServerExited, "The Codex service stopped unexpectedly.");
        }
        catch (Exception ex)
        {
            var failure = ex as CodexAppServerException ?? new CodexAppServerException(CodexFailureKind.ServerExited, "The Codex service stopped unexpectedly.", innerException: ex);
            foreach (var pending in _pending.Values) pending.TrySetException(failure);
        }
    }

    private Task DeclineServerRequestAsync(JsonElement id, string method)
    {
        object response = method.EndsWith("requestApproval", StringComparison.Ordinal)
            ? new { id, result = new { decision = "decline" } }
            : new { id, error = new { code = -32601, message = "Dynamic Island Q does not enable tools or interactive requests." } };
        _log?.Info($"Codex server request declined: method={method}");
        return WriteAsync(JsonSerializer.Serialize(response), CancellationToken.None);
    }

    private async Task<CodexRuntimeInfo> ValidateRuntimeAsync(CodexRuntimeInfo runtime, CancellationToken cancellationToken)
    {
        if (runtime.Version is not null && Version.TryParse(runtime.Version, out var manifestVersion))
        {
            if (manifestVersion < CodexRuntimeResolver.MinimumSupportedVersion) throw new CodexAppServerException(CodexFailureKind.RuntimeTooOld, CodexErrorClassifier.UserMessage(CodexFailureKind.RuntimeTooOld));
            return runtime;
        }
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = runtime.ExecutablePath, Arguments = "--version", UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            }
        };
        if (!process.Start()) throw new CodexAppServerException(CodexFailureKind.RuntimeInvalid, "Codex could not be started.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var token = output.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (!Version.TryParse(token, out var version)) throw new CodexAppServerException(CodexFailureKind.RuntimeInvalid, "The Codex version could not be verified.");
        if (version < CodexRuntimeResolver.MinimumSupportedVersion) throw new CodexAppServerException(CodexFailureKind.RuntimeTooOld, CodexErrorClassifier.UserMessage(CodexFailureKind.RuntimeTooOld));
        return runtime with { Version = version.ToString(), IsValidated = true };
    }

    private void OnProcessExited()
    {
        _initialized = false;
        var failure = new CodexAppServerException(CodexFailureKind.ServerExited, "The Codex service stopped unexpectedly.");
        foreach (var pending in _pending.Values) pending.TrySetException(failure);
        _log?.Error("Codex service exited: category=ServerExited");
    }

    private void AddNotificationHandler(Action<JsonElement> handler) { lock (_gate) _notification += handler; }
    private void RemoveNotificationHandler(Action<JsonElement> handler) { lock (_gate) _notification -= handler; }
    private static string? String(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private void StopProcess()
    {
        Process? process;
        lock (_gate) { process = _process; _process = null; _writer = null; _initialized = false; }
        if (process is not null && !process.HasExited) try { process.Kill(true); } catch { }
        process?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        StopProcess();
        if (_readerTask is not null) try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _startGate.Dispose();
        _writeGate.Dispose();
    }
}
