using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace MonitorDeck;

internal sealed record DisplayInfo(
    string Key,
    string Name,
    string Adapter,
    string DisplayPath,
    string HardwareId,
    int Width,
    int Height,
    int Left,
    int Top,
    IntPtr HMonitor);

internal static class DisplayEnumerator
{
    // Friendly fallbacks for the five monitor IDs reported by your Windows WMI query.
    // Unknown models are discovered generically, not filtered out.
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACI23C1"] = "ASUS VX238",
        ["BNQ7F32"] = "BenQ XL2411Z",
        ["AOCB403"] = "AOC Q27G4",
        ["SAM79CF"] = "Samsung Odyssey G5",
        ["SAM71EE"] = "Samsung Odyssey G8"
    };

    public static List<DisplayInfo> GetActiveDisplays()
    {
        var adapters = GetAdapters();
        var result = new List<DisplayInfo>();

        // Unlike Monitorian, discovery never checks DDC/CI first.
        NativeMethods.MonitorEnumProc callback = (handle, _, _, _) =>
        {
            var info = new NativeMethods.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
            };
            if (!NativeMethods.GetMonitorInfo(handle, ref info))
                return true;

            string displayPath = info.szDevice ?? string.Empty;
            var monitorDevice = NativeMethods.DISPLAY_DEVICE.Create();
            bool hasDevice = NativeMethods.EnumDisplayDevices(displayPath, 0, ref monitorDevice, 0);
            string hardwareId = hasDevice ? ExtractHardwareId(monitorDevice.DeviceID) : "";
            string reportedName = hasDevice ? (monitorDevice.DeviceString ?? "").Trim() : "";

            string name;
            if (KnownNames.TryGetValue(hardwareId, out string? known))
                name = known;
            else if (!string.IsNullOrWhiteSpace(reportedName) &&
                     !reportedName.Contains("Generic", StringComparison.OrdinalIgnoreCase) &&
                     !reportedName.Contains("PnP Monitor", StringComparison.OrdinalIgnoreCase))
                name = reportedName;
            else
                name = string.IsNullOrEmpty(hardwareId) ? "External display" : $"Monitor {hardwareId}";

            adapters.TryGetValue(displayPath, out string? adapter);
            int width = info.rcMonitor.Right - info.rcMonitor.Left;
            int height = info.rcMonitor.Bottom - info.rcMonitor.Top;
            var mode = new NativeMethods.DEVMODE { dmSize = (short)Marshal.SizeOf<NativeMethods.DEVMODE>() };
            if (NativeMethods.EnumDisplaySettings(displayPath, NativeMethods.ENUM_CURRENT_SETTINGS, ref mode) &&
                mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0)
            {
                width = mode.dmPelsWidth;
                height = mode.dmPelsHeight;
            }
            string key = $"{displayPath}|{hardwareId}";

            result.Add(new DisplayInfo(key, name, adapter ?? "Graphics adapter unknown",
                displayPath, hardwareId, width, height, info.rcMonitor.Left, info.rcMonitor.Top, handle));
            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                "Windows monitor enumeration failed");

        return result.OrderBy(d => DisplayNumber(d.DisplayPath)).ThenBy(d => d.Name).ToList();
    }

    public static string Fingerprint(IEnumerable<DisplayInfo> displays) => string.Join(";",
        displays.OrderBy(d => d.Key).Select(d =>
            $"{d.Key}/{d.Width}x{d.Height}@{d.Left},{d.Top}/{d.HMonitor.ToInt64()}"));

    private static int DisplayNumber(string deviceName)
    {
        int i = deviceName.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return int.MaxValue;
        return int.TryParse(deviceName[(i + 7)..], out int n) ? n : int.MaxValue;
    }

    private static string ExtractHardwareId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return "";
        // Typical EnumDisplayDevices DeviceID: MONITOR\\SAM79CF\\{...}
        var parts = deviceId.Split('\\');
        return parts.Length >= 2 ? parts[1] : "";
    }

    private static Dictionary<string, string> GetAdapters()
    {
        var adapters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; index < 32; index++)
        {
            var device = NativeMethods.DISPLAY_DEVICE.Create();
            if (!NativeMethods.EnumDisplayDevices(null, index, ref device, 0)) break;
            if (!string.IsNullOrEmpty(device.DeviceName))
                adapters[device.DeviceName] = device.DeviceString?.Trim() ?? "Unknown GPU";
        }
        return adapters;
    }
}
