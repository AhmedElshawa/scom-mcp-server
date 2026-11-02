using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ScomMcp;

// Windows 10 / Server 2016 or later: JOB_LIST binds the child during creation,
// not after Start (or even after a suspended CreateProcess). A worker crash at
// any instruction must never leave an unbound collector behind. Fail closed
// if the host cannot provide these attributes or allow nested job membership.
internal sealed class NormalizationChildProcess : IDisposable
{
    public Process Process { get; private set; } = null!;
    public Stream Input { get; private set; } = null!;
    public Stream Output { get; private set; } = null!;
    public Stream Error { get; private set; } = null!;
    private SafeFileHandle? job;

    public static NormalizationChildProcess Start(ProcessStartInfo start)
    {
        var child = new NormalizationChildProcess();
        try
        {
            if (OperatingSystem.IsWindows()) child.StartWindows(start);
            else
            {
                child.Process = new Process { StartInfo = start };
                if (!child.Process.Start())
                    throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_BRIDGE_START_FAILED");
                child.Input = child.Process.StandardInput.BaseStream;
                child.Output = child.Process.StandardOutput.BaseStream;
                child.Error = child.Process.StandardError.BaseStream;
            }
            return child;
        }
        catch { child.Dispose(); throw; }
    }

    public async Task TerminateAndWaitAsync()
    {
        if (job is not null)
        {
            Check(TerminateJobObject(job, 1));
            await Process.WaitForExitAsync();
            // Root exit does not imply descendant exit. Keep the collection
            // guard held until the kernel reports that the entire job is empty.
            while (true)
            {
                Check(QueryInformationJobObject(job, 1, out var accounting,
                    (uint)Marshal.SizeOf<JobAccounting>(), IntPtr.Zero));
                if (accounting.ActiveProcesses == 0) break;
                await Task.Delay(10);
            }
        }
        else
        {
            if (!Process.HasExited)
                try { Process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (Process.HasExited) { }
            await Process.WaitForExitAsync();
        }
    }

    public void Dispose()
    {
        // The job handle is non-inheritable and is never in the handle list.
        // Closing the worker's last handle also covers abrupt worker death.
        job?.Dispose();
        Input?.Dispose();
        Output?.Dispose();
        Error?.Dispose();
        Process?.Dispose();
    }

    private void StartWindows(ProcessStartInfo start)
    {
        job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new JobLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()));
        using var stdin = new Pipe(childReads: true);
        using var stdout = new Pipe(childReads: false);
        using var stderr = new Pipe(childReads: false);
        using var attributes = new ProcessAttributes();
        attributes.Add(0x00020002, [stdin.Child.DangerousGetHandle(), stdout.Child.DangerousGetHandle(), stderr.Child.DangerousGetHandle()]);
        attributes.Add(0x0002000D, [job.DangerousGetHandle()]);
        var startup = new StartupInfoEx
        {
            Startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                Input = stdin.Child.DangerousGetHandle(), Output = stdout.Child.DangerousGetHandle(),
                Error = stderr.Child.DangerousGetHandle()
            },
            Attributes = attributes.Pointer
        };
        var executable = start.FileName;
        if (!Path.IsPathFullyQualified(executable))
        {
            var resolved = new StringBuilder(32768);
            var length = SearchPathW(null, executable, ".exe", resolved.Capacity, resolved, IntPtr.Zero);
            if (length == 0 || length >= resolved.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
            executable = resolved.ToString();
        }
        var arguments = start.ArgumentList.Count == 0 ? start.Arguments :
            string.Join(" ", start.ArgumentList.Select(Quote));
        var command = new StringBuilder(Quote(executable) + " " + arguments);
        var environment = string.Join("\0", start.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Where(p => p.Value is not null).Select(p => p.Key + "=" + p.Value)) + "\0\0";
        // SUSPENDED lets us open the managed process handle before a fast child
        // exits. JOB_LIST, not suspension, provides the crash-safe binding.
        Check(CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true,
            0x00080000 | 0x00000400 | 0x08000000 | 0x00000004, environment,
            string.IsNullOrEmpty(start.WorkingDirectory) ? null : start.WorkingDirectory, ref startup, out var created));
        using var processHandle = new SafeFileHandle(created.Process, true);
        using var threadHandle = new SafeFileHandle(created.Thread, true);
        try
        {
            Process = Process.GetProcessById(created.ProcessId);
            _ = Process.SafeHandle;
            Input = new FileStream(stdin.TakeParent(), FileAccess.Write, 4096, false);
            Output = new FileStream(stdout.TakeParent(), FileAccess.Read, 4096, false);
            Error = new FileStream(stderr.TakeParent(), FileAccess.Read, 4096, false);
            if (ResumeThread(threadHandle) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            Check(TerminateJobObject(job, 1));
            throw;
        }
        GC.KeepAlive(job);
    }

    private static string Quote(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private sealed class Pipe : IDisposable
    {
        public SafeFileHandle Child { get; }
        private SafeFileHandle? parent;
        public Pipe(bool childReads)
        {
            var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            Check(CreatePipe(out var read, out var write, ref security, 0));
            Child = childReads ? read : write;
            parent = childReads ? write : read;
            try { Check(SetHandleInformation(parent, 1, 0)); }
            catch { Dispose(); throw; }
        }
        public SafeFileHandle TakeParent() { var handle = parent!; parent = null; return handle; }
        public void Dispose() { Child.Dispose(); parent?.Dispose(); }
    }

    private sealed class ProcessAttributes : IDisposable
    {
        public IntPtr Pointer { get; private set; }
        private readonly List<IntPtr> values = [];
        public ProcessAttributes()
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            Pointer = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(Pointer, 2, 0, ref size))
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        public void Add(nint attribute, IntPtr[] handles)
        {
            var value = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
            values.Add(value);
            Marshal.Copy(handles, 0, value, handles.Length);
            Check(UpdateProcThreadAttribute(Pointer, 0, attribute, value,
                (nuint)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero));
        }
        public void Dispose()
        {
            if (Pointer != IntPtr.Zero) { DeleteProcThreadAttributeList(Pointer); Marshal.FreeHGlobal(Pointer); }
            foreach (var value in values) Marshal.FreeHGlobal(value);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Size; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting { public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out JobAccounting accounting, uint size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, string environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint SearchPathW(string? path, string file, string extension, int size, StringBuilder buffer, IntPtr part);
}
