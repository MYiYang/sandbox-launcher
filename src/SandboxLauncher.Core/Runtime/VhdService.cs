using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text;
using System.Text.Json;

namespace SandboxLauncher.Core.Runtime;

/// <summary>一个 VHD 文件（基础盘或差分盘）的描述。</summary>
public sealed class VhdInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>差分盘的父盘路径；基础盘为 null。</summary>
    public string? ParentPath { get; set; }
    public long SizeBytes { get; set; }
    public long FileBytes { get; set; }
    public bool IsDifferencing => ParentPath != null;
    /// <summary>当前挂载到的盘符（未挂载为 null）。</summary>
    public char? DriveLetter { get; set; }
    public bool Mounted => DriveLetter != null;
}

/// <summary>可以放 VHD 的位置（给用户挑：哪个盘空间够、是不是固态）。</summary>
public sealed class DiskCandidate
{
    public char Letter { get; set; }
    public string Root { get; set; } = "";
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public long FreeBytes { get; set; }
    public long TotalBytes { get; set; }
    /// <summary>SSD / HDD（拿不到就空）。</summary>
    public string MediaType { get; set; } = "";
    public string Display =>
        $"{Letter}:  {FormatGb(FreeBytes)} 可用 / {FormatGb(TotalBytes)}" +
        (string.IsNullOrWhiteSpace(Label) ? "" : $"  「{Label}」") +
        (string.IsNullOrWhiteSpace(MediaType) ? "" : $"  {MediaType}");

    public static string FormatGb(long bytes) => (bytes / 1024.0 / 1024 / 1024).ToString("0.#") + " GB";

    public override string ToString() => Display;
}

/// <summary>
/// VHD 差分盘（块级写时复制）：把一份 85 GB 的程序/数据放进「基础盘」，
/// 每个分身挂一个「差分盘」（只存改动块），用户视角是「程序只有一份、每个分身各一套数据」，
/// 磁盘成本 ≈ 0（差分盘初始只有几 MB，写出多少才占多少）。
/// 依赖 Windows 自带的 Hyper-V/VHD 能力（New-VHD / Mount-VHD / 磁盘管理），需要管理员。
/// </summary>
public sealed class VhdService
{
    public string Root { get; }

    public VhdService(string? root = null)
    {
        Root = root ?? System.IO.Path.Combine(AppPaths.Root, "vhd");
        Directory.CreateDirectory(Root);
    }

    /// <summary>本机能不能用（Hyper-V 的 VHD 模块 + 管理员权限）。</summary>
    public static bool IsSupported()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"if (Get-Command New-VHD -ErrorAction SilentlyContinue) { 'ok' } else { 'no' }\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return false;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(15000);
            return output.Contains("ok");
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<VhdInfo> List()
    {
        var result = new List<VhdInfo>();
        if (!Directory.Exists(Root)) return result;

        var letters = MountedLetters();
        foreach (var file in Directory.GetFiles(Root, "*.vhdx").OrderBy(f => f))
        {
            result.Add(Describe(file, letters));
        }

        return result;
    }

    /// <summary>把整个程序目录「搬进」基础盘（不复制：先建盘、再把目录移进去，磁盘占用不增加）。</summary>
    public VhdInfo CreateBase(string name, string sourceDirectory, long sizeBytes, char? driveLetter = null)
    {
        if (!Directory.Exists(sourceDirectory)) throw new DirectoryNotFoundException(sourceDirectory);

        var path = System.IO.Path.Combine(Root, SafeName(name) + "-base.vhdx");
        if (File.Exists(path)) throw new IOException($"基础盘已存在：{path}");

        var letter = driveLetter ?? FreeLetter();
        var sourceSize = DirectorySize(sourceDirectory);
        if (sizeBytes <= 0) sizeBytes = Math.Max(sourceSize * 2, 8L * 1024 * 1024 * 1024);

        var script = $@"
$ErrorActionPreference = 'Stop'
New-VHD -Path '{path}' -SizeBytes {sizeBytes} -Dynamic | Out-Null
$disk = Mount-VHD -Path '{path}' -Passthru | Get-Disk
Initialize-Disk -Number $disk.Number -PartitionStyle MBR -ErrorAction Stop | Out-Null
$part = New-Partition -DiskNumber $disk.Number -UseMaximumSize -DriveLetter '{letter}'
Format-Volume -Partition $part -FileSystem NTFS -NewFileSystemLabel 'SandboxLauncher' -Confirm:$false | Out-Null
'ok'
";
        Run(script);

        try
        {
            MoveDirectory(sourceDirectory, $"{letter}:\\app");
        }
        finally
        {
            Run($"Dismount-VHD -Path '{path}' -ErrorAction SilentlyContinue; 'ok'");
        }

        return Describe(path, MountedLetters());
    }

    /// <summary>建一个差分盘（子盘），初始只有几 MB。</summary>
    public VhdInfo CreateChild(string name, string parentNameOrPath)
    {
        var parent = Resolve(parentNameOrPath);
        if (parent == null) throw new FileNotFoundException($"找不到基础盘：{parentNameOrPath}");

        var path = System.IO.Path.Combine(Root, SafeName(name) + "-child.vhdx");
        if (File.Exists(path)) throw new IOException($"差分盘已存在：{path}");

        Run($@"
$ErrorActionPreference = 'Stop'
New-VHD -Path '{path}' -ParentPath '{parent.Path}' -Differencing | Out-Null
'ok'
");
        return Describe(path, MountedLetters());
    }

    /// <summary>
    /// 挂载瞬间临时关掉 AutoPlay（否则 Windows 可能弹「打开新驱动器」窗口），挂完立刻恢复原设置。
    /// </summary>
    private static (bool Had, int OldValue) SuppressAutoPlayTemporarily()
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            var existing = key?.GetValue("DisableAutoplay");
            var oldValue = existing is int i ? i : (existing is long l ? (int)l : 0);
            key?.SetValue("DisableAutoplay", 1, RegistryValueKind.DWord);
            return (existing != null, oldValue);
        }
        catch
        {
            return (false, 0);
        }
    }

    private static void RestoreAutoPlay((bool Had, int OldValue) state)
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            if (key == null) return;
            if (state.Had) key.SetValue("DisableAutoplay", state.OldValue, RegistryValueKind.DWord);
            else key.DeleteValue("DisableAutoplay", throwOnMissingValue: false);
        }
        catch
        {
            // 恢复失败不影响主流程
        }
    }

    /// <summary>
    /// 新卷挂载时 Windows 会自动弹一个资源管理器窗口（「打开新驱动器」）。
    /// 我们不需要它——挂完就把这个盘符对应的 Explorer 窗口关掉（连续看几次，避免它晚一步弹出来）。
    /// </summary>
    private static void CloseExplorerForDrive(char letter)
    {
        try
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                if (CloseExplorerWindows(letter) > 0) return;
                Thread.Sleep(250);
            }
        }
        catch
        {
            // 关不掉就算了，不影响挂载
        }
    }

    private static int CloseExplorerWindows(char letter)
    {
        var closed = 0;
        object? shell = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return 0;
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return 0;

            dynamic windows = shellType.InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null,
                shell, null)!;
            var count = (int)windows.Count;
            for (var i = count - 1; i >= 0; i--)
            {
                object? window = null;
                try
                {
                    window = windows.Item(i);
                    dynamic w = window!;
                    string? url = null;
                    try { url = w.LocationURL as string; } catch { }
                    if (string.IsNullOrEmpty(url)) continue;

                    var prefix = "file:///" + letter + ":";
                    if (!url!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                    w.Quit();
                    closed++;
                }
                catch
                {
                    // 单个窗口失败不影响其它
                }
                finally
                {
                    if (window != null && Marshal.IsComObject(window)) Marshal.ReleaseComObject(window);
                }
            }
        }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }

        return closed;
    }

    /// <summary>挂载 VHD，返回 Windows 实际分配的盘符。</summary>
    public char Mount(string nameOrPath, char? preferredLetter = null)
    {
        var info = Resolve(nameOrPath) ?? throw new FileNotFoundException($"找不到 VHD：{nameOrPath}");

        // 已经挂载就直接返回现成盘符
        var mounted = MountedLetters();
        foreach (var (existingLetter, existingPath) in mounted)
        {
            if (string.Equals(existingPath, info.Path, StringComparison.OrdinalIgnoreCase))
                return existingLetter;
        }

        var fallback = preferredLetter ?? FreeLetter();

        // 差分盘会继承基础盘的分区（包括记录的盘符），所以先让 Windows 自己挂；
        // 拿不到盘符才手动指定一个空闲盘符。
        var autoPlayState = SuppressAutoPlayTemporarily();
        string output;
        try
        {
            output = Run($@"
$ErrorActionPreference = 'Stop'
$disk = Mount-VHD -Path '{info.Path}' -Passthru | Get-Disk
Start-Sleep -Milliseconds 1200
$part = Get-Partition -DiskNumber $disk.Number | Where-Object {{ $_.Type -eq 'IFS' -or $_.DriveLetter }} | Select-Object -First 1
$letter = $part.DriveLetter
if (-not $letter) {{
    Set-Partition -DiskNumber $disk.Number -PartitionNumber $part.PartitionNumber -NewDriveLetter '{fallback}' | Out-Null
    $letter = '{fallback}'
}}
$letter
");
        }
        finally
        {
            RestoreAutoPlay(autoPlayState);
        }

        var text = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        if (!string.IsNullOrEmpty(text) && text.Length == 1 && char.IsLetter(text[0]))
        {
            var mountedLetter = char.ToUpperInvariant(text[0]);
            CloseExplorerForDrive(mountedLetter);   // 关掉 Windows 自动弹出的「打开新驱动器」窗口
            return mountedLetter;
        }

        throw new InvalidOperationException("挂载成功但没拿到盘符：" + output.Trim());
    }

    /// <summary>卸载 VHD。</summary>
    public void Dismount(string nameOrPath)
    {
        var info = Resolve(nameOrPath) ?? throw new FileNotFoundException($"找不到 VHD：{nameOrPath}");
        Run($"Dismount-VHD -Path '{info.Path}' -ErrorAction SilentlyContinue; 'ok'");
    }

    /// <summary>差分盘有多少块落到了子盘（用于展示"实际占了多少"）。</summary>
    public static long FileBytes(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    /// <summary>枚举能放 VHD 的磁盘（按剩余空间从大到小），给用户挑位置。</summary>
    public static IReadOnlyList<DiskCandidate> ListDisks()
    {
        var list = new List<DiskCandidate>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType != DriveType.Fixed) continue;
                list.Add(new DiskCandidate
                {
                    Letter = drive.Name[0],
                    Root = drive.RootDirectory.FullName,
                    Label = drive.VolumeLabel,
                    FileSystem = drive.DriveFormat,
                    FreeBytes = drive.AvailableFreeSpace,
                    TotalBytes = drive.TotalSize
                });
            }
            catch
            {
                // 读不到的盘跳过
            }
        }

        // 补上介质类型（SSD/HDD）
        try
        {
            var json = Run(@"
$items = @()
foreach ($p in (Get-Partition | Where-Object { $_.DriveLetter })) {
  $disk = Get-Disk -Number $p.DiskNumber -ErrorAction SilentlyContinue
  if ($disk) { $items += [pscustomobject]@{ Letter = [string]$p.DriveLetter; Media = [string]$disk.MediaType } }
}
ConvertTo-Json -InputObject $items -Compress
");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var letter = item.GetProperty("Letter").GetString();
                    var media = item.GetProperty("Media").GetString();
                    if (string.IsNullOrEmpty(letter)) continue;
                    var hit = list.FirstOrDefault(d => d.Letter == letter[0]);
                    if (hit != null) hit.MediaType = media ?? "";
                }
            }
        }
        catch
        {
            // 介质类型拿不到不影响使用
        }

        return list.OrderByDescending(d => d.FreeBytes).ToList();
    }

    /// <summary>这个 VHD 现在是不是挂载着。</summary>
    public bool IsMounted(string vhdPath)
    {
        foreach (var (_, path) in MountedLetters())
        {
            if (string.Equals(path, vhdPath, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>确保这个 VHD 已经挂载（没挂就按偏好盘符挂），返回盘符。</summary>
    public char EnsureMounted(string vhdPath, char? preferredLetter = null)
    {
        foreach (var (letter, path) in MountedLetters())
        {
            if (string.Equals(path, vhdPath, StringComparison.OrdinalIgnoreCase))
                return letter;
        }

        return Mount(vhdPath, preferredLetter);
    }

    // ---------- 内部 ----------

    private VhdInfo Describe(string path, Dictionary<char, string> mountedLetters)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var file = new FileInfo(path);
        var info = new VhdInfo
        {
            Name = name,
            Path = path,
            SizeBytes = VirtualSize(path),
            FileBytes = file.Exists ? file.Length : 0
        };

        // 差分盘的父盘：读取 VHDX 里的 differencing 关系（用 Hyper-V 查询）
        var parent = Run($"(Get-VHD -Path '{path}').ParentPath");
        if (!string.IsNullOrWhiteSpace(parent) && parent.Trim() != "" && parent.Trim() != "False")
            info.ParentPath = parent.Trim();

        foreach (var (letter, mountedPath) in mountedLetters)
        {
            if (string.Equals(mountedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                info.DriveLetter = letter;
                break;
            }
        }

        return info;
    }

    private static Dictionary<char, string> MountedLetters()
    {
        var map = new Dictionary<char, string>();
        var json = Run(@"
$items = @()
foreach ($disk in (Get-Disk | Where-Object { $_.FriendlyName -eq 'Msft Virtual Disk' })) {
  $part = Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | Select-Object -First 1
  if ($part) {
    $path = (Get-VHD -DiskNumber $disk.Number -ErrorAction SilentlyContinue).Path
    if ($path) { $items += [pscustomobject]@{ Letter = [string]$part.DriveLetter; Path = $path } }
  }
}
ConvertTo-Json -InputObject $items -Compress
");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var letter = item.GetProperty("Letter").GetString();
                    var path = item.GetProperty("Path").GetString();
                    if (!string.IsNullOrEmpty(letter) && !string.IsNullOrEmpty(path))
                        map[letter[0]] = path!;
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                var letter = root.GetProperty("Letter").GetString();
                var path = root.GetProperty("Path").GetString();
                if (!string.IsNullOrEmpty(letter) && !string.IsNullOrEmpty(path))
                    map[letter[0]] = path!;
            }
        }
        catch
        {
            // 解析失败就当没挂载
        }

        return map;
    }

    private long VirtualSize(string path)
    {
        var text = Run($"(Get-VHD -Path '{path}').Size");
        return long.TryParse(text.Trim(), out var size) ? size : 0;
    }

    private VhdInfo? Resolve(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return Describe(nameOrPath, MountedLetters());

        // 允许用短名（testapp）或全名（testapp-base / testapp-child）
        var safe = SafeName(nameOrPath);
        var candidates = new[]
        {
            nameOrPath, safe,
            safe + "-base", safe + "-child",
            nameOrPath + "-base", nameOrPath + "-child"
        };

        return List().FirstOrDefault(v => candidates.Any(c =>
            string.Equals(v.Name, c, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(System.IO.Path.GetFileNameWithoutExtension(v.Path), c, StringComparison.OrdinalIgnoreCase)));
    }

    private static char FreeLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        for (var c = 'V'; c <= 'Z'; c++)
        {
            if (!used.Contains(c)) return c;
        }

        for (var c = 'E'; c <= 'U'; c++)
        {
            if (!used.Contains(c)) return c;
        }

        throw new InvalidOperationException("没有空闲盘符可用");
    }

    private static long DirectorySize(string directory)
    {
        long total = 0;
        foreach (var file in Snapshot.FsWalker.EnumerateFiles(directory)) total += file.SizeBytes;
        return total;
    }

    /// <summary>同卷移动；跨卷时退化成复制+删除。</summary>
    private static void MoveDirectory(string source, string destination)
    {
        if (Directory.Exists(destination)) throw new IOException($"目标已存在：{destination}");
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException)
        {
            CopyDirectory(source, destination);
            Snapshot.FsWalker.EnumerateFiles(source).ToList();
            Directory.Delete(source, true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Snapshot.FsWalker.EnumerateFiles(source))
        {
            var target = System.IO.Path.Combine(destination, file.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file.HostPath, target, true);
        }
    }

    private static string SafeName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "vhd" : cleaned;
    }

    private static string Run(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + Quote(script))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 PowerShell");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(180_000);

        if (process.ExitCode != 0)
            throw new InvalidOperationException("VHD 操作失败：" +
                (string.IsNullOrWhiteSpace(error) ? output : error).Trim());

        return output;
    }

    private static string Quote(string script)
    {
        // -Command 里用双引号包脚本、把内部双引号转义成 `"
        return "\"" + script.Replace("\"", "`\"") + "\"";
    }
}
