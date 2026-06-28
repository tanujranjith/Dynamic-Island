using System.Runtime.InteropServices;
using DynamicIsland.Windows.Interop;

namespace DynamicIsland.Windows.Services;

/// <summary>
/// Real audio spectrum from a WASAPI loopback capture of the default render device. Computes magnitudes
/// at a handful of representative frequencies (Goertzel) to drive the island's visualiser bars.
/// Best-effort: if loopback can't initialise, <see cref="IsActive"/> stays false and the UI falls back
/// to the animated wave.
/// </summary>
public sealed class AudioSpectrumService(LoggingService log) : IDisposable
{
    public const int BandCount = 7;
    private static readonly double[] Frequencies = { 60, 150, 400, 1000, 2500, 6000, 11000 };
    private const int Window = 1024;

    private readonly CancellationTokenSource _shutdown = new();
    private Thread? _thread;
    private readonly double[] _bands = new double[BandCount];
    private readonly float[] _buffer = new float[Window];
    private int _bufferPos;
    private long _lastEmit;

    public event EventHandler<double[]>? BandsChanged;
    public bool IsActive { get; private set; }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "DynamicIsland.Spectrum" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Loop()
    {
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        var formatPtr = nint.Zero;
        try
        {
            (device, client) = CoreAudioFactory.ActivateDefault<IAudioClient>();
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out formatPtr));
            var format = Marshal.PtrToStructure<WaveFormatEx>(formatPtr);
            const uint loopback = 0x00020000;
            Marshal.ThrowExceptionForHR(client.Initialize(0, loopback, 2_000_000, 0, formatPtr, nint.Zero));
            var captureIid = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref captureIid, out var captureObj));
            capture = (IAudioCaptureClient)captureObj;
            Marshal.ThrowExceptionForHR(client.Start());
            IsActive = true;

            var channels = Math.Max(1, (int)format.nChannels);
            var bytesPerSample = format.wBitsPerSample / 8;
            var isFloat = format.wFormatTag == 3 || (format.wFormatTag == 0xFFFE && format.wBitsPerSample == 32);
            var sampleRate = format.nSamplesPerSec;

            while (!_shutdown.IsCancellationRequested)
            {
                Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out var packetFrames));
                if (packetFrames == 0) { _shutdown.Token.WaitHandle.WaitOne(12); continue; }

                while (packetFrames != 0)
                {
                    var hr = capture.GetBuffer(out var dataPtr, out var frames, out var flags, out _, out _);
                    if (hr < 0) break;
                    if (frames > 0 && dataPtr != nint.Zero)
                        Accumulate(dataPtr, (int)frames, channels, bytesPerSample, isFloat, sampleRate);
                    capture.ReleaseBuffer(frames);
                    capture.GetNextPacketSize(out packetFrames);
                }
            }
        }
        catch (Exception ex)
        {
            log.Debug($"Audio loopback unavailable: {ex.Message}");
        }
        finally
        {
            IsActive = false;
            try { client?.Stop(); } catch { }
            if (formatPtr != nint.Zero) Marshal.FreeCoTaskMem(formatPtr);
            CoreAudioFactory.Release(capture);
            CoreAudioFactory.Release(client);
            CoreAudioFactory.Release(device);
        }
    }

    private unsafe void Accumulate(nint data, int frames, int channels, int bytesPerSample, bool isFloat, uint sampleRate)
    {
        var ptr = (byte*)data;
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var sampleByte = ptr + (f * channels + c) * bytesPerSample;
                sum += isFloat ? *(float*)sampleByte
                    : bytesPerSample == 2 ? *(short*)sampleByte / 32768.0
                    : 0;
            }
            _buffer[_bufferPos++] = (float)(sum / channels);
            if (_bufferPos >= Window)
            {
                _bufferPos = 0;
                Analyze(sampleRate);
            }
        }
    }

    private void Analyze(uint sampleRate)
    {
        for (var b = 0; b < BandCount; b++)
        {
            var magnitude = Goertzel(_buffer, Frequencies[b], sampleRate);
            // Log-scale + auto-gain into 0..1, with fast attack / slow decay.
            var level = Math.Clamp(Math.Log10(1 + magnitude * 40) , 0, 1);
            _bands[b] = Math.Max(level, _bands[b] * 0.82);
        }

        var now = Environment.TickCount64;
        if (now - _lastEmit < 33) return; // ~30 fps
        _lastEmit = now;
        BandsChanged?.Invoke(this, (double[])_bands.Clone());
    }

    private static double Goertzel(float[] samples, double freq, uint sampleRate)
    {
        var k = 2 * Math.Cos(2 * Math.PI * freq / sampleRate);
        double s0, s1 = 0, s2 = 0;
        foreach (var sample in samples)
        {
            s0 = sample + k * s1 - s2;
            s2 = s1; s1 = s0;
        }
        var power = s1 * s1 + s2 * s2 - k * s1 * s2;
        return Math.Sqrt(Math.Max(0, power)) / samples.Length;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _shutdown.Dispose();
    }
}
