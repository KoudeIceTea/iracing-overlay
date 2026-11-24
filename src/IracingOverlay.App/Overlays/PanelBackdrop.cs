using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using DispatcherQueue = Windows.System.DispatcherQueue;
using DispatcherQueueController = Windows.System.DispatcherQueueController;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;
using IracingOverlay.App.Interop;

namespace IracingOverlay.App.Overlays;

internal readonly record struct PanelShape(int Left, int Top, int Right, int Bottom, Vector2 Radius);

/// <summary>A click-through composition layer directly behind the WPF text window.</summary>
internal sealed class PanelBackdrop : IDisposable
{
    // Both overlays use the same WPF UI thread. Keep its dispatcher queue for the app lifetime.
    private static DispatcherQueueController? _queue;
    private readonly IntPtr _foreground;
    private IntPtr _window;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private ContainerVisual? _root;
    private CompositionBackdropBrush? _brush;
    private readonly List<(SpriteVisual Sprite, CompositionRoundedRectangleGeometry Geometry, CompositionGeometricClip Clip)> _panels = [];
    private NativeMethods.RECT _bounds;
    private bool _shown;

    private PanelBackdrop(IntPtr foreground) => _foreground = foreground;

    public static PanelBackdrop? TryCreate(IntPtr foreground)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return null;
        var layer = new PanelBackdrop(foreground);
        try
        {
            layer.Initialize();
            return layer;
        }
        catch (Exception ex) when (ex is COMException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            Debug.WriteLine($"Panel backdrop unavailable: {ex.Message}");
            layer.Dispose();
            return null;
        }
    }

    private void Initialize()
    {
        if (DispatcherQueue.GetForCurrentThread() == null)
        {
            var options = new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, ApartmentType = 2 };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var controller));
            try { _queue = DispatcherQueueController.FromAbi(controller); }
            finally { Marshal.Release(controller); }
        }

        const int noRedirectionBitmap = 0x00200000;
        const uint popup = 0x80000000;
        _window = CreateWindowExW(noRedirectionBitmap | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT |
            NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW, "STATIC", "", popup,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_window == IntPtr.Zero || !SetLayeredWindowAttributes(_window, 0, 255, 2))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        NativeMethods.EnableHostBackdrop(_window);
        _compositor = new Compositor();
        var interop = _compositor.As<ICompositorDesktopInterop>();
        Marshal.ThrowExceptionForHR(interop.CreateDesktopWindowTarget(_window, false, out var target));
        try { _target = DesktopWindowTarget.FromAbi(target); }
        finally { Marshal.Release(target); }
        _root = _compositor.CreateContainerVisual();
        _target.Root = _root;
        _brush = _compositor.CreateHostBackdropBrush();
    }

    public void SetPanels(IReadOnlyList<PanelShape> panels)
    {
        while (_panels.Count > panels.Count) RemoveLastPanel();
        for (int i = 0; i < panels.Count; i++)
        {
            if (i == _panels.Count)
            {
                var geometry = _compositor!.CreateRoundedRectangleGeometry();
                var clip = _compositor.CreateGeometricClip(geometry);
                var sprite = _compositor.CreateSpriteVisual();
                sprite.Clip = clip;
                sprite.Brush = _brush;
                _root!.Children.InsertAtTop(sprite);
                _panels.Add((sprite, geometry, clip));
            }
            var panel = panels[i];
            var size = new Vector2(panel.Right - panel.Left, panel.Bottom - panel.Top);
            var visual = _panels[i];
            visual.Geometry.Size = size;
            visual.Geometry.CornerRadius = panel.Radius;
            visual.Sprite.Size = size;
            visual.Sprite.Offset = new Vector3(panel.Left, panel.Top, 0);
        }
    }

    private void RemoveLastPanel()
    {
        var panel = _panels[^1];
        _root!.Children.Remove(panel.Sprite);
        panel.Sprite.Dispose();
        panel.Clip.Dispose();
        panel.Geometry.Dispose();
        _panels.RemoveAt(_panels.Count - 1);
    }

    public void Sync(bool visible)
    {
        if (_window == IntPtr.Zero) return;
        if (!visible || _panels.Count == 0)
        {
            if (_shown) ShowWindow(_window, 0);
            _shown = false;
            return;
        }

        if (!NativeMethods.GetWindowRect(_foreground, out var bounds)) return;
        const uint nextWindow = 2, showWindow = 0x0040;
        if (_shown && bounds.Equals(_bounds) && GetWindow(_foreground, nextWindow) == _window) return;
        _shown = NativeMethods.SetWindowPos(_window, _foreground, bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, NativeMethods.SWP_NOACTIVATE | showWindow);
        _bounds = bounds;
    }

    public void Dispose()
    {
        if (_window != IntPtr.Zero) ShowWindow(_window, 0);
        while (_panels.Count != 0) RemoveLastPanel();
        _target?.Dispose();
        _root?.Dispose();
        _brush?.Dispose();
        _compositor?.Dispose();
        if (_window != IntPtr.Zero) DestroyWindow(_window);
        _window = IntPtr.Zero;
    }

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        [PreserveSig] int CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueueOptions { public int Size, ThreadType, ApartmentType; }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
}

