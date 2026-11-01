using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SandboxLauncher.Core.Model;

namespace SandboxLauncher.Core.Snapshot;

/// <summary>
/// 沙盒快照文件（.sbx）：本质是一个 ZIP 容器，里面装：
///   manifest.json   —— 沙盒元数据（名称、客户端列表、隔离设置）
///   index.json      —— 文件索引（大小/时间/散列），用于增量回写
///   fs/…            —— 沙盒的完整虚拟文件系统（用户目录、应用目录）
///   registry/…      —— 被隔离的注册表子树快照
/// 整个沙盒 = 一个文件，可随意移动/复制/导入。
/// </summary>
public static class SnapshotFile
{
    public const string Kind = "sandbox-launcher-snapshot";
    public const int FormatVersion = 1;
    public const string Extension = ".sbx";
    public const string FsPrefix = "fs/";
    public const string RegPrefix = "registry/";

    private const string ManifestEntry = "manifest.json";
    private const string IndexEntry = "index.json";

    // ---------- 创建 / 读取 ----------

    public static void Create(string path, SnapshotManifest manifest)
    {
        path = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(path)) throw new IOException($"快照文件已存在：{path}");

        manifest.FormatVersion = FormatVersion;
        manifest.Kind = Kind;
        if (manifest.CreatedAt == default) manifest.CreatedAt = DateTimeOffset.Now;
        manifest.UpdatedAt = DateTimeOffset.Now;

        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        WriteText(zip, ManifestEntry, JsonSerializer.Serialize(manifest, JsonStore.Options));
        WriteText(zip, IndexEntry, JsonSerializer.Serialize(new SnapshotIndex(), JsonStore.Options));
    }

    public static SnapshotManifest ReadManifest(string path)
    {
        using var fs = File.OpenRead(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("不是有效的沙盒快照：缺少 manifest.json");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(stream, JsonStore.Options)
                       ?? throw new InvalidDataException("不是有效的沙盒快照：manifest.json 解析失败");
        if (!string.Equals(manifest.Kind, Kind, StringComparison.Ordinal))
            throw new InvalidDataException($"不是沙盒快照文件（kind={manifest.Kind}）");
        return manifest;
    }

    public static SnapshotIndex ReadIndex(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = zip.GetEntry(IndexEntry);
            if (entry == null) return new SnapshotIndex();
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<SnapshotIndex>(stream, JsonStore.Options) ?? new SnapshotIndex();
        }
        catch (InvalidDataException)
        {
            return new SnapshotIndex();
        }
    }

    public static SnapshotInfo Inspect(string path)
    {
        var info = new SnapshotInfo { Path = Path.GetFullPath(path) };
        var fi = new FileInfo(info.Path);
        if (!fi.Exists) throw new FileNotFoundException("快照文件不存在", info.Path);
        info.FileSizeBytes = fi.Length;
        info.Manifest = ReadManifest(info.Path);
        using var fs = File.OpenRead(info.Path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        foreach (var e in zip.Entries)
        {
            info.EntryCount++;
            info.UncompressedBytes += e.Length;
        }
        return info;
    }

    public static bool LooksLikeSnapshot(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = File.OpenRead(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            return zip.GetEntry(ManifestEntry) != null;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 挂载 / 回写 ----------

    /// <summary>把快照解包到会话目录：fs/ → {session}/fs，registry/ → {session}/reg。</summary>
    public static SnapshotManifest Extract(string path, string sessionDir, Action<string>? log = null)
    {
        path = Path.GetFullPath(path);
        sessionDir = Path.GetFullPath(sessionDir);
        Directory.CreateDirectory(sessionDir);

        var manifest = ReadManifest(path);
        var sessionRoot = sessionDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using (var fs = File.OpenRead(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                if (!entry.FullName.StartsWith(FsPrefix, StringComparison.OrdinalIgnoreCase) &&
                    !entry.FullName.StartsWith(RegPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                // fs/… → {session}/fs/…    registry/… → {session}/reg/…
                string rel;
                if (entry.FullName.StartsWith(RegPrefix, StringComparison.OrdinalIgnoreCase))
                    rel = Path.Combine("reg", entry.FullName[RegPrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
                else
                    rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);

                var full = Path.GetFullPath(Path.Combine(sessionDir, rel));
                if (!full.StartsWith(sessionRoot, StringComparison.OrdinalIgnoreCase))
                    continue; // 防 zip-slip

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                entry.ExtractToFile(full, overwrite: true);
                try { File.SetLastWriteTimeUtc(full, GetEntryTimeUtc(entry)); } catch { /* 只影响增量判断 */ }
            }
        }

        log?.Invoke($"快照已挂载：{path} → {sessionDir}（{manifest.Apps.Count} 个客户端）");

        foreach (var sub in new[]
                 {
                     Path.Combine(sessionDir, "fs", manifest.Settings.ProfileFolderName),
                     Path.Combine(sessionDir, "fs", manifest.Settings.ProfileFolderName, "AppData", "Roaming"),
                     Path.Combine(sessionDir, "fs", manifest.Settings.ProfileFolderName, "AppData", "Local"),
                     Path.Combine(sessionDir, "fs", manifest.Settings.ProfileFolderName, "Temp"),
                     Path.Combine(sessionDir, "fs", "apps"),
                     Path.Combine(sessionDir, "reg")
                 })
        {
            Directory.CreateDirectory(sub);
        }

        return manifest;
    }

    /// <summary>把会话目录的改动写回快照（增量：没变的文件不重新压缩）。</summary>
    public static SnapshotManifest SyncFrom(string snapshotPath, string sessionDir, SnapshotManifest manifest, Action<string>? log = null)
    {
        snapshotPath = Path.GetFullPath(snapshotPath);
        sessionDir = Path.GetFullPath(sessionDir);
        var dir = Path.GetDirectoryName(snapshotPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(snapshotPath)) Create(snapshotPath, manifest);

        var baseline = ReadIndex(snapshotPath);
        var tmp = snapshotPath + ".tmp";
        File.Copy(snapshotPath, tmp, true);

        int written = 0, kept = 0, deleted = 0, skipped = 0;

        try
        {
            using (var stream = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Update))
            {
                // 用 FsWalker 而不是 Directory.EnumerateFiles：一个锁住的目录
                // （典型是 INetCache\Content.IE5）不能让整次回写失败。
                var filter = PathFilter.Create(manifest.Settings.ExcludeFromSnapshot, manifest.Settings.SkipSystemCaches);
                var items = FsWalker.EnumerateFiles(Path.Combine(sessionDir, "fs"), filter)
                    .Select(f => new ScanItem(FsPrefix + f.RelativePath, f.HostPath, f.SizeBytes, f.ModifiedUtc))
                    .Concat(ScanTree(Path.Combine(sessionDir, "reg"), RegPrefix, "*.json", recursive: false))
                    .ToList();

                var existing = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.StartsWith(FsPrefix, StringComparison.OrdinalIgnoreCase) ||
                        e.FullName.StartsWith(RegPrefix, StringComparison.OrdinalIgnoreCase))
                        existing[e.FullName] = e;
                }

                var newIndex = new SnapshotIndex();
                var onDisk = new HashSet<string>(items.Select(i => i.Entry), StringComparer.OrdinalIgnoreCase);

                foreach (var item in items)
                {
                    baseline.Files.TryGetValue(item.Entry, out var old);
                    var sha = old?.Sha256 ?? "";

                    // 精确比较（大小 + 修改时间）：时间相等就当没变，省掉一次全量哈希；
                    // 只要大小或时间有任何差异就重新哈希，避免"刚写完 2 秒内"的改动被漏掉。
                    var unchanged = old != null && old.Size == item.Size && old.LastWriteUtc == item.MtimeUtc;

                    if (!unchanged)
                    {
                        try
                        {
                            sha = HashFile(item.DiskPath);
                        }
                        catch (Exception ex)
                        {
                            // 文件正被客户端独占（日志、数据库等），这一轮先跳过，下次退出再回写。
                            skipped++;
                            if (skipped <= 20) log?.Invoke($"  跳过暂时读不到的文件：{item.Entry}（{ex.GetType().Name}）");
                            continue;
                        }

                        unchanged = old != null && old.Size == item.Size &&
                                    string.Equals(old.Sha256, sha, StringComparison.OrdinalIgnoreCase);
                    }

                    if (unchanged && existing.ContainsKey(item.Entry))
                    {
                        kept++;
                        newIndex.Files[item.Entry] = new SnapshotIndexEntry { Size = item.Size, LastWriteUtc = item.MtimeUtc, Sha256 = sha };
                        continue;
                    }

                    if (existing.TryGetValue(item.Entry, out var oldEntry))
                    {
                        oldEntry.Delete();
                        existing.Remove(item.Entry);
                    }

                    try
                    {
                        var entry = zip.CreateEntry(item.Entry, CompressionLevel.Optimal);
                        entry.LastWriteTime = ClampZipTime(item.MtimeUtc);
                        using (var es = entry.Open())
                        using (var src = File.Open(item.DiskPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            src.CopyTo(es);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped++;
                        if (skipped <= 20) log?.Invoke($"  跳过打包失败的文件：{item.Entry}（{ex.Message}）");
                        continue;
                    }

                    // ZIP 的时间戳只有 2 秒精度，这里按解包后能读回的精度入索引，
                    // 保证下次挂载后「磁盘时间 == 索引时间」，增量判断不会误判。
                    newIndex.Files[item.Entry] = new SnapshotIndexEntry
                    {
                        Size = item.Size,
                        LastWriteUtc = ToZipRoundTripUtc(item.MtimeUtc),
                        Sha256 = sha
                    };
                    written++;
                }

                foreach (var kv in existing)
                {
                    if (!onDisk.Contains(kv.Key))
                    {
                        kv.Value.Delete();
                        deleted++;
                    }
                }

                manifest.UpdatedAt = DateTimeOffset.Now;
                manifest.Stats = new SnapshotStats
                {
                    FileCount = newIndex.Files.Count,
                    TotalBytes = newIndex.Files.Values.Sum(v => v.Size),
                    LastSyncAt = DateTimeOffset.Now
                };

                WriteText(zip, ManifestEntry, JsonSerializer.Serialize(manifest, JsonStore.Options));
                WriteText(zip, IndexEntry, JsonSerializer.Serialize(newIndex, JsonStore.Options));
            }

            ReplaceFile(tmp, snapshotPath);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        log?.Invoke($"快照回写完成：新增/更新 {written}，未变 {kept}，删除 {deleted}" +
                    (skipped > 0 ? $"，跳过 {skipped}（被占用或读不到）" : "") + $" → {snapshotPath}");
        return manifest;
    }

    // ---------- 就地修改（导入客户端用，不需要解包） ----------

    public static SnapshotManifest Mutate(string snapshotPath, Action<ZipArchive>? content,
        Action<SnapshotManifest>? manifestChange = null, Action<string>? log = null)
    {
        snapshotPath = Path.GetFullPath(snapshotPath);
        var dir = Path.GetDirectoryName(snapshotPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        SnapshotManifest manifest;
        if (!File.Exists(snapshotPath))
        {
            manifest = new SnapshotManifest();
            manifestChange?.Invoke(manifest);
            Create(snapshotPath, manifest);
        }
        else
        {
            manifest = ReadManifest(snapshotPath);
            manifestChange?.Invoke(manifest);
        }

        manifest.UpdatedAt = DateTimeOffset.Now;

        var tmp = snapshotPath + ".tmp";
        File.Copy(snapshotPath, tmp, true);
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Update))
            {
                content?.Invoke(zip);
                WriteText(zip, ManifestEntry, JsonSerializer.Serialize(manifest, JsonStore.Options));
            }

            ReplaceFile(tmp, snapshotPath);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        log?.Invoke($"快照已更新：{snapshotPath}");
        return manifest;
    }

    /// <summary>往快照里增删单个文件（导入文件、清理文件用），同时维护索引，不需要解包。</summary>
    public sealed record SnapshotFileItem(string EntryName, string? HostPath, bool Delete = false);

    /// <summary>
    /// 就地增删快照里的文件并同步 index.json：
    ///   • <see cref="SnapshotFileItem.HostPath"/> 非空 → 写入/覆盖这个文件
    ///   • <see cref="SnapshotFileItem.HostPath"/> 为空或 <see cref="SnapshotFileItem.Delete"/> → 删除
    /// 索引里按「解包后能读回的时间精度」记录，避免下次回写把新文件当成旧文件。
    /// </summary>
    public static SnapshotManifest MutateFiles(string snapshotPath, IEnumerable<SnapshotFileItem> items,
        Action<SnapshotManifest>? manifestChange = null, Action<string>? log = null)
    {
        snapshotPath = Path.GetFullPath(snapshotPath);
        var dir = Path.GetDirectoryName(snapshotPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (!File.Exists(snapshotPath)) Create(snapshotPath, new SnapshotManifest());

        var manifest = ReadManifest(snapshotPath);
        manifestChange?.Invoke(manifest);
        manifest.UpdatedAt = DateTimeOffset.Now;

        var list = items.ToList();
        var index = ReadIndex(snapshotPath);
        var tmp = snapshotPath + ".tmp";
        File.Copy(snapshotPath, tmp, true);

        int written = 0, removed = 0;
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Update))
            {
                foreach (var item in list)
                {
                    var entryName = item.EntryName.Replace('\\', '/').TrimStart('/');
                    if (entryName.Length == 0) continue;

                    var old = zip.GetEntry(entryName);
                    if (old != null)
                    {
                        old.Delete();
                        index.Files.Remove(entryName);
                    }

                    if (item.Delete || string.IsNullOrWhiteSpace(item.HostPath) || !File.Exists(item.HostPath))
                    {
                        removed++;
                        continue;
                    }

                    var info = new FileInfo(item.HostPath);
                    var mtimeUtc = info.LastWriteTimeUtc;
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    entry.LastWriteTime = ClampZipTime(mtimeUtc);

                    string sha;
                    using (var es = entry.Open())
                    using (var src = File.Open(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sha256 = SHA256.Create())
                    using (var cs = new CryptoStream(es, sha256, CryptoStreamMode.Write, leaveOpen: true))
                    {
                        src.CopyTo(cs);
                        cs.FlushFinalBlock();
                        sha = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
                    }

                    index.Files[entryName] = new SnapshotIndexEntry
                    {
                        Size = info.Length,
                        LastWriteUtc = ToZipRoundTripUtc(mtimeUtc),
                        Sha256 = sha
                    };
                    written++;
                }

                manifest.Stats = new SnapshotStats
                {
                    FileCount = index.Files.Count,
                    TotalBytes = index.Files.Values.Sum(v => v.Size),
                    LastSyncAt = DateTimeOffset.Now
                };

                WriteText(zip, ManifestEntry, JsonSerializer.Serialize(manifest, JsonStore.Options));
                WriteText(zip, IndexEntry, JsonSerializer.Serialize(index, JsonStore.Options));
            }

            ReplaceFile(tmp, snapshotPath);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        log?.Invoke($"快照文件已更新：写入 {written}，删除 {removed} → {snapshotPath}");
        return manifest;
    }

    /// <summary>把一个目录整棵塞进快照（返回写入的文件数/字节数）。</summary>
    public static (int Count, long Bytes) AddDirectory(ZipArchive zip, string entryPrefix, string sourceDir,
        Action<string>? log = null, int maxFiles = int.MaxValue)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        if (!entryPrefix.EndsWith('/')) entryPrefix += "/";
        var count = 0;
        long bytes = 0;

        foreach (var file in FsWalker.EnumerateFiles(sourceDir))
        {
            if (count >= maxFiles) break;
            var rel = file.RelativePath;
            var entryName = entryPrefix + rel;

            try
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                entry.LastWriteTime = ClampZipTime(file.ModifiedUtc);
                using var es = entry.Open();
                using var src = File.Open(file.HostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                src.CopyTo(es);
            }
            catch (IOException ex)
            {
                log?.Invoke($"  跳过无法读取的文件：{rel}（{ex.Message}）");
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                log?.Invoke($"  跳过没有权限的文件：{rel}");
                continue;
            }

            count++;
            bytes += file.SizeBytes;
            if (count % 500 == 0) log?.Invoke($"  已打包 {count} 个文件（{bytes / 1024.0 / 1024.0:F1} MB）…");
        }

        return (count, bytes);
    }

    /// <summary>把单个文件塞进快照。</summary>
    public static void AddFile(ZipArchive zip, string entryName, string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = ClampZipTime(info.LastWriteTimeUtc);
        using var es = entry.Open();
        using var src = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        src.CopyTo(es);
    }

    public static void CopyFile(string sourcePath, string targetPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(sourcePath, targetPath, overwrite: true);
    }

    // ---------- 内部工具 ----------

    private readonly record struct ScanItem(string Entry, string DiskPath, long Size, DateTime MtimeUtc);

    private static IEnumerable<ScanItem> ScanTree(string root, string prefix, string pattern = "*", bool recursive = true)
    {
        if (!Directory.Exists(root)) yield break;

        if (recursive)
        {
            foreach (var file in FsWalker.EnumerateFiles(root))
                yield return new ScanItem(prefix + file.RelativePath, file.HostPath, file.SizeBytes, file.ModifiedUtc);
            yield break;
        }

        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(root).GetFiles(pattern, new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            });
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var info in files)
        {
            long size;
            DateTime mtime;
            try
            {
                size = info.Length;
                mtime = info.LastWriteTimeUtc;
            }
            catch (Exception)
            {
                continue;
            }

            yield return new ScanItem(prefix + info.Name, info.FullName, size, mtime);
        }
    }

    /// <summary>
    /// 读 ZIP 条目的修改时间（UTC）。
    /// ZIP（DOS 时间戳）里存的是「UTC 墙钟时间」——写快照时就是按 UTC 写的，
    /// 所以读回来时忽略本地时区偏移，直接把墙钟时间当 UTC 用，否则会差一个时区。
    /// </summary>
    public static DateTime GetEntryTimeUtc(ZipArchiveEntry entry) =>
        DateTime.SpecifyKind(entry.LastWriteTime.DateTime, DateTimeKind.Utc);

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        if (zip.Mode != ZipArchiveMode.Create)
        {
            var old = zip.GetEntry(name);
            old?.Delete();
        }

        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string HashFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>把时间舍入成 ZIP(DOS) 时间戳能表示的值，用于「写进去能原样读回来」的比对。</summary>
    private static DateTime ToZipRoundTripUtc(DateTime utc)
    {
        try
        {
            var local = utc.Kind == DateTimeKind.Utc ? utc.ToLocalTime() : utc;
            var truncated = new DateTime(local.Ticks - local.Ticks % (2 * TimeSpan.TicksPerSecond), DateTimeKind.Unspecified);
            var roundTrip = DateTime.SpecifyKind(truncated, DateTimeKind.Local).ToUniversalTime();
            return DateTime.SpecifyKind(roundTrip, DateTimeKind.Utc);
        }
        catch
        {
            return utc;
        }
    }

    private static DateTimeOffset ClampZipTime(DateTime utc)
    {
        var min = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Utc);
        if (utc < min) utc = min;
        if (utc > max) utc = max;
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static void ReplaceFile(string tmp, string target)
    {
        if (File.Exists(target)) File.Replace(tmp, target, null, ignoreMetadataErrors: true);
        else File.Move(tmp, target);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}

public sealed class SnapshotInfo
{
    public string Path { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public int EntryCount { get; set; }
    public long UncompressedBytes { get; set; }
    public SnapshotManifest Manifest { get; set; } = new();
}
