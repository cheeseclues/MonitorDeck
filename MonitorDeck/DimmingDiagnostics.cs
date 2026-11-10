using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonitorDeck;

// Only records display/overlay state. Never includes window titles, file paths,
// browser contents, credentials, or raw Windows Night Light registry values.
internal static class DimmingDiagnostics
{
    private const string Marker = " [DIM] ";

    internal static void Record(string message) => DiagnosticLog.Write("[DIM] " + message);

    // The existing diagnostics log is rotated. Read only specifically tagged
    // dimming records so the copied report excludes legacy startup file paths.
    internal static string RecentEvents(int maxLines = 160)
    {
        try
        {
            var events = new Queue<string>();
            foreach (string path in new[] { DiagnosticLog.PathOnDisk + ".old", DiagnosticLog.PathOnDisk })
            {
                if (!File.Exists(path)) continue;
                foreach (string line in File.ReadLines(path))
                {
                    int marker = line.IndexOf(Marker, StringComparison.Ordinal);
                    if (marker < 0) continue;
                    // Skip unrelated events without accidentally copying arbitrary log text.
                    events.Enqueue(line[..marker] + line[marker..]);
                    while (events.Count > maxLines) events.Dequeue();
                }
            }
            return events.Count == 0 ? "(No dimming events recorded yet.)"
                : string.Join(Environment.NewLine, events);
        }
        catch (Exception ex)
        {
            return "(Couldn't read recent dimming events: " + ex.GetType().Name + ")";
        }
    }
}
