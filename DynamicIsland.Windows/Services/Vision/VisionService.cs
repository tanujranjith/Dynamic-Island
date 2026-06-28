using DynamicIsland.Windows.Models;

namespace DynamicIsland.Windows.Services.Vision;

public enum EnrollPhase { Searching, Capturing, Completed, Failed }
public readonly record struct EnrollProgress(int Captured, int Target, EnrollPhase Phase);

/// <summary>
/// Background webcam monitor. Modeled on <see cref="AudioSessionService"/>: a single dedicated thread
/// reads frames, runs detection, and raises <see cref="Changed"/> with an immutable <see cref="VisionState"/>
/// only when it differs. The camera is opened only while running and released on <see cref="Stop"/>, so the
/// feature toggle in Settings is a true on/off switch (and the camera LED goes dark when off).
/// </summary>
public sealed class VisionService(LoggingService log, VisionModelManager models) : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource _shutdown = new();
    private Thread? _thread;

    private volatile bool _privacy;
    private volatile int _fps = 7;
    private volatile int _cameraIndex;
    private float _threshold = 0.363f;
    private float[]? _ownerEmbedding;
    private EnrollSession? _enroll;
    private int _previewClients;

    public event EventHandler<VisionState>? Changed;
    public event EventHandler<byte[]>? FrameReady;
    public event EventHandler<EnrollProgress>? EnrollProgressChanged;
    public VisionState Current { get; private set; } = VisionState.Disabled;
    public bool IsRunning => _thread is not null;
    public bool IsEnrolled => _ownerEmbedding is not null;

    // Reference-counted so the expanded island and the camera window can each ask for live preview
    // frames independently; encoding only happens while at least one wants them.
    public void RetainPreview() => Interlocked.Increment(ref _previewClients);
    public void ReleasePreview() { if (Interlocked.Decrement(ref _previewClients) < 0) Interlocked.Exchange(ref _previewClients, 0); }

    public void Configure(bool privacy, int fps, int cameraIndex, double threshold)
    {
        _privacy = privacy;
        _fps = Math.Clamp(fps, 3, 15);
        _cameraIndex = Math.Max(0, cameraIndex);
        _threshold = (float)Math.Clamp(threshold, 0.2, 0.6);
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_thread is not null) return;
            _ownerEmbedding = models.LoadOwnerEmbedding();
            if (_shutdown.IsCancellationRequested) _shutdown = new CancellationTokenSource();
            _thread = new Thread(Loop) { IsBackground = true, Name = "DynamicIsland.Vision" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_lock)
        {
            if (_thread is null) return;
            _shutdown.Cancel();
            thread = _thread;
            _thread = null;
        }
        thread.Join(TimeSpan.FromSeconds(2));
        lock (_lock) { _shutdown = new CancellationTokenSource(); }
        Publish(VisionState.Disabled);
    }

    /// <summary>
    /// Requests owner enrollment. The running loop captures several frames, averages the embeddings,
    /// persists them, then completes the returned task. Requires the camera to be running.
    /// </summary>
    public Task<bool> EnrollAsync()
    {
        if (!IsRunning) return Task.FromResult(false);
        var session = new EnrollSession();
        lock (_lock) { _enroll = session; }
        return session.Completion.Task;
    }

    public void CancelEnroll()
    {
        lock (_lock) { if (_enroll is not null) _enroll.Cancelled = true; }
    }

    public void RemoveEnrollment()
    {
        models.DeleteOwnerEmbedding();
        _ownerEmbedding = null;
    }

    private void Loop()
    {
        var token = _shutdown.Token;
        Publish(Current with { Availability = VisionAvailability.Initializing, PrivacyOn = _privacy, Enrolled = IsEnrolled });

        VisionDetector? detector = null;
        try
        {
            detector = new VisionDetector(log, _cameraIndex);
            if (!detector.Initialize(models.Resolve()))
            {
                Publish(new VisionState { Availability = VisionAvailability.CameraError, PrivacyOn = _privacy, Enrolled = IsEnrolled });
                return;
            }

            // The preview runs smoothly (~22 fps) while detection is throttled to _fps — person/face
            // detection is far more expensive than grabbing a frame, so this keeps the feed fluid and the
            // CPU cost reasonable.
            const int previewFps = 22;
            long lastDetect = 0;
            while (!token.IsCancellationRequested)
            {
                var enroll = TakeEnroll();
                if (enroll is not null) { RunEnroll(detector, enroll); lastDetect = Environment.TickCount64; continue; }

                var detInterval = Math.Max(60, 1000 / Math.Max(1, _fps));
                var wantPreview = Volatile.Read(ref _previewClients) > 0;
                var now = Environment.TickCount64;
                var runDetection = now - lastDetect >= detInterval;
                if (runDetection) lastDetect = now;

                var result = detector.Process(_ownerEmbedding, _threshold, runDetection, wantPreview);
                if (result is { } r)
                {
                    if (r.Preview is not null) FrameReady?.Invoke(this, r.Preview);
                    if (runDetection)
                        Publish(new VisionState
                        {
                            Availability = VisionAvailability.Running,
                            DetectorReady = r.DetectorReady,
                            PrivacyOn = _privacy,
                            Enrolled = IsEnrolled,
                            Enrolling = _enroll is not null,
                            PeopleCount = r.PeopleCount,
                            OwnerSignature = VisionState.BuildSignature(r.OwnerIndexes),
                            LastFrameUtc = DateTimeOffset.UtcNow
                        });
                }

                token.WaitHandle.WaitOne(wantPreview ? Math.Max(20, 1000 / previewFps) : detInterval);
            }
        }
        catch (Exception ex)
        {
            log.Error("Vision loop crashed", ex);
            Publish(new VisionState { Availability = VisionAvailability.CameraError, PrivacyOn = _privacy, Enrolled = IsEnrolled });
        }
        finally
        {
            detector?.Dispose();
            FailPendingEnroll();
        }
    }

    private EnrollSession? TakeEnroll()
    {
        lock (_lock) return _enroll;
    }

    private void RunEnroll(VisionDetector detector, EnrollSession session)
    {
        // Collect many good face embeddings for a robust average, reporting progress and keeping the
        // preview live throughout. Always emit a preview frame so the circle stays fluid while capturing.
        const int target = 24;
        var samples = new List<float[]>();
        var attempts = 0;
        Report(EnrollPhase.Searching, 0, target);

        while (samples.Count < target && attempts < 300
               && !_shutdown.IsCancellationRequested && !session.Cancelled)
        {
            attempts++;
            var (embedding, faceFound, preview) = detector.CaptureFaceForEnroll(wantPreview: true);
            if (preview is not null) FrameReady?.Invoke(this, preview);
            if (embedding is not null)
            {
                samples.Add(embedding);
                Report(EnrollPhase.Capturing, samples.Count, target);
            }
            else
            {
                Report(faceFound ? EnrollPhase.Capturing : EnrollPhase.Searching, samples.Count, target);
            }
            _shutdown.Token.WaitHandle.WaitOne(40);
        }

        bool ok = false;
        if (!session.Cancelled && samples.Count >= 3)
        {
            var size = samples[0].Length;
            var avg = new float[size];
            foreach (var s in samples)
                for (var i = 0; i < size; i++) avg[i] += s[i];
            double sum = 0;
            for (var i = 0; i < size; i++) { avg[i] /= samples.Count; sum += avg[i] * avg[i]; }
            var norm = Math.Sqrt(sum);
            if (norm > 1e-6)
            {
                for (var i = 0; i < size; i++) avg[i] = (float)(avg[i] / norm);
                try { models.SaveOwnerEmbedding(avg); _ownerEmbedding = avg; ok = true; }
                catch (Exception ex) { log.Error("Saving owner face failed", ex); }
            }
        }

        lock (_lock) { _enroll = null; }
        Report(ok ? EnrollPhase.Completed : EnrollPhase.Failed, samples.Count, target);
        session.Completion.TrySetResult(ok);
    }

    private void Report(EnrollPhase phase, int captured, int target) =>
        EnrollProgressChanged?.Invoke(this, new EnrollProgress(captured, target, phase));

    private void FailPendingEnroll()
    {
        EnrollSession? pending;
        lock (_lock) { pending = _enroll; _enroll = null; }
        pending?.Completion.TrySetResult(false);
    }

    private void Publish(VisionState state)
    {
        if (state == Current) return;
        Current = state;
        Changed?.Invoke(this, state);
    }

    public void Dispose()
    {
        Stop();
        _shutdown.Dispose();
    }

    private sealed class EnrollSession
    {
        public volatile bool Cancelled;
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
