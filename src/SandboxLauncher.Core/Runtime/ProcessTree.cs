using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SandboxLauncher.Core.Runtime;

/// <summary>启动结果：托管进程句柄 + 整棵树跟踪器（拿不到 job 时为 null）。</summary>
public sealed record LaunchResult(Process Process, ProcessTree? Tree);

/// <summary>
/// 用 Windows 作业对象（Job Object）跟踪「启动器 + 它拉起来的子进程」这一整棵树。
/// 很多游戏/客户端是一个启动器进程先把界面或更新做完，再把真正的程序作为子进程拉起来，
/// 自己退出 0 —— 如果只盯着第一个进程，沙盒会在子进程刚起来时就把会话目录删掉，表现为「起不来」。
/// 这里把进程「挂起创建 → 放进 job → 再放行」，保证启动器拉起来的子进程一定在 job 里；
/// 等 ActiveProcesses 归零再收尾，job 句柄关闭时 Windows 会杀掉残留子进程，启动器崩溃也不会留下孤儿。
/// </summary>
public sealed class ProcessTree : IDisposable
{
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, int length,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? applicationName, string? commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr threadHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    private IntPtr _handle;

    /// <summary>作业对象是否可用（拿不到就退化成「只等第一个进程」的旧行为）。</summary>
    public bool Valid => _handle != IntPtr.Zero;

    /// <summary>job 名字（调试用）。</summary>
    public string Name { get; }

    private ProcessTree(IntPtr handle, string name)
    {
        _handle = handle;
        Name = name;
    }

    /// <summary>
    /// 启动进程并把它放进作业对象：先挂起创建 → 加入 job → 再放行，
    /// 这样启动器在启动瞬间拉起来的子进程也一定在 job 里（否则会漏掉、提前收尾）。
    /// </summary>
    public static LaunchResult Launch(ProcessStartInfo startInfo, string name)
    {
        if (startInfo.UseShellExecute)
            throw new InvalidOperationException("Launch 不支持 UseShellExecute=true");

        var job = CreateJob(out var jobHandle);
        IntPtr environment = IntPtr.Zero;
        ProcessInformation info = default;

        try
        {
            environment = BuildEnvironmentBlock(startInfo.Environment);

            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var commandLine = BuildCommandLine(startInfo.FileName, startInfo.Arguments);
            var directory = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
                ? null
                : startInfo.WorkingDirectory;

            if (!CreateProcessW(startInfo.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CreateSuspended | CreateUnicodeEnvironment, environment, directory, ref startup, out info))
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"启动进程失败（Win32 错误 {error}）：{startInfo.FileName}");
            }

            if (jobHandle != IntPtr.Zero)
                AssignProcessToJobObject(jobHandle, info.Process); // 失败就退化成「只跟踪第一个进程」

            ResumeThread(info.Thread);

            var process = Process.GetProcessById(info.ProcessId);
            process.EnableRaisingEvents = true;
            return new LaunchResult(process, job);
        }
        catch
        {
            if (info.Process != IntPtr.Zero)
            {
                try { ResumeThread(info.Thread); } catch { }
                try { using var p = Process.GetProcessById(info.ProcessId); p.Kill(true); } catch { }
            }

            job?.Dispose();
            throw;
        }
        finally
        {
            if (info.Thread != IntPtr.Zero) CloseHandle(info.Thread);
            if (info.Process != IntPtr.Zero) CloseHandle(info.Process);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    /// <summary>把已经启动的进程补进作业对象（拿不到就返回 null）。</summary>
    public static ProcessTree? Attach(Process process, string name)
    {
        if (process.HasExited) return null;

        var tree = CreateJob(out var handle);
        if (tree == null) return null;

        if (!AssignProcessToJobObject(handle, process.Handle))
        {
            tree.Dispose();
            return null;
        }

        return tree;
    }

    private static ProcessTree? CreateJob(out IntPtr handle)
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return null;

        var info = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
        };

        var size = Marshal.SizeOf<ExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, size);
        }
        catch
        {
            // 设置失败不影响把进程放进 job
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new ProcessTree(handle, "sandbox");
    }

    private static string BuildCommandLine(string fileName, string arguments)
    {
        var exe = fileName.Contains(' ') ? $"\"{fileName}\"" : fileName;
        return string.IsNullOrWhiteSpace(arguments) ? exe : exe + " " + arguments;
    }

    private static IntPtr BuildEnvironmentBlock(IEnumerable<KeyValuePair<string, string?>>? environment)
    {
        if (environment == null) return IntPtr.Zero;

        var pairs = new List<string>();
        foreach (var entry in environment)
        {
            if (string.IsNullOrEmpty(entry.Key)) continue;
            pairs.Add(entry.Key + "=" + (entry.Value ?? ""));
        }

        pairs.Sort(StringComparer.OrdinalIgnoreCase);
        var text = string.Join("\0", pairs) + "\0\0";
        var bytes = Encoding.Unicode.GetBytes(text);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    /// <summary>作业里还活着几个进程（读不到就当 1，避免提前收尾）。</summary>
    public int ActiveProcesses
    {
        get
        {
            if (!Valid) return 0;

            var size = Marshal.SizeOf<BasicAccountingInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, buffer, size, out _))
                    return 1;

                var info = Marshal.PtrToStructure<BasicAccountingInformation>(buffer);
                return (int)info.ActiveProcesses;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>累计进过这个 job 的进程数（调试/日志用）。</summary>
    public int TotalProcesses
    {
        get
        {
            if (!Valid) return 0;

            var size = Marshal.SizeOf<BasicAccountingInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, buffer, size, out _))
                    return 0;

                return (int)Marshal.PtrToStructure<BasicAccountingInformation>(buffer).TotalProcesses;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>杀掉整棵树（停止沙盒用）。</summary>
    public bool KillAll()
    {
        return Valid && TerminateJobObject(_handle, 1);
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            // KillOnJobClose：关掉句柄时残留子进程会被系统杀掉，不会留下孤儿
            CloseHandle(handle);
        }
    }
}
