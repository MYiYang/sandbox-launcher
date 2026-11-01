using SandboxLauncher.Core.Model;

namespace SandboxLauncher.Core;

/// <summary>沙盒目录（catalog.json）与全局设置的读写。</summary>
public sealed class CatalogStore
{
    public string Root { get; }
    public Catalog Catalog { get; private set; } = new();
    public LauncherSettings Settings { get; private set; } = new();

    private readonly string _catalogFile;
    private readonly string _settingsFile;

    public CatalogStore(string? root = null)
    {
        Root = root ?? AppPaths.Root;
        Directory.CreateDirectory(Root);
        _catalogFile = Path.Combine(Root, "catalog.json");
        _settingsFile = Path.Combine(Root, "settings.json");

        Catalog = JsonStore.Load<Catalog>(_catalogFile) ?? new Catalog();
        Settings = JsonStore.Load<LauncherSettings>(_settingsFile) ?? new LauncherSettings();
        Settings.SessionRoot ??= Path.Combine(Root, "sessions");
        Settings.DefaultSnapshotDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppPaths.ProductName, "Snapshots");
    }

    public string SessionRoot => Settings.SessionRoot!;
    public string DefaultSnapshotDirectory => Settings.DefaultSnapshotDirectory!;

    public void Save()
    {
        Directory.CreateDirectory(Root);
        JsonStore.Save(_catalogFile, Catalog);
        JsonStore.Save(_settingsFile, Settings);
    }

    public SandboxRecord? Find(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        var key = idOrName.Trim();
        return Catalog.Sandboxes.FirstOrDefault(s => string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase))
            ?? Catalog.Sandboxes.FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase))
            ?? Catalog.Sandboxes.FirstOrDefault(s => s.Name.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    public SandboxRecord Create(string? name, string? snapshotPath = null, bool overwriteExisting = false)
    {
        var display = string.IsNullOrWhiteSpace(name) ? $"沙盒 {Catalog.Sandboxes.Count + 1}" : name.Trim();

        string path;
        if (!string.IsNullOrWhiteSpace(snapshotPath))
        {
            path = Path.GetFullPath(snapshotPath);
            if (!path.EndsWith(Snapshot.SnapshotFile.Extension, StringComparison.OrdinalIgnoreCase))
                path += Snapshot.SnapshotFile.Extension;
            if (File.Exists(path) && !overwriteExisting)
                throw new IOException($"快照文件已存在：{path}");
        }
        else
        {
            var dir = DefaultSnapshotDirectory;
            Directory.CreateDirectory(dir);
            var baseName = SanitizeFileName(display);
            path = Path.Combine(dir, baseName + Snapshot.SnapshotFile.Extension);
            var i = 2;
            while (File.Exists(path))
                path = Path.Combine(dir, $"{baseName}-{i++}{Snapshot.SnapshotFile.Extension}");
        }

        var record = new SandboxRecord { Name = display, SnapshotPath = path };
        record.Settings.IsolateRegistryKeys = new List<string>();
        Catalog.Sandboxes.Add(record);
        Save();
        return record;
    }

    public void Remove(SandboxRecord record, bool deleteSnapshotFile)
    {
        Catalog.Sandboxes.Remove(record);
        Save();
        if (deleteSnapshotFile)
        {
            try
            {
                if (File.Exists(record.SnapshotPath)) File.Delete(record.SnapshotPath);
            }
            catch (IOException) { /* 占用时留给用户手动删除 */ }
        }
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(s) ? "sandbox" : s;
    }
}
