using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace MonitorDeck;

// Experimental, fail-closed overlay. Do not show a dim layer if Windows refuses
// WDA_EXCLUDEFROMCAPTURE. Caller must still test OBS and screenshot behavior.
internal sealed class OverlayDimming : IDisposable
{
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    private const uint GwHwndPrev = 3;
    private const int DwmaCloaked = 14;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd,
        System.Text.StringBuilder className, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute,
        out int value, int valueSize);

    private static string WindowClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(none)";
        var buffer = new System.Text.StringBuilder(128);
        return GetClassName(hwnd, buffer, buffer.Capacity) == 0 ? "(unknown)" : buffer.ToString();
    }

    // Read-only snapshot. Deliberately does NOT call KeepAlive, SetWindowPos,
    // or attempt a repair: diagnostics must capture the broken state first.
    public string CaptureSnapshot(IReadOnlyList<DisplayInfo> displays,
        IReadOnlyDictionary<string, int> requestedLevels)
    {
        var output = new System.Text.StringBuilder();
        foreach (DisplayInfo display in displays)
        {
            int requested = requestedLevels.TryGetValue(display.Key, out int level) ? level : 100;
            output.AppendLine($"{display.Name} ({display.DisplayPath}, model {display.HardwareId})");
            output.AppendLine($"  Requested software brightness: {requested}%");
            output.AppendLine($"  Expected bounds: {display.Left},{display.Top} {display.Width}x{display.Height}");
            if (!_forms.TryGetValue(display.Key, out DimForm? form))
            {
                output.AppendLine("  Overlay: MISSING");
                continue;
            }
            try
            {
                IntPtr hwnd = form.IsHandleCreated ? form.Handle : IntPtr.Zero;
                bool exists = hwnd != IntPtr.Zero && IsWindow(hwnd);
                bool visible = exists && IsWindowVisible(hwnd);
                bool affinityRead = exists && GetWindowDisplayAffinity(hwnd, out uint affinityValue);
                // Assign a known value when GetWindowDisplayAffinity failed.
                uint affinity = 0;
                if (affinityRead)
                    GetWindowDisplayAffinity(hwnd, out affinity);
                bool rectRead = exists && NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rectValue);
                NativeMethods.RECT rect = default;
                if (rectRead) NativeMethods.GetWindowRect(hwnd, out rect);
                int cloak = -1;
                int dwmResult = exists ? DwmGetWindowAttribute(hwnd, DwmaCloaked,
                    out cloak, sizeof(int)) : -1;
                IntPtr previous = exists ? GetWindow(hwnd, GwHwndPrev) : IntPtr.Zero;
                output.AppendLine($"  HWND: 0x{hwnd.ToInt64():X}; Exists={exists}; IsWindowVisible={visible}; Form.Visible={form.Visible}; Disposed={form.IsDisposed}");
                output.AppendLine($"  Form.TopMost={form.TopMost}; Opacity={form.Opacity:F3}; Bounds={form.Bounds}");
                output.AppendLine($"  Capture affinity: {(affinityRead ? $"0x{affinity:X}" : "unavailable")} (expected 0x11)");
                output.AppendLine($"  Win32 bounds: {(rectRead ? $"{rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}" : "unavailable")}");
                output.AppendLine($"  DWM cloak: {(dwmResult == 0 ? cloak.ToString() : "unavailable")}; window immediately above: {WindowClass(previous)} (class only)");
                if (!exists || !visible || !affinityRead || affinity != WdaExcludeFromCapture ||
                    (dwmResult == 0 && cloak != 0))
                    output.AppendLine("  WARNING: overlay has a detectable window-state issue");
                else
                    output.AppendLine("  Window APIs appear healthy; composition/z-order may still prevent visible dimming");
            }
            catch (Exception ex)
            {
                output.AppendLine("  Snapshot failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
        return output.ToString();
    }

    // A TV can renegotiate its HDMI link without Windows reporting a new
    // monitor fingerprint. A live overlay needs its own health check.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);


    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    private sealed class DimForm : Forms.Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override Forms.CreateParams CreateParams
        {
            get
            {
                Forms.CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
                return cp;
            }
        }

        protected override void WndProc(ref Forms.Message m)
        {
            if (m.Msg == WmNcHitTest)
            {
                m.Result = (IntPtr)HtTransparent;
                return;
            }
            base.WndProc(ref m);
        }

        internal DimForm(DisplayInfo display, double opacity)
        {
            FormBorderStyle = Forms.FormBorderStyle.None;
            StartPosition = Forms.FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            Bounds = new Rectangle(display.Left, display.Top, display.Width, display.Height);
            Opacity = opacity;
        }

        internal void SetOpacity(double opacity) => Opacity = opacity;
        internal void MoveTo(DisplayInfo display) => Bounds =
            new Rectangle(display.Left, display.Top, display.Width, display.Height);
    }

    private readonly Dictionary<string, DimForm> _forms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastFailedRepair = new(StringComparer.OrdinalIgnoreCase);

    // Only retry a failed overlay after a pause. For example, some Windows
    // capture/display configurations refuse WDA_EXCLUDEFROMCAPTURE temporarily.
    private static readonly TimeSpan RepairRetry = TimeSpan.FromSeconds(20);


    public string Set(DisplayInfo display, int brightness)
    {
        brightness = Math.Clamp(brightness, 0, 100);
        if (brightness == 100)
        {
            Remove(display.Key);
            return "Off. Original on-screen image restored.";
        }

        // A 0% software level creates a fully opaque black overlay. Physical
        // backlight brightness does not change. The global emergency hotkey
        // Ctrl+Alt+Shift+F12 removes all overlays and resets saved levels.
        double opacity = (100.0 - brightness) / 100.0;
        if (_forms.TryGetValue(display.Key, out DimForm? active))
        {
            active.MoveTo(display);
            active.SetOpacity(opacity);
            return "Overlay active • capture exclusion requested; verify with OBS/Win+Shift+S.";
        }

        // Explicit slider changes should not wait for the repair cooldown.
        _lastFailedRepair.Remove(display.Key);
        var form = new DimForm(display, opacity);
        try
        {
            // Get a real top-level HWND before showing the filter.
            IntPtr hwnd = form.Handle;
            // Windows may not accept exclusion on all layered/transparent
            // forms. Fail closed rather than silently affect recordings.
            bool excluded = SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
            int error = excluded ? 0 : Marshal.GetLastWin32Error();
            if (!excluded || !GetWindowDisplayAffinity(hwnd, out uint actual) ||
                actual != WdaExcludeFromCapture)
            {
                form.Dispose();
                return $"Overlay DISABLED: capture exclusion was rejected (Win32 error {error}).";
            }
            form.Show();
            // Verify again after Show(), because WinForms can re-create the HWND.
            IntPtr visibleHwnd = form.Handle;
            if (!GetWindowDisplayAffinity(visibleHwnd, out actual) ||
                actual != WdaExcludeFromCapture)
            {
                if (!SetWindowDisplayAffinity(visibleHwnd, WdaExcludeFromCapture))
                {
                    error = Marshal.GetLastWin32Error();
                    form.Close();
                    form.Dispose();
                    return $"Overlay DISABLED: Windows lost capture exclusion (error {error}).";
                }
            }
            _forms.Add(display.Key, form);
            DimmingDiagnostics.Record($"Overlay created: {display.Name} {display.DisplayPath}, brightness={brightness}%, hwnd=0x{form.Handle.ToInt64():X}");
            return "Overlay active • capture exclusion requested; verify with OBS/Win+Shift+S.";
        }
        catch (Exception ex)
        {
            form.Close();
            form.Dispose();
            return "Overlay DISABLED: " + ex.Message;
        }
    }

    // Called from the periodic display check EVEN IF the screen layout has
    // not changed. TV input modes, HDMI handshakes and other topmost windows
    // can affect an existing dim overlay without changing the display list.
    public IReadOnlyList<string> KeepAlive(
        IEnumerable<DisplayInfo> displays, IReadOnlyDictionary<string, int> levels)
    {
        var notices = new List<string>();
        foreach (DisplayInfo display in displays)
        {
            if (!levels.TryGetValue(display.Key, out int brightness) || brightness >= 100)
                continue;

            // A manually saved 0% level is valid and should remain fully black.
            brightness = Math.Clamp(brightness, 0, 100);
            string? issue = null;
            if (!_forms.TryGetValue(display.Key, out DimForm? form))
                issue = "overlay missing";
            else if (form.IsDisposed || !form.IsHandleCreated)
                issue = "overlay window destroyed";
            else
            {
                IntPtr hwnd = form.Handle;
                if (!IsWindow(hwnd) || !IsWindowVisible(hwnd))
                    issue = "overlay window hidden";
                else if (!GetWindowDisplayAffinity(hwnd, out uint affinity) ||
                         affinity != WdaExcludeFromCapture)
                    issue = "capture exclusion lost";
                else
                {
                    var bounds = new Rectangle(display.Left, display.Top, display.Width, display.Height);
                    if (form.Bounds != bounds) form.MoveTo(display);
                    double opacity = (100.0 - brightness) / 100.0;
                    if (Math.Abs(form.Opacity - opacity) > 0.005) form.SetOpacity(opacity);

                    // Keep the overlay in the topmost band without activating it.
                    // This can recover visibility after a TV renegotiates HDMI or
                    // another fullscreen/borderless window changes the Z-order.
                    if (!NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                        0, 0, 0, 0, NativeMethods.SWP_NOMOVE |
                        NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                        NativeMethods.SWP_NOOWNERZORDER))
                        issue = $"topmost refresh failed (Win32 {Marshal.GetLastWin32Error()})";
                }
            }

            if (issue is null)
            {
                _lastFailedRepair.Remove(display.Key);
                continue;
            }

            if (_lastFailedRepair.TryGetValue(display.Key, out DateTime lastAttempt) &&
                DateTime.UtcNow - lastAttempt < RepairRetry)
                continue;

            Remove(display.Key);
            string result = Set(display, brightness);
            if (result.StartsWith("Overlay DISABLED", StringComparison.Ordinal))
            {
                _lastFailedRepair[display.Key] = DateTime.UtcNow;
                notices.Add($"{display.Name}: repair failed ({issue}): {result}");
                DimmingDiagnostics.Record($"Watchdog FAILED: {display.Name} {display.DisplayPath}, {issue}, {result}");
            }
            else
            {
                notices.Add($"{display.Name}: restored {brightness}% software brightness ({issue})");
                DimmingDiagnostics.Record($"Watchdog restored: {display.Name} {display.DisplayPath}, brightness={brightness}%, trigger={issue}");
            }
        }
        return notices;
    }

    // Shell-owned UI (including Start/Search) may appear above ordinary overlays.
    // Do not continuously raise our forms: Windows may ignore it, and it can
    // interfere with the monitor controls.
    public void Reconcile(IEnumerable<DisplayInfo> displays)
    {
        var active = new Dictionary<string, DisplayInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (DisplayInfo display in displays) active[display.Key] = display;
        foreach (string key in new List<string>(_forms.Keys))
        {
            if (!active.TryGetValue(key, out DisplayInfo? display)) Remove(key);
            else _forms[key].MoveTo(display);
        }
    }

    // Pausing a software overlay is different from clearing its saved setting.
    // The GPU-dimming preview uses this to avoid multiplying both effects.
    public bool IsActive(string key) => _forms.ContainsKey(key);

    public void Remove(string key)
    {
        if (_forms.Remove(key, out DimForm? form))
        {
            DimmingDiagnostics.Record($"Overlay removed: key={key}, hwnd=0x{(form.IsHandleCreated ? form.Handle.ToInt64() : 0):X}");
            form.Close();
            form.Dispose();
        }
    }

    public void Clear()
    {
        foreach (string key in new List<string>(_forms.Keys)) Remove(key);
        _lastFailedRepair.Clear();
    }

    public void Dispose() => Clear();
}
