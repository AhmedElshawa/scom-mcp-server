using System.Diagnostics;

namespace ScomMcp;

// Windows Application event log writer. The source is registered by Register-ScomWorkerTask.ps1
// (creating a source needs administrator rights; the worker account only writes). Messages carry
// stable codes only, never paths, configuration, credentials or exception text. Best effort: a
// missing source or a denied write must never affect worker behaviour.
public static class WorkerEventLog
{
    public const string Source = "ScomMcpWorker";
    // 1001 started; 2001 stale marker recovered or restartable stop; 3001 non-restartable failure.
    public const int Started = 1001;
    public const int Recovered = 2001;
    public const int Failed = 3001;

    public static Action<int, string>? Sink { get; set; }

    public static void Write(int eventId, string message)
    {
        if (eventId is not (Started or Recovered or Failed) || message.Length > 256) return;
        var sink = Sink;
        if (sink is not null)
        {
            sink(eventId, message);
            return;
        }
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            EventLog.WriteEntry(Source, message, eventId switch
            {
                Started => EventLogEntryType.Information,
                Recovered => EventLogEntryType.Warning,
                _ => EventLogEntryType.Error
            }, eventId);
        }
        catch (Exception)
        {
        }
    }
}
