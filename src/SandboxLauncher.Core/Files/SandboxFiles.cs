using System.IO.Compression;
using SandboxLauncher.Core.Model;
using SandboxLauncher.Core.Snapshot;

namespace SandboxLauncher.Core.Files;

/// <summary>一个文件作用域从哪来：运行中的会话目录 / .sbx 快照 / 宿主目录。</summary>
public enum FileScopeKind
{
    LiveSession,
    Snapshot,
    HostFolder
}

public sealed class SandboxFileEntry
{
    public string Name { get; init; } = "";
    /// <summary>相对作用域根的路径，用 / 分隔。</summary>
    public string RelativePath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset Modified { get; init; }

    public string KindText => IsDirectory ? "文件夹" : "文件";
    public string SizeText => IsDirectory ? "" : FileCopyResult.FormatSize(SizeBytes);
    public string ModifiedText => Modified == default ? "" : Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed class FileCopyResult
{
    public int FileCount { get; set; }
    public long Bytes { get; set; }
    /// <summary>写出去的绝对路径（导出）或沙盒内相对路径（导入进快照）。</summary>
    public List<string> Paths { get; } = new();
    public List<string> Skipped { get; } = new();
    public string? TargetPath { get; set; }

    public string Summary => $"{FileCount} 个文件（{FormatSize(Bytes)}）";

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}

/// <summary>
/// 一个可浏览、可导入导出的文件作用域。沙盒作用域在沙盒运行时直接读写会话目录，
/// 沙盒没运行时读写 .sbx 快照；也可以是用户添加的宿主目录。
/// </summary>
public interface IFileScope : IDisposable
{
    FileScopeKind Kind { get; }
    string KindText { get; }
    /// <summary>给人看的根位置（会话 fs 目录 / 快照文件 / 宿主目录）。</summary>
    string DisplayRoot { get; }
    /// <summary>能直接用资源管理器打开的根目录（快照作用域为 null）。</summary>
    string? HostRoot { get; }
    bool CanWrite { get; }
    /// <summary>路径是沙盒里的虚拟路径（快照作用域），不能直接交给资源管理器。</summary>
    bool IsVirtual { get; }

    IReadOnlyList<SandboxFileEntry> List(string relativePath);
    bool DirectoryExists(string relativePath);
    bool FileExists(string relativePath);
    string Normalize(string relativePath);
    Stream OpenRead(string relativePath);
    string? GetHostPath(string relativePath);
    /// <summary>把文件/目录取到宿主临时目录，返回宿主路径（运行中会话直接返回真实路径）。</summary>
    string Materialize(string relativePath, string tempRoot, Action<string>? log = null);
    FileCopyResult CopyOut(IEnumerable<string> relativePaths, string targetDirectory, bool overwrite = true,
        Action<string>? log = null);
    FileCopyResult CopyIn(string hostSourcePath, string targetRelativeDirectory, bool overwrite = true,
        Action<string>? log = null);
    /// <summary>删除沙盒/宿主目录里的文件或整个目录，返回删除的文件数。</summary>
    int Delete(IEnumerable<string> relativePaths);
    /// <summary>统计文件/目录里的文件数和总字节数（不实际复制）。</summary>
    (int Count, long Bytes) Measure(string relativePath);
    void Refresh();
}

/// <summary>路径归一化等小工具。</summary>
public static class ScopePath
{
    /// <summary>把用户/沙盒给出的路径归一成「相对根、/ 分隔、没有 .. 」的形式。</summary>
    public static string NormalizeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var normalized = path.Trim().Trim('"').Replace('\\', '/');
        if (normalized.StartsWith(SnapshotFile.FsPrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[SnapshotFile.FsPrefix.Length..];

        var parts = new List<string>();
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment.Trim());
        }

        var result = string.Join('/', parts);
        return result.Equals("fs", StringComparison.OrdinalIgnoreCase) ? "" : result;
    }

    public static string Combine(string parent, string name) =>
        string.IsNullOrEmpty(parent) ? name : parent + "/" + name;

    public static string Name(string relative)
    {
        var index = relative.LastIndexOf('/');
        return index < 0 ? relative : relative[(index + 1)..];
    }

    public static string Parent(string relative)
    {
        var index = relative.LastIndexOf('/');
        return index < 0 ? "" : relative[..index];
    }

    public static string ToHostRelative(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return result.Length == 0 ? "未命名" : result;
    }

    public static bool IsUnder(string fullPath, string root)
    {
        var full = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar);
        var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(full, basePath, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class FileAttributeTools
{
    /// <summary>
    /// 复制出来的文件如果是只读的（微信收到的文件、只读介质来的文件常常如此），
    /// 用户拿到宿主机后会改不动、删不掉，这里统一去掉只读位。
    /// </summary>
    public static void ClearReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception)
        {
            // 个别文件改不了属性不影响导出本身
        }
    }
}

/// <summary>
/// 沙盒文件系统：沙盒运行时直接读会话目录（实时，新收到的文件马上能看到），
/// 没运行时读 .sbx 快照（ZIP 里的 fs/…），导入的文件就地写回快照。
/// </summary>
public sealed class SandboxFileSystem : IFileScope
{
    private readonly SandboxRecord _record;
    private readonly string? _fsRoot;
    private FileStream? _zipStream;
    private ZipArchive? _zip;

    public SandboxFileSystem(SandboxRecord record, string? sessionDir = null)
    {
        _record = record;
        if (!string.IsNullOrWhiteSpace(sessionDir))
        {
            var fs = Path.Combine(sessionDir!, "fs");
            if (Directory.Exists(fs)) _fsRoot = fs;
        }

        if (_fsRoot == null)
        {
            Kind = FileScopeKind.Snapshot;
            if (!File.Exists(record.SnapshotPath))
                throw new FileNotFoundException("快照文件不存在：" + record.SnapshotPath);
        }
        else
        {
            Kind = FileScopeKind.LiveSession;
        }
    }

    public static SandboxFileSystem Open(SandboxRecord record, string? sessionDir = null) => new(record, sessionDir);

    public FileScopeKind Kind { get; }
    public SandboxRecord Record => _record;
    public bool IsLive => Kind == FileScopeKind.LiveSession;
    public bool CanWrite => true;
    public bool IsVirtual => !IsLive;
    public string KindText => IsLive ? "沙盒内（实时会话目录）" : "沙盒快照（.sbx）";
    public string DisplayRoot => IsLive ? _fsRoot! : _record.SnapshotPath;
    public string? HostRoot => IsLive ? _fsRoot : null;

    private string SessionFsRoot => _fsRoot!;

    private ZipArchive Zip
    {
        get
        {
            if (_zip != null) return _zip;
            _zipStream = new FileStream(_record.SnapshotPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            _zip = new ZipArchive(_zipStream, ZipArchiveMode.Read, leaveOpen: true);
            return _zip;
        }
    }

    public void Refresh()
    {
        try { _zip?.Dispose(); } catch { /* ignore */ }
        try { _zipStream?.Dispose(); } catch { /* ignore */ }
        _zip = null;
        _zipStream = null;
    }

    public void Dispose() => Refresh();

    // ---------- 浏览 ----------

    public string Normalize(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "";
        var trimmed = relativePath.Trim().Trim('"');
        if (IsLive && Path.IsPathRooted(trimmed))
        {
            try
            {
                var full = Path.GetFullPath(trimmed);
                if (ScopePath.IsUnder(full, SessionFsRoot))
                {
                    var basePath = Path.GetFullPath(SessionFsRoot).TrimEnd(Path.DirectorySeparatorChar);
                    var relative = full.Length > basePath.Length ? full[(basePath.Length + 1)..] : "";
                    return ScopePath.NormalizeRelative(relative);
                }
            }
            catch (Exception)
            {
                // 不是合法路径，按相对路径处理
            }
        }

        return ScopePath.NormalizeRelative(trimmed);
    }

    public IReadOnlyList<SandboxFileEntry> List(string relativePath)
    {
        var relative = Normalize(relativePath);
        var entries = IsLive ? ListLive(relative) : ListSnapshot(relative);
        return entries
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private List<SandboxFileEntry> ListLive(string relative)
    {
        var result = new List<SandboxFileEntry>();
        var directory = new DirectoryInfo(ToHostPath(relative));
        if (!directory.Exists) return result;

        FileSystemInfo[] infos;
        try { infos = directory.GetFileSystemInfos(); }
        catch (Exception) { return result; }

        foreach (var info in infos)
        {
            try
            {
                var attributes = info.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue; // 不展示 junction，免得绕圈
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                result.Add(new SandboxFileEntry
                {
                    Name = info.Name,
                    RelativePath = ScopePath.Combine(relative, info.Name),
                    IsDirectory = isDirectory,
                    SizeBytes = isDirectory ? 0 : ((FileInfo)info).Length,
                    Modified = ToOffset(info.LastWriteTimeUtc)
                });
            }
            catch (Exception)
            {
                // 单个条目读不到不影响其它条目
            }
        }

        return result;
    }

    private List<SandboxFileEntry> ListSnapshot(string relative)
    {
        var result = new List<SandboxFileEntry>();
        var byName = new Dictionary<string, SandboxFileEntry>(StringComparer.OrdinalIgnoreCase);
        var prefix = SnapshotFile.FsPrefix + (relative.Length == 0 ? "" : relative + "/");

        foreach (var entry in Zip.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = entry.FullName[prefix.Length..];
            if (rest.Length == 0) continue;
            var slash = rest.IndexOf('/');
            var name = slash < 0 ? rest : rest[..slash];
            if (name.Length == 0 || byName.ContainsKey(name)) continue;

            var isDirectory = slash >= 0 || entry.FullName.EndsWith('/');
            byName[name] = new SandboxFileEntry
            {
                Name = name,
                RelativePath = ScopePath.Combine(relative, name),
                IsDirectory = isDirectory,
                SizeBytes = isDirectory ? 0 : entry.Length,
                Modified = new DateTimeOffset(SnapshotFile.GetEntryTimeUtc(entry))
            };
        }

        result.AddRange(byName.Values);
        return result;
    }

    public bool FileExists(string relativePath)
    {
        var relative = Normalize(relativePath);
        if (relative.Length == 0) return false;
        if (IsLive) return File.Exists(ToHostPath(relative));
        return FindEntry(relative) != null;
    }

    public bool DirectoryExists(string relativePath)
    {
        var relative = Normalize(relativePath);
        if (relative.Length == 0) return true;
        if (IsLive) return Directory.Exists(ToHostPath(relative));
        var prefix = SnapshotFile.FsPrefix + relative + "/";
        return Zip.Entries.Any(entry => entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public Stream OpenRead(string relativePath)
    {
        var relative = Normalize(relativePath);
        if (IsLive)
            return new FileStream(ToHostPath(relative), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        var entry = FindEntry(relative) ?? throw new FileNotFoundException("沙盒里没有这个文件：" + relative);
        return entry.Open();
    }

    public string? GetHostPath(string relativePath) =>
        IsLive ? ToHostPath(Normalize(relativePath)) : null;

    private string ToHostPath(string relative) =>
        Path.GetFullPath(Path.Combine(SessionFsRoot, ScopePath.ToHostRelative(relative)));

    private ZipArchiveEntry? FindEntry(string relative)
    {
        var name = SnapshotFile.FsPrefix + ScopePath.NormalizeRelative(relative);
        var entry = Zip.GetEntry(name);
        if (entry != null) return entry;
        return Zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
    }

    private static DateTimeOffset ToOffset(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTimeOffset(utc);
    }

    // ---------- 取出到宿主 ----------

    public string Materialize(string relativePath, string tempRoot, Action<string>? log = null)
    {
        var relative = Normalize(relativePath);
        if (IsLive) return ToHostPath(relative);

        var targetRoot = Path.GetFullPath(tempRoot);
        Directory.CreateDirectory(targetRoot);
        var target = Path.GetFullPath(Path.Combine(targetRoot,
            ScopePath.ToHostRelative(relative.Length == 0 ? "_root" : relative)));
        if (!ScopePath.IsUnder(target, targetRoot))
            throw new IOException("导出路径超出了临时目录：" + target);

        var isDirectory = relative.Length == 0 || DirectoryExists(relative);
        if (isDirectory)
        {
            Directory.CreateDirectory(target);
            foreach (var item in EnumerateFileItems(relative))
            {
                var destination = Path.Combine(target, ScopePath.ToHostRelative(item.RelativeWithin));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                CopyItem(item, destination, overwrite: true, new FileCopyResult(), log);
            }

            return target;
        }

        var entry = FindEntry(relative) ?? throw new FileNotFoundException("沙盒里没有这个文件：" + relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        CopyItem(new FileItem("", ScopePath.Name(relative), entry.Length, null, entry), target, true,
            new FileCopyResult(), log);
        return target;
    }

    public FileCopyResult CopyOut(IEnumerable<string> relativePaths, string targetDirectory, bool overwrite = true,
        Action<string>? log = null)
    {
        var result = new FileCopyResult { TargetPath = targetDirectory };
        Directory.CreateDirectory(targetDirectory);

        foreach (var raw in relativePaths)
        {
            var relative = Normalize(raw);
            if (relative.Length == 0)
            {
                result.Skipped.Add("沙盒根目录（请选择具体文件/文件夹）");
                continue;
            }

            if (!DirectoryExists(relative) && !FileExists(relative))
            {
                result.Skipped.Add(raw + "（不存在）");
                continue;
            }

            if (!DirectoryExists(relative))
            {
                var destination = Path.Combine(targetDirectory, ScopePath.SanitizeName(ScopePath.Name(relative)));
                CopyPath(relative, destination, overwrite, result, log);
                continue;
            }

            var baseTarget = Path.Combine(targetDirectory, ScopePath.SanitizeName(ScopePath.Name(relative)));
            foreach (var item in EnumerateFileItems(relative))
            {
                var destination = Path.Combine(baseTarget, ScopePath.ToHostRelative(item.RelativeWithin));
                if (!overwrite && File.Exists(destination))
                {
                    result.Skipped.Add(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                CopyItem(item, destination, true, result, log);
            }
        }

        return result;
    }

    private void CopyPath(string relative, string destination, bool overwrite, FileCopyResult result,
        Action<string>? log)
    {
        if (!overwrite && File.Exists(destination))
        {
            result.Skipped.Add(destination);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (IsLive)
        {
            using var source = new FileStream(ToHostPath(relative), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
            result.Bytes += target.Length;
        }
        else
        {
            var entry = FindEntry(relative) ?? throw new FileNotFoundException("沙盒里没有这个文件：" + relative);
            using (var source = entry.Open())
            using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                source.CopyTo(target);
            result.Bytes += entry.Length;
            try { File.SetLastWriteTimeUtc(destination, SnapshotFile.GetEntryTimeUtc(entry)); } catch { /* ignore */ }
            FileAttributeTools.ClearReadOnly(destination);
        }

        result.FileCount++;
        result.Paths.Add(destination);
        if (result.FileCount <= 30) log?.Invoke($"  导出 {relative} → {destination}");
    }

    private void CopyItem(FileItem item, string destination, bool overwrite, FileCopyResult result,
        Action<string>? log)
    {
        if (!overwrite && File.Exists(destination))
        {
            result.Skipped.Add(destination);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var source = item.HostPath != null
            ? new FileStream(item.HostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            : item.Entry!.Open();
        using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
        result.Bytes += target.Length;
        result.FileCount++;
        result.Paths.Add(destination);
        FileAttributeTools.ClearReadOnly(destination);
        if (result.FileCount <= 30) log?.Invoke($"  导出 {item.Name} → {destination}");
    }

    // ---------- 从宿主放回沙盒 ----------

    public FileCopyResult CopyIn(string hostSourcePath, string targetRelativeDirectory, bool overwrite = true,
        Action<string>? log = null)
    {
        var source = Path.GetFullPath(hostSourcePath.Trim().Trim('"'));
        if (!File.Exists(source) && !Directory.Exists(source))
            throw new FileNotFoundException("要导入的文件/目录不存在：" + source);

        var targetRelative = Normalize(targetRelativeDirectory);
        return IsLive
            ? CopyInLive(source, targetRelative, overwrite, log)
            : CopyInSnapshot(source, targetRelative, overwrite, log);
    }

    private FileCopyResult CopyInLive(string source, string targetRelative, bool overwrite, Action<string>? log)
    {
        var result = new FileCopyResult { TargetPath = ToHostPath(targetRelative) };
        var baseDirectory = ToHostPath(targetRelative);
        Directory.CreateDirectory(baseDirectory);

        if (File.Exists(source))
        {
            var destination = Path.Combine(baseDirectory, ScopePath.SanitizeName(Path.GetFileName(source)));
            if (!overwrite && File.Exists(destination))
            {
                result.Skipped.Add(destination);
                return result;
            }

            File.Copy(source, destination, true);
            result.FileCount = 1;
            result.Bytes = new FileInfo(destination).Length;
            result.Paths.Add(destination);
            log?.Invoke($"  导入 {source} → {destination}");
            return result;
        }

        var rootName = ScopePath.SanitizeName(new DirectoryInfo(source).Name);
        var root = Path.Combine(baseDirectory, rootName);
        foreach (var file in FsWalker.EnumerateFiles(source))
        {
            var destination = Path.Combine(root, ScopePath.ToHostRelative(file.RelativePath));
            if (!overwrite && File.Exists(destination))
            {
                result.Skipped.Add(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file.HostPath, destination, true);
            result.FileCount++;
            result.Bytes += file.SizeBytes;
            result.Paths.Add(destination);
            if (result.FileCount <= 30) log?.Invoke($"  导入 {file.RelativePath} → {destination}");
        }

        return result;
    }

    private FileCopyResult CopyInSnapshot(string source, string targetRelative, bool overwrite,
        Action<string>? log)
    {
        var result = new FileCopyResult { TargetPath = targetRelative };
        var items = new List<SnapshotFile.SnapshotFileItem>();
        var prefix = SnapshotFile.FsPrefix + (targetRelative.Length == 0 ? "" : targetRelative + "/");

        if (File.Exists(source))
        {
            var entryName = prefix + ScopePath.SanitizeName(Path.GetFileName(source));
            if (!overwrite && FindEntry(entryName) != null)
            {
                result.Skipped.Add(entryName);
                return result;
            }

            items.Add(new SnapshotFile.SnapshotFileItem(entryName, source));
            result.FileCount = 1;
            result.Bytes = new FileInfo(source).Length;
            result.Paths.Add(entryName);
        }
        else
        {
            var rootName = ScopePath.SanitizeName(new DirectoryInfo(source).Name);
            foreach (var file in FsWalker.EnumerateFiles(source))
            {
                var entryName = prefix + rootName + "/" + file.RelativePath;
                if (!overwrite && FindEntry(entryName) != null)
                {
                    result.Skipped.Add(entryName);
                    continue;
                }

                items.Add(new SnapshotFile.SnapshotFileItem(entryName, file.HostPath));
                result.FileCount++;
                result.Bytes += file.SizeBytes;
                result.Paths.Add(entryName);
            }
        }

        if (items.Count == 0) return result;

        Refresh(); // 先关掉读句柄，才能就地改写快照
        SnapshotFile.MutateFiles(_record.SnapshotPath, items,
            manifest =>
            {
                manifest.SandboxId = _record.Id;
                manifest.Name = _record.Name;
            },
            log);
        Refresh();

        log?.Invoke($"已导入沙盒「{_record.Name}」：{result.Summary} → {targetRelative switch { "" => "fs 根目录", _ => targetRelative }}");
        return result;
    }

    // ---------- 统计 ----------

    public (int Count, long Bytes) Measure(string relativePath)
    {
        var relative = Normalize(relativePath);
        if (IsLive)
        {
            var path = ToHostPath(relative);
            if (File.Exists(path)) return (1, new FileInfo(path).Length);
            return Directory.Exists(path) ? FsWalker.Measure(path) : (0, 0);
        }

        var count = 0;
        long bytes = 0;
        foreach (var item in EnumerateFileItems(relative))
        {
            count++;
            bytes += item.Size;
        }

        return (count, bytes);
    }

    // ---------- 删除 ----------
    public int Delete(IEnumerable<string> relativePaths)
    {
        var targets = relativePaths
            .Select(Normalize)
            .Where(relative => relative.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0) return 0;

        if (IsLive)
        {
            var deleted = 0;
            foreach (var relative in targets)
            {
                var path = ToHostPath(relative);
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        deleted++;
                    }
                    else if (Directory.Exists(path))
                    {
                        deleted += FsWalker.Measure(path).Count;
                        Directory.Delete(path, true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 被客户端占用的文件留着，下次再删
                }
            }

            return deleted;
        }

        var items = new List<SnapshotFile.SnapshotFileItem>();
        foreach (var relative in targets)
        {
            var exact = FindEntry(relative);
            if (exact != null) items.Add(new SnapshotFile.SnapshotFileItem(exact.FullName, null, Delete: true));

            var prefix = SnapshotFile.FsPrefix + relative + "/";
            foreach (var entry in Zip.Entries
                         .Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .ToList())
                items.Add(new SnapshotFile.SnapshotFileItem(entry.FullName, null, Delete: true));
        }

        if (items.Count == 0) return 0;
        Refresh();
        SnapshotFile.MutateFiles(_record.SnapshotPath, items.DistinctBy(i => i.EntryName).ToList(),
            manifest =>
            {
                manifest.SandboxId = _record.Id;
                manifest.Name = _record.Name;
            });
        Refresh();
        return items.Count;
    }

    // ---------- 内部 ----------

    private readonly record struct FileItem(string RelativeWithin, string Name, long Size, string? HostPath,
        ZipArchiveEntry? Entry);

    private IEnumerable<FileItem> EnumerateFileItems(string relative)
    {
        if (IsLive)
        {
            var path = ToHostPath(relative);
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                yield return new FileItem("", info.Name, info.Length, info.FullName, null);
                yield break;
            }

            if (!Directory.Exists(path)) yield break;
            foreach (var file in FsWalker.EnumerateFiles(path))
                yield return new FileItem(file.RelativePath, ScopePath.Name(file.RelativePath), file.SizeBytes,
                    file.HostPath, null);
            yield break;
        }

        var exact = relative.Length == 0 ? null : FindEntry(relative);
        if (exact != null && !exact.FullName.EndsWith('/'))
        {
            yield return new FileItem("", ScopePath.Name(relative), exact.Length, null, exact);
            yield break;
        }

        var entryPrefix = SnapshotFile.FsPrefix + (relative.Length == 0 ? "" : relative + "/");
        foreach (var entry in Zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            if (!entry.FullName.StartsWith(entryPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var within = entry.FullName[entryPrefix.Length..];
            if (within.Length == 0) continue;
            yield return new FileItem(within, ScopePath.Name(within), entry.Length, null, entry);
        }
    }
}

/// <summary>宿主上的一个目录（沙盒外的真实文件夹），用来浏览/拷贝程序实际写在外面的数据。</summary>
public sealed class HostFolderScope : IFileScope
{
    private readonly string _root;

    public HostFolderScope(string root)
    {
        _root = Path.GetFullPath(root.Trim().Trim('"'));
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("宿主目录不存在：" + _root);
    }

    public FileScopeKind Kind => FileScopeKind.HostFolder;
    public string KindText => "宿主目录（沙盒外）";
    public string DisplayRoot => _root;
    public string? HostRoot => _root;
    public bool CanWrite => true;
    public bool IsVirtual => false;

    public string Normalize(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "";
        var trimmed = relativePath.Trim().Trim('"');
        if (Path.IsPathRooted(trimmed))
        {
            var full = Path.GetFullPath(trimmed);
            if (ScopePath.IsUnder(full, _root))
            {
                var basePath = _root.TrimEnd(Path.DirectorySeparatorChar);
                return full.Length > basePath.Length ? ScopePath.NormalizeRelative(full[(basePath.Length + 1)..]) : "";
            }

            throw new ArgumentException("路径不在这个宿主目录里：" + trimmed);
        }

        return ScopePath.NormalizeRelative(trimmed);
    }

    public IReadOnlyList<SandboxFileEntry> List(string relativePath)
    {
        var relative = Normalize(relativePath);
        var result = new List<SandboxFileEntry>();
        var directory = new DirectoryInfo(ToHostPath(relative));
        if (!directory.Exists) return result;

        FileSystemInfo[] infos;
        try { infos = directory.GetFileSystemInfos(); }
        catch (Exception) { return result; }

        foreach (var info in infos)
        {
            try
            {
                var attributes = info.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                result.Add(new SandboxFileEntry
                {
                    Name = info.Name,
                    RelativePath = ScopePath.Combine(relative, info.Name),
                    IsDirectory = isDirectory,
                    SizeBytes = isDirectory ? 0 : ((FileInfo)info).Length,
                    Modified = new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc))
                });
            }
            catch (Exception)
            {
                // ignore
            }
        }

        return result
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public bool DirectoryExists(string relativePath) => Directory.Exists(ToHostPath(Normalize(relativePath)));
    public bool FileExists(string relativePath) => File.Exists(ToHostPath(Normalize(relativePath)));

    public Stream OpenRead(string relativePath) =>
        new FileStream(ToHostPath(Normalize(relativePath)), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

    public string? GetHostPath(string relativePath) => ToHostPath(Normalize(relativePath));

    public string Materialize(string relativePath, string tempRoot, Action<string>? log = null) =>
        ToHostPath(Normalize(relativePath));

    public FileCopyResult CopyOut(IEnumerable<string> relativePaths, string targetDirectory, bool overwrite = true,
        Action<string>? log = null)
    {
        var result = new FileCopyResult { TargetPath = targetDirectory };
        Directory.CreateDirectory(targetDirectory);

        foreach (var raw in relativePaths)
        {
            var relative = Normalize(raw);
            if (relative.Length == 0) continue;
            var sourcePath = ToHostPath(relative);
            if (File.Exists(sourcePath))
            {
                var destination = Path.Combine(targetDirectory, ScopePath.SanitizeName(Path.GetFileName(sourcePath)));
                CopyHostFile(sourcePath, destination, overwrite, result, log);
                continue;
            }

            if (!Directory.Exists(sourcePath)) continue;
            var baseTarget = Path.Combine(targetDirectory, ScopePath.SanitizeName(Path.GetFileName(sourcePath)));
            foreach (var file in FsWalker.EnumerateFiles(sourcePath))
            {
                var destination = Path.Combine(baseTarget, ScopePath.ToHostRelative(file.RelativePath));
                CopyHostFile(file.HostPath, destination, overwrite, result, log);
            }
        }

        return result;
    }

    private static void CopyHostFile(string source, string destination, bool overwrite, FileCopyResult result,
        Action<string>? log)
    {
        if (!overwrite && File.Exists(destination))
        {
            result.Skipped.Add(destination);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, true);
        result.FileCount++;
        result.Bytes += new FileInfo(destination).Length;
        result.Paths.Add(destination);
        FileAttributeTools.ClearReadOnly(destination);
        if (result.FileCount <= 30) log?.Invoke($"  复制 {source} → {destination}");
    }

    public FileCopyResult CopyIn(string hostSourcePath, string targetRelativeDirectory, bool overwrite = true,
        Action<string>? log = null)
    {
        var source = Path.GetFullPath(hostSourcePath.Trim().Trim('"'));
        if (!File.Exists(source) && !Directory.Exists(source))
            throw new FileNotFoundException("要导入的文件/目录不存在：" + source);

        var baseDirectory = ToHostPath(Normalize(targetRelativeDirectory));
        Directory.CreateDirectory(baseDirectory);
        var result = new FileCopyResult { TargetPath = baseDirectory };

        if (File.Exists(source))
        {
            CopyHostFile(source, Path.Combine(baseDirectory, ScopePath.SanitizeName(Path.GetFileName(source))),
                overwrite, result, log);
            return result;
        }

        var root = Path.Combine(baseDirectory, ScopePath.SanitizeName(new DirectoryInfo(source).Name));
        foreach (var file in FsWalker.EnumerateFiles(source))
        {
            CopyHostFile(file.HostPath, Path.Combine(root, ScopePath.ToHostRelative(file.RelativePath)), overwrite,
                result, log);
        }

        return result;
    }

    public int Delete(IEnumerable<string> relativePaths)
    {
        var deleted = 0;
        foreach (var relativePath in relativePaths)
        {
            var path = ToHostPath(Normalize(relativePath));
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
                else if (Directory.Exists(path))
                {
                    deleted += FsWalker.Measure(path).Count;
                    Directory.Delete(path, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 保留被占用的文件
            }
        }

        return deleted;
    }

    public (int Count, long Bytes) Measure(string relativePath)
    {
        var path = ToHostPath(Normalize(relativePath));
        if (File.Exists(path)) return (1, new FileInfo(path).Length);
        return Directory.Exists(path) ? FsWalker.Measure(path) : (0, 0);
    }

    public void Refresh()
    {
    }

    public void Dispose()
    {
    }

    private string ToHostPath(string relative) =>
        Path.GetFullPath(Path.Combine(_root, ScopePath.ToHostRelative(relative)));
}
