using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ScomMcp;

// Identity of the current process and host. Abstracted so liveness decisions are testable
// without spawning or killing real processes.
public interface IWorkerProcessProbe
{
    string HostName { get; }
    // OS last boot time, UTC, ISO-8601 truncated to seconds.
    string BootId { get; }
    DateTimeOffset OwnStartTimeUtc { get; }
    // Null when the process does not exist or its start time cannot be read.
    DateTimeOffset? ProcessStartTimeUtc(int processId);
}

public sealed class WorkerProcessProbe : IWorkerProcessProbe
{
    public static readonly WorkerProcessProbe Default = new();
    private const string BootIdFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public string HostName => Environment.MachineName;

    public string BootId => FormatBootId(BootTimeUtc());

    public DateTimeOffset OwnStartTimeUtc
    {
        get
        {
            using var self = Process.GetCurrentProcess();
            return new DateTimeOffset(self.StartTime.ToUniversalTime());
        }
    }

    public DateTimeOffset? ProcessStartTimeUtc(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return null;
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    internal static string FormatBootId(DateTimeOffset bootTime) =>
        bootTime.UtcDateTime.ToString(BootIdFormat, CultureInfo.InvariantCulture);

    internal static bool TryParseBootId(string? bootId, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(bootId, BootIdFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    private static DateTimeOffset BootTimeUtc()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = NtQuerySystemInformation(3, out var info, Marshal.SizeOf<TimeOfDayInformation>(), out _);
            if (status >= 0 && info.BootTime > 0)
                return DateTimeOffset.FromFileTime(info.BootTime).ToUniversalTime();
        }
        return DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeOfDayInformation
    {
        public long BootTime;
        public long CurrentTime;
        public long TimeZoneBias;
        public uint TimeZoneId;
        public uint Reserved;
        public ulong BootTimeBias;
        public ulong SleepTimeBias;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, out TimeOfDayInformation information,
        int length, out int returnLength);
}
