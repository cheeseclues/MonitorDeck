using System;
using System.IO;
using System.Reflection;

namespace MonitorDeck;

internal static class DiagnosticLog
{
    private static readonly object Gate = new();
    internal static string PathOnDisk => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MonitorDeck", "diagnostics.log");

    internal static void Write(string message)
    {
        // Intentionally log only monitor names / VCP readback / Night Light percentage.
        // Never log raw CloudStore bytes, usernames, tokens, or file contents.
        try
        {
            lock (Gate)
            {
                string path = PathOnDisk;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".old", overwrite: true);
                string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
                File.AppendAllText(path, $"{DateTime.Now:O} v{version} {message}{Environment.NewLine}");
            }
        }
        catch { /* Diagnostics must never stop the actual monitor controls. */ }
    }
}
