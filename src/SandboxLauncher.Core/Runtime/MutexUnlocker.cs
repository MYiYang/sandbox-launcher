using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SandboxLauncher.Core.Runtime;

/// <summary>
/// 释放「别的实例」手里的命名互斥体（Mutant）句柄。
/// 用途：同一个程序第二个实例启动时，很多客户端会用自己的固定名字互斥体做单实例判断
/// （典型形态：`<产品名>-Mutex-Window`、`<产品名>-Mutex-Install-<编号>` 这类固定名字）。
/// 把已运行实例的这几个句柄关掉，
/// 第二个实例就能正常起来。**只操作我们自己启动的进程，不注入、不读写对方内存**。
/// </summary>
public static class MutexUnlocker
{
    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectNameInformation = 1;
    private const int ObjectTypeInformation = 2;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out IntPtr targetHandle, uint access, bool inherit, uint options);

    private const uint ProcessDupHandle = 0x0040;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint DuplicateCloseSource = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    /// <summary>进程里所有「命名互斥体」的名字（不含无名对象）。</summary>
    public static IReadOnlyList<string> EnumerateMutantNames(int pid)
    {
        var names = new List<string>();
        var process = OpenProcess(ProcessDupHandle, false, pid);
        if (process == IntPtr.Zero) return names;

        try
        {
            foreach (var (handle, ownerPid) in EnumerateHandles())
            {
                if (ownerPid != pid) continue;

                var duplicated = IntPtr.Zero;
                if (!DuplicateHandle(process, new IntPtr((long)handle), Process.GetCurrentProcess().Handle,
                        out duplicated, 0, false, DuplicateSameAccess))
                    continue;

                try
                {
                    if (!IsMutant(duplicated)) continue;
                    var name = QueryName(duplicated);
                    if (!string.IsNullOrEmpty(name) && IsSessionNamedObject(name!)) names.Add(name!);
                }
                finally
                {
                    CloseHandle(duplicated);
                }
            }
        }
        finally
        {
            CloseHandle(process);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 关掉进程里名字匹配的通配符模式的互斥体句柄，返回关掉的名字列表。
    /// 模式大小写不敏感，支持 * 通配（例如 AppName-Mutex-*）。
    /// </summary>
    public static IReadOnlyList<string> Release(int pid, IEnumerable<string> patterns)
    {
        var patternList = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (patternList.Count == 0) return Array.Empty<string>();

        var closed = new List<string>();
        var process = OpenProcess(ProcessDupHandle, false, pid);
        if (process == IntPtr.Zero) return closed;

        try
        {
            foreach (var (handle, ownerPid) in EnumerateHandles())
            {
                if (ownerPid != pid) continue;

                var duplicated = IntPtr.Zero;
                if (!DuplicateHandle(process, new IntPtr((long)handle), Process.GetCurrentProcess().Handle,
                        out duplicated, 0, false, DuplicateSameAccess))
                    continue;

                string? name = null;
                var isMutant = false;
                try
                {
                    isMutant = IsMutant(duplicated);
                    if (isMutant) name = QueryName(duplicated);
                }
                finally { CloseHandle(duplicated); }

                if (!isMutant || string.IsNullOrEmpty(name)) continue;
                if (!IsSessionNamedObject(name!)) continue;

                // 允许用户只写短名（AppName-Mutex-*），也接受完整对象路径
                var leaf = name!.Substring(name.LastIndexOf('\\') + 1);
                if (!patternList.Any(p => GlobMatch(name!, p) || GlobMatch(leaf, p))) continue;

                // 关键一步：DUPLICATE_CLOSE_SOURCE 把对方进程里的句柄关掉
                if (DuplicateHandle(process, new IntPtr((long)handle), Process.GetCurrentProcess().Handle,
                        out var closing, 0, false, DuplicateCloseSource))
                {
                    if (closing != IntPtr.Zero) CloseHandle(closing);
                    closed.Add(name!);
                }
            }
        }
        finally
        {
            CloseHandle(process);
        }

        return closed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 从一组互斥体名字里挑出「像是这个程序自己用来做单实例判断的」那些：
    /// 过滤掉系统/输入法/GUID 名字，剩下的通常是应用的固定名字锁。
    /// </summary>
    public static IReadOnlyList<string> GuessAppMutexNames(IEnumerable<string> names)
    {
        string[] skip =
        {
            "{", "Zones", "UrlZones", "windows_shell", "INPUT_METHOD", "sogou", "SGPY", "sgim", "sgfm",
            "Wil", "SM0:", "CrSharedMem", "msys-", "DirectX", "DXGI", "d3d", "nv", "amd", "Coherent",
            "CEF", "SessionImmersive", "mojo.", "chrome", "Wininet", "WinInet", "ie_", "IE_",
            "filemap", "Filemap", "Retrieve_Bundle", "CheckFastpassport", "sgrarecc", "component_config",
            "DictManager", "ImeKernel", "userenv", "statuserenv", "mem_picface", "QQ_", "QPCore", "QP_"
        };

        return names
            .Where(name =>
            {
                var leaf = name.Substring(name.LastIndexOf('\\') + 1).Replace("\\", "");
                if (leaf.Length < 6) return false;
                // 只保留「名字里带 Mutex」的那类互斥体——应用的单实例锁几乎都长这样
                if (!leaf.Contains("Mutex", StringComparison.OrdinalIgnoreCase)) return false;
                return !skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------- 内部 ----------

    private static IEnumerable<(ulong Handle, int Pid)> EnumerateHandles()
    {
        var size = 1024 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            while (true)
            {
                var status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out var needed);
                if (status == unchecked((int)0xC0000004)) // STATUS_INFO_LENGTH_MISMATCH
                {
                    Marshal.FreeHGlobal(buffer);
                    size = Math.Max(needed + 0x10000, size * 2);
                    buffer = Marshal.AllocHGlobal(size);
                    continue;
                }

                if (status != 0) yield break;

                var count = Marshal.ReadIntPtr(buffer);
                var entryPtr = IntPtr.Add(buffer, IntPtr.Size * 2);
                var entrySize = 40; // SYSTEM_HANDLE_INFORMATION_EX entry on x64
                var total = count.ToInt64();

                for (long i = 0; i < total; i++)
                {
                    var entry = IntPtr.Add(entryPtr, (int)(i * entrySize));
                    var objectPointer = Marshal.ReadIntPtr(entry);
                    var pid = Marshal.ReadIntPtr(entry, IntPtr.Size).ToInt32();
                    var handle = (ulong)Marshal.ReadIntPtr(entry, IntPtr.Size * 2).ToInt64();
                    if (objectPointer == IntPtr.Zero || handle == 0) continue;
                    yield return (handle, pid);
                }

                yield break;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>这个句柄是不是互斥体（Mutant）。</summary>
    private static bool IsMutant(IntPtr handle)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            var status = NtQueryObject(handle, ObjectTypeInformation, buffer, 512, out var needed);
            if (status != 0 && needed > 512)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(needed);
                status = NtQueryObject(handle, ObjectTypeInformation, buffer, needed, out _);
            }

            if (status != 0) return false;

            var typeName = Marshal.PtrToStructure<UnicodeString>(buffer);
            if (typeName.Buffer == IntPtr.Zero || typeName.Length == 0) return false;
            var text = Marshal.PtrToStringUni(typeName.Buffer, typeName.Length / 2);
            return string.Equals(text, "Mutant", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>只处理会话命名空间里的对象（BaseNamedObjects 下），别碰设备/注册表/窗口站。</summary>
    private static bool IsSessionNamedObject(string name)
    {
        return name.Contains("BaseNamedObjects", StringComparison.OrdinalIgnoreCase);
    }

    private static string? QueryName(IntPtr handle)
    {
        var size = 4096;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = NtQueryObject(handle, ObjectNameInformation, buffer, size, out var needed);
            if (status != 0)
            {
                if (needed <= 0) return null;
                Marshal.FreeHGlobal(buffer);
                size = needed + 64;
                buffer = Marshal.AllocHGlobal(size);
                status = NtQueryObject(handle, ObjectNameInformation, buffer, size, out _);
                if (status != 0) return null;
            }

            var unicode = Marshal.PtrToStructure<UnicodeString>(buffer);
            if (unicode.Buffer == IntPtr.Zero || unicode.Length == 0 || unicode.Length > 32768) return null;

            try { return Marshal.PtrToStringUni(unicode.Buffer, unicode.Length / 2); }
            catch { return null; }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool GlobMatch(string text, string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(text, regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
