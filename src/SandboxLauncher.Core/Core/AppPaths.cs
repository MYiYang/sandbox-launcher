namespace SandboxLauncher.Core;

public static class AppPaths
{
    public const string ProductName = "SandboxLauncher";
    public const string Version = "0.4.3";

    /// <summary>%LOCALAPPDATA%\SandboxLauncher —— 只放启动器自己的元数据，不放沙盒内容。</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    public static string CatalogFile => Path.Combine(Root, "catalog.json");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string DefaultSessionsRoot => Path.Combine(Root, "sessions");

    public static string DefaultSnapshotDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ProductName, "Snapshots");

    public static void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDir);
    }

    public static string NewLogFile(string prefix)
    {
        Directory.CreateDirectory(LogsDir);
        return Path.Combine(LogsDir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }
}
