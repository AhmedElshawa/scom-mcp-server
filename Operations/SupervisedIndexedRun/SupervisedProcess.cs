using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

// Windows 10 / Server 2016+: bind the job during creation, before child code runs.
public sealed class SupervisedProcess : IDisposable
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);
    public static string[] SplitCommandLine(string commandLine) {
        int count;
        IntPtr memory = CommandLineToArgvW(commandLine, out count);
        if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            var args = new string[count];
            for (int i = 0; i < count; i++)
                args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size));
            return args;
        }
        finally { LocalFree(memory); }
    }
    IntPtr job, process;
    public int Id { get; private set; }
    public uint ActiveProcesses {
        get {
            Accounting data;
            Check(QueryInformationJobObject(job, 1, out data, (uint)Marshal.SizeOf(typeof(Accounting)), IntPtr.Zero));
            return data.Active;
        }
    }
    public bool HasExited { get { return WaitForExit(0); } }
    public int ExitCode {
        get {
            uint value;
            if (!HasExited) throw new InvalidOperationException("Process is still running.");
            Check(GetExitCodeProcess(process, out value));
            return unchecked((int)value);
        }
    }
    public bool WaitForExit(int milliseconds) {
        uint result = WaitForSingleObject(process, (uint)milliseconds);
        if (result == 0xffffffff) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result == 0;
    }
    public bool Stop(int milliseconds) {
        Check(TerminateJobObject(job, 1));
        return WaitEmpty(milliseconds);
    }
    public bool WaitEmpty(int milliseconds) {
        var clock = Stopwatch.StartNew();
        do {
            Accounting data;
            Check(QueryInformationJobObject(job, 1, out data, (uint)Marshal.SizeOf(typeof(Accounting)), IntPtr.Zero));
            if (data.Active == 0) return true;
            System.Threading.Thread.Sleep(10);
        } while (clock.ElapsedMilliseconds < milliseconds);
        return false;
    }
    public static SupervisedProcess Start(string executable, string arguments, string stdout, string stderr) {
        var child = new SupervisedProcess();
        IntPtr output = IntPtr.Zero, error = IntPtr.Zero, input = IntPtr.Zero, attrs = IntPtr.Zero;
        IntPtr handles = IntPtr.Zero, jobs = IntPtr.Zero;
        try {
            child.job = CreateJobObject(IntPtr.Zero, null);
            Check(child.job != IntPtr.Zero);
            var limits = new Limits();
            limits.Basic.Flags = 0x2000;
            Check(SetInformationJobObject(child.job, 9, ref limits, (uint)Marshal.SizeOf(typeof(Limits))));
            var security = new Security();
            security.Size = Marshal.SizeOf(typeof(Security)); security.Inherit = true;
            output = CreateFile(stdout, 0x40000000, 1, ref security, 2, 0x80, IntPtr.Zero);
            error = CreateFile(stderr, 0x40000000, 1, ref security, 2, 0x80, IntPtr.Zero);
            input = CreateFile("NUL", 0x80000000, 1, ref security, 3, 0x80, IntPtr.Zero);
            Check(output != new IntPtr(-1) && error != new IntPtr(-1) && input != new IntPtr(-1));
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attrs = Marshal.AllocHGlobal(size);
            Check(InitializeProcThreadAttributeList(attrs, 2, 0, ref size));
            handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.Copy(new IntPtr[] { input, output, error }, 0, handles, 3);
            jobs = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobs, child.job);
            Check(UpdateProcThreadAttribute(attrs, 0, new IntPtr(0x20002), handles, new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero));
            Check(UpdateProcThreadAttribute(attrs, 0, new IntPtr(0x2000D), jobs, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
            var start = new StartupEx();
            start.Start.Size = Marshal.SizeOf(typeof(StartupEx));
            start.Start.Flags = 0x100;
            start.Start.Input = input; start.Start.Output = output; start.Start.Error = error;
            start.Attributes = attrs;
            Created created;
            Check(CreateProcess(executable, new StringBuilder("\"" + executable + "\" " + arguments),
                IntPtr.Zero, IntPtr.Zero, true, 0x80000 | 0x08000000 | 4, IntPtr.Zero, null, ref start, out created));
            child.process = created.Process;
            child.Id = created.ProcessId;
            try { Check(ResumeThread(created.Thread) != uint.MaxValue); }
            finally { CloseHandle(created.Thread); }
            return child;
        }
        catch {
            try { if (child.job != IntPtr.Zero && child.process != IntPtr.Zero) child.Stop(5000); }
            finally { child.Dispose(); }
            throw;
        }
        finally {
            foreach (var h in new IntPtr[] { input, output, error })
                if (h != IntPtr.Zero && h != new IntPtr(-1)) CloseHandle(h);
            if (attrs != IntPtr.Zero) { DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs); }
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
        }
    }
    public void Dispose() {
        if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
        if (process != IntPtr.Zero) { CloseHandle(process); process = IntPtr.Zero; }
    }
    static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] struct Security { public int Size; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits { public long A, B; public uint Flags; public UIntPtr Min, Max; public uint Count; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong A,B,C,D,E,F; }
    [StructLayout(LayoutKind.Sequential)] struct Limits { public BasicLimits Basic; public Io Io; public UIntPtr A,B,C,D; }
    [StructLayout(LayoutKind.Sequential)] struct Accounting { public long A,B,C,D; public uint Faults, Total, Active, Terminated; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Startup { public int Size; public string Reserved, Desktop, Title; public uint X,Y,W,H,CX,CY,Fill,Flags; public short Show, ReservedSize; public IntPtr ReservedPtr, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] struct StartupEx { public Startup Start; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct Created { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode, EntryPoint="CreateJobObjectW")] static extern IntPtr CreateJobObject(IntPtr a,string b);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr j,int c,ref Limits d,uint n);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr j,int c,out Accounting d,uint n,IntPtr r);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateJobObject(IntPtr j,uint c);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr h,out uint c);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(IntPtr h,uint t);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint ResumeThread(IntPtr t);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr p,int c,int f,ref IntPtr s);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr p);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr p,uint f,IntPtr a,IntPtr v,IntPtr s,IntPtr old,IntPtr ret);
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode, EntryPoint="CreateFileW")] static extern IntPtr CreateFile(string p,uint a,uint s,ref Security sec,uint c,uint f,IntPtr t);
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode, EntryPoint="CreateProcessW")] static extern bool CreateProcess(string app,StringBuilder cmd,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string cwd,ref StartupEx start,out Created result);
}
