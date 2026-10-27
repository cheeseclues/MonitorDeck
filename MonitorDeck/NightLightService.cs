using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace MonitorDeck;

/// <summary>
/// Experimental Windows Night Light state and strength control. Windows provides
/// no supported public setter. Its per-user CloudStore data uses an undocumented
/// version of Bond CompactBinary. The on/off method edits only the state key;
/// strength edits only the temperature field of the settings key; both preserve
/// schedule fields and unrelated CloudStore data.
///
/// Safety rules: accept only explicitly verified layouts, refuse unknown
/// data, preserve untouched fields byte-for-byte, back up originals and verify readback.
/// Windows updates may change this format. Reading back a changed registry value
/// does not itself prove the visual effect was applied by Windows.
///
/// Format reference: https://github.com/kvnxiao/win-nightlight-cli/blob/main/docs/nightlight-registry-format.md
/// </summary>
internal static class NightLightService
{
    private const string StatePath =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\" +
        @"default$windows.data.bluelightreduction.bluelightreductionstate\" +
        @"windows.data.bluelightreduction.bluelightreductionstate";

    private const string SettingsPath =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\" +
        @"default$windows.data.bluelightreduction.settings\" +
        @"windows.data.bluelightreduction.settings";

    internal readonly record struct Status(bool Available, bool Enabled, string Message);
    internal readonly record struct StrengthStatus(bool Available, int Percent, int Kelvin, string Message);

    // Windows' strength slider corresponds approximately to 6500 K (0%) through
    // 1200 K (100%). The registry stores kelvin, not a percentage.
    private static int PercentToKelvin(int strength) => 6500 - 53 * strength;
    private static int KelvinToPercent(int kelvin) => Math.Clamp(
        (int)Math.Round((6500 - kelvin) / 53.0), 0, 100);

    internal static StrengthStatus GetStrength()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsPath, writable: false);
            if (key?.GetValue("Data") is not byte[] bytes)
                return new StrengthStatus(false, 0, 0, "Night Light strength not found");
            SettingsField field = ParseSettings(ParseEnvelope(bytes).InnerData);
            return new StrengthStatus(true, KelvinToPercent(field.Kelvin), field.Kelvin,
                $"{field.Kelvin} K • Windows Night Light");
        }
        catch (Exception ex)
        {
            return new StrengthStatus(false, 0, 0, "Strength unavailable: " + ex.Message);
        }
    }

    /// <summary>Only replace field #40 (BT_INT16 temperature) inside the existing
    /// validated CloudStore payload. Keep schedule, time blocks and flags intact.</summary>
    internal static StrengthStatus SetStrength(int percent, bool backupOriginal = true)
    {
        if (percent < 0 || percent > 100)
            return new StrengthStatus(false, 0, 0, "Strength must be between 0 and 100");
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsPath, writable: true);
            if (key?.GetValue("Data") is not byte[] original)
                return new StrengthStatus(false, 0, 0, "Night Light strength not found");

            Envelope envelope = ParseEnvelope(original);
            SettingsField field = ParseSettings(envelope.InnerData);
            int targetKelvin = PercentToKelvin(percent);
            if (targetKelvin == field.Kelvin)
                return new StrengthStatus(true, percent, targetKelvin, $"{targetKelvin} K • unchanged");

            // Stitch back the original binary around precisely the temperature value.
            byte[] inner = envelope.InnerData;
            var replacement = new List<byte>(inner.Length + 2);
            for (int i = 0; i < field.ValueOffset; i++) replacement.Add(inner[i]);
            WriteVarUInt(replacement, (ulong)targetKelvin * 2); // Bond ZigZag BT_INT16
            for (int i = field.ValueOffset + field.ValueLength; i < inner.Length; i++)
                replacement.Add(inner[i]);

            byte[] updatedInner = replacement.ToArray();
            if (ParseSettings(updatedInner).Kelvin != targetKelvin)
                throw new InvalidDataException("Night Light temperature encoding verification failed");
            // CloudStore stores a Unix-seconds modified timestamp. Advancing it by
            // two seconds on every ~180 ms slider tick can push it far into the
            // future and may prevent Windows from applying live updates.
            // Use the actual clock rather than fabricating future timestamps.
            ulong modified = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (envelope.ModifiedUnixSeconds > modified + 10)
                DiagnosticLog.Write($"Night Light settings timestamp drift: {envelope.ModifiedUnixSeconds - modified}s in the future; correcting to wall clock");
            byte[] updated = BuildEnvelope(modified, updatedInner);
            if (ParseSettings(ParseEnvelope(updated).InnerData).Kelvin != targetKelvin)
                throw new InvalidDataException("Night Light CloudStore verification failed");

            if (backupOriginal) SaveBackup(original, "settings");
            key.SetValue("Data", updated, RegistryValueKind.Binary);
            StrengthStatus confirmed = GetStrength();
            if (!confirmed.Available || confirmed.Kelvin != targetKelvin)
                return new StrengthStatus(false, field.Kelvin == 0 ? 0 : KelvinToPercent(field.Kelvin),
                    field.Kelvin, "Windows did not accept the new Night Light strength");
            return confirmed with { Message = $"{targetKelvin} K • verify screen warmth" };
        }
        catch (Exception ex)
        {
            return new StrengthStatus(false, 0, 0, "Cannot change Night Light strength: " + ex.Message);
        }
    }

    // Safely parse the subset of Windows 11's Bond CompactBinary settings schema
    // established by observed builds. Unknown formats disable the strength slider.
    // The opaque schedule fields are preserved exactly; no settings struct is rebuilt.
    private readonly record struct SettingsField(int Kelvin, int ValueOffset, int ValueLength);

    private static SettingsField ParseSettings(byte[] data)
    {
        int at = 0;
        Expect(data, ref at, 0x43, 0x42, 0x01, 0x00);
        int previousId = -1;
        int kelvin = -1, offset = -1, length = 0;
        while (at < data.Length)
        {
            byte tag = data[at++];
            if (tag == 0)
            {
                if (at != data.Length || kelvin < 1200 || kelvin > 6500)
                    throw new InvalidDataException("Unsupported Night Light settings structure");
                return new SettingsField(kelvin, offset, length);
            }
            int type = tag & 0x1F;
            int id = tag >> 5;
            if (id == 6) id = checked((int)ReadVarUInt(data, ref at));
            else if (id == 7)
                throw new InvalidDataException("Unknown Night Light settings identifier");
            if (id <= previousId)
                throw new InvalidDataException("Unordered Night Light settings fields");
            previousId = id;
            if ((id == 0 || id == 10 || id == 70) && type == 0x02)
            {
                if (at >= data.Length || data[at++] > 1)
                    throw new InvalidDataException("Invalid Night Light settings flag");
            }
            else if ((id == 20 || id == 30 || id == 50 || id == 60) && type == 0x0A)
            {
                ReadTimeBlock(data, ref at);
            }
            else if (id == 40 && type == 0x0F)
            {
                offset = at;
                ulong encoded = ReadVarUInt(data, ref at);
                if ((encoded & 1) != 0 || encoded / 2 > int.MaxValue)
                    throw new InvalidDataException("Invalid Night Light temperature value");
                kelvin = (int)(encoded / 2);
                length = at - offset;
            }
            else throw new InvalidDataException("Unrecognized Night Light settings field");
        }
        throw new InvalidDataException("Unterminated Night Light settings data");
    }

    private static void ReadTimeBlock(byte[] data, ref int at)
    {
        int previousId = -1;
        while (at < data.Length)
        {
            byte tag = data[at++];
            if (tag == 0) return;
            int id = tag >> 5;
            if ((tag & 0x1F) != 0x0E || id > 1 || id <= previousId)
                throw new InvalidDataException("Unexpected Night Light time-block layout");
            previousId = id;
            ulong value = ReadVarUInt(data, ref at);
            if (value > (id == 0 ? 23UL : 59UL))
                throw new InvalidDataException("Invalid Night Light schedule time");
        }
        throw new InvalidDataException("Unterminated Night Light time block");
    }

    private static void SaveBackup(byte[] original, string suffix)
    {
        string folder = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "MonitorDeck", "NightLightBackup");
        Directory.CreateDirectory(folder);
        string backup = Path.Combine(folder,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + suffix + ".bin");
        File.WriteAllBytes(backup, original);
    }

    internal static Status GetStatus()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StatePath, writable: false);
            if (key?.GetValue("Data") is not byte[] bytes)
                return new Status(false, false, "Windows Night Light state not found");
            Envelope envelope = ParseEnvelope(bytes);
            State state = ParseState(envelope.InnerData);
            return new Status(true, state.IsOn, state.IsOn ? "On" : "Off");
        }
        catch (Exception ex)
        {
            return new Status(false, false, "Night Light state unavailable: " + ex.Message);
        }
    }

    internal static Status SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StatePath, writable: true);
            if (key?.GetValue("Data") is not byte[] original)
                return new Status(false, false, "Windows Night Light state not found");

            Envelope envelope = ParseEnvelope(original);
            State state = ParseState(envelope.InnerData);
            if (state.IsOn == enabled)
                return new Status(true, enabled, enabled ? "Already on" : "Already off");

            byte[] newInner = BuildState(enabled, state.UsableField);
            ulong modified = Math.Max((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                checked(envelope.ModifiedUnixSeconds + 2));
            byte[] updated = BuildEnvelope(modified, newInner);

            // Parse our own output BEFORE modifying the registry.
            Envelope check = ParseEnvelope(updated);
            if (ParseState(check.InnerData).IsOn != enabled)
                throw new InvalidDataException("Night Light output verification failed");

            SaveBackup(original, "state");

            key.SetValue("Data", updated, RegistryValueKind.Binary);
            Status readback = GetStatus();
            if (!readback.Available || readback.Enabled != enabled)
                return new Status(false, state.IsOn,
                    "Windows did not accept the change. Open Windows Settings.");

            return new Status(true, enabled,
                enabled ? "On • verify screen warmth" : "Off • verify screen warmth");
        }
        catch (Exception ex)
        {
            return new Status(false, false, "Cannot toggle Night Light: " + ex.Message);
        }
    }

    private readonly record struct Envelope(ulong ModifiedUnixSeconds, byte[] InnerData);
    private readonly record struct State(bool IsOn, byte[]? UsableField);

    private static Envelope ParseEnvelope(byte[] data)
    {
        int at = 0;
        Expect(data, ref at, 0x43, 0x42, 0x01, 0x00,
            0x0A, 0x02, 0x01, 0x00, 0x2A, 0x06);
        ulong modified = ReadVarUInt(data, ref at);
        Expect(data, ref at, 0x2A, 0x2B, 0x0E);
        ulong count = ReadVarUInt(data, ref at);
        if (data.Length < at + 3 || count > 4096 ||
            count > (ulong)(data.Length - at - 3))
            throw new InvalidDataException("Unexpected Night Light CloudStore length");
        byte[] inner = new byte[(int)count];
        Array.Copy(data, at, inner, 0, inner.Length);
        at += inner.Length;
        Expect(data, ref at, 0x00, 0x00, 0x00);
        if (at != data.Length)
            throw new InvalidDataException("Unrecognized Night Light CloudStore envelope");
        return new Envelope(modified, inner);
    }

    private static byte[] BuildEnvelope(ulong modified, byte[] payload)
    {
        var result = new List<byte>(payload.Length + 40)
        {
            0x43, 0x42, 0x01, 0x00, 0x0A, 0x02, 0x01, 0x00, 0x2A, 0x06
        };
        WriteVarUInt(result, modified);
        result.AddRange(new byte[] { 0x2A, 0x2B, 0x0E });
        WriteVarUInt(result, (ulong)payload.Length);
        result.AddRange(payload);
        result.AddRange(new byte[] { 0x00, 0x00, 0x00 });
        return result.ToArray();
    }

    private static State ParseState(byte[] data)
    {
        int at = 0;
        Expect(data, ref at, 0x43, 0x42, 0x01, 0x00);
        bool? isOn = null;
        byte[]? usableField = null;
        int previousId = -1;

        while (at < data.Length)
        {
            int start = at;
            byte tag = data[at++];
            if (tag == 0)
            {
                if (at != data.Length)
                    throw new InvalidDataException("Unexpected data after Night Light state");
                return new State(isOn == true, usableField);
            }

            int type = tag & 0x1F;
            int id = tag >> 5;
            if (id == 6)
                id = checked((int)ReadVarUInt(data, ref at));
            else if (id == 7)
                throw new InvalidDataException("Unknown Night Light state field identifier");
            if (id <= previousId)
                throw new InvalidDataException("Unexpected Night Light state field ordering");
            previousId = id;

            if (id == 0 && type == 0x10) // BT_INT32: present means 'on'
            {
                ulong value = ReadVarUInt(data, ref at);
                if (value != 0)
                    throw new InvalidDataException("Unknown Night Light active-state value");
                isOn = true;
            }
            else if (id == 10 && type == 0x10) // transition cause
            {
                _ = ReadVarUInt(data, ref at);
            }
            else if (id == 20 && type == 0x06) // FILETIME
            {
                _ = ReadVarUInt(data, ref at);
            }
            else if (id == 30 && type == 0x02) // 'usable' flag
            {
                if (at >= data.Length || data[at] > 1)
                    throw new InvalidDataException("Invalid Night Light usable state");
                at++;
                usableField = data[start..at]; // preserve Windows' existing value
            }
            else
                throw new InvalidDataException("Unsupported Night Light state layout; no changes made");
        }
        throw new InvalidDataException("Unterminated Night Light state");
    }

    private static byte[] BuildState(bool enabled, byte[]? usableField)
    {
        var data = new List<byte> { 0x43, 0x42, 0x01, 0x00 };
        if (enabled)
        {
            data.Add(0x10); // Field 0, BT_INT32 (state: on)
            data.Add(0x00); // ZigZag-encoded int32(0)
        }
        data.AddRange(new byte[] { 0xD0, 0x0A, 0x02 }); // field 10: manual change
        data.AddRange(new byte[] { 0xC6, 0x14 }); // field 20: FILETIME
        WriteVarUInt(data, (ulong)DateTime.UtcNow.ToFileTimeUtc());
        if (usableField is not null) data.AddRange(usableField);
        data.Add(0x00); // end of struct
        return data.ToArray();
    }

    private static ulong ReadVarUInt(byte[] data, ref int at)
    {
        ulong value = 0;
        for (int i = 0; i < 10; i++)
        {
            if (at >= data.Length)
                throw new InvalidDataException("Truncated Night Light binary data");
            byte next = data[at++];
            if (i == 9 && next > 1)
                throw new InvalidDataException("Night Light integer overflow");
            value |= ((ulong)(next & 0x7F)) << (i * 7);
            if ((next & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Night Light integer too long");
    }

    private static void WriteVarUInt(List<byte> bytes, ulong value)
    {
        while (value >= 0x80)
        {
            bytes.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
    }

    private static void Expect(byte[] data, ref int at, params byte[] expected)
    {
        foreach (byte value in expected)
        {
            if (at >= data.Length || data[at++] != value)
                throw new InvalidDataException("Unrecognized Windows Night Light data format");
        }
    }
}
