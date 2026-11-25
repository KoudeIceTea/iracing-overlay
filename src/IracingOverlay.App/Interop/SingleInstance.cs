namespace IracingOverlay.App.Interop;

/// <summary>
/// One running copy per Windows session. A second launch (a double-click while the app already runs from the
/// tray, or Windows starting it at sign-in while it's already open) asks the first copy to show its settings
/// window and then quits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\IracingOverlay.Instance";
    private const string OpenSettingsName = @"Local\IracingOverlay.OpenSettings";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _openSettings;
    private RegisteredWaitHandle? _wait;

    public SingleInstance()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
            IsFirst = created;
            if (!created)
            {
                _mutex.Dispose();
                _mutex = null;
            }
            _openSettings = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSettingsName);
        }
        catch (UnauthorizedAccessException)
        {
            // Created by a copy running as administrator: that copy is running, so this one is the second.
            IsFirst = false;
        }
    }

    public bool IsFirst { get; }

    /// <summary>Second copy: bring the running copy's settings window to the front.</summary>
    public void OpenSettingsInFirst()
    {
        // The user launched this copy, so it may bring a window to the front; pass that right on.
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        try
        {
            if (_openSettings != null) _openSettings.Set();
            else if (EventWaitHandle.TryOpenExisting(OpenSettingsName, out var existing))
                using (existing) existing.Set();
        }
        catch (Exception)
        {
            // Nothing to bring forward; quitting is still right.
        }
    }

    /// <summary>First copy: <paramref name="openSettings"/> runs (on a pool thread) whenever another copy starts.</summary>
    public void OnSecondLaunch(Action openSettings)
    {
        if (_openSettings == null || _wait != null) return;
        _wait = ThreadPool.RegisterWaitForSingleObject(_openSettings, (_, _) => openSettings(), null,
            Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _openSettings?.Dispose();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { } // not owned by this thread; closing the handle frees it anyway
            _mutex.Dispose();
        }
    }
}
