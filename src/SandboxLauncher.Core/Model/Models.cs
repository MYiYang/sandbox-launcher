using System.Text.Json.Serialization;

namespace SandboxLauncher.Core.Model;

/// <summary>一个沙盒：一个快照文件 + 一个或多个已导入的客户端程序。</summary>
public sealed class SandboxRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    /// <summary>快照文件（单文件，.sbx）的绝对路径，可放在任意位置。</summary>
    public string SnapshotPath { get; set; } = "";
    public List<SandboxAppRecord> Apps { get; set; } = new();
    public SandboxSettings Settings { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastStartedAt { get; set; }
    public DateTimeOffset? LastStoppedAt { get; set; }
    public string? Note { get; set; }
    public string? LastError { get; set; }

    [JsonIgnore]
    public string AppsSummary => Apps.Count == 0 ? "（未导入客户端）" : string.Join("、", Apps.Select(a => a.Name));
}

/// <summary>客户端程序的来源方式。</summary>
public enum AppSourceMode
{
    /// <summary>复制进快照（默认）：程序文件是沙盒自己的副本，不再依赖原安装目录。</summary>
    Copy = 0,

    /// <summary>
    /// 引用安装目录：不复制任何程序文件，运行时直接在原目录启动，只把用户数据重定向进沙盒。
    /// 适合几十 GB 的大软件（微信、Office、游戏……），导入是秒级、快照几乎不增长。
    /// </summary>
    Reference = 1
}

/// <summary>沙盒里导入的一个客户端程序。</summary>
public sealed class SandboxAppRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    /// <summary>
    /// 可执行文件路径：Copy 模式是相对沙盒根目录的路径（例如 apps/wechat/WeChat.exe）；
    /// Reference 模式是源程序的绝对路径（例如 D:\Software\Weixin\Weixin.exe）。
    /// </summary>
    public string ExecutablePath { get; set; } = "";
    /// <summary>
    /// 工作目录：Copy 模式相对沙盒根目录；Reference 模式是源安装目录的绝对路径。
    /// </summary>
    public string WorkingDirectory { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string? SourcePath { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.Now;
    public long SizeBytes { get; set; }
    public string? IconBase64 { get; set; }
    public int FileCount { get; set; }

    /// <summary>来源方式：默认复制进快照。</summary>
    public AppSourceMode SourceMode { get; set; } = AppSourceMode.Copy;

    /// <summary>程序所在的 VHD（基础盘或差分盘）绝对路径；为空表示不在 VHD 上。</summary>
    public string? VhdPath { get; set; }

    /// <summary>启动时希望挂载到的盘符（为空则自动分配）。</summary>
    public char? VhdDriveLetter { get; set; }

    /// <summary>VHD 内的相对程序路径，例如 app\Game.exe。</summary>
    public string? VhdRelativeExecutable { get; set; }

    /// <summary>已知的单实例互斥体名字（可含 * 通配）；启动第二个实例前会把这些句柄释放掉。</summary>
    public List<string> MutexNames { get; set; } = new();

    /// <summary>启动时是否自动释放已运行实例的互斥体（默认开）。</summary>
    public bool AutoReleaseMutex { get; set; } = true;

    [JsonIgnore]
    public bool IsReference => SourceMode == AppSourceMode.Reference;

    /// <summary>界面上显示用的一句话说明。</summary>
    [JsonIgnore]
    public string ModeText => VhdPath != null ? "虚拟磁盘（VHDX）" : IsReference ? "引用安装目录" : "复制进快照";

    /// <summary>列表列里用的短标签。</summary>
    [JsonIgnore]
    public string ModeShortText => VhdPath != null ? "VHD" : IsReference ? "引用" : "复制";

    /// <summary>引用模式下源程序是否还在（安装目录被移动/卸载后为 false）。</summary>
    [JsonIgnore]
    public bool SourceAvailable => !IsReference ||
        (ExecutablePath.Length > 0 && File.Exists(ExecutablePath));
}

/// <summary>沙盒的运行期设置（随快照一起保存，跟着快照走）。</summary>
public sealed class SandboxSettings
{
    /// <summary>是否把 USERPROFILE/APPDATA/LOCALAPPDATA/TEMP 指向沙盒内部。</summary>
    public bool RedirectUserProfile { get; set; } = true;
    /// <summary>要一起隔离的 HKCU 注册表子树（相对 HKEY_CURRENT_USER），如 Software\Tencent\WeChat。</summary>
    public List<string> IsolateRegistryKeys { get; set; } = new();
    public Dictionary<string, string> ExtraEnvironment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? DefaultAppId { get; set; }
    /// <summary>退出后保留会话目录（调试用，默认关闭，退出即回写并清理）。</summary>
    public bool KeepSessionDirectory { get; set; }
    /// <summary>沙盒内的虚拟用户目录名（默认 profile）。</summary>
    public string ProfileFolderName { get; set; } = "profile";

    // ---------- 文件互传（浏览 / 导出 / 导入） ----------

    /// <summary>手动添加的宿主目录快捷入口（沙盒外的真实目录），只用于浏览/拷贝，不改动程序行为。</summary>
    public List<string> HostFolders { get; set; } = new();

    /// <summary>回写快照时额外跳过的路径（相对沙盒 fs/，用 / 分隔，每段支持 * 通配）。</summary>
    public List<string> ExcludeFromSnapshot { get; set; } = new();

    /// <summary>是否跳过 Windows 系统缓存目录（INetCache 等，默认跳过；关掉可保留全部缓存）。</summary>
    public bool SkipSystemCaches { get; set; } = true;
}

public sealed class Catalog
{
    public int Version { get; set; } = 1;
    public List<SandboxRecord> Sandboxes { get; set; } = new();
}

public sealed class LauncherSettings
{
    public string? SessionRoot { get; set; }
    public string? DefaultSnapshotDirectory { get; set; }
    public string? LastUsedDirectory { get; set; }
    public string? LastOpenSnapshotDirectory { get; set; }
}

/// <summary>快照文件里的清单（manifest.json）。</summary>
public sealed class SnapshotManifest
{
    public int FormatVersion { get; set; } = Snapshot.SnapshotFile.FormatVersion;
    public string Kind { get; set; } = Snapshot.SnapshotFile.Kind;
    public string SandboxId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public List<SandboxAppRecord> Apps { get; set; } = new();
    public SandboxSettings Settings { get; set; } = new();
    public SnapshotStats Stats { get; set; } = new();
    public string LauncherVersion { get; set; } = AppPaths.Version;
}

public sealed class SnapshotStats
{
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
}

/// <summary>快照文件内部的文件索引，用于增量回写（避免每次重新压缩整个沙盒）。</summary>
public sealed class SnapshotIndex
{
    public Dictionary<string, SnapshotIndexEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SnapshotIndexEntry
{
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string Sha256 { get; set; } = "";
}
