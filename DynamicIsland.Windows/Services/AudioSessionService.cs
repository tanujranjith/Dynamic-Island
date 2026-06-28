using System.Runtime.InteropServices;
using DynamicIsland.Windows.Interop;
using DynamicIsland.Windows.Models;

namespace DynamicIsland.Windows.Services;

public sealed class AudioSessionService(LoggingService log) : IDisposable
{
    private const float AudibleThreshold = 0.008f;
    private readonly CancellationTokenSource _shutdown = new();
    private Thread? _thread;

    public event EventHandler<AudioState>? Changed;
    public AudioState Current { get; private set; } = AudioState.Unknown;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "DynamicIsland.CoreAudio" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void SetMasterVolume(int percent)
    {
        IAudioEndpointVolume? endpoint = null;
        IMMDevice? device = null;
        try
        {
            (device, endpoint) = CoreAudioFactory.ActivateDefault<IAudioEndpointVolume>();
            Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, nint.Zero));
        }
        catch (Exception ex) { log.Error("Unable to set master volume", ex); }
        finally { CoreAudioFactory.Release(endpoint); CoreAudioFactory.Release(device); }
    }

    public void SetMuted(bool muted)
    {
        IAudioEndpointVolume? endpoint = null;
        IMMDevice? device = null;
        try
        {
            (device, endpoint) = CoreAudioFactory.ActivateDefault<IAudioEndpointVolume>();
            Marshal.ThrowExceptionForHR(endpoint.SetMute(muted, nint.Zero));
        }
        catch (Exception ex) { log.Error("Unable to update master mute", ex); }
        finally { CoreAudioFactory.Release(endpoint); CoreAudioFactory.Release(device); }
    }

    private void PollLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var next = ReadState();
            if (next != Current)
            {
                Current = next;
                Changed?.Invoke(this, next);
            }
            _shutdown.Token.WaitHandle.WaitOne(300);
        }
    }

    private AudioState ReadState()
    {
        IMMDevice? endpointDevice = null;
        IAudioEndpointVolume? endpointVolume = null;
        IAudioMeterInformation? endpointMeter = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            (endpointDevice, endpointVolume) = CoreAudioFactory.ActivateDefault<IAudioEndpointVolume>();
            endpointVolume.GetMasterVolumeLevelScalar(out var volume);
            endpointVolume.GetMute(out var systemMuted);

            (_, endpointMeter) = CoreAudioFactory.ActivateDefault<IAudioMeterInformation>();
            endpointMeter.GetPeakValue(out var endpointPeak);

            (_, manager) = CoreAudioFactory.ActivateDefault<IAudioSessionManager2>();
            Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
            sessions.GetCount(out var count);
            var active = 0;
            var audible = 0;
            var anyMuted = false;

            for (var index = 0; index < count; index++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    if (sessions.GetSession(index, out control) < 0) continue;
                    if (control.GetState(out var state) < 0 || state != AudioSessionState.Active) continue;
                    active++;
                    if (control is ISimpleAudioVolume simple && simple.GetMute(out var sessionMuted) >= 0)
                        anyMuted |= sessionMuted;
                    if (control is IAudioMeterInformation meter && meter.GetPeakValue(out var peak) >= 0 && peak >= AudibleThreshold)
                        audible++;
                }
                catch { }
                finally { CoreAudioFactory.Release(control); }
            }

            return new AudioState
            {
                Availability = AudioAvailability.Available,
                MasterVolumePercent = (int)Math.Round(Math.Clamp(volume, 0f, 1f) * 100),
                SystemMuted = systemMuted,
                AnySessionMuted = anyMuted,
                ActiveAudioOutput = !systemMuted && (endpointPeak >= AudibleThreshold || audible > 0),
                ActiveSessionCount = active,
                AudibleSessionCount = audible,
                OutputDeviceName = "Default output"
            };
        }
        catch (COMException ex)
        {
            log.Debug($"CoreAudio unavailable: 0x{ex.HResult:X8}");
            return new AudioState { Availability = AudioAvailability.Unsupported };
        }
        catch (Exception ex)
        {
            log.Error("CoreAudio polling failed", ex);
            return AudioState.Unknown;
        }
        finally
        {
            CoreAudioFactory.Release(sessions);
            CoreAudioFactory.Release(manager);
            CoreAudioFactory.Release(endpointMeter);
            CoreAudioFactory.Release(endpointVolume);
            CoreAudioFactory.Release(endpointDevice);
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _shutdown.Dispose();
    }
}
