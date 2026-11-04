using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AIrCon.BasicPlugin;

// Observe foreground activation of AIrCon's own ToolWindow, but never require
// activation to update its visible channel list. The Host's WinForms WebBrowser
// owns that list. We scroll only #aircon-service-list on its UI thread; never
// activate another window, navigate the Host page or change the user's focus.
internal sealed class AirConForegroundObserver : IDisposable
{
    internal readonly record struct CenterScrollResult(bool Applied, string Reason,
        int TargetTop = 0, int ActualTop = 0, int MaxTop = 0);

    private readonly Action _activated;
    private readonly System.Threading.Timer _timer;
    private int _wasForeground;
    private int _lastWidth;
    private int _lastHeight;
    private int _disposed;

    public AirConForegroundObserver(Action activated)
    {
        _activated = activated;
        _timer = new System.Threading.Timer(CheckForeground, null, 200, 500);
    }

    // Read-only Win32 check. The selected live ViewerSession supplies the PID; this
    // never enumerates unrelated processes or requests any focus transition.
    internal static bool IsViewerProcessForeground(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0) return false;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        return foregroundProcessId == (uint)processId;
    }

    private static bool IsOwnToolWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId != (uint)Environment.ProcessId) return false;
        var title = new StringBuilder(128);
        return GetWindowText(hwnd, title, title.Capacity) > 0
            && title.ToString().Equals(AIrConRenderer.PluginToolWindowTitle, StringComparison.Ordinal);
    }

    // This is a direct UI operation on AIrCon's list, not a Host refresh and not
    // browser script. Success means the native list.ScrollTop was read back.
    // A visible background ToolWindow is allowed; a hidden/closed one is not.
    // Ownership is checked by process and exact window title, never foreground.
    private static IntPtr FindOwnVisibleToolWindow()
    {
        var foreground = GetForegroundWindow();
        if (IsOwnToolWindow(foreground) && IsWindowVisible(foreground))
            return foreground;

        // ProgramGuide leaves TVTest or the main TvAIr window in the foreground.
        // Enumerate only top-level windows of this same process, then locate our
        // existing ToolWindow without SetForegroundWindow / SetFocus side effects.
        var found = IntPtr.Zero;
        EnumWindows((candidate, _) =>
        {
            if (!IsOwnToolWindow(candidate) || !IsWindowVisible(candidate)) return true;
            found = candidate;
            return false;
        }, IntPtr.Zero);
        return found;
    }
    internal static async Task<CenterScrollResult> CenterOwnListRowAsync(
        string rowId, long renderVersion,
        Func<bool> stillCurrent, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "platform_unsupported");
        var hwnd = FindOwnVisibleToolWindow();
        if (hwnd == IntPtr.Zero) return new(false, "window_not_visible");
        Control? root;
        try { root = Control.FromHandle(hwnd); }
        catch (Exception) { return new(false, "control_missing"); }
        if (root == null || root.IsDisposed || !root.IsHandleCreated)
            return new(false, "control_missing");

        var completion = new TaskCompletionSource<CenterScrollResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            root.BeginInvoke(new MethodInvoker(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }
                if (!IsOwnToolWindow(hwnd) || !IsWindowVisible(hwnd))
                {
                    completion.TrySetResult(new(false, "window_not_visible"));
                    return;
                }
                try
                {
                    if (!stillCurrent())
                    {
                        completion.TrySetResult(new(false, "superseded"));
                        return;
                    }
                    var browser = FindWebBrowser(root);
                    var document = browser?.Document;
                    if (document == null)
                    {
                        completion.TrySetResult(new(false, "document_not_ready"));
                        return;
                    }
                    // A render request is not DOM completion. The existing page may
                    // still expose the same service row during Host navigation.
                    // Inspect an ID on the actual rendered list wrapper instead of
                    // reading HTML5 data-* attributes via the legacy IE WebBrowser API.
                    // Each render version is unique within this Plugin instance; its
                    // wrapper identity proves wave and profile belong to THAT HTML.
                    var documentRoot = document.GetElementById("aircon-render-" +
                        renderVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (documentRoot == null)
                    {
                        completion.TrySetResult(new(false, "document_outdated"));
                        return;
                    }
                    var list = document.GetElementById("aircon-service-list");
                    var row = document.GetElementById(rowId);
                    if (list == null || row == null)
                    {
                        completion.TrySetResult(new(false, "row_not_ready"));
                        return;
                    }
                    // Both the list and row must belong to the same identified DOM,
                    // not merely have a reusable ID in a different navigation target.
                    var listOwner = list.Parent;
                    while (listOwner != null && listOwner.Id != documentRoot.Id)
                        listOwner = listOwner.Parent;
                    if (listOwner == null)
                    {
                        completion.TrySetResult(new(false, "document_outdated"));
                        return;
                    }
                    var viewportHeight = list.ClientRectangle.Height;
                    if (viewportHeight <= 0)
                    {
                        completion.TrySetResult(new(false, "viewport_not_ready"));
                        return;
                    }
                    var rowTop = row.OffsetRectangle.Top;
                    var rowHeight = row.OffsetRectangle.Height;
                    var ancestor = row.OffsetParent;
                    // Measure the row in the list's own scroll coordinate system.
                    // Never change row layout or insert spacers to make the row centerable.
                    while (ancestor != null && !string.Equals(ancestor.Id, list.Id, StringComparison.Ordinal))
                    {
                        rowTop += ancestor.OffsetRectangle.Top;
                        ancestor = ancestor.OffsetParent;
                    }
                    if (ancestor == null)
                    {
                        completion.TrySetResult(new(false, "unexpected_scroll_parent"));
                        return;
                    }
                    var maxTop = Math.Max(0, list.ScrollRectangle.Height - viewportHeight);
                    var target = Math.Clamp(rowTop + (rowHeight - viewportHeight) / 2, 0, maxTop);
                    if (!stillCurrent())
                    {
                        completion.TrySetResult(new(false, "superseded"));
                        return;
                    }
                    list.ScrollTop = target;
                    var actual = list.ScrollTop;
                    completion.TrySetResult(new(Math.Abs(actual - target) <= 2,
                        Math.Abs(actual - target) <= 2 ? "verified" : "scroll_readback_mismatch",
                        target, actual, maxTop));
                }
                catch (Exception)
                {
                    completion.TrySetResult(new(false, "dom_access_failed"));
                }
            }));
        }
        catch (Exception) { return new(false, "control_unavailable"); }
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(1200), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException) { return new(false, "ui_dispatch_timeout"); }
    }

    private static WebBrowser? FindWebBrowser(Control parent)
    {
        if (parent is WebBrowser browser) return browser;
        foreach (Control child in parent.Controls)
        {
            var found = FindWebBrowser(child);
            if (found != null) return found;
        }
        return null;
    }

    private void CheckForeground(object? _)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var hwnd = OperatingSystem.IsWindows() ? GetForegroundWindow() : IntPtr.Zero;
        var current = IsOwnToolWindow(hwnd) ? 1 : 0;
        var previous = Interlocked.Exchange(ref _wasForeground, current);
        if (current != 1 || Volatile.Read(ref _disposed) != 0) return;
        var sizeChanged = false;
        if (GetWindowRect(hwnd, out var rect))
        {
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            sizeChanged = width != _lastWidth || height != _lastHeight;
            _lastWidth = width;
            _lastHeight = height;
        }
        if (previous == 1 && !sizeChanged) return;
        try { _activated(); }
        catch (Exception) { /* Observation never interrupts Host processing. */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder title, int maxCount);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
