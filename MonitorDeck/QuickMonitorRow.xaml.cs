using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MonitorDeck;

public partial class QuickMonitorRow : UserControl
{
    private bool _suppress = true;
    internal DisplayInfo Display { get; }
    internal event Action<DisplayInfo, ControlKind, int>? HardwareRequested;
    internal event Action<DisplayInfo, int>? SoftwareRequested;

    internal QuickMonitorRow(DisplayInfo display, int softwareLevel)
    {
        InitializeComponent();
        Display = display;
        MonitorName.Text = display.Name;
        MonitorMeta.Text = $"{display.DisplayPath}  ·  {display.Adapter}";
        MonitorMeta.ToolTip = $"{display.Width}×{display.Height} · {display.Adapter}";
        SetSoftwareLevel(softwareLevel);
        _suppress = false;
    }

    internal void ApplyProbe(ProbeResult result)
    {
        bool hasBrightness = result.Brightness.Available;
        bool hasContrast = result.Contrast.Available;
        HardwareBrightnessSection.Visibility = hasBrightness ? Visibility.Visible : Visibility.Collapsed;
        HardwareContrastSection.Visibility = hasContrast ? Visibility.Visible : Visibility.Collapsed;
        // A missing hardware brightness control gets the capture-excluded fallback,
        // even if another VCP feature (contrast) was detected.
        // The G8 accepts hardware contrast writes without updating the picture.
        // Offer independent overlay dimming even when its DDC/CI brightness works.
        bool optionalG8Dimming = displayIsNeoG8;
        // If a monitor becomes hardware-controllable after changing cables,
        // keep the software slider visible while an old dim level is active.
        bool softwareAlreadyActive = SoftwareSlider.Value < 100;
        SoftwareBrightnessSection.Visibility = (!hasBrightness || optionalG8Dimming || softwareAlreadyActive)
            ? Visibility.Visible : Visibility.Collapsed;
        SoftwareLabel.Text = optionalG8Dimming && hasBrightness
            ? "Software dimming (optional)" : "Software dimming";
        if (hasBrightness) SetHardwareValue(ControlKind.Brightness, result.Brightness.Percent);
        if (hasContrast) SetHardwareValue(ControlKind.Contrast, result.Contrast.Percent);
        ModeText.Text = hasBrightness && optionalG8Dimming ? "Hybrid" :
            hasBrightness ? "DDC/CI" : hasContrast ? "Hybrid" : "Software";
        ModeBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            hasBrightness ? "#194537" : hasContrast ? "#39425A" : "#35402C"));
        ModeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            hasBrightness ? "#ACF3D2" : hasContrast ? "#D2DAFF" : "#DBF2AD"));
        DetailText.Text = hasBrightness && hasContrast ? "Hardware controls" :
            hasBrightness ? "Hardware brightness · contrast not available" :
            hasContrast ? "Software brightness · hardware contrast" :
            "Screen overlay · hardware controls unavailable";
        if (displayIsNeoG8 && hasContrast)
            DetailText.Text = "G8: contrast readback ≠ visible change; dimming is separate";
        DetailText.ToolTip = !hasBrightness ? result.Brightness.Detail :
            !hasContrast ? result.Contrast.Detail : "DDC/CI readback does not guarantee a visual change";
    }

    private bool displayIsNeoG8 => Display.HardwareId.Equals("SAM71EE", StringComparison.OrdinalIgnoreCase);

    internal void SetHardwareValue(ControlKind kind, int percent)
    {
        _suppress = true;
        if (kind == ControlKind.Brightness)
        {
            BrightnessSlider.Value = Math.Clamp(percent, 0, 100);
            BrightnessValue.Text = $"{percent}%";
        }
        else
        {
            ContrastSlider.Value = Math.Clamp(percent, 0, 100);
            ContrastValue.Text = $"{percent}%";
        }
        _suppress = false;
    }

    internal void SetSoftwareLevel(int percent)
    {
        _suppress = true;
        SoftwareSlider.Value = Math.Clamp(percent, 0, 100);
        SoftwareValue.Text = $"{(int)SoftwareSlider.Value}%";
        _suppress = false;
    }

    internal void ShowWriteResult(ControlKind kind, WriteResult result)
    {
        DetailText.Text = result.Verified && displayIsNeoG8 && kind == ControlKind.Contrast
            ? "Neo G8: value saved, but picture may not update"
            : result.Verified ? "DDC/CI readback OK · visual change unverified"
            : "DDC/CI write issue · see tooltip";
        DetailText.ToolTip = result.Detail;
        if (result.Verified || result.Detail.StartsWith("Monitor returned", StringComparison.Ordinal) ||
            result.Detail.StartsWith("Write rejected", StringComparison.Ordinal))
            SetHardwareValue(kind, result.ActualPercent);
    }

    internal void ShowMessage(string message)
    {
        DetailText.Text = message;
        DetailText.ToolTip = message;
    }

    private void BrightnessSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => HardwareChanged(ControlKind.Brightness, e.NewValue);
    private void ContrastSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => HardwareChanged(ControlKind.Contrast, e.NewValue);
    private void HardwareChanged(ControlKind kind, double value)
    {
        if (_suppress || !IsLoaded) return;
        int percent = Math.Clamp((int)Math.Round(value), 0, 100);
        if (kind == ControlKind.Brightness) BrightnessValue.Text = $"{percent}%";
        else ContrastValue.Text = $"{percent}%";
        HardwareRequested?.Invoke(Display, kind, percent);
    }
    private void SoftwareSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress || !IsLoaded) return;
        int percent = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        SoftwareValue.Text = $"{percent}%";
        SoftwareRequested?.Invoke(Display, percent);
    }
}
