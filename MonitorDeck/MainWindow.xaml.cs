using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using System.Reflection;

namespace MonitorDeck;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _periodicTimer;
    private readonly DispatcherTimer _eventDebounce;
    private readonly DispatcherTimer _gpuTimeout;
    private readonly GpuContrastPreview _gpuPreview = new();
    private const int EmergencyClearHotkey = 0x4D44;
    private bool _emergencyHotkeyRegistered;
    private readonly OverlayDimming _dimming = new();
    private readonly SoftwareDimmingSettings _softwareSettings = SoftwareDimmingSettings.Load();
    private readonly DispatcherTimer _saveSettingsTimer = new()
        { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Dictionary<string, int> _softwareLevels = new(StringComparer.OrdinalIgnoreCase);
    private bool _gpuConsentShown;
    private bool _presetRunning;
    private readonly Dictionary<string, MonitorCard> _cards = new();
    private readonly Dictionary<string, CancellationTokenSource> _pendingWrites = new();
    private readonly Dictionary<string, SemaphoreSlim> _monitorGates = new();
    private readonly Forms.NotifyIcon _tray;
    private readonly Drawing.Icon _appIcon;
    private readonly QuickPanel _quickPanel;
    private string? _popupScreenDevice;
    private string _fingerprint = "";
    private bool _scanBusy;
    private bool _scanAgain;
    private bool _exiting;
    private int _generation;

    public MainWindow()
    {
        InitializeComponent();

        _saveSettingsTimer.Tick += (_, _) =>
        {
            _saveSettingsTimer.Stop();
            SaveSoftwareSettings();
        };

        _quickPanel = new QuickPanel();
        _quickPanel.HardwareRequested += OnQuickHardwareRequested;
        _quickPanel.SoftwareRequested += OnSoftwareDimRequested;
        _quickPanel.RescanRequested += ScheduleRescan;
        _quickPanel.ClearDimmingRequested += ClearDimming;
        _quickPanel.FullViewRequested += ShowMainWindow;
        // Re-anchor to the bottom of the taskbar screen as asynchronous probe
        // results add/remove slider sections and change the popup's height.
        _quickPanel.SizeChanged += (_, _) =>
        {
            if (_quickPanel.IsVisible)
                Dispatcher.BeginInvoke(new Action(PositionQuickPanel), DispatcherPriority.Background);
        };

        _gpuTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _gpuTimeout.Tick += (_, _) => RestoreGpuPreview();

        _eventDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _eventDebounce.Tick += async (_, _) =>
        {
            _eventDebounce.Stop();
            await RescanAsync(force: true);
        };

        // A fallback is essential for displays that do not generate reliable hot-plug messages.
        _periodicTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _periodicTimer.Tick += async (_, _) => await RescanAsync(force: false);

        var menu = new Forms.ContextMenuStrip();
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        menu.Items.Add(new Forms.ToolStripMenuItem($"MonitorDeck v{version?.ToString(3) ?? "unknown"}")
        { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quick controls", null, (_, _) => Dispatcher.Invoke(ToggleQuickPanel));
        menu.Items.Add("Full monitor diagnostics", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add("Rescan monitors", null, (_, _) => Dispatcher.Invoke(ScheduleRescan));
        menu.Items.Add("Reapply software dimming", null, (_, _) => Dispatcher.Invoke(ReapplySoftwareDimming));
        menu.Items.Add("Open diagnostics log", null, (_, _) =>
        {
            DiagnosticLog.Write("Diagnostics log opened");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe", Arguments = $"\"{DiagnosticLog.PathOnDisk}\"",
                UseShellExecute = true
            });
        });
        menu.Items.Add("Clear dimming + restore full brightness", null, (_, _) => Dispatcher.Invoke(ClearDimming));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Install/update all-users startup...", null, (_, _) => Dispatcher.Invoke(() =>
        {
            if (StartupRegistration.IsCurrentVersionRegistered)
            {
                MessageBox.Show("This MonitorDeck version already starts when any Windows user signs in.",
                    "MonitorDeck — startup", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            bool okay = StartupRegistration.RequestInstallWithUac(out string result);
            MessageBox.Show(result, "MonitorDeck — startup",
                MessageBoxButton.OK, okay ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        // A real multi-size .ico is embedded in the single-file Windows EXE.
        using (var iconStream = Assembly.GetExecutingAssembly()
                   .GetManifestResourceStream("MonitorDeck.TrayIcon.ico")
                   ?? throw new InvalidOperationException("MonitorDeck icon resource missing"))
            _appIcon = new Drawing.Icon(iconStream);
        _tray = new Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = $"MonitorDeck v{version?.ToString(3) ?? "unknown"} - monitor controls",
            ContextMenuStrip = menu,
            Visible = true
        };
        // One click opens the compact popup. The tray right-click menu is still available.
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(new Action(ToggleQuickPanel));
        };

        SourceInitialized += (_, _) =>
        {
            HwndSource? source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WindowsMessageHook);
            _emergencyHotkeyRegistered = NativeMethods.RegisterHotKey(
                new WindowInteropHelper(this).Handle, EmergencyClearHotkey,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT |
                NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT,
                NativeMethods.VK_F12);
        };
        Loaded += async (_, _) =>
        {
            DiagnosticLog.Write("Application started from " + Environment.ProcessPath);
            // StartupUri briefly creates this hidden HWND for display notifications.
            // The user interacts with the tray popup, not a full-sized startup window.
            Hide();
            await RescanAsync(force: true);
            _periodicTimer.Start();
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                ShowInTaskbar = false;
                Hide();
            }
        };
        Closing += (_, e) =>
        {
            if (!_exiting)
            {
                e.Cancel = true;
                ShowInTaskbar = false;
                Hide();
            }
        };
        Closed += (_, _) =>
        {
            _periodicTimer.Stop();
            _eventDebounce.Stop();
            _saveSettingsTimer.Stop();
            SaveSoftwareSettings();
            if (_emergencyHotkeyRegistered)
                NativeMethods.UnregisterHotKey(new WindowInteropHelper(this).Handle,
                    EmergencyClearHotkey);
            RestoreGpuPreview();
            _quickPanel.ClosePermanently();
            _dimming.Dispose();
            CancelPendingWrites();
            _tray.Visible = false;
            _tray.Dispose();
            _appIcon.Dispose();
            menu.Dispose();
        };
    }

    private IntPtr WindowsMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
        ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt64() == EmergencyClearHotkey)
        {
            // Emergency recovery should only remove overlays, not touch hardware.
            ClearSoftwareDimming();
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == NativeMethods.WM_DISPLAYCHANGE || msg == NativeMethods.WM_DEVICECHANGE ||
            (msg == NativeMethods.WM_POWERBROADCAST &&
             (wParam.ToInt32() == NativeMethods.PBT_APMRESUMEAUTOMATIC ||
              wParam.ToInt32() == NativeMethods.PBT_APMRESUMESUSPEND)))
            ScheduleRescan();

        return IntPtr.Zero;
    }

    private void ScheduleRescan()
    {
        if (_exiting) return;
        if (_gpuPreview.HasPreview) RestoreGpuPreview();
        _eventDebounce.Stop();
        _eventDebounce.Start();
    }

    private async void RescanButton_OnClick(object sender, RoutedEventArgs e)
        => await RescanAsync(force: true);

    private void ResetDimmingButton_OnClick(object sender, RoutedEventArgs e) => ClearDimming();

    // The visible Clear dimming button applies a real, verified hardware preset.
    // Re-probe on demand rather than trusting a cached maximum from an earlier scan.
    // Keep the global emergency hotkey separate: it clears overlays immediately.
    private readonly record struct PresetSpec(string Id, string Name, int Brightness, int Contrast);
    private readonly record struct PresetResult(string Name, string Outcome, int Verified, int Failed);

    private async void ClearDimming()
    {
        if (_presetRunning)
        {
            _quickPanel.SetStatus("Full-brightness preset is still running...");
            return;
        }
        _presetRunning = true;
        try
        {
            // The dimming overlay is removed immediately, even if DDC/CI later fails.
            CancelPendingWrites();
            RestoreGpuPreview();
            ClearSoftwareDimming();
            StatusText.Text = "Software dimming cleared. Applying hardware presets...";
            _quickPanel.SetStatus("Applying hardware presets...");
            DiagnosticLog.Write("Full-brightness preset requested");

            PresetSpec[] specs =
            {
                new("AOCB403", "AOC Q27G4", 100, 50),
                new("SAM71EE", "Samsung Odyssey Neo G8", 100, 75),
                new("ACI23C1", "ASUS VX238", 100, 65)
            };
            List<PresetResult> results = await Task.Run(() =>
            {
                List<DisplayInfo> active = DisplayEnumerator.GetActiveDisplays();
                DiagnosticLog.Write("Detected monitor IDs: " + string.Join(", ",
                    active.Select(d => $"{d.Name}={d.HardwareId}")));
                var collected = new List<PresetResult>();
                foreach (PresetSpec spec in specs)
                {
                    List<DisplayInfo> matches = active.Where(d =>
                        string.Equals(d.HardwareId, spec.Id,
                            StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count != 1)
                    {
                        string reason = matches.Count == 0 ? "display not found" :
                            "multiple matching displays; skipped for safety";
                        DiagnosticLog.Write($"{spec.Name}: {reason}");
                        collected.Add(new PresetResult(spec.Name, reason, 0, 2));
                        continue;
                    }

                    DisplayInfo display = matches[0];
                    int verified = 0, failed = 0;
                    var outcomes = new List<string>();
                    try
                    {
                        ProbeResult probe = DdcService.Probe(display);
                        foreach (ControlKind kind in new[] { ControlKind.Brightness, ControlKind.Contrast })
                        {
                            FeatureRead feature = kind == ControlKind.Brightness ? probe.Brightness : probe.Contrast;
                            int target = kind == ControlKind.Brightness ? spec.Brightness : spec.Contrast;
                            if (!feature.Available || feature.Maximum == 0)
                            {
                                failed++;
                                outcomes.Add($"{kind}: unavailable ({feature.Detail})");
                                continue;
                            }
                            WriteResult write = DdcService.Set(display, kind, target, feature.Maximum);
                            if (write.Verified) verified++; else failed++;
                            outcomes.Add($"{kind} {target}%: " +
                                (write.Verified ? "verified" : "failed") + " (" + write.Detail + ")");
                        }
                    }
                    catch (Exception ex)
                    {
                        failed += 2;
                        outcomes.Add("DDC/CI exception: " + ex.Message);
                    }
                    string outcome = string.Join("; ", outcomes);
                    DiagnosticLog.Write($"{spec.Name}: {outcome}");
                    collected.Add(new PresetResult(spec.Name, outcome, verified, failed));
                }
                return collected;
            });

            // Rescanning refreshes both hardware sliders with actual monitor values.
            await RescanAsync(force: true);
            int good = results.Sum(r => r.Verified);
            int failed = results.Sum(r => r.Failed);
            string report = $"Software 100%. Preset: {good}/6 DDC/CI writes verified" +
                (failed > 0 ? $", {failed} skipped/failed. Check diagnostics log." : ".");
            StatusText.Text = report;
            _quickPanel.SetStatus(report);
            DiagnosticLog.Write(report);
        }
        catch (Exception ex)
        {
            string message = "Hardware preset failed: " + ex.Message;
            StatusText.Text = message;
            _quickPanel.SetStatus(message);
            DiagnosticLog.Write(message);
        }
        finally { _presetRunning = false; }
    }

    // Also used by the emergency hotkey: no hardware changes, even if an
    // overlay was set to 0% and the user cannot see the desktop.
    private void ClearSoftwareDimming()
    {
        _dimming.Clear();
        _softwareLevels.Clear();
        _softwareSettings.Clear();
        _saveSettingsTimer.Stop();
        SaveSoftwareSettings();
        foreach (MonitorCard card in _cards.Values)
        {
            card.SetSoftwareLevel(100);
            card.SetSoftwareMessage("Off. Removed software overlay.");
            _quickPanel.SetSoftwareLevel(card.Display.Key, 100, "Software dimming cleared");
        }
    }

    private void OnSoftwareDimRequested(MonitorCard card, int percent)
        => OnSoftwareDimRequested(card.Display, percent);

    private void OnSoftwareDimRequested(DisplayInfo display, int percent)
    {
        // Same per-output identity and backend for the full and compact sliders.
        if (!_cards.TryGetValue(display.Key, out MonitorCard? card)) return;
        string message = _dimming.Set(card.Display, percent);
        bool failed = message.StartsWith("Overlay DISABLED", StringComparison.Ordinal);
        int actual = failed ? 100 : percent;
        if (failed)
        {
            _softwareLevels.Remove(display.Key);
        }
        else
        {
            _softwareLevels[display.Key] = actual;
            _softwareSettings.SetLevel(display, actual);
            QueueSaveSoftwareSettings();
        }
        card.SetSoftwareLevel(actual);
        card.SetSoftwareMessage(message, warning: failed);
        _quickPanel.SetSoftwareLevel(display.Key, actual, message);
        if (_quickPanel.IsVisible) _quickPanel.Activate();
    }

    private void QueueSaveSoftwareSettings()
    {
        _saveSettingsTimer.Stop();
        _saveSettingsTimer.Start();
    }

    private void SaveSoftwareSettings()
    {
        if (!_softwareSettings.Save(out string error))
        {
            StatusText.Text = "Could not save software dimming: " + error;
            _quickPanel.SetStatus("Could not save settings · open full view");
        }
    }

    private async Task RescanAsync(bool force)
    {
        if (_exiting) return;
        if (_scanBusy)
        {
            if (force) _scanAgain = true;
            return;
        }
        _scanBusy = true;
        try
        {
            List<DisplayInfo> displays = await Task.Run(DisplayEnumerator.GetActiveDisplays);
            string newFingerprint = DisplayEnumerator.Fingerprint(displays);
            if (!force && newFingerprint == _fingerprint)
            {
                // The display may remain enumerated while a TV's HDMI link
                // or fullscreen app disrupts the existing dim overlay.
                MaintainDimming(displays);
                return;
            }

            _fingerprint = newFingerprint;
            // If a display was unplugged during the preview, restore the
            // previous gamma/overlay BEFORE reconciling active outputs. That
            // prevents reopening an overlay on a disconnected display.
            _dimming.Reconcile(displays);
            // Restore software levels on startup and when a monitor reconnects.
            // Saving is per Windows user, independent of the EXE location.
            foreach (DisplayInfo display in displays)
            {
                if (!_softwareLevels.TryGetValue(display.Key, out int level))
                {
                    if (!_softwareSettings.TryGetLevel(display, displays, out level))
                        continue;
                }
                level = Math.Clamp(level, 0, 100);
                if (level == 100)
                {
                    _softwareLevels[display.Key] = 100;
                    continue;
                }
                string restoreResult = _dimming.Set(display, level);
                if (restoreResult.StartsWith("Overlay DISABLED", StringComparison.Ordinal))
                {
                    // Leave the stored preference untouched so a later
                    // reconnect can retry; don't claim an active dim overlay.
                    _softwareLevels.Remove(display.Key);
                }
                else
                {
                    _softwareLevels[display.Key] = level;
                }
            }
            MaintainDimming(displays);
            int scan = ++_generation;
            CancelPendingWrites();
            RestoreGpuPreview();
            CardsPanel.Children.Clear();
            _cards.Clear();
            _quickPanel.SetDisplays(displays, _softwareLevels);
            if (_quickPanel.IsVisible)
                Dispatcher.BeginInvoke(new Action(PositionQuickPanel), DispatcherPriority.Background);
            StatusText.Text = "Checking DDC/CI on each active display...";
            Subtitle.Text = $"{displays.Count} active display{(displays.Count == 1 ? "" : "s")}  •  across all graphics adapters";
            if (displays.Count == 0)
            {
                _quickPanel.SetStatus("No active displays");
                CardsPanel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "No active displays were reported by Windows.",
                    Foreground = System.Windows.Media.Brushes.LightGray,
                    Margin = new Thickness(16)
                });
                return;
            }

            foreach (DisplayInfo display in displays)
            {
                var card = new MonitorCard(display);
                card.SettingRequested += OnSettingRequested;
                card.GpuPreviewRequested += OnGpuPreviewRequested;
                card.SoftwareDimRequested += OnSoftwareDimRequested;
                if (_softwareLevels.TryGetValue(display.Key, out int level))
                {
                    card.SetSoftwareLevel(level);
                    card.SetSoftwareMessage(level == 100 ? "Off." :
                        "Overlay active • verify screenshot/OBS exclusion.");
                }
                card.GpuRestoreRequested += _ => RestoreGpuPreview();
                _cards.Add(display.Key, card);
                CardsPanel.Children.Add(card);
                _monitorGates.TryAdd(display.Key, new SemaphoreSlim(1, 1));
            }

            var probes = displays.Select(async display =>
            {
                SemaphoreSlim gate = _monitorGates[display.Key];
                await gate.WaitAsync();
                ProbeResult result;
                try { result = await Task.Run(() => DdcService.Probe(display)); }
                catch (Exception ex)
                {
                    var error = new FeatureRead(false, 0, 0, ex.Message);
                    result = new ProbeResult(error, error, "Probe failed");
                }
                finally { gate.Release(); }

                if (_exiting || scan != _generation) return;
                if (_cards.TryGetValue(display.Key, out MonitorCard? card) &&
                    ReferenceEquals(card.Display, display))
                {
                    card.ApplyProbe(result);
                    _quickPanel.ApplyProbe(display.Key, result);
                }
            }).ToArray();

            await Task.WhenAll(probes);
            if (scan == _generation)
            {
                StatusText.Text = $"Last scanned {DateTime.Now:HH:mm:ss}  •  display changes and overlays checked every 3 seconds";
                _quickPanel.SetStatus("Updated " + DateTime.Now.ToString("HH:mm:ss"));
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan error: " + ex.Message;
            _quickPanel.SetStatus("Scan failed · open full view");
        }
        finally
        {
            _scanBusy = false;
            if (_scanAgain && !_exiting)
            {
                _scanAgain = false;
                await RescanAsync(force: true);
            }
        }
    }

    private void ReapplySoftwareDimming()
    {
        // Manual recovery option for TVs that switch into a composition mode
        // where an apparently healthy overlay is not actually rendered.
        // Do not erase preferences or touch hardware brightness / Night Light.
        _dimming.Clear();
        List<DisplayInfo> displays = DisplayEnumerator.GetActiveDisplays();
        MaintainDimming(displays);
        _quickPanel.SetStatus("Reapplied software dimming overlays");
        DiagnosticLog.Write("Manual software overlay reapply requested");
    }

    private void MaintainDimming(IReadOnlyList<DisplayInfo> displays)
    {
        // After a temporarily failed overlay, the monitor fingerprint may
        // remain unchanged. Recover requested levels from persisted settings.
        foreach (DisplayInfo display in displays)
        {
            if (!_softwareLevels.ContainsKey(display.Key) &&
                _softwareSettings.TryGetLevel(display, displays, out int saved) && saved < 100)
            {
                _softwareLevels[display.Key] = saved;
            }
        }

        // Keep levels and saved preferences unchanged during repair.
        // We only log exceptional repairs; normal checks are silent.
        foreach (string message in _dimming.KeepAlive(displays, _softwareLevels))
        {
            DiagnosticLog.Write("Overlay watchdog: " + message);
        }

        // Refreshing an overlay's topmost position must not obscure our tray
        // popup if it is open on the same display. Do not steal input focus.
        if (_quickPanel.IsVisible)
        {
            IntPtr hwnd = new WindowInteropHelper(_quickPanel).Handle;
            if (hwnd != IntPtr.Zero)
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                    0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        }
    }

    private void OnGpuPreviewRequested(MonitorCard card, int percent)
    {
        if (!card.Display.HardwareId.Equals("SAM71EE", StringComparison.OrdinalIgnoreCase)) return;
        if (!_gpuConsentShown)
        {
            var answer = MessageBox.Show(
                "Experimental SDR GPU contrast (NOT the NVIDIA Control Panel slider).\n\n" +
                "This alters the display's gamma ramp temporarily. It may do nothing, " +
                "or it may also affect other monitors. It may conflict with HDR, ICC " +
                "color calibration and NVIDIA settings. Turn HDR off before testing.\n\n" +
                "MonitorDeck will try to restore the original ramp after 10 seconds. " +
                "If your picture remains wrong, sign out of Windows or restart.\n\nContinue?",
                "MonitorDeck — temporary GPU experiment",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            _gpuConsentShown = true;
        }
        try
        {
            _gpuTimeout.Stop();
            string result = _gpuPreview.Apply(card.Display.DisplayPath, percent);
            card.SetGpuMessage(result, warning: true);
            _gpuTimeout.Start();
        }
        catch (Exception ex)
        {
            card.SetGpuMessage("GPU preview failed: " + ex.Message, warning: true);
            if (_gpuPreview.HasPreview) _gpuTimeout.Start();
        }
    }

    private void RestoreGpuPreview()
    {
        _gpuTimeout.Stop();
        try
        {
            _gpuPreview.Restore();
            foreach (MonitorCard card in _cards.Values)
                card.SetGpuMessage("Original ramp restore requested. SDR gamma test is inactive.");
        }
        catch (Exception ex)
        {
            foreach (MonitorCard card in _cards.Values)
                card.SetGpuMessage("Could not restore GPU ramp: " + ex.Message +
                    ". Use Reset now, or sign out/reboot if colors remain wrong.", warning: true);
            // Retry once after a short delay unless we're exiting.
            if (!_exiting && _gpuPreview.HasPreview) _gpuTimeout.Start();
        }
    }

    private void OnSettingRequested(MonitorCard card, ControlKind kind, int percent)
        => RequestHardwareWrite(card.Display, kind, percent);

    private void OnQuickHardwareRequested(DisplayInfo display, ControlKind kind, int percent)
        => RequestHardwareWrite(display, kind, percent);

    private void RequestHardwareWrite(DisplayInfo display, ControlKind kind, int percent)
    {
        if (!_cards.TryGetValue(display.Key, out MonitorCard? card)) return;
        string key = $"{display.Key}|{kind}";
        if (_pendingWrites.Remove(key, out CancellationTokenSource? previous))
            previous.Cancel();

        // Keep the popup and advanced window in sync without generating new writes.
        _quickPanel.SetHardwareValue(display.Key, kind, percent);
        var cts = new CancellationTokenSource();
        _pendingWrites[key] = cts;
        _ = WriteAfterPauseAsync(card, kind, percent, key, cts);
    }

    private async Task WriteAfterPauseAsync(MonitorCard card, ControlKind kind, int percent,
        string key, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(250, cts.Token);
            if (!_cards.TryGetValue(card.Display.Key, out MonitorCard? current) ||
                !ReferenceEquals(current, card)) return;

            uint maximum = card.GetMaximum(kind);
            card.ShowPending(kind);
            SemaphoreSlim gate = _monitorGates[card.Display.Key];
            await gate.WaitAsync(cts.Token);
            WriteResult result;
            try
            {
                result = await Task.Run(() => DdcService.Set(card.Display, kind, percent, maximum), cts.Token);
            }
            finally { gate.Release(); }

            if (!cts.IsCancellationRequested && _cards.TryGetValue(card.Display.Key, out current) &&
                ReferenceEquals(current, card))
            {
                card.ShowWriteResult(kind, result);
                _quickPanel.ShowWriteResult(card.Display.Key, kind, result);
            }
        }
        catch (OperationCanceledException) { /* A newer slider value replaced this one. */ }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && _cards.TryGetValue(card.Display.Key, out MonitorCard? current) &&
                ReferenceEquals(current, card))
            {
                var failure = new WriteResult(false, percent, ex.Message);
                card.ShowWriteResult(kind, failure);
                _quickPanel.ShowWriteResult(card.Display.Key, kind, failure);
            }
        }
        finally
        {
            if (_pendingWrites.TryGetValue(key, out CancellationTokenSource? active) &&
                ReferenceEquals(active, cts))
                _pendingWrites.Remove(key);
            cts.Dispose();
        }
    }

    private void CancelPendingWrites()
    {
        foreach (CancellationTokenSource token in _pendingWrites.Values)
        {
            token.Cancel();
            // Let the task's finally dispose it after native work finishes.
        }
        _pendingWrites.Clear();
    }

    private void ToggleQuickPanel()
    {
        if (_exiting) return;
        if (_quickPanel.IsVisible)
        {
            _quickPanel.Hide();
            return;
        }
        _popupScreenDevice = Forms.Screen.FromPoint(Forms.Cursor.Position).DeviceName;
        _quickPanel.Show();
        _ = _quickPanel.RefreshNightLightAsync();
        _quickPanel.UpdateLayout();
        PositionQuickPanel();
        _quickPanel.Activate();
        Dispatcher.BeginInvoke(new Action(PositionQuickPanel), DispatcherPriority.Loaded);
    }

    private void PositionQuickPanel()
    {
        if (!_quickPanel.IsVisible) return;
        // Pin to the original tray screen while the user moves the mouse;
        // fall back safely if that display disconnects during a rescan.
        Forms.Screen target = Forms.Screen.AllScreens.FirstOrDefault(screen =>
            string.Equals(screen.DeviceName, _popupScreenDevice, StringComparison.OrdinalIgnoreCase))
            ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.FromPoint(Forms.Cursor.Position);
        var area = target.WorkingArea;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_quickPanel);
        double availableHeight = Math.Max(210, area.Height / dpi.DpiScaleY - 16);
        // No fixed 535-DIP scroll area: expand all five rows to their desired
        // heights, up to the actual usable screen height. A hidden wheel-only
        // overflow remains on physically short work areas.
        double chrome = _quickPanel.HeaderGrid.ActualHeight +
                        _quickPanel.NightLightCard.ActualHeight +
                        _quickPanel.NightLightCard.Margin.Top + _quickPanel.NightLightCard.Margin.Bottom +
                        _quickPanel.FooterBorder.ActualHeight + 2;
        _quickPanel.MonitorScroll.MaxHeight = Math.Max(120, availableHeight - chrome);
        _quickPanel.MaxHeight = availableHeight;
        _quickPanel.UpdateLayout();
        IntPtr hwnd = new WindowInteropHelper(_quickPanel).Handle;
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect)) return;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        int x = Math.Max(area.Left + 4, area.Right - width - 12);
        int y = Math.Max(area.Top + 4, area.Bottom - height - 12);
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER);
    }

    private void ShowMainWindow()
    {
        _quickPanel.Hide();
        Opacity = 1;
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        ScheduleRescan();
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }
}
