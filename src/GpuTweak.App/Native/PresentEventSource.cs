using GpuTweak.Core;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace GpuTweak.App.Native;

/// <summary>
/// Counts frames the way PresentMon does: by listening to the Present events Windows itself logs (ETW),
/// so nothing is injected into the game and anti-cheat is not involved. Covers DirectX 9–12 games, and
/// Vulkan/OpenGL games when the NVIDIA driver presents them through DXGI (its default).
/// </summary>
/// <remarks>
/// The session sees every Present on the system (the desktop, a browser playing video, ...), so it only runs
/// while the widget shows FPS (<see cref="Active"/>), and asks Windows to deliver only the Present events.
/// </remarks>
internal sealed class PresentEventSource : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3D9PresentStart = 1;
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    private readonly FrameRateTracker _tracker;
    private readonly object _gate = new();
    private TraceEventSession? _session;
    private DateTime _nextTry = DateTime.MinValue;

    public PresentEventSource(FrameRateTracker tracker) => _tracker = tracker;

    /// <summary>Starts the event session when true, stops it when false. Cheap to set on every sample.</summary>
    public bool Active
    {
        get { lock (_gate) return _session is not null; }
        set
        {
            lock (_gate)
            {
                if (value && _session is null && DateTime.UtcNow >= _nextTry) Start();
                else if (!value && _session is not null) Stop();
            }
        }
    }

    private void Start()
    {
        TraceEventSession? session = null;
        try
        {
            // A session left over from a crash has the same name and is replaced.
            session = new TraceEventSession("GpuTweak-FPS") { StopOnDispose = true };
            session.EnableProvider(Dxgi, TraceEventLevel.Informational, ulong.MaxValue,
                new TraceEventProviderOptions { EventIDsToEnable = new List<int> { DxgiPresentStart, DxgiPresentMpoStart } });
            session.EnableProvider(D3D9, TraceEventLevel.Informational, ulong.MaxValue,
                new TraceEventProviderOptions { EventIDsToEnable = new List<int> { D3D9PresentStart } });
            var source = session.Source;
            var tracker = _tracker;
            source.AllEvents += e =>
            {
                var id = (int)e.ID;
                if ((e.ProviderGuid == Dxgi && id is DxgiPresentStart or DxgiPresentMpoStart) || (e.ProviderGuid == D3D9 && id == D3D9PresentStart))
                    tracker.OnPresent(e.ProcessID, Seconds(e.TimeStamp));
            };
            new Thread(() =>
            {
                try { source.Process(); } catch (Exception) { /* session stopped */ }
            }) { IsBackground = true, Name = "FPS events" }.Start();
            _session = session;
        }
        catch (Exception)
        {
            // Not elevated, or ETW refused: FPS is simply not shown. Do not retry on every sample.
            session?.Dispose();
            _nextTry = DateTime.UtcNow + RetryAfterFailure;
        }
    }

    private void Stop()
    {
        try { _session?.Dispose(); } catch (Exception) { }
        _session = null;
    }

    public static double Seconds(DateTime time) => time.ToUniversalTime().Ticks / (double)TimeSpan.TicksPerSecond;

    public void Dispose() { lock (_gate) Stop(); }
}
