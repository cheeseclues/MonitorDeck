using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace MonitorDeck;

/// <summary>
/// Machine-wide startup: an elevated one-time install to Program Files and
/// the HKLM Run key. The normal UI process never needs elevation.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunEntry = "MonitorDeck";
    private const string InstallFlag = "--install-all-users";
    private static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MonitorDeck");

    // Versioned EXE: a new version can be installed while an older version
    // is still running; the Run entry is then atomically pointed at the new EXE.
    private static string InstallPath
    {
        get
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 10);
            return Path.Combine(InstallDirectory,
                $"MonitorDeck-v{version.Major}.{version.Minor}.{version.Build}.exe");
        }
    }

    private static string Command => $"\"{InstallPath}\" --autostart";
    internal static bool IsCurrentVersionRegistered
    {
        get
        {
            try
            {
                using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey? run = machine.OpenSubKey(RunKey, writable: false);
                string? registered = run?.GetValue(RunEntry) as string;
                return File.Exists(InstallPath) &&
                    string.Equals(registered, Command, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }
    }

    internal static bool InstallMachineWide(out string details)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            // Must be a Windows self-contained single-file publish; copying a
            // framework-dependent apphost alone would break on other accounts.
            if (string.IsNullOrEmpty(exe) ||
                !Path.GetFileName(exe).StartsWith("MonitorDeck", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location))
            {
                details = "Run the standalone EXE produced by Build-Release.cmd first.";
                return false;
            }

            Directory.CreateDirectory(InstallDirectory);
            if (!Path.GetFullPath(exe).Equals(Path.GetFullPath(InstallPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(exe, InstallPath, overwrite: true);

            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey run = machine.CreateSubKey(RunKey, writable: true)
                ?? throw new InvalidOperationException("Cannot open the all-users Windows Run key.");
            run.SetValue(RunEntry, Command, RegistryValueKind.String);
            details = "Enabled for all Windows user accounts at their next sign-in.";
            return true;
        }
        catch (Exception ex)
        {
            details = ex.Message;
            return false;
        }
    }

    internal static bool RequestInstallWithUac(out string details)
    {
        if (IsCurrentVersionRegistered)
        {
            details = "Already enabled.";
            return true;
        }
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                details = "The program's executable path is unavailable.";
                return false;
            }
            using Process? setup = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = InstallFlag,
                Verb = "runas", // Windows UAC prompt, not silent elevation
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
            });
            if (setup is null)
            {
                details = "Windows did not launch the setup helper.";
                return false;
            }
            setup.WaitForExit();
            bool okay = setup.ExitCode == 0 && IsCurrentVersionRegistered;
            details = okay ? "Enabled for all users." :
                "The elevated setup did not register this version. " +
                "Check that you're running the standalone release EXE and have administrator rights.";
            return okay;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            details = "The administrator approval was cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            details = ex.Message;
            return false;
        }
    }
}
