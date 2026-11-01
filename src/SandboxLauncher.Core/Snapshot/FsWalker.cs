namespace SandboxLauncher.Core.Snapshot;

/// <summary>遍历目录时读到的一个文件（路径相对遍历根，/ 分隔）。</summary>
public readonly record struct WalkedFile(string RelativePath, string HostPath, long SizeBytes, DateTime ModifiedUtc);

/// <summary>
/// 健壮的目录遍历：永不抛出。跳过
///   • 无权访问 / 已经被删掉 / 被系统锁住的目录（例如 INetCache\Content.IE5）
///   • 重解析点（junction / 符号链接，可能循环或指到沙盒外面）
///   • PathFilter 里配置的缓存目录
/// 快照回写、导入客户端、统计体积都走这里，避免一个坏目录毁掉整次回写。
/// </summary>
public static class FsWalker
{
    public static IEnumerable<WalkedFile> EnumerateFiles(string root, PathFilter? filter = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;

        var stack = new Stack<(string Dir, string Rel)>();
        stack.Push((root, ""));

        while (stack.Count > 0)
        {
            var (dir, rel) = stack.Pop();

            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(dir).GetFileSystemInfos();
            }
            catch (Exception)
            {
                // UnauthorizedAccessException / IOException / SecurityException / DirectoryNotFound：
                // 一个目录读不了就跳过它，继续遍历兄弟目录。
                continue;
            }

            foreach (var info in entries)
            {
                var childRel = rel.Length == 0 ? info.Name : rel + "/" + info.Name;

                FileAttributes attributes;
                try
                {
                    attributes = info.Attributes;
                }
                catch (Exception)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (filter != null && filter.IsExcluded(childRel)) continue;

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    stack.Push((info.FullName, childRel));
                    continue;
                }

                long size;
                DateTime modifiedUtc;
                try
                {
                    size = ((FileInfo)info).Length;
                    modifiedUtc = info.LastWriteTimeUtc;
                }
                catch (Exception)
                {
                    continue;
                }

                yield return new WalkedFile(childRel, info.FullName, size, modifiedUtc);
            }
        }
    }

    /// <summary>统计目录里的文件数和总字节数（同样跳过读不到的目录）。</summary>
    public static (int Count, long Bytes) Measure(string root, PathFilter? filter = null)
    {
        var count = 0;
        long bytes = 0;
        foreach (var file in EnumerateFiles(root, filter))
        {
            count++;
            bytes += file.SizeBytes;
        }

        return (count, bytes);
    }

    public static int Count(string root, PathFilter? filter = null) => Measure(root, filter).Count;
}
