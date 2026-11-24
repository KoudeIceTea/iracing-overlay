using System.Diagnostics;

namespace IracingOverlay.App.Interop;

internal sealed class SimulatorPresence
{
    private readonly HashSet<int> _processIds = [];
    private readonly int _sessionId;
    private long _nextScan;

    public SimulatorPresence()
    {
        using var current = Process.GetCurrentProcess();
        _sessionId = current.SessionId;
    }

    public bool IsRunning => _processIds.Count != 0;
    public bool OwnsWindow(int processId) => _processIds.Contains(processId);

    public void Refresh()
    {
        // Focus is sampled more often than process creation and exit.
        long now = Environment.TickCount64;
        if (now < _nextScan) return;
        _nextScan = now + 1000;
        _processIds.Clear();
        foreach (var process in Process.GetProcessesByName("iRacingSim64DX11"))
        {
            using (process)
            {
                if (process.SessionId == _sessionId) _processIds.Add(process.Id);
            }
        }
    }
}
