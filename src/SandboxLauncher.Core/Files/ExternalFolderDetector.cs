using System.Text;

namespace SandboxLauncher.Core.Files;

/// <summary>检测到的一个「沙盒外」目录：程序把自己的数据写到了宿主真实路径上。</summary>
public sealed record ExternalFolder(string Path, string Reason);

/// <summary>
/// 找出程序实际写到沙盒外面的数据目录。
///
/// 背景：这个启动器靠环境变量重定向用户目录，不是驱动级沙盒。像微信 4.x 这种
/// 把「文件保存位置」记在自己配置里（绝对路径）的程序，收到的文件其实落在宿主
/// 真实目录（例如 D:\WeChatFiles\xwechat_files），不在 .sbx 快照里。这里从沙盒内的
/// 程序配置里把这个路径读出来，让界面能直接打开/拷贝，而不是让用户找不着。
/// </summary>
public static class ExternalFolderDetector
{
    /// <summary>微信 4.x / 3.x 放「文件保存位置」的配置目录（相对沙盒 fs 根）。</summary>
    private static readonly string[] WeChatConfigDirectories =
    {
        "profile/AppData/Roaming/Tencent/xwechat/config",
        "profile/AppData/Roaming/Tencent/WeChat/All Users/config",
        "profile/AppData/Roaming/Tencent/WeChat/config"
    };

    public static IReadOnlyList<ExternalFolder> Detect(IFileScope scope)
    {
        var found = new List<ExternalFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, string reason)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path.Trim().Trim('"'));
                if (!Directory.Exists(full)) return;
                if (seen.Add(full)) found.Add(new ExternalFolder(full, reason));
            }
            catch (Exception)
            {
                // 非法路径直接忽略
            }
        }

        foreach (var directory in WeChatConfigDirectories)
        {
            foreach (var entry in SafeList(scope, directory))
            {
                if (entry.IsDirectory || !entry.Name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;
                var text = SafeReadText(scope, ScopePath.Combine(directory, entry.Name), 8 * 1024);
                if (string.IsNullOrWhiteSpace(text)) continue;

                var content = text.Trim().Trim('"', '\'', '\0', '\uFEFF');
                if (content.Length < 3 || content.Length > 260) continue;
                if (!IsDrivePath(content)) continue;

                try
                {
                    var dataRoot = content.TrimEnd('\\', '/');
                    var filesRoot = Path.Combine(dataRoot, "xwechat_files");
                    if (Directory.Exists(filesRoot))
                        Add(filesRoot, $"微信的「文件保存位置」设置成 {dataRoot}，收到的文件存在沙盒外");
                    else if (Directory.Exists(dataRoot))
                        Add(dataRoot, $"微信的数据根目录是 {dataRoot}，在沙盒外");
                }
                catch (Exception)
                {
                    // ignore
                }
            }
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Add(Path.Combine(documents, "xwechat_files"), "微信 4.x 默认文件目录（宿主）");
        Add(Path.Combine(documents, "WeChat Files"), "微信 3.x 默认文件目录（宿主）");
        Add(Path.Combine(documents, "Tencent Files"), "QQ 默认接收文件目录（宿主）");

        return found;
    }

    private static bool IsDrivePath(string value) =>
        value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/');

    private static IReadOnlyList<SandboxFileEntry> SafeList(IFileScope scope, string relative)
    {
        try
        {
            return scope.List(relative);
        }
        catch (Exception)
        {
            return Array.Empty<SandboxFileEntry>();
        }
    }

    private static string? SafeReadText(IFileScope scope, string relative, int maxBytes)
    {
        try
        {
            using var stream = scope.OpenRead(relative);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while (buffer.Length < maxBytes && (read = stream.Read(chunk, 0, chunk.Length)) > 0)
                buffer.Write(chunk, 0, read);
            var bytes = buffer.ToArray();
            var text = Encoding.UTF8.GetString(bytes);
            return text.Replace("\0", "").Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
