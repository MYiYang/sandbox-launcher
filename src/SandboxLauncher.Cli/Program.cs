using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SandboxLauncher.Core;
using SandboxLauncher.Core.Files;
using SandboxLauncher.Core.Model;
using SandboxLauncher.Core.Runtime;
using SandboxLauncher.Core.Snapshot;

namespace SandboxLauncher.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            args = ExtractGlobalOption(args, "root", out var rootOverride);
            args = ExtractGlobalOption(args, "vhd-root", out var vhdRootOverride);

            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            var command = args[0].ToLowerInvariant();
            var options = ParseOptions(args.Skip(1).ToArray());

            if (command is "help" or "-h" or "--help")
            {
                PrintUsage();
                return 0;
            }

            if (command is "version" or "--version")
            {
                Print(new { version = AppPaths.Version, runtime = Environment.Version.ToString(), root = AppPaths.Root });
                return 0;
            }

            var root = rootOverride ?? options.GetValueOrDefault("root");
            var store = new CatalogStore(root);
            using var service = new SandboxService(store);
            service.Log += line => Console.Error.WriteLine(line);

            switch (command)
            {
                case "list":
                    Print(new
                    {
                        root = store.Root,
                        sessionRoot = store.SessionRoot,
                        count = store.Catalog.Sandboxes.Count,
                        sandboxes = store.Catalog.Sandboxes.Select(Describe).ToList()
                    });
                    return 0;

                case "new":
                {
                    var name = options.GetValueOrDefault("name");
                    var snapshotPath = options.GetValueOrDefault("snapshot");
                    var record = store.Create(name, snapshotPath);
                    if (!File.Exists(record.SnapshotPath))
                    {
                        SnapshotFile.Create(record.SnapshotPath, new SnapshotManifest
                        {
                            SandboxId = record.Id,
                            Name = record.Name,
                            Settings = record.Settings
                        });
                    }

                    Print(Describe(record));
                    return 0;
                }

                case "app":
                {
                    var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                    var sandboxKey = options.GetValueOrDefault("sandbox") ?? throw new ArgumentException("缺少 --sandbox");
                    var record = store.Find(sandboxKey) ?? throw new ArgumentException($"找不到沙盒：{sandboxKey}");
                    if (sub == "add")
                    {
                        var path = options.GetValueOrDefault("path") ?? throw new ArgumentException("缺少 --path");
                        var mode = ParseMode(options.GetValueOrDefault("mode"), path);
                        var app = service.ImportApp(record, path, options.GetValueOrDefault("name"),
                            msg => Console.Error.WriteLine(msg), mode);
                        Print(new
                        {
                            app.Id,
                            app.Name,
                            app.ExecutablePath,
                            app.WorkingDirectory,
                            mode = app.SourceMode.ToString().ToLowerInvariant(),
                            app.SourcePath,
                            app.FileCount,
                            app.SizeBytes,
                            reference = app.IsReference
                        });
                        return 0;
                    }

                    if (sub == "remove")
                    {
                        var appId = options.GetValueOrDefault("app") ?? throw new ArgumentException("缺少 --app");
                        service.RemoveApp(record, appId);
                        Print(new { removed = appId, sandbox = record.Name });
                        return 0;
                    }

                    if (sub == "list")
                    {
                        Print(record.Apps);
                        return 0;
                    }

                    throw new ArgumentException("未知子命令：app " + sub);
                }

                case "run":
                {
                    var sandboxKey = options.GetValueOrDefault("sandbox") ?? throw new ArgumentException("缺少 --sandbox");
                    var record = store.Find(sandboxKey) ?? throw new ArgumentException($"找不到沙盒：{sandboxKey}");
                    var appKey = options.GetValueOrDefault("app");
                    var app = appKey == null
                        ? null
                        : record.Apps.FirstOrDefault(a => string.Equals(a.Id, appKey, StringComparison.OrdinalIgnoreCase))
                          ?? record.Apps.FirstOrDefault(a => string.Equals(a.Name, appKey, StringComparison.OrdinalIgnoreCase))
                          ?? throw new ArgumentException($"沙盒里没有客户端：{appKey}");

                    var session = service.Start(record, app, options.GetValueOrDefault("args"));
                    // CLI 是前台工具：等客户端退出、回写快照后再返回（后台常驻请用图形界面）
                    var wait = true;

                    Print(new
                    {
                        sandbox = record.Name,
                        sandboxId = record.Id,
                        app = session.AppName,
                        pid = session.ProcessId,
                        sessionDir = session.SessionDir,
                        snapshot = session.SnapshotPath,
                        logFile = session.LogFile,
                        startWarning = session.StartWarning,
                        treeTracking = session.Tree?.Valid == true,
                        waited = wait
                    });

                    if (wait)
                    {
                        if (!service.WaitForExit(session, Timeout.Infinite))
                            Console.Error.WriteLine("等待客户端退出超时");
                        Print(new
                        {
                            sandbox = record.Name,
                            exited = true,
                            snapshot = record.SnapshotPath,
                            snapshotBytes = new FileInfo(record.SnapshotPath).Length,
                            exitHint = session.ExitHint
                        });
                        if (session.ExitHint != null) Console.Error.WriteLine(session.ExitHint);
                    }

                    return 0;
                }

                case "stop":
                {
                    var sandboxKey = options.GetValueOrDefault("sandbox") ?? throw new ArgumentException("缺少 --sandbox");
                    var record = store.Find(sandboxKey) ?? throw new ArgumentException($"找不到沙盒：{sandboxKey}");
                    service.Stop(record.Id);
                    var orphanHandled = service.FinalizeOrphanSession(record.Id);
                    Print(new { sandbox = record.Name, stopped = true, recoveredDetachedSession = orphanHandled });
                    return 0;
                }

                case "set":
                {
                    var sandboxKey = options.GetValueOrDefault("sandbox") ?? throw new ArgumentException("缺少 --sandbox");
                    var record = store.Find(sandboxKey) ?? throw new ArgumentException($"找不到沙盒：{sandboxKey}");

                    if (options.TryGetValue("isolate-registry", out var isolate))
                        record.Settings.IsolateRegistryKeys = isolate
                            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(x => x.Trim())
                            .Where(x => x.Length > 0)
                            .ToList();
                    if (options.ContainsKey("clear-isolate")) record.Settings.IsolateRegistryKeys.Clear();
                    if (options.TryGetValue("default-app", out var defaultApp))
                        record.Settings.DefaultAppId = record.Apps
                            .FirstOrDefault(a => string.Equals(a.Id, defaultApp, StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(a.Name, defaultApp, StringComparison.OrdinalIgnoreCase))?.Id;
                    if (options.ContainsKey("keep-session")) record.Settings.KeepSessionDirectory = true;
                    if (options.ContainsKey("no-keep-session")) record.Settings.KeepSessionDirectory = false;
                    if (options.TryGetValue("name", out var newName) && !string.IsNullOrWhiteSpace(newName)) record.Name = newName.Trim();

                    store.Save();
                    Print(Describe(record));
                    return 0;
                }

                case "status":
                {
                    var sessions = service.ListSessionsOnDisk().Select(x => new
                    {
                        sandbox = x.Meta.SandboxName,
                        sandboxId = x.Meta.SandboxId,
                        app = x.Meta.AppName,
                        pid = x.Meta.ProcessId,
                        startedAt = x.Meta.StartedAt,
                        alive = x.Alive,
                        ownedByThisProcess = service.IsRunning(x.Meta.SandboxId),
                        sessionDir = x.Dir
                    }).ToList();

                    Print(new { count = sessions.Count, sessions });
                    return 0;
                }

                case "inspect":
                {
                    var path = options.GetValueOrDefault("snapshot");
                    if (path == null)
                    {
                        var sandboxKey = options.GetValueOrDefault("sandbox") ?? throw new ArgumentException("缺少 --snapshot 或 --sandbox");
                        path = (store.Find(sandboxKey) ?? throw new ArgumentException($"找不到沙盒：{sandboxKey}")).SnapshotPath;
                    }

                    var info = SnapshotFile.Inspect(path);
                    Print(new
                    {
                        path = info.Path,
                        fileSizeBytes = info.FileSizeBytes,
                        entries = info.EntryCount,
                        uncompressedBytes = info.UncompressedBytes,
                        manifest = info.Manifest
                    });
                    return 0;
                }

                case "import":
                {
                    var path = options.GetValueOrDefault("path") ?? throw new ArgumentException("缺少 --path");
                    var record = service.RegisterSnapshot(path, options.GetValueOrDefault("name"));
                    Print(Describe(record));
                    return 0;
                }

                case "export":
                {
                    var sandboxKey = options.GetValueOrDefault("sandbox") ?? throw new ArgumentException("缺少 --sandbox");
                    var record = store.Find(sandboxKey) ?? throw new ArgumentException($"找不到沙盒：{sandboxKey}");
                    var to = options.GetValueOrDefault("to") ?? throw new ArgumentException("缺少 --to");
                    var target = service.ExportSnapshot(record, to);
                    Print(new { exported = target, bytes = new FileInfo(target).Length });
                    return 0;
                }

                case "relocate":
                {
                    var sandboxKey = options.GetValueOrDefault("sandbox") ?? throw new ArgumentException("缺少 --sandbox");
                    var record = store.Find(sandboxKey) ?? throw new ArgumentException($"找不到沙盒：{sandboxKey}");
                    var to = options.GetValueOrDefault("to") ?? throw new ArgumentException("缺少 --to");
                    service.RelocateSnapshot(record, to, moveFile: !options.ContainsKey("copy"));
                    Print(Describe(record));
                    return 0;
                }

                case "rm":
                {
                    var sandboxKey = options.GetValueOrDefault("sandbox") ?? throw new ArgumentException("缺少 --sandbox");
                    var record = store.Find(sandboxKey) ?? throw new ArgumentException($"找不到沙盒：{sandboxKey}");
                    service.Stop(record.Id);
                    store.Remove(record, options.ContainsKey("delete-snapshot"));
                    Print(new { removed = record.Name, snapshotDeleted = options.ContainsKey("delete-snapshot") });
                    return 0;
                }

                case "files":
                {
                    var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "info";
                    var hostPath = options.GetValueOrDefault("host");
                    var sandboxKey = options.GetValueOrDefault("sandbox");

                    SandboxRecord? record = null;
                    SandboxActivity? activity = null;
                    string? liveSessionDir = null;

                    using IFileScope scope = !string.IsNullOrWhiteSpace(hostPath)
                        ? new HostFolderScope(hostPath)
                        : OpenSandboxScope(store, service, sandboxKey, out record, out activity, out liveSessionDir);

                    var hostName = Path.GetFileName(hostPath?.TrimEnd('\\', '/') ?? "");
                    var scopeName = record?.Name ?? (hostName.Length > 0 ? hostName : hostPath ?? "宿主目录");
                    var path = options.GetValueOrDefault("path") ?? "";
                    var scopeId = record?.Id ?? "host";
                    var snapshotPath = record?.SnapshotPath;
                    var hostFolders = record?.Settings.HostFolders ?? new List<string>();

                    switch (sub)
                    {
                        case "info":
                            Print(new
                            {
                                sandbox = record?.Name,
                                host = hostPath,
                                sandboxId = record?.Id,
                                status = activity?.StatusText ?? "宿主目录",
                                running = activity?.Running ?? false,
                                sessionDir = liveSessionDir,
                                scope = scope.KindText,
                                scopeRoot = scope.DisplayRoot,
                                snapshot = snapshotPath,
                                snapshotBytes = snapshotPath != null && File.Exists(snapshotPath)
                                    ? new FileInfo(snapshotPath).Length
                                    : 0,
                                current = scope.Normalize(path),
                                hostFolders
                            });
                            return 0;

                        case "ls":
                        {
                            var entries = scope.List(path);
                            var filter = options.GetValueOrDefault("filter");
                            if (!string.IsNullOrWhiteSpace(filter))
                                entries = entries
                                    .Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                                    .ToList();
                            var current = scope.Normalize(path);
                            Print(new
                            {
                                sandbox = record?.Name,
                                host = hostPath,
                                scope = scope.KindText,
                                running = activity?.Running ?? false,
                                sessionDir = liveSessionDir,
                                path = current,
                                parent = ScopePath.Parent(current),
                                count = entries.Count,
                                entries = entries.Select(DescribeEntry).ToList()
                            });
                            return 0;
                        }

                        case "find":
                        {
                            var pattern = options.GetValueOrDefault("name") ?? options.GetValueOrDefault("filter") ?? "*";
                            var limit = int.TryParse(options.GetValueOrDefault("limit"), out var parsedLimit) ? parsedLimit : 200;
                            var found = new List<object>();
                            var pending = new Stack<string>();
                            pending.Push(scope.Normalize(path));
                            while (pending.Count > 0 && found.Count < limit)
                            {
                                var directory = pending.Pop();
                                IReadOnlyList<SandboxFileEntry> entries;
                                try { entries = scope.List(directory); }
                                catch (Exception) { continue; }
                                foreach (var entry in entries)
                                {
                                    if (entry.IsDirectory)
                                    {
                                        pending.Push(entry.RelativePath);
                                        continue;
                                    }

                                    if (!GlobMatch(entry.Name, pattern)) continue;
                                    found.Add(DescribeEntry(entry));
                                    if (found.Count >= limit) break;
                                }
                            }

                            Print(new { sandbox = record?.Name, host = hostPath, pattern, count = found.Count, entries = found });
                            return 0;
                        }

                        case "export":
                        {
                            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("缺少 --path（要导出的文件/目录）");
                            var target = options.GetValueOrDefault("to");
                            if (string.IsNullOrWhiteSpace(target)) target = DefaultExportDirectory(scopeName);
                            var overwrite = !options.ContainsKey("no-overwrite");
                            var result = scope.CopyOut(new[] { path }, Path.GetFullPath(target), overwrite,
                                message => Console.Error.WriteLine(message));
                            Print(new
                            {
                                sandbox = record?.Name,
                                host = hostPath,
                                from = scope.Normalize(path),
                                sourceKind = scope.KindText,
                                to = result.TargetPath,
                                files = result.FileCount,
                                bytes = result.Bytes,
                                skipped = result.Skipped,
                                paths = result.Paths
                            });
                            return 0;
                        }

                        case "import":
                        {
                            var from = options.GetValueOrDefault("from") ?? throw new ArgumentException("缺少 --from（宿主上的文件/目录）");
                            var overwrite = !options.ContainsKey("no-overwrite");
                            var result = scope.CopyIn(from, path, overwrite, message => Console.Error.WriteLine(message));
                            Print(new
                            {
                                sandbox = record?.Name,
                                host = hostPath,
                                from = Path.GetFullPath(from),
                                to = scope.Normalize(path),
                                scope = scope.KindText,
                                files = result.FileCount,
                                bytes = result.Bytes,
                                skipped = result.Skipped,
                                paths = result.Paths
                            });
                            return 0;
                        }

                        case "rm":
                        {
                            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("缺少 --path（要删除的文件/目录）");
                            var deleted = scope.Delete(path.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
                            Print(new { sandbox = record?.Name, host = hostPath, removed = deleted, path = scope.Normalize(path) });
                            return 0;
                        }

                        case "open":
                        {
                            var current = scope.Normalize(path);
                            var tempRoot = Path.Combine(Path.GetTempPath(), "SandboxLauncher", "preview", scopeId);
                            string opened;
                            if (current.Length > 0 && scope.FileExists(current))
                            {
                                opened = scope.Materialize(current, tempRoot);
                                Process.Start(new ProcessStartInfo { FileName = opened, UseShellExecute = true });
                            }
                            else
                            {
                                opened = scope.Materialize(current, tempRoot);
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = "explorer.exe",
                                    Arguments = "\"" + opened + "\"",
                                    UseShellExecute = true
                                });
                            }

                            Print(new { sandbox = record?.Name, host = hostPath, opened, virtualSource = scope.IsVirtual });
                            return 0;
                        }

                        case "host":
                        {
                            if (record == null) throw new ArgumentException("files host 需要 --sandbox");
                            var action = args.Length > 2 ? args[2].ToLowerInvariant() : "list";
                            if (action == "add")
                            {
                                var directory = options.GetValueOrDefault("path") ?? throw new ArgumentException("缺少 --path");
                                directory = Path.GetFullPath(directory.Trim().Trim('"'));
                                if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("宿主目录不存在：" + directory);
                                if (!record.Settings.HostFolders.Contains(directory, StringComparer.OrdinalIgnoreCase))
                                    record.Settings.HostFolders.Add(directory);
                                store.Save();
                            }
                            else if (action == "remove")
                            {
                                var directory = options.GetValueOrDefault("path") ?? throw new ArgumentException("缺少 --path");
                                record.Settings.HostFolders.RemoveAll(d =>
                                    string.Equals(d, directory, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(Path.GetFullPath(d), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase));
                                store.Save();
                            }

                            Print(new { sandbox = record.Name, hostFolders = record.Settings.HostFolders });
                            return 0;
                        }

                        case "detect":
                        {
                            var detected = ExternalFolderDetector.Detect(scope);
                            Print(new
                            {
                                sandbox = record?.Name,
                                host = hostPath,
                                scope = scope.KindText,
                                detected = detected.Select(d => new { path = d.Path, reason = d.Reason }).ToList(),
                                hostFolders
                            });
                            return 0;
                        }

                        default:
                            throw new ArgumentException("未知子命令：files " + sub);
                    }
                }

                case "vhd":
                {
                    var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
                    var vhd = new VhdService(vhdRootOverride ?? options.GetValueOrDefault("vhd-root"));
                    switch (sub)
                    {
                        case "list":
                            Print(new { supported = VhdService.IsSupported(), root = vhd.Root, items = vhd.List() });
                            return 0;

                        case "disks":
                            Print(new
                            {
                                note = "能放 VHD 的盘（按剩余空间排序）；用 --at <盘符>: 指定放哪，用 --letter <盘符> 指定挂载盘符",
                                disks = VhdService.ListDisks().Select(d => new
                                {
                                    drive = d.Letter.ToString() + ":",
                                    d.Root,
                                    d.Label,
                                    d.FileSystem,
                                    freeGb = Math.Round(d.FreeBytes / 1024.0 / 1024 / 1024, 1),
                                    totalGb = Math.Round(d.TotalBytes / 1024.0 / 1024 / 1024, 1),
                                    d.MediaType,
                                    d.Display
                                })
                            });
                            return 0;

                        case "import":
                        {
                            var sandboxKey = options.GetValueOrDefault("sandbox") ?? throw new ArgumentException("缺少 --sandbox");
                            var record = store.Find(sandboxKey) ?? throw new ArgumentException($"找不到沙盒：{sandboxKey}");
                            var name = options.GetValueOrDefault("name") ?? throw new ArgumentException("缺少 --name");
                            var source = options.GetValueOrDefault("source") ?? throw new ArgumentException("缺少 --source");
                            var at = options.GetValueOrDefault("at") ?? "G:";
                            var sizeGb = int.TryParse(options.GetValueOrDefault("size-gb"), out var gb2) ? gb2 : 0;
                            char? letter = ParseLetter(options.GetValueOrDefault("letter"));
                            var (app2, vhdInfo2) = service.ImportAppToVhd(record, source, options.GetValueOrDefault("app-name"),
                                at, sizeGb > 0 ? sizeGb * 1024L * 1024 * 1024 : 0, letter,
                                msg => Console.Error.WriteLine(msg));
                            Print(new
                            {
                                sandbox = record.Name,
                                app = new { app2.Name, app2.ExecutablePath, app2.VhdPath, vhdDriveLetter = app2.VhdDriveLetter?.ToString(), app2.VhdRelativeExecutable },
                                vhd = new { vhdInfo2.Path, sizeBytes = vhdInfo2.SizeBytes, fileBytes = vhdInfo2.FileBytes },
                                note = "VHD 已挂载，沙盒启动时会自动挂载（换成同一个盘符）"
                            });
                            return 0;
                        }

                        case "base":
                        {
                            var name = options.GetValueOrDefault("name") ?? throw new ArgumentException("缺少 --name");
                            var source = options.GetValueOrDefault("source") ?? throw new ArgumentException("缺少 --source");
                            var sizeGb = int.TryParse(options.GetValueOrDefault("size-gb"), out var gb) ? gb : 0;
                            var sizeBytes = sizeGb > 0 ? gb * 1024L * 1024 * 1024 : 0;
                            var info = vhd.CreateBase(name, source, sizeBytes);
                            Print(new { created = "base", info, note = "源目录已移入基础盘（不额外占磁盘）" });
                            return 0;
                        }

                        case "child":
                        {
                            var name = options.GetValueOrDefault("name") ?? throw new ArgumentException("缺少 --name");
                            var parent = options.GetValueOrDefault("parent") ?? throw new ArgumentException("缺少 --parent");
                            var info = vhd.CreateChild(name, parent);
                            Print(new { created = "child", info, note = "差分盘初始只有几 MB，写出多少才占多少" });
                            return 0;
                        }

                        case "attach":
                        {
                            var sandboxKey = options.GetValueOrDefault("sandbox") ?? throw new ArgumentException("缺少 --sandbox");
                            var record = store.Find(sandboxKey) ?? throw new ArgumentException($"找不到沙盒：{sandboxKey}");
                            var vhdPath = options.GetValueOrDefault("path") ?? throw new ArgumentException("缺少 --path <VHD 文件>");
                            var rel = options.GetValueOrDefault("rel") ?? throw new ArgumentException("缺少 --rel <VHD 内相对路径，例如 app\\x.exe>");
                            var letter = ParseLetter(options.GetValueOrDefault("letter"));
                            var mounted = new VhdService(vhdRootOverride).EnsureMounted(Path.GetFullPath(vhdPath), letter);
                            var exe = Path.Combine(mounted + ":\\", rel.Replace('/', '\\'));
                            if (!File.Exists(exe)) throw new FileNotFoundException($"挂载后找不到程序：{exe}");
                            var app = service.ImportApp(record, exe, options.GetValueOrDefault("name"), null,
                                AppSourceMode.Reference);
                            app.VhdPath = Path.GetFullPath(vhdPath);
                            app.VhdDriveLetter = mounted;
                            app.VhdRelativeExecutable = rel;
                            service.UpdateApp(record, app);
                            Print(new { sandbox = record.Name, app = app.Name, vhd = app.VhdPath, driveLetter = mounted.ToString(), exe });
                            return 0;
                        }

                        case "mount":
                        {
                            var name = options.GetValueOrDefault("name") ?? throw new ArgumentException("缺少 --name");
                            var letter = vhd.Mount(name);
                            Print(new { mounted = name, driveLetter = letter.ToString(), appPath = $"{letter}:\\app" });
                            return 0;
                        }

                        case "dismount":
                        {
                            var name = options.GetValueOrDefault("name") ?? throw new ArgumentException("缺少 --name");
                            vhd.Dismount(name);
                            Print(new { dismounted = name });
                            return 0;
                        }

                        default:
                            throw new ArgumentException("未知子命令：vhd " + sub);
                    }
                }

                case "settings":
                {
                    if (options.TryGetValue("session-root", out var sessionRoot)) store.Settings.SessionRoot = Path.GetFullPath(sessionRoot);
                    if (options.TryGetValue("snapshot-dir", out var snapshotDir)) store.Settings.DefaultSnapshotDirectory = Path.GetFullPath(snapshotDir);
                    store.Save();
                    Print(new { store.Settings, store.SessionRoot, store.DefaultSnapshotDirectory });
                    return 0;
                }

                case "env":
                {
                    var sandboxKey = options.GetValueOrDefault("sandbox") ?? throw new ArgumentException("缺少 --sandbox");
                    var record = store.Find(sandboxKey) ?? throw new ArgumentException($"找不到沙盒：{sandboxKey}");
                    var fakeSession = Path.Combine(store.SessionRoot, record.Id);
                    var env = EnvBuilder.Build(fakeSession, record.Settings);
                    Print(env);
                    return 0;
                }

                default:
                    Print(new { error = "未知命令：" + command });
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Print(new { error = ex.Message, type = ex.GetType().Name });
            return 1;
        }
    }

    private static IFileScope OpenSandboxScope(CatalogStore store, SandboxService service, string? sandboxKey,
        out SandboxRecord? record, out SandboxActivity? activity, out string? liveSessionDir)
    {
        if (string.IsNullOrWhiteSpace(sandboxKey))
            throw new ArgumentException("缺少 --sandbox（或用 --host <宿主目录> 直接浏览宿主目录）");

        record = store.Find(sandboxKey) ?? throw new ArgumentException($"找不到沙盒：{sandboxKey}");
        activity = service.GetActivity(record);
        liveSessionDir = activity.HasSession ? activity.SessionDir : null;
        return SandboxFileSystem.Open(record, liveSessionDir);
    }

    private static object DescribeEntry(SandboxFileEntry entry) => new
    {
        name = entry.Name,
        path = entry.RelativePath,
        dir = entry.IsDirectory,
        size = entry.SizeBytes,
        modified = entry.Modified
    };

    private static bool GlobMatch(string name, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*") return true;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string DefaultExportDirectory(string sandboxName)
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var baseDirectory = Directory.Exists(downloads)
            ? downloads
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(baseDirectory, "SandboxLauncher", sandboxName);
    }

    private static object Describe(SandboxRecord r) => new
    {
        id = r.Id,
        name = r.Name,
        snapshot = r.SnapshotPath,
        snapshotExists = File.Exists(r.SnapshotPath),
        snapshotBytes = File.Exists(r.SnapshotPath) ? new FileInfo(r.SnapshotPath).Length : 0,
        apps = r.Apps.Select(a => new
        {
            a.Id, a.Name, a.ExecutablePath, mode = a.SourceMode.ToString().ToLowerInvariant(), a.FileCount, a.SizeBytes
        }),
        isolateRegistryKeys = r.Settings.IsolateRegistryKeys,
        lastStartedAt = r.LastStartedAt,
        lastStoppedAt = r.LastStoppedAt,
        lastError = r.LastError
    };

    /// <summary>把全局选项（如 --root）从任意位置摘出来，剩下的才是命令与子选项。</summary>
    private static string[] ExtractGlobalOption(string[] args, string name, out string? value)
    {
        value = null;
        var list = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--" + name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                value = args[i + 1];
                i++;
                continue;
            }

            list.Add(args[i]);
        }

        return list.ToArray();
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith('-')) continue;

            // 支持 --key=value 写法（值本身可以以 - 开头，例如 --args=--user-data-dir=xxx）
            var eq = token.IndexOf('=');
            if (eq > 0)
            {
                result[token[1..eq].TrimStart('-')] = token[(eq + 1)..];
                continue;
            }

            var key = token.TrimStart('-');
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                result[key] = args[i + 1];
                i++;
            }
            else
            {
                result[key] = "true";
            }
        }

        return result;
    }

    /// <summary>
    /// 解析 --mode ：
    ///   copy      → 复制进快照（原来的行为）
    ///   reference → 引用安装目录，不复制文件（几十 GB 大软件用它）
    ///   auto      → 默认值：按体积自动决定（≥ 2 GB 走引用）
    /// </summary>
    private static AppSourceMode ParseMode(string? raw, string sourcePath)
    {
        var text = (raw ?? "auto").Trim().ToLowerInvariant();
        return text switch
        {
            "copy" or "snapshot" or "pack" => AppSourceMode.Copy,
            "reference" or "ref" or "link" or "inplace" or "in-place" or "reuse" => AppSourceMode.Reference,
            "auto" or "" => AppImporter.SuggestMode(sourcePath),
            _ => throw new ArgumentException($"--mode 只能是 copy / reference / auto，收到：{raw}")
        };
    }

    /// <summary>解析一个盘符参数，例如 V / V: / v。</summary>
    private static char? ParseLetter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim().TrimEnd(':').Trim();
        return trimmed.Length == 1 && char.IsLetter(trimmed[0]) ? char.ToUpperInvariant(trimmed[0]) : null;
    }

    private static void Print(object value)
    {
        Console.WriteLine(value is string s ? s : JsonSerializer.Serialize(value, JsonStore.Options));
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            SandboxLauncher CLI 0.4.3 —— 沙盒启动器命令行

            用法：sl <命令> [--选项 值]

              version                                 版本信息
              list                                    列出所有沙盒
              new --name <名称> [--snapshot <路径>]    新建沙盒（可指定快照文件放到任意位置）
              app add --sandbox <沙盒> --path <exe|目录> [--mode copy|reference|auto] [--name <显示名>]   导入客户端
                      copy=复制进快照（原行为）  reference=引用安装目录不复制（几十 GB 大软件用）  auto=默认按体积自动选
              app remove --sandbox <沙盒> --app <客户端ID>                   移除客户端
              app list --sandbox <沙盒>                                      列出客户端
              run --sandbox <沙盒> [--app <客户端>] [--args <参数>]   在沙盒内启动客户端（等退出并回写快照）
              stop --sandbox <沙盒>                   停止沙盒（退出并把改动写回快照）
              status                                  查看正在运行的沙盒（含其他进程遗留的会话）
              inspect --snapshot <路径> | --sandbox <沙盒>   查看快照内容
              import --path <快照> [--name <名称>]     导入/登记已有快照
              export --sandbox <沙盒> --to <路径>      导出快照副本
              relocate --sandbox <沙盒> --to <新路径> [--copy]   把快照移动到别处
              settings [--session-root <路径>] [--snapshot-dir <路径>]  全局设置
              env --sandbox <沙盒>                    查看沙盒内环境变量
              files info --sandbox <沙盒>              沙盒文件来源（运行中会话 / 快照 / 宿主）
              files ls --sandbox <沙盒> [--path <沙盒内路径>] [--filter <片段>]   列出沙盒里的文件
              files find --sandbox <沙盒> [--path <起点>] [--name <通配>] [--limit N]  递归找文件
              files export --sandbox <沙盒> --path <文件/目录> [--to <宿主目录>] [--no-overwrite]   导出到宿主机
              files import --sandbox <沙盒> --from <宿主文件/目录> [--path <沙盒内目录>] [--no-overwrite]  放进沙盒
              files rm --sandbox <沙盒> --path <沙盒内文件/目录>    删除沙盒里的文件（多个用逗号隔开）
              files open --sandbox <沙盒> [--path <沙盒内路径>]    打开文件（快照会先解到临时目录）
              files host add|remove|list --sandbox <沙盒> [--path <宿主目录>]   管理宿主目录快捷入口
              files detect --sandbox <沙盒>            检测程序写到沙盒外的数据目录（例如微信文件目录）
              （以上 files 命令都可以把 --sandbox 换成 --host <宿主目录>，直接浏览/拷贝沙盒外的真实目录）

              vhd list                                                列出 VHD 基础盘 / 差分盘
              vhd base --name <名> --source <程序目录> [--size-gb N]    把程序目录搬进基础盘（不复制，不额外占磁盘）
              vhd child --name <名> --parent <基础盘名>                 建差分盘（只存改动块，初始几 MB）
              vhd mount --name <名> | vhd dismount --name <名>          挂载 / 卸载（挂载后是 <盘符>:pp）
              rm --sandbox <沙盒> [--delete-snapshot]  删除沙盒记录（可连同快照）

            全局选项：--root <目录>   指定启动器元数据目录（默认 %LOCALAPPDATA%\SandboxLauncher）
            """);
    }
}
