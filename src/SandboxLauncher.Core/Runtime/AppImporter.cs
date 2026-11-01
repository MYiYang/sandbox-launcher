using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using SandboxLauncher.Core.Model;
using SandboxLauncher.Core.Snapshot;

namespace SandboxLauncher.Core.Runtime;

/// <summary>把客户端程序（一个 exe 或整个目录）导入沙盒时的解析结果。</summary>
public sealed record SourcePlan(string ExeFullPath, string RootDirectory, bool SingleFileOnly,
    AppSourceMode Mode = AppSourceMode.Copy);

public static class AppImporter
{
    /// <summary>「大软件」阈值：超过这个体积就默认走引用模式（不复制）。</summary>
    public const long LargeAppThreshold = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// 解析用户给的路径：
    ///   • 目录 → 取目录里最像主程序的那个 exe，导入整个目录
    ///   • exe  → 只导入这一个文件（Copy 模式）/ 引用它所在的整个安装目录（Reference 模式）
    /// </summary>
    public static SourcePlan ResolveSource(string sourcePath, AppSourceMode mode = AppSourceMode.Copy)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("请选择客户端可执行文件或程序目录");

        var path = Path.GetFullPath(sourcePath.Trim().Trim('"'));

        if (Directory.Exists(path))
        {
            var exes = Directory.GetFiles(path, "*.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(e => new FileInfo(e).Length)
                .ToList();
            if (exes.Count == 0) throw new FileNotFoundException($"目录里没有找到可执行文件（.exe）：{path}");

            var dirName = new DirectoryInfo(path).Name;
            var preferred = exes.FirstOrDefault(e =>
                string.Equals(Path.GetFileNameWithoutExtension(e), dirName, StringComparison.OrdinalIgnoreCase));
            return new SourcePlan(preferred ?? exes[0], path, SingleFileOnly: false, mode);
        }

        if (File.Exists(path))
        {
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择 .exe 文件；如果想连同依赖一起导入，请选择程序所在目录");
            // 引用模式：exe 只是一个入口，启动时它旁边的依赖都还在原目录，所以单文件也够。
            return new SourcePlan(path, Path.GetDirectoryName(path)!, SingleFileOnly: true, mode);
        }

        throw new FileNotFoundException("找不到要导入的程序：" + path);
    }

    /// <summary>
    /// 快速估算体积：累计到上限就停，几十 GB 的目录也能在一两秒内给出「至少这么大」的结论。
    /// 用来判断要不要建议「引用安装目录」（大软件不复制）。
    /// </summary>
    public static (long Bytes, int Files, bool Truncated) Probe(string pathOrDir,
        long maxBytes = 8L * 1024 * 1024 * 1024, int maxFiles = 60_000)
    {
        try
        {
            var path = Path.GetFullPath(pathOrDir.Trim().Trim('"'));
            if (File.Exists(path))
            {
                if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return (new FileInfo(path).Length, 1, false);

                // exe：把同目录（不递归）的大小算进来，判断「这软件是不是很大」时更准
                long dirBytes = 0;
                var dirFiles = 0;
                try
                {
                    foreach (var f in new DirectoryInfo(Path.GetDirectoryName(path)!).EnumerateFiles("*",
                                 SearchOption.TopDirectoryOnly))
                    {
                        dirBytes += f.Length;
                        if (++dirFiles >= maxFiles) break;
                    }
                }
                catch
                {
                    // 读不了同目录就只用 exe 自己的体积
                }

                return (Math.Max(dirBytes, new FileInfo(path).Length), Math.Max(dirFiles, 1), false);
            }

            if (!Directory.Exists(path)) return (0, 0, false);

            long bytes = 0;
            var count = 0;
            foreach (var file in FsWalker.EnumerateFiles(path))
            {
                bytes += file.SizeBytes;
                if (++count >= maxFiles || bytes >= maxBytes) return (bytes, count, true);
            }

            return (bytes, count, false);
        }
        catch
        {
            return (0, 0, false);
        }
    }

    /// <summary>
    /// 自动决定导入方式：
    ///   • 选的是「一个 exe」→ 引用（复制这一个文件几乎必然缺 DLL/资源，跑不起来）
    ///   • 选的是「一个目录」→ 大目录（≥ 2 GB）引用，小目录复制进快照
    /// </summary>
    public static AppSourceMode SuggestMode(string sourcePath)
    {
        try
        {
            var path = Path.GetFullPath(sourcePath.Trim().Trim('"'));
            if (File.Exists(path)) return AppSourceMode.Reference;
        }
        catch
        {
            // 路径本身有问题就退回按体积判断
        }

        var (bytes, _, truncated) = Probe(sourcePath, LargeAppThreshold);
        return bytes >= LargeAppThreshold || truncated ? AppSourceMode.Reference : AppSourceMode.Copy;
    }

    public static string MakeSlug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) && ch < 128 ? ch : '_');
        var slug = sb.ToString().Trim('_');
        if (slug.Length == 0) slug = "app";
        if (slug.Length > 40) slug = slug[..40];
        return $"{slug}-{ShortHash(name)}";
    }

    public static (int Count, long Bytes) Measure(string directory)
    {
        if (!Directory.Exists(directory)) return (0, 0);
        return Snapshot.FsWalker.Measure(directory);
    }

    public static string? ExtractIconBase64(string exePath, int maxBytes = 300_000)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            if (stream.Length == 0 || stream.Length > maxBytes) return null;
            return Convert.ToBase64String(stream.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static string ShortHash(string text)
    {
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..6].ToLowerInvariant();
    }
}
