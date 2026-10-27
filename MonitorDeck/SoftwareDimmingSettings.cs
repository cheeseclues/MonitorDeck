using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MonitorDeck;

// Persist only software-overlay preferences. Hardware DDC/CI settings and
// Windows Night Light already have their own stores and are not modified here.
internal sealed class SoftwareDimmingSettings
{
    internal sealed class SavedMonitor
    {
        public SavedMonitor() { }
        public string HardwareId { get; set; } = string.Empty;
        public int BrightnessPercent { get; set; } = 100;
    }

    public SoftwareDimmingSettings() { }

    public Dictionary<string, SavedMonitor> Monitors { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    internal static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MonitorDeck", "software-dimming.json");

    internal static SoftwareDimmingSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new SoftwareDimmingSettings();
            var result = JsonSerializer.Deserialize<SoftwareDimmingSettings>(File.ReadAllText(SettingsPath))
                ?? new SoftwareDimmingSettings();
            // The comparer is not preserved by JSON deserialization.
            result.Monitors = new Dictionary<string, SavedMonitor>(
                result.Monitors ?? new Dictionary<string, SavedMonitor>(),
                StringComparer.OrdinalIgnoreCase);
            return result;
        }
        catch (Exception)
        {
            // Unreadable/corrupt settings must never prevent monitor controls
            // from opening. The user can re-save settings normally.
            return new SoftwareDimmingSettings();
        }
    }

    internal bool TryGetLevel(DisplayInfo display, IReadOnlyList<DisplayInfo> activeDisplays,
        out int brightness)
    {
        if (Monitors.TryGetValue(display.Key, out SavedMonitor? exact) && exact is not null &&
            string.Equals(exact.HardwareId, display.HardwareId, StringComparison.OrdinalIgnoreCase))
        {
            brightness = Math.Clamp(exact.BrightnessPercent, 0, 100);
            return true;
        }

        // Windows may renumber \.\DISPLAY* after changing the layout. Only
        // fall back to model ID when it is unique in the current setup AND the
        // saved file. Never apply another identical model's saved darkness.
        if (!string.IsNullOrWhiteSpace(display.HardwareId) &&
            activeDisplays.Count(d => string.Equals(d.HardwareId, display.HardwareId,
                StringComparison.OrdinalIgnoreCase)) == 1)
        {
            SavedMonitor[] matches = Monitors.Values.Where(v => v is not null &&
                string.Equals(v.HardwareId, display.HardwareId,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1)
            {
                brightness = Math.Clamp(matches[0].BrightnessPercent, 0, 100);
                return true;
            }
        }

        brightness = 100;
        return false;
    }

    internal void SetLevel(DisplayInfo display, int brightness)
    {
        Monitors[display.Key] = new SavedMonitor
        {
            HardwareId = display.HardwareId,
            BrightnessPercent = Math.Clamp(brightness, 0, 100)
        };
    }

    internal void Clear() => Monitors.Clear();

    internal bool Save(out string error)
    {
        string temp = SettingsPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, SettingsPath, overwrite: true);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception) { /* Best effort temporary-file cleanup. */ }
            error = ex.Message;
            return false;
        }
    }
}
