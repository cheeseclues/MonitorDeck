using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MonitorDeck;

/// <summary>
/// Reversible experiment: applies an SDR gamma-ramp approximation of contrast to
/// one named Windows display output. This is NOT the NVIDIA Control Panel API.
/// Windows, HDR and the GPU driver may reject, ignore, or replace the ramp.
/// </summary>
internal sealed class GpuContrastPreview
{
    private const int RampLength = 3 * 256;
    private string? _displayPath;
    private ushort[]? _original;

    public bool HasPreview => _displayPath != null && _original != null;

    public string Apply(string devicePath, int percent)
    {
        if (HasPreview)
            Restore(); // Always restore original ramp before starting a new preview.

        percent = Math.Clamp(percent, 0, 100);
        IntPtr dc = OpenDisplayDc(devicePath);
        try
        {
            var initial = new ushort[RampLength];
            if (!NativeMethods.GetDeviceGammaRamp(dc, initial))
                throw LastError("Cannot read the current gamma ramp");

            // Preserve all three color channels from whatever ICC/NVIDIA settings
            // the driver currently exposes, instead of assuming a linear ramp.
            var modified = new ushort[RampLength];
            double scale = 0.80 + percent * 0.004; // 0.80..1.20, neutral 50.
            for (int i = 0; i < RampLength; i++)
            {
                double input = initial[i] / 65535.0;
                double mapped = Math.Clamp(0.5 + (input - 0.5) * scale, 0.0, 1.0);
                modified[i] = (ushort)Math.Round(mapped * 65535.0);
            }

            // Save the baseline BEFORE setting the ramp, so a native exception
            // after this point can still be recovered by the reset button.
            _displayPath = devicePath;
            _original = initial;
            if (!NativeMethods.SetDeviceGammaRamp(dc, modified))
            {
                int error = Marshal.GetLastWin32Error();
                Restore();
                throw new Win32Exception(error, "Windows rejected the software contrast ramp");
            }
            // TRUE isn't proof the driver actually applied the new ramp.
            return $"Preview requested for {devicePath} at {percent}%. Automatically restores in 10 seconds. Visual effect unverified.";
        }
        finally { NativeMethods.DeleteDC(dc); }
    }

    public void Restore()
    {
        if (_displayPath is null || _original is null) return;
        string devicePath = _displayPath;
        ushort[] ramp = _original;
        // Keep a retry path on errors; caller can use Reset or log out of Windows.
        IntPtr dc = OpenDisplayDc(devicePath);
        try
        {
            if (!NativeMethods.SetDeviceGammaRamp(dc, ramp))
                throw LastError("Windows rejected restoring the original gamma ramp");
            _displayPath = null;
            _original = null;
        }
        finally { NativeMethods.DeleteDC(dc); }
    }

    private static IntPtr OpenDisplayDc(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            throw new ArgumentException("No Windows display path was provided", nameof(devicePath));
        // IMPORTANT: Passing the specific \\.\DISPLAYn device avoids changing all outputs.
        IntPtr dc = NativeMethods.CreateDC("DISPLAY", devicePath, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) throw LastError($"Could not open display {devicePath}");
        return dc;
    }

    private static Win32Exception LastError(string message)
        => new(Marshal.GetLastWin32Error(), message);
}
