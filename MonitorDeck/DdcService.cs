using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MonitorDeck;

internal enum ControlKind { Brightness, Contrast }
internal enum ContrastTestMode { WindowsApi, Pulse, Save }

internal sealed record FeatureRead(bool Available, uint Current, uint Maximum, string Detail)
{
    public int Percent => Maximum > 0 ? (int)Math.Round(100.0 * Current / Maximum) : 0;
}

internal sealed record ProbeResult(FeatureRead Brightness, FeatureRead Contrast, string Description);
internal sealed record WriteResult(bool Verified, int ActualPercent, string Detail);

internal static class DdcService
{
    private const byte BrightnessVcp = 0x10;
    private const byte ContrastVcp = 0x12;

    public static ProbeResult Probe(DisplayInfo display)
    {
        if (!TryOpen(display.HMonitor, out var handles, out string error))
        {
            var unavailable = new FeatureRead(false, 0, 0, error);
            return new ProbeResult(unavailable, unavailable, "DDC/CI unavailable");
        }

        try
        {
            // A standard desktop output typically has exactly one physical monitor.
            // Check each returned handle rather than assuming GetPhysicalMonitors always returns one.
            FeatureRead brightness = new(false, 0, 0, "No valid brightness response");
            FeatureRead contrast = new(false, 0, 0, "No valid contrast response");
            string description = "Physical monitor";
            foreach (var physical in handles)
            {
                var b = Read(physical.hPhysicalMonitor, BrightnessVcp);
                var c = Read(physical.hPhysicalMonitor, ContrastVcp);
                if (b.Available || c.Available || handles.Length == 1)
                {
                    brightness = b;
                    contrast = c;
                    description = physical.szPhysicalMonitorDescription ?? "Physical monitor";
                    break;
                }
            }
            return new ProbeResult(brightness, contrast, description);
        }
        finally
        {
            NativeMethods.DestroyPhysicalMonitors((uint)handles.Length, handles);
        }
    }

    public static WriteResult Set(DisplayInfo display, ControlKind kind, int percent, uint expectedMaximum)
    {
        if (!TryOpen(display.HMonitor, out var handles, out string error))
            return new WriteResult(false, percent, error);

        byte code = kind == ControlKind.Brightness ? BrightnessVcp : ContrastVcp;
        try
        {
            if (expectedMaximum == 0)
                return new WriteResult(false, percent, "Monitor reported a maximum of zero");

            uint raw = (uint)Math.Clamp((long)Math.Round(expectedMaximum * (percent / 100.0)),
                0L, (long)expectedMaximum);

            foreach (var physical in handles)
            {
                // Match the same physical monitor that responded during probe.
                FeatureRead before = Read(physical.hPhysicalMonitor, code);
                if (!before.Available) continue;

                if (!NativeMethods.SetVCPFeature(physical.hPhysicalMonitor, code, raw))
                    return new WriteResult(false, before.Percent,
                        $"Write rejected (Windows error {Marshal.GetLastWin32Error()})");

                Thread.Sleep(120);
                FeatureRead after = Read(physical.hPhysicalMonitor, code);
                if (!after.Available)
                    return new WriteResult(false, percent,
                        "Write sent, but DDC/CI readback failed; change unverified");

                int actual = after.Percent;
                // Some displays round or quantize values.
                bool matched = Math.Abs((long)after.Current - raw) <=
                               Math.Max(1L, (long)expectedMaximum / 100);
                return new WriteResult(matched, actual,
                    matched ? "DDC/CI readback matched" :
                    $"Monitor returned {actual}% rather than requested {percent}%");
            }

            return new WriteResult(false, percent, "No physical display accepted DDC/CI queries");
        }
        finally
        {
            NativeMethods.DestroyPhysicalMonitors((uint)handles.Length, handles);
        }
    }

    // These tests are opt-in: a DDC/CI readback does NOT prove the image changed.
    public static WriteResult TestContrast(DisplayInfo display, int percent, ContrastTestMode mode)
    {
        if (!TryOpen(display.HMonitor, out var handles, out string error))
            return new WriteResult(false, percent, error);

        try
        {
            foreach (var physical in handles)
            {
                IntPtr handle = physical.hPhysicalMonitor;
                FeatureRead before = Read(handle, ContrastVcp);
                if (!before.Available) continue;

                uint target = (uint)Math.Clamp((long)Math.Round(before.Maximum * percent / 100.0),
                    0L, (long)before.Maximum);
                string action;

                if (mode == ContrastTestMode.WindowsApi)
                {
                    if (!NativeMethods.GetMonitorContrast(handle, out uint minimum, out _, out uint maximum))
                        return new WriteResult(false, before.Percent,
                            $"Windows GetMonitorContrast failed (error {Marshal.GetLastWin32Error()})");
                    if (maximum <= minimum)
                        return new WriteResult(false, before.Percent,
                            $"Windows contrast range invalid: {minimum}..{maximum}");
                    uint windowsValue = minimum + (uint)Math.Round((maximum - minimum) * (percent / 100.0));
                    if (!NativeMethods.SetMonitorContrast(handle, windowsValue))
                        return new WriteResult(false, before.Percent,
                            $"Windows SetMonitorContrast failed (error {Marshal.GetLastWin32Error()})");
                    action = "Windows high-level contrast command";
                }
                else if (mode == ContrastTestMode.Pulse)
                {
                    // Set an adjacent value and immediately restore the selected target.
                    // This might encourage firmware to refresh its picture-processing state.
                    uint neighbour = target < before.Maximum ? target + 1 : target - 1;
                    if (!NativeMethods.SetVCPFeature(handle, ContrastVcp, neighbour))
                        return new WriteResult(false, before.Percent,
                            $"Pulse first write failed (error {Marshal.GetLastWin32Error()})");
                    Thread.Sleep(250);
                    if (!NativeMethods.SetVCPFeature(handle, ContrastVcp, target))
                    {
                        int code = Marshal.GetLastWin32Error();
                        // Best-effort restoration, since the first write was accepted.
                        NativeMethods.SetVCPFeature(handle, ContrastVcp, target);
                        return new WriteResult(false, before.Percent,
                            $"Pulse restore failed (error {code}); check the monitor's physical OSD");
                    }
                    action = "DDC/CI +1/-1 pulse then restore";
                }
                else // ContrastTestMode.Save
                {
                    // The slider has already written its requested value. Save it only
                    // on an explicit click, never automatically or during dragging.
                    if (!NativeMethods.SaveCurrentSettings(handle))
                        return new WriteResult(false, before.Percent,
                            $"SaveCurrentSettings failed (error {Marshal.GetLastWin32Error()})");
                    action = "SaveCurrentSettings sent to nonvolatile monitor memory";
                }

                Thread.Sleep(200);
                FeatureRead after = Read(handle, ContrastVcp);
                if (!after.Available)
                    return new WriteResult(false, percent,
                        $"{action}; readback unavailable. Visually inspect the image.");
                bool matched = mode == ContrastTestMode.Save ||
                    Math.Abs((long)after.Current - target) <= Math.Max(1L, (long)before.Maximum / 100);
                return new WriteResult(matched, after.Percent,
                    $"{action}; VCP readback {after.Current}/{after.Maximum}. " +
                    "Only visual inspection can confirm an actual contrast change.");
            }
            return new WriteResult(false, percent, "No physical monitor returned valid contrast values");
        }
        finally
        {
            NativeMethods.DestroyPhysicalMonitors((uint)handles.Length, handles);
        }
    }

    private static FeatureRead Read(IntPtr handle, byte code)
    {
        bool ok = NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle,
            code, out _, out uint current, out uint maximum);
        if (!ok)
            return new FeatureRead(false, 0, 0,
                $"VCP 0x{code:X2} read failed (Windows error {Marshal.GetLastWin32Error()})");

        // The Samsung G5 in the test setup reported 0/0 for every control.
        // That is NOT a functional 0%-brightness control.
        if (maximum == 0)
            return new FeatureRead(false, current, maximum,
                $"VCP 0x{code:X2} returned {current}/{maximum} (invalid range)");
        if (current > maximum)
            return new FeatureRead(false, current, maximum,
                $"VCP 0x{code:X2} returned {current}/{maximum} (out of range)");

        return new FeatureRead(true, current, maximum,
            $"VCP 0x{code:X2}: {current}/{maximum}");
    }

    private static bool TryOpen(IntPtr monitor, out NativeMethods.PHYSICAL_MONITOR[] handles,
        out string error)
    {
        handles = Array.Empty<NativeMethods.PHYSICAL_MONITOR>();
        if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count))
        {
            error = $"Could not enumerate physical displays (Windows error {Marshal.GetLastWin32Error()})";
            return false;
        }
        if (count == 0 || count > 16)
        {
            error = $"Invalid physical display count: {count}";
            return false;
        }

        handles = new NativeMethods.PHYSICAL_MONITOR[count];
        if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(monitor, count, handles))
        {
            error = $"Could not open DDC/CI handle (Windows error {Marshal.GetLastWin32Error()})";
            handles = Array.Empty<NativeMethods.PHYSICAL_MONITOR>();
            return false;
        }
        error = "";
        return true;
    }
}
