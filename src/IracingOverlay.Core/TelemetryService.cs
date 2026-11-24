using System.Diagnostics;
using IracingOverlay.Core.Race;
using IracingOverlay.Core.Sdk;

namespace IracingOverlay.Core;

/// <summary>
/// Polls iRacing on a background thread, builds <see cref="RaceSnapshot"/>s at a fixed rate and
/// handles connect/disconnect. Events are raised on the background thread.
/// </summary>
public sealed class TelemetryService : IDisposable
{
    private readonly IRacingSdk _sdk = new();
    private Thread? _thread;
    private volatile bool _stop;
    private SessionData? _session;
    private bool _connected;

    public event Action<RaceSnapshot>? SnapshotUpdated;
    public event Action<bool>? ConnectionChanged;

    public int UpdateHz { get; set; } = 10;
    private volatile RaceOptions _options = new();
    public RaceOptions Options { get => _options; set => _options = value; }
    public string? LastError { get; private set; }

    public void Start()
    {
        if (_thread != null) return;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "iRacing telemetry", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(2000);
        _thread = null;
        _sdk.Close();
    }

    public void Dispose() => Stop();

    private void Run()
    {
        int lastTick = -1;
        LapHistory? lapHistory = null;
        FuelHistory? fuelHistory = null;
        FlagTracker? flagTracker = null;
        var staleTimer = Stopwatch.StartNew();

        while (!_stop)
        {
            if (!_sdk.TryOpen() || !_sdk.IsConnected)
            {
                SetConnected(false);
                _sdk.Close();
                _session = null;
                lapHistory = null;
                fuelHistory = null;
                Thread.Sleep(1000);
                continue;
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                var frame = _sdk.ReadTelemetry();
                var yaml = _sdk.ReadSessionInfoIfChanged();
                if (yaml != null) _session = SessionData.Parse(yaml);

                if (frame != null)
                {
                    // If the sim crashes the status bit can stay set; detect a frozen tick counter.
                    if (frame.TickCount != lastTick) { lastTick = frame.TickCount; staleTimer.Restart(); }
                    else if (staleTimer.Elapsed > TimeSpan.FromSeconds(3)) { _sdk.Close(); continue; }

                    if (_session != null)
                    {
                        var snapshot = RaceStateBuilder.Build(frame, _session, Options,
                            lapHistory ??= new LapHistory(), fuelHistory ??= new FuelHistory(), flagTracker ??= new FlagTracker());

                        SetConnected(true);
                        SnapshotUpdated?.Invoke(snapshot);
                    }
                }
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.WriteLine($"[TelemetryService] {ex}");
            }

            int intervalMs = 1000 / Math.Clamp(UpdateHz, 1, 60);
            int elapsedMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Thread.Sleep(Math.Max(1, intervalMs - elapsedMs));
        }
    }

    private void SetConnected(bool connected)
    {
        if (_connected == connected) return;
        _connected = connected;
        ConnectionChanged?.Invoke(connected);
    }
}
