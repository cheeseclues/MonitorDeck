using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MonitorDeck;

public partial class MonitorCard : System.Windows.Controls.UserControl
{
    private bool _suppressEvents = true;
    private ProbeResult? _probe;
    internal DisplayInfo Display { get; }
    internal event Action<MonitorCard, ControlKind, int>? SettingRequested;
    internal event Action<MonitorCard, int>? GpuPreviewRequested;
    internal event Action<MonitorCard>? GpuRestoreRequested;
    internal event Action<MonitorCard, int>? SoftwareDimRequested;

    internal MonitorCard(DisplayInfo display)
    {
        InitializeComponent();
        Display = display;
        NameText.Text = display.Name;
        ConnectionText.Text = $"{display.DisplayPath}  •  {display.Adapter}  •  {display.Width}×{display.Height}";
        StateText.Text = "Checking DDC/CI";
        // Limit first GPU experiment to the exact Samsung G8 previously tested.
        GpuContrastPanel.Visibility = display.HardwareId.Equals("SAM71EE", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
        GpuContrastSlider.ValueChanged += (_, _) =>
            GpuContrastValue.Text = $"{(int)Math.Round(GpuContrastSlider.Value)}%";
        _suppressEvents = false;
    }

    internal uint GetMaximum(ControlKind kind) => kind == ControlKind.Brightness
        ? _probe?.Brightness.Maximum ?? 0
        : _probe?.Contrast.Maximum ?? 0;

    internal void ApplyProbe(ProbeResult probe)
    {
        _probe = probe;
        UpdateFeature(ControlKind.Brightness, probe.Brightness);
        UpdateFeature(ControlKind.Contrast, probe.Contrast);

        bool any = probe.Brightness.Available || probe.Contrast.Available;
        StateText.Text = any ? "DDC/CI detected" : "DDC/CI unavailable";
        StateBadge.Background = Brush(any ? "#173D35" : "#45331B");
        StateBadge.BorderBrush = Brush(any ? "#236F58" : "#8C612C");
        StateText.Foreground = Brush(any ? "#A7F3D0" : "#FDE68A");
        StateBadge.ToolTip = probe.Description;
    }

    internal void ShowWriteResult(ControlKind kind, WriteResult result)
    {
        var label = kind == ControlKind.Brightness ? BrightnessDetail : ContrastDetail;
        label.Text = result.Verified ? "Readback OK • visual effect unverified" : "DDC/CI issue • see tooltip";
        label.Foreground = Brush("#FDE68A");
        label.ToolTip = result.Detail;
        if (result.Verified || result.Detail.StartsWith("Monitor returned", StringComparison.Ordinal) ||
            result.Detail.StartsWith("Write rejected", StringComparison.Ordinal))
            SetSlider(kind, result.ActualPercent);
    }

    internal void ShowPending(ControlKind kind)
    {
        var label = kind == ControlKind.Brightness ? BrightnessDetail : ContrastDetail;
        label.Text = "Writing DDC/CI...";
        label.Foreground = Brush("#93C5FD");
    }

    private void UpdateFeature(ControlKind kind, FeatureRead feature)
    {
        var slider = kind == ControlKind.Brightness ? BrightnessSlider : ContrastSlider;
        var valueLabel = kind == ControlKind.Brightness ? BrightnessValue : ContrastValue;
        var detail = kind == ControlKind.Brightness ? BrightnessDetail : ContrastDetail;
        _suppressEvents = true;
        slider.IsEnabled = feature.Available;
        slider.Value = feature.Available ? feature.Percent : 0;
        valueLabel.Text = feature.Available ? $"{feature.Percent}%" : "—";
        detail.Text = feature.Available ? $"Hardware • {feature.Current}/{feature.Maximum}" : "Control unavailable";
        detail.ToolTip = feature.Detail;
        detail.Foreground = Brush("#8D9CB2");
        _suppressEvents = false;
    }

    private void SetSlider(ControlKind kind, int value)
    {
        var slider = kind == ControlKind.Brightness ? BrightnessSlider : ContrastSlider;
        var label = kind == ControlKind.Brightness ? BrightnessValue : ContrastValue;
        _suppressEvents = true;
        slider.Value = Math.Clamp(value, 0, 100);
        label.Text = $"{value}%";
        _suppressEvents = false;
    }

    private void BrightnessSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => SliderChanged(ControlKind.Brightness, e.NewValue);

    private void ContrastSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => SliderChanged(ControlKind.Contrast, e.NewValue);

    private void SliderChanged(ControlKind kind, double value)
    {
        if (_suppressEvents || !IsLoaded || _probe is null) return;
        int percent = Math.Clamp((int)Math.Round(value), 0, 100);
        if (kind == ControlKind.Brightness)
            BrightnessValue.Text = $"{percent}%";
        else
            ContrastValue.Text = $"{percent}%";
        SettingRequested?.Invoke(this, kind, percent);
    }

    private void SoftwareBrightnessSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _suppressEvents) return;
        int percent = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        SoftwareBrightnessValue.Text = $"{percent}%";
        SoftwareDimRequested?.Invoke(this, percent);
    }

    internal void SetSoftwareLevel(int percent)
    {
        _suppressEvents = true;
        SoftwareBrightnessSlider.Value = percent;
        SoftwareBrightnessValue.Text = $"{percent}%";
        _suppressEvents = false;
    }

    internal void SetSoftwareMessage(string message, bool warning = false)
    {
        SoftwareBrightnessDetail.Text = message;
        SoftwareBrightnessDetail.Foreground = Brush(warning ? "#FDE68A" : "#97A9BD");
        SoftwareBrightnessDetail.ToolTip = message;
    }

    private void GpuTestButton_OnClick(object sender, RoutedEventArgs e)
    {
        int percent = (int)Math.Round(GpuContrastSlider.Value);
        GpuPreviewRequested?.Invoke(this, percent);
    }

    private void GpuRestoreButton_OnClick(object sender, RoutedEventArgs e)
        => GpuRestoreRequested?.Invoke(this);

    internal void SetGpuMessage(string message, bool warning = false)
    {
        GpuContrastDetail.Text = message;
        GpuContrastDetail.Foreground = Brush(warning ? "#FDE68A" : "#8D9CB2");
    }

    private static SolidColorBrush Brush(string hex) =>
        new((System.Windows.Media.Color)
            System.Windows.Media.ColorConverter.ConvertFromString(hex));
}
