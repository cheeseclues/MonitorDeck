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
            }
            else
            {
                notices.Add($"{display.Name}: restored {brightness}% software brightness ({issue})");
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
