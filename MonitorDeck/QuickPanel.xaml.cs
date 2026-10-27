using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MonitorDeck;

public partial class QuickPanel : Window
{
    private readonly Dictionary<string, QuickMonitorRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    internal event Action<DisplayInfo, ControlKind, int>? HardwareRequested;
    internal event Action<DisplayInfo, int>? SoftwareRequested;
    internal event Action? RescanRequested;
    internal event Action? ClearDimmingRequested;
    internal event Action? FullViewRequested;
    private bool _allowClose;
    private bool _updatingNightLight;
    private bool _nightLightBusy;
    private bool _updatingNightLightStrength;
    private int _currentNightLightKelvin = -1;
    // Live preview is intentionally rate-limited: Windows Night Light uses an
    // undocumented CloudStore format, so do not write for every mouse pixel.
    private readonly DispatcherTimer _nightLightStrengthThrottle = new()
        { Interval = TimeSpan.FromMilliseconds(180) };
    private int? _pendingNightLightStrength;
    private bool _nightLightStrengthWriting;
    private bool _nightLightStrengthNeedsBackup = true;
    private readonly DispatcherTimer _nightLightRefresh = new()
        { Interval = TimeSpan.FromSeconds(10) };

    internal QuickPanel()
    {
        InitializeComponent();
        // This is a persistent floating palette: clicking away does NOT hide it.
        // Left-clicking the notification icon again, or pressing ×, toggles it off.
        // Hiding the panel preserves its monitor rows and current slider values.
        Closing += (_, e) => { if (!_allowClose) { e.Cancel = true; Hide(); } };
        _nightLightRefresh.Tick += (_, _) =>
        {
            if (IsVisible && !_nightLightBusy) _ = RefreshNightLightAsync();
        };
        _nightLightRefresh.Start();
        _nightLightStrengthThrottle.Tick += async (_, _) =>
            await FlushNightLightStrengthAsync();
    }

    internal void ClosePermanently()
    {
        _allowClose = true;
        _nightLightRefresh.Stop();
        _nightLightStrengthThrottle.Stop();
        Close();
    }

    internal void SetDisplays(IReadOnlyList<DisplayInfo> displays,
        IReadOnlyDictionary<string, int> softwareLevels)
    {
        MonitorRows.Children.Clear();
        _rows.Clear();
        CountText.Text = $"{displays.Count} active monitor{(displays.Count == 1 ? "" : "s")}";
        foreach (DisplayInfo display in displays)
        {
            int brightness = softwareLevels.TryGetValue(display.Key, out int value) ? value : 100;
            var row = new QuickMonitorRow(display, brightness);
            row.HardwareRequested += (d, kind, percent) => HardwareRequested?.Invoke(d, kind, percent);
            row.SoftwareRequested += (d, percent) => SoftwareRequested?.Invoke(d, percent);
            _rows.Add(display.Key, row);
            MonitorRows.Children.Add(row);
        }
        StatusText.Text = "Probing...";
    }

    internal void ApplyProbe(string key, ProbeResult result)
    {
        if (_rows.TryGetValue(key, out QuickMonitorRow? row)) row.ApplyProbe(result);
    }

    internal void SetHardwareValue(string key, ControlKind kind, int percent)
    {
        if (_rows.TryGetValue(key, out QuickMonitorRow? row))
            row.SetHardwareValue(kind, percent);
    }

    internal void ShowWriteResult(string key, ControlKind kind, WriteResult result)
    {
        if (_rows.TryGetValue(key, out QuickMonitorRow? row))
            row.ShowWriteResult(kind, result);
    }

    internal void SetSoftwareLevel(string key, int percent, string? message = null)
    {
        if (_rows.TryGetValue(key, out QuickMonitorRow? row))
        {
            row.SetSoftwareLevel(percent);
            if (message is not null) row.ShowMessage(message);
        }
    }

    internal void SetStatus(string text) => StatusText.Text = text;

    internal async Task RefreshNightLightAsync()
    {
        if (_nightLightBusy || _pendingNightLightStrength.HasValue ||
            NightLightStrengthSlider.IsMouseCaptureWithin) return;
        _nightLightBusy = true;
        try
        {
            var result = await Task.Run(() =>
                (NightLightService.GetStatus(), NightLightService.GetStrength()));
            UpdateNightLight(result.Item1);
            UpdateNightLightStrength(result.Item2);
        }
        finally { _nightLightBusy = false; }
    }

    private void UpdateNightLight(NightLightService.Status status)
    {
        _updatingNightLight = true;
        try
        {
            NightLightSwitch.IsChecked = status.Available && status.Enabled;
            NightLightSwitch.IsEnabled = status.Available;
            NightLightStateText.Text = status.Message;
            NightLightStateText.Foreground = status.Available
                ? System.Windows.Media.Brushes.LightSteelBlue
                : System.Windows.Media.Brushes.Orange;
        }
        finally { _updatingNightLight = false; }
    }

    private void UpdateNightLightStrength(NightLightService.StrengthStatus strength,
        bool force = false)
    {
        // Don't move the thumb from under the user's pointer during a scheduled refresh.
        if (!force && (NightLightStrengthSlider.IsMouseCaptureWithin ||
                       _pendingNightLightStrength.HasValue ||
                       _nightLightStrengthWriting)) return;
        _updatingNightLightStrength = true;
        try
        {
            NightLightStrengthSlider.IsEnabled = strength.Available;
            NightLightStrengthNote.Text = strength.Message;
            NightLightStrengthNote.Foreground = strength.Available
                ? System.Windows.Media.Brushes.LightSteelBlue
                : System.Windows.Media.Brushes.Orange;
            _currentNightLightKelvin = strength.Available ? strength.Kelvin : -1;
            if (strength.Available)
                NightLightStrengthSlider.Value = strength.Percent;
            NightLightStrengthValue.Text = strength.Available ? $"{strength.Percent}%" : "—";
        }
        finally { _updatingNightLightStrength = false; }
    }

    private void NightLightStrength_OnValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _updatingNightLightStrength ||
            !NightLightStrengthSlider.IsEnabled) return;

        int requested = (int)Math.Round(e.NewValue);
        NightLightStrengthValue.Text = $"{requested}%";
        if (6500 - 53 * requested == _currentNightLightKelvin &&
            !_nightLightStrengthWriting)
        {
            _pendingNightLightStrength = null;
            return;
        }

        bool startingDrag = !_nightLightStrengthThrottle.IsEnabled;
        if (startingDrag)
        {
            _nightLightStrengthNeedsBackup = true;
            _nightLightStrengthThrottle.Start();
        }
        // Latest-value-wins: intermediate positions are discarded while a
        // previous registry update is still running on the worker thread.
        _pendingNightLightStrength = requested;
        // Apply the first movement immediately rather than waiting for the
        // timer to fire. Subsequent writes are serialized/coalesced.
        if (startingDrag) _ = FlushNightLightStrengthAsync();
    }

    private void NightLightStrength_OnCommit(object sender, MouseButtonEventArgs e)
        => _ = FlushNightLightStrengthAsync();

    private void NightLightStrength_OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down
            or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            _ = FlushNightLightStrengthAsync();
    }

    private async Task FlushNightLightStrengthAsync()
    {
        if (_nightLightStrengthWriting || _nightLightBusy) return;
        if (!_pendingNightLightStrength.HasValue)
        {
            if (!NightLightStrengthSlider.IsMouseCaptureWithin)
                _nightLightStrengthThrottle.Stop();
            return;
        }

        int requested = _pendingNightLightStrength.Value;
        _pendingNightLightStrength = null;
        if (_currentNightLightKelvin == 6500 - 53 * requested)
            return;

        _nightLightStrengthWriting = true;
        _nightLightBusy = true;
        bool backupThisWrite = _nightLightStrengthNeedsBackup;
        NightLightStrengthNote.Text = $"Applying {requested}%…";
        try
        {
            // Serialize Windows writes and keep the UI slider enabled. After
            // the first successful write in a drag, do not create another
            // backup for each live preview step.
            NightLightService.StrengthStatus result = await Task.Run(() =>
                NightLightService.SetStrength(requested, backupThisWrite));
            DiagnosticLog.Write($"Night Light {requested}%: " + result.Message);
            if (result.Available)
            {
                _nightLightStrengthNeedsBackup = false;
                _currentNightLightKelvin = result.Kelvin;
                if (!_pendingNightLightStrength.HasValue &&
                    !NightLightStrengthSlider.IsMouseCaptureWithin)
                    UpdateNightLightStrength(result, force: true);
                else
                    NightLightStrengthNote.Text = result.Message;
            }
            else
            {
                // Stop the live preview after any validation/write failure.
                _pendingNightLightStrength = null;
                _nightLightStrengthThrottle.Stop();
                NightLightStrengthNote.Text = result.Message;
                NightLightStrengthNote.Foreground = System.Windows.Media.Brushes.Orange;
            }
        }
        catch (Exception ex)
        {
            _pendingNightLightStrength = null;
            _nightLightStrengthThrottle.Stop();
            NightLightStrengthNote.Text = "Strength failed: " + ex.Message;
            NightLightStrengthNote.Foreground = System.Windows.Media.Brushes.Orange;
        }
        finally
        {
            _nightLightStrengthWriting = false;
            _nightLightBusy = false;
            if (_pendingNightLightStrength.HasValue)
            {
                // If the user moved the thumb while a write was in flight,
                // reliably drain the newest request instead of relying on a
                // future timer tick or mouse-up notification.
                Dispatcher.BeginInvoke(new Action(() =>
                    _ = FlushNightLightStrengthAsync()), DispatcherPriority.Background);
            }
            else if (!NightLightStrengthSlider.IsMouseCaptureWithin)
            {
                _nightLightStrengthThrottle.Stop();
            }
        }
    }

    private async void NightLightSwitch_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingNightLight || _nightLightBusy || !IsLoaded) return;
        bool requested = NightLightSwitch.IsChecked == true;
        _nightLightBusy = true;
        NightLightSwitch.IsEnabled = false;
        NightLightStateText.Text = "Requesting " + (requested ? "on" : "off") + "…";
        try
        {
            NightLightService.Status status = await Task.Run(
                () => NightLightService.SetEnabled(requested));
            UpdateNightLight(status);
        }
        catch (Exception ex)
        {
            UpdateNightLight(new NightLightService.Status(false, false,
                "Toggle failed: " + ex.Message));
        }
        finally { _nightLightBusy = false; }
    }

    // Windows sometimes caches a Night Light temperature change without
    // redrawing it immediately. This explicit, one-click fallback may flash;
    // never invoke it from slider ValueChanged or a timer.
    private async void NightLightForceRefresh_OnClick(object sender, RoutedEventArgs e)
    {
        if (_nightLightBusy || _nightLightStrengthWriting) return;

        NightLightService.Status original = NightLightService.GetStatus();
        if (!original.Available)
        {
            NightLightStrengthNote.Text = original.Message;
            return;
        }
        if (!original.Enabled)
        {
            NightLightStrengthNote.Text = "Enable Night Light first, then apply warmth.";
            return;
        }

        _nightLightBusy = true;
        NightLightRefreshButton.IsEnabled = false;
        try
        {
            NightLightStrengthNote.Text = "Reapplying Night Light (brief flash possible)…";
            NightLightService.Status off = await Task.Run(() => NightLightService.SetEnabled(false));
            if (!off.Available || off.Enabled)
            {
                NightLightStrengthNote.Text = "Refresh failed: " + off.Message;
                return;
            }
            try
            {
                await Task.Delay(120);
            }
            finally
            {
                // Always attempt to turn Night Light back ON, even if delay
                // is interrupted. A failure is surfaced to the user.
                NightLightService.Status on = await Task.Run(() => NightLightService.SetEnabled(true));
                UpdateNightLight(on);
                NightLightStrengthNote.Text = on.Available && on.Enabled
                    ? "Manual refresh complete"
                    : "Night Light could not restore; turn it on in Windows Settings.";
            }
        }
        catch (Exception ex)
        {
            // Best effort restore even if something unexpected happened.
            NightLightService.Status restore = await Task.Run(() => NightLightService.SetEnabled(true));
            UpdateNightLight(restore);
            NightLightStrengthNote.Text = "Refresh failed: " + ex.Message;
        }
        finally
        {
            NightLightRefreshButton.IsEnabled = true;
            _nightLightBusy = false;
        }
    }

    private void NightLightSettings_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:nightlight") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open Windows Night Light settings: " + ex.Message,
                "MonitorDeck", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void RescanButton_OnClick(object sender, RoutedEventArgs e)
        => RescanRequested?.Invoke();
    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Hide();
    private void ClearDimmingButton_OnClick(object sender, RoutedEventArgs e)
        => ClearDimmingRequested?.Invoke();
    private void FullViewButton_OnClick(object sender, RoutedEventArgs e)
    {
        Hide();
        FullViewRequested?.Invoke();
    }
}
