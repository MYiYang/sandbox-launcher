using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;
using SandboxLauncher.Core;
using SandboxLauncher.Core.Files;
using SandboxLauncher.Core.Model;
using SandboxLauncher.Core.Runtime;
using SandboxLauncher.Core.Snapshot;

namespace SandboxLauncher.Tests;

/// <summary>
/// 端到端测试（不依赖外部测试框架，直接 dotnet run 即可）：
/// 新建沙盒 → 导入客户端 → 沙盒内启动 → 验证环境重定向 / 注册表隔离 / 快照回写 →
/// 移动快照 → 再次导入并启动，验证状态延续。
/// </summary>
internal static class Program
{
    private const string DemoKey = @"Software\SandboxLauncherDemo";
    private static readonly List<string> _logs = new();
    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var testClientDir = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "TestClient", "bin", "Release", "net9.0-windows"));

        var testClientExe = Path.Combine(testClientDir, "TestClient.exe");
        if (!File.Exists(testClientExe))
        {
            Console.Error.WriteLine($"找不到测试客户端：{testClientExe}（先 build tools/TestClient）");
            return 2;
        }

        var root = Path.Combine(Path.GetTempPath(), "sbx-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Console.WriteLine($"测试根目录：{root}");

        try
        {
            CleanupRegistry();

            var store = new CatalogStore(root);
            using var service = new SandboxService(store, recoverStaleSessions: false);
            service.Log += line => { lock (_logs) _logs.Add(line); };

            var snapshotPath = Path.Combine(root, "snapshots", "demo.sbx");

            // ---- 1. 新建沙盒 ----
            var record = store.Create("测试沙盒", snapshotPath);
            Check("新建沙盒后目录里有记录", store.Catalog.Sandboxes.Count == 1);
            Check("快照路径指向用户指定位置", record.SnapshotPath == Path.GetFullPath(snapshotPath));

            // ---- 2. 导入客户端 ----
            var app = service.ImportApp(record, testClientDir);
            Check("客户端已导入（记录）", record.Apps.Count == 1 && app.Name.Length > 0);
            Check("快照文件已生成", File.Exists(record.SnapshotPath));
            var info = SnapshotFile.Inspect(record.SnapshotPath);
            Check("快照是单个文件且含清单", info.Manifest.Apps.Count == 1);
            Check("快照里打进了客户端文件", info.EntryCount > 3);
            Console.WriteLine($"  快照大小 {info.FileSizeBytes / 1024.0:F1} KB，内部条目 {info.EntryCount} 个，客户端 {app.Name}");

            // ---- 3. 隔离注册表 + 启动 ----
            record.Settings.IsolateRegistryKeys.Add(DemoKey);

            var session = service.Start(record);
            Check("启动后会话在运行", service.IsRunning(record.Id));
            Check("会话目录里是沙盒内的用户目录", Directory.Exists(Path.Combine(session.SessionDir, "fs", "profile")));
            var finished = service.WaitForExit(session, 60_000);
            Check("客户端能正常退出并回写", finished);
            Check("会话目录已清理", !Directory.Exists(session.SessionDir));

            var run1 = ReadStateFromSnapshot(record.SnapshotPath);
            Check("沙盒内写入的文件进了快照", run1 != null);
            Check("第一次运行计数=1", run1?.Trim() == "1");
            Check("注册表已还原（宿主没有该键）", Registry.CurrentUser.OpenSubKey(DemoKey) == null);
            Check("快照里保存了沙盒注册表状态", SnapshotHasEntry(record.SnapshotPath, "registry/software_sandboxlauncherdemo.json"));

            // ---- 4. 第二次启动：状态延续 ----
            var session2 = service.Start(record);
            service.WaitForExit(session2, 60_000);
            var run2 = ReadStateFromSnapshot(record.SnapshotPath);
            Check("第二次运行计数=2（文件状态延续）", run2?.Trim() == "2");
            Check("第二次运行注册表计数=2（注册表状态延续）", ReadRegistryRunsFromSnapshot(record.SnapshotPath) == 2);
            Check("第二次运行后宿主注册表依然干净", Registry.CurrentUser.OpenSubKey(DemoKey) == null);

            // ---- 5. 移动快照到别的位置 ----
            var movedPath = Path.Combine(root, "另一个位置", "移动后的沙盒.sbx");
            service.RelocateSnapshot(record, movedPath);
            Check("快照已移动到新位置", File.Exists(movedPath) && !File.Exists(snapshotPath));
            Check("目录里的快照路径已更新", record.SnapshotPath == Path.GetFullPath(movedPath));

            var session3 = service.Start(record);
            service.WaitForExit(session3, 60_000);
            Check("移动后计数=3", ReadStateFromSnapshot(record.SnapshotPath)?.Trim() == "3");

            // ---- 6. 导出副本 + 当成新沙盒导入 ----
            var exported = service.ExportSnapshot(record, Path.Combine(root, "导出", "副本.sbx"));
            Check("导出副本存在且可解析", SnapshotFile.LooksLikeSnapshot(exported));

            var root2 = Path.Combine(root, "另一台电脑");
            var store2 = new CatalogStore(root2);
            using var service2 = new SandboxService(store2, recoverStaleSessions: false);
            var imported = service2.RegisterSnapshot(exported, "导入的沙盒");
            Check("导入后拿到了沙盒记录", imported.Apps.Count == 1);
            var session4 = service2.Start(imported);
            service2.WaitForExit(session4, 60_000);
            Check("导入的沙盒继续计数=4", ReadStateFromSnapshot(imported.SnapshotPath)?.Trim() == "4");

            // ---- 7. 单文件模式导入 + 移除客户端 ----
            var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            if (File.Exists(notepad))
            {
                var single = store.Create("单文件导入", Path.Combine(root, "single", "notepad.sbx"));
                var singleApp = service.ImportApp(single, notepad, "记事本");
                Check("单文件模式只打包 1 个 exe", singleApp.FileCount == 1);
                Check("单文件模式快照里只有 1 个 fs 条目", CountEntries(single.SnapshotPath, "fs/") == 1);
                Check("单文件模式可执行路径正确", singleApp.ExecutablePath.EndsWith("notepad.exe", StringComparison.OrdinalIgnoreCase));

                service.RemoveApp(single, singleApp.Id);
                Check("移除客户端后清单清空", SnapshotFile.Inspect(single.SnapshotPath).Manifest.Apps.Count == 0);
                Check("移除客户端后文件也删掉", CountEntries(single.SnapshotPath, "fs/") == 0);
            }

            // ---- 7.5 引用模式：几十 GB 的大软件不复制，直接在原目录启动 ----
            var bigDir = Path.Combine(root, "bigapp");
            Directory.CreateDirectory(bigDir);
            foreach (var file in Directory.GetFiles(testClientDir))
                File.Copy(file, Path.Combine(bigDir, Path.GetFileName(file)), true);

            // 3 GB 的「数据文件」（SetLength 只占元数据，不真写盘，秒级）—— 模拟几十 GB 的大软件
            var bigAsset = Path.Combine(bigDir, "assets.dat");
            using (var assetStream = File.Create(bigAsset)) assetStream.SetLength(3L * 1024 * 1024 * 1024);
            Check("造出一个 3 GB 的假程序目录", new FileInfo(bigAsset).Length == 3L * 1024 * 1024 * 1024);

            var refSandbox = store.Create("引用模式", Path.Combine(root, "ref", "ref.sbx"));
            var importWatch = Stopwatch.StartNew();
            var bigApp = service.ImportApp(refSandbox, bigDir, "大软件", null, AppSourceMode.Reference);
            importWatch.Stop();
            Check("引用模式：没有把程序文件写进快照", CountEntries(refSandbox.SnapshotPath, "fs/apps/") == 0);
            Check("引用模式：记录里是引用方式", bigApp.IsReference &&
                bigApp.ExecutablePath.EndsWith("TestClient.exe", StringComparison.OrdinalIgnoreCase));
            Check("引用模式：快照几乎不增长（< 200 KB）", new FileInfo(refSandbox.SnapshotPath).Length < 200 * 1024);
            Check("引用模式：清单里也记了来源方式",
                SnapshotFile.ReadManifest(refSandbox.SnapshotPath).Apps.Single().IsReference);
            Check($"引用模式：3 GB 目录的导入是秒级（用了 {importWatch.ElapsedMilliseconds} 毫秒）",
                importWatch.ElapsedMilliseconds < 15_000);

            var bigSession = service.Start(refSandbox);
            Check("引用模式能启动（程序在原安装目录里跑）", service.IsRunning(refSandbox.Id));
            Check("引用模式的工作目录就是源目录",
                string.Equals(Path.GetFullPath(bigSession.App.WorkingDirectory), Path.GetFullPath(bigDir),
                    StringComparison.OrdinalIgnoreCase));
            Check("引用模式：启动的不是沙盒副本",
                !bigSession.App.ExecutablePath.Contains("apps", StringComparison.OrdinalIgnoreCase));
            Check("引用模式：客户端正常退出", service.WaitForExit(bigSession, 60_000));
            Check("引用模式：用户数据照样写进沙盒（计数=1）", ReadStateFromSnapshot(refSandbox.SnapshotPath)?.Trim() == "1");
            Check("引用模式：快照里依然没有程序文件", CountEntries(refSandbox.SnapshotPath, "fs/apps/") == 0);
            Check("引用模式：源程序没有被改动", File.Exists(Path.Combine(bigDir, "TestClient.exe")));

            // 安装目录被挪走 → 启动时要有明确的中文报错（而不是莫名其妙的失败）
            var movedBigDir = bigDir + "-moved";
            Directory.Move(bigDir, movedBigDir);
            Exception? missingSourceError = null;
            try { service.Start(refSandbox); }
            catch (Exception ex) { missingSourceError = ex; }
            Check("引用模式：源程序不见了会明确报错",
                missingSourceError != null && missingSourceError.Message.Contains("源程序不在了"));
            Directory.Move(movedBigDir, bigDir);

            // 引用模式也可以只给一个 exe：依赖还在原目录，照样能跑
            var exeOnlySandbox = store.Create("引用单exe", Path.Combine(root, "ref", "exeonly.sbx"));
            var exeOnlyApp = service.ImportApp(exeOnlySandbox, testClientExe, "只引用exe", null,
                AppSourceMode.Reference);
            Check("引用模式可以只给一个 exe", exeOnlyApp.IsReference &&
                string.Equals(exeOnlyApp.ExecutablePath, testClientExe, StringComparison.OrdinalIgnoreCase));
            var exeOnlySession = service.Start(exeOnlySandbox);
            Check("只给 exe 的引用模式能真正跑起来", service.WaitForExit(exeOnlySession, 60_000));
            Check("只给 exe 的引用模式：状态照样写进沙盒",
                ReadStateFromSnapshot(exeOnlySandbox.SnapshotPath) != null);

            service.RemoveApp(exeOnlySandbox, exeOnlyApp.Id);
            Check("移除引用模式客户端后清单清空",
                SnapshotFile.Inspect(exeOnlySandbox.SnapshotPath).Manifest.Apps.Count == 0);
            Check("移除引用模式客户端不会碰源目录", File.Exists(testClientExe));

            // 小软件仍然默认复制（auto 建议）；只给一个 exe 时默认引用（复制单文件必然缺依赖）
            Check("小目录的导入建议是复制进快照", AppImporter.SuggestMode(testClientDir) == AppSourceMode.Copy);
            Check("大目录的导入建议是引用安装目录", AppImporter.SuggestMode(bigDir) == AppSourceMode.Reference);
            Check("只给一个 exe 的导入建议是引用安装目录",
                AppImporter.SuggestMode(testClientExe) == AppSourceMode.Reference);

            // ---- 7.6 启动器把客户端交给子进程 / 单实例秒退（网游启动器这类程序的典型形态） ----
            // (a) 启动器自己秒退、真正的客户端是子进程：会话必须继续挂着，等整棵树结束再回写
            var launcherSandbox = store.Create("启动器模式", Path.Combine(root, "launcher", "launcher.sbx"));
            var launcherApp = service.ImportApp(launcherSandbox, testClientDir);
            launcherApp.Arguments = "--spawn-child 4000";
            service.UpdateApp(launcherSandbox, launcherApp);
            var launcherSession = service.Start(launcherSandbox);
            Check("启动器自己退了但沙盒还在运行（等子进程）",
                WaitUntil(() => launcherSession.HasExited, 8000) && service.IsRunning(launcherSandbox.Id) &&
                launcherSession.IsTreeAlive);
            Check("等子进程结束后才收尾", service.WaitForExit(launcherSession, 60_000));
            Check("子进程写的文件进了快照",
                SnapshotHasEntry(launcherSandbox.SnapshotPath, "fs/profile/AppData/Roaming/DemoClient/child.done"));
            Check("启动器模式退出后会话目录已清理", !Directory.Exists(launcherSession.SessionDir));

            // (b) 停止沙盒要把整棵树杀掉，不能留孤儿客户端
            var stopSandbox = store.Create("停止整棵树", Path.Combine(root, "launcher", "stop.sbx"));
            var stopApp = service.ImportApp(stopSandbox, testClientDir);
            stopApp.Arguments = "--spawn-child 120000";
            service.UpdateApp(stopSandbox, stopApp);
            var stopSession = service.Start(stopSandbox);
            WaitUntil(() => stopSession.HasExited, 8000);
            Check("子进程一直在跑时沙盒保持运行", service.IsRunning(stopSandbox.Id));
            service.Stop(stopSandbox.Id, 15000);
            Check("停止沙盒会把整棵进程树杀掉", WaitUntil(() => !HasProcesses("TestClient"), 20_000));
            Check("停止后会话目录被清理", !Directory.Exists(stopSession.SessionDir));

            // (c) 同一个程序已经在别处运行：启动时给提示，秒退后给解释（而不是只显示「退出码 0」）
            var conflictSandbox = store.Create("单实例提示", Path.Combine(root, "conflict", "c.sbx"));
            service.ImportApp(conflictSandbox, testClientExe, "测试客户端", null, AppSourceMode.Reference);

            using var hostRun = Process.Start(new ProcessStartInfo(testClientExe, "--sleep 9000")
            {
                UseShellExecute = false,
                WorkingDirectory = testClientDir
            })!;
            WaitUntil(() => !hostRun.HasExited, 3000);
            var conflictSession = service.Start(conflictSandbox);
            Check("启动时能发现同一个程序已经在运行", conflictSession.StartWarning != null);
            Check("秒退后给出解释（不是只有退出码）",
                service.WaitForExit(conflictSession, 30_000) && conflictSession.ExitHint != null);
            try { hostRun.Kill(true); } catch { }
            CleanupRealDemoClient();

            // ---- 8. 文件浏览 / 导出 / 导入（沙盒快照） ----
            using (var filesScope = SandboxFileSystem.Open(record))
            {
                Check("快照作用域识别正确", filesScope.Kind == FileScopeKind.Snapshot);

                var hostFile = Path.Combine(root, "sources", "放进沙盒.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(hostFile)!);
                File.WriteAllText(hostFile, "来自宿主机的文件");

                var importedFile = filesScope.CopyIn(hostFile, "profile/Documents");
                Check("导入文件到快照成功", importedFile.FileCount == 1 && importedFile.Skipped.Count == 0);
                Check("快照里能列出刚导入的文件",
                    filesScope.List("profile/Documents").Any(e => e.Name == "放进沙盒.txt" && !e.IsDirectory));
                Check("快照索引里有新文件（下次回写不会漏）",
                    SnapshotFile.ReadIndex(record.SnapshotPath).Files.ContainsKey("fs/profile/Documents/放进沙盒.txt"));

                using (var stream = filesScope.OpenRead("profile/Documents/放进沙盒.txt"))
                using (var reader = new StreamReader(stream))
                    Check("能直接读取沙盒里的文件内容", reader.ReadToEnd() == "来自宿主机的文件");

                var exportDir = Path.Combine(root, "export-1");
                var exportedFiles = filesScope.CopyOut(new[] { "profile/Documents/放进沙盒.txt" }, exportDir);
                Check("导出单个文件到宿主机", exportedFiles.FileCount == 1 &&
                                              File.Exists(Path.Combine(exportDir, "放进沙盒.txt")));
                Check("导出的内容是一致的", File.ReadAllText(Path.Combine(exportDir, "放进沙盒.txt")) == "来自宿主机的文件");

                var exportDir2 = Path.Combine(root, "export-2");
                var exportedDir = filesScope.CopyOut(new[] { "profile/AppData/Roaming/DemoClient" }, exportDir2);
                Check("导出整个目录（保留子目录结构）", exportedDir.FileCount >= 1 &&
                    File.Exists(Path.Combine(exportDir2, "DemoClient", "state.txt")));

                var measurement = filesScope.Measure("profile/AppData/Roaming/DemoClient");
                Check("统计目录大小", measurement.Count >= 1 && measurement.Bytes > 0);

                Check("删除快照里的文件", filesScope.Delete(new[] { "profile/Documents/放进沙盒.txt" }) == 1 &&
                                         !filesScope.FileExists("profile/Documents/放进沙盒.txt"));
            }

            // ---- 9. 运行中的会话（live，实时读写会话目录） ----
            var liveSessionDir = Path.Combine(store.SessionRoot, record.Id);
            Directory.CreateDirectory(Path.Combine(liveSessionDir, "fs", "profile", "Documents"));
            File.WriteAllText(Path.Combine(liveSessionDir, "fs", "profile", "Documents", "live.txt"), "运行中的文件");
            JsonStore.Save(Path.Combine(liveSessionDir, "session.json"), new SessionMeta
            {
                SandboxId = record.Id,
                SandboxName = record.Name,
                SnapshotPath = record.SnapshotPath,
                AppId = "test",
                AppName = "测试客户端",
                ProcessId = Environment.ProcessId,
                ProcessName = Process.GetCurrentProcess().ProcessName
            });

            var activity = service.GetActivity(record);
            Check("检测到活动会话（跨进程也一样）", activity.HasSession && activity.ProcessAlive && activity.Running);

            using (var liveScope = service.OpenFiles(record))
            {
                Check("有活动会话时优先读运行中的目录", liveScope.Kind == FileScopeKind.LiveSession);
                Check("运行中写入的文件能立刻列出来",
                    liveScope.List("profile/Documents").Any(e => e.Name == "live.txt"));

                var liveExportDir = Path.Combine(root, "export-live");
                var liveExport = liveScope.CopyOut(new[] { "profile/Documents/live.txt" }, liveExportDir);
                Check("从运行中的沙盒导出到宿主机", liveExport.FileCount == 1 &&
                    File.ReadAllText(Path.Combine(liveExportDir, "live.txt")) == "运行中的文件");

                var hostFileAgain = Path.Combine(root, "sources", "放进运行中沙盒.txt");
                File.WriteAllText(hostFileAgain, "直接写会话目录");
                liveScope.CopyIn(hostFileAgain, "profile/Documents");
                Check("导入到运行中的沙盒（直接写会话目录）",
                    File.Exists(Path.Combine(liveSessionDir, "fs", "profile", "Documents", "放进运行中沙盒.txt")));
            }

            TryDeleteWithReadOnly(liveSessionDir);

            // ---- 10. 回写健壮性：无权限目录 / junction / 只读文件 / 默认跳过缓存 ----
            var robustSession = Path.Combine(store.SessionRoot, record.Id);
            // 模拟一次真实的会话目录：先把快照完整解包出来，再往里放“麻烦文件”
            SnapshotFile.Extract(record.SnapshotPath, robustSession);
            var robustFs = Path.Combine(robustSession, "fs");
            Directory.CreateDirectory(Path.Combine(robustFs, "profile", "Documents"));
            Directory.CreateDirectory(Path.Combine(robustFs, "profile", "AppData", "Local", "Microsoft", "Windows", "INetCache"));
            var readOnlyFile = Path.Combine(robustFs, "profile", "Documents", "只读文件.txt");
            File.WriteAllText(readOnlyFile, "只读内容");
            File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
            File.WriteAllText(Path.Combine(robustFs, "profile", "AppData", "Local", "Microsoft", "Windows", "INetCache", "cache.dat"), "缓存");

            var junctionMade = MakeJunction(Path.Combine(robustFs, "profile", "测试Junction"), @"C:\Windows\System32\config");
            var danglingMade = MakeJunction(Path.Combine(robustFs, "profile", "悬空Junction"), Path.Combine(root, "不存在的目标"));

            JsonStore.Save(Path.Combine(robustSession, "session.json"), new SessionMeta
            {
                SandboxId = record.Id,
                SandboxName = record.Name,
                SnapshotPath = record.SnapshotPath,
                AppId = "test",
                AppName = "测试客户端（已退出）",
                ProcessId = 999_999,
                ProcessName = "no-such-process"
            });

            Exception? recoverError = null;
            try { service.RecoverStaleSessions(); }
            catch (Exception ex) { recoverError = ex; }

            Check("有无权限目录/junction 时回写依然成功", recoverError == null);
            Check("只读文件也能写回快照", SnapshotHasEntry(record.SnapshotPath, "fs/profile/Documents/只读文件.txt"));
            Check("默认跳过系统缓存目录", !SnapshotHasEntry(record.SnapshotPath,
                "fs/profile/AppData/Local/Microsoft/Windows/INetCache/cache.dat"));
            Check("junction 没有被跟进快照", junctionMade && danglingMade &&
                !SnapshotFile.ReadIndex(record.SnapshotPath).Files.Keys.Any(k =>
                    k.Contains("测试Junction", StringComparison.OrdinalIgnoreCase) ||
                    k.Contains("悬空Junction", StringComparison.OrdinalIgnoreCase)));
            Check("会话目录会被清理（只读文件不影响）", !Directory.Exists(robustSession));
            Check("回写不会误删已有文件", SnapshotHasEntry(record.SnapshotPath, "fs/profile/AppData/Roaming/DemoClient/state.txt"));

            // ---- 11. 沙盒外目录探测（微信把文件存在沙盒外） + 宿主目录 ----
            var fakeWechatRoot = Path.Combine(root, "fake-wx");
            var fakeWechatFiles = Path.Combine(fakeWechatRoot, "xwechat_files");
            Directory.CreateDirectory(Path.Combine(fakeWechatFiles, "wxid_abc", "msg", "file", "2026-01"));
            File.WriteAllText(Path.Combine(fakeWechatFiles, "wxid_abc", "msg", "file", "2026-01", "收到的资料.pdf"), "pdf");

            using (var detectScope = SandboxFileSystem.Open(record))
            {
                var configFile = Path.Combine(root, "sources", "wxconfig.ini");
                File.WriteAllText(configFile, fakeWechatRoot);
                detectScope.CopyIn(configFile, "profile/AppData/Roaming/Tencent/xwechat/config");
                var detected = ExternalFolderDetector.Detect(detectScope);
                Check("能从沙盒里的微信配置找到沙盒外的文件目录",
                    detected.Any(d => string.Equals(d.Path, fakeWechatFiles, StringComparison.OrdinalIgnoreCase)));
                detectScope.Delete(new[] { "profile/AppData/Roaming/Tencent/xwechat/config/wxconfig.ini" });
            }

            using (var hostScope = new HostFolderScope(fakeWechatFiles))
            {
                Check("宿主目录能浏览", hostScope.List("").Any(e => e.Name == "wxid_abc" && e.IsDirectory) &&
                                       hostScope.DirectoryExists("wxid_abc/msg/file/2026-01"));
                var hostExportDir = Path.Combine(root, "export-host");
                var hostCopy = hostScope.CopyOut(new[] { "wxid_abc/msg/file/2026-01/收到的资料.pdf" }, hostExportDir);
                Check("能从宿主目录拷贝出来", hostCopy.FileCount == 1 &&
                                              File.Exists(Path.Combine(hostExportDir, "收到的资料.pdf")));
            }

            // ---- 14. VHD 差分盘（块级写时复制）：一份程序 + N 份差分盘 ----
            if (!VhdService.IsSupported())
            {
                Console.WriteLine("  [SKIP] 本机没有 Hyper-V/VHD 能力，跳过 VHD 差分盘测试");
            }
            else
            {
                var vhd = new VhdService(Path.Combine(root, "vhd"));
                var vhdSrc = Path.Combine(root, "vhd-src");
                Directory.CreateDirectory(vhdSrc);
                File.Copy(testClientExe, Path.Combine(vhdSrc, "TestClient.exe"), true);
                using (var big = File.Create(Path.Combine(vhdSrc, "big.bin"))) big.SetLength(32L * 1024 * 1024);

                VhdInfo? baseInfo = null;
                VhdInfo? child1 = null;
                VhdInfo? child2 = null;
                try
                {
                    baseInfo = vhd.CreateBase("t", vhdSrc, 512L * 1024 * 1024);
                    Check("基础盘已创建", File.Exists(baseInfo.Path));
                    Check("源目录被搬进基础盘（不额外占盘）", !Directory.Exists(vhdSrc));

                    child1 = vhd.CreateChild("c1", "t");
                    child2 = vhd.CreateChild("c2", "t");
                    Check("差分盘初始很小（< 20 MB）", new FileInfo(child1.Path).Length < 20L * 1024 * 1024);

                    var l1 = vhd.Mount("c1");
                    var l2 = vhd.Mount("c2");
                    Check("两个差分盘都挂上了（盘符不同）", l1 != l2);

                    var f1 = l1 + ":\\app\\big.bin";
                    var f2 = l2 + ":\\app\\big.bin";
                    Check("两个盘里都能看到同一份程序数据", File.Exists(f1) && File.Exists(f2));

                    using (var s1 = new FileStream(f1, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    using (var s2 = new FileStream(f2, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        s1.WriteByte(1);
                        s2.WriteByte(2);
                        Check("两个盘能同时独占写同一个文件（块级写时复制）", true);
                    }

                    vhd.Dismount("c1");
                    vhd.Dismount("c2");
                    child1 = child2 = null;

                    var lb = vhd.Mount("t");
                    using (var reader = new FileStream(lb + ":\\app\\big.bin", FileMode.Open, FileAccess.Read, FileShare.Read))
                        Check("基础盘里的原始文件没被改动", reader.ReadByte() == 0);
                    vhd.Dismount("t");
                }
                catch (Exception ex)
                {
                    Check("VHD 差分盘测试异常：" + ex.Message, false);
                }
                finally
                {
                    foreach (var item in new[] { child1, child2, baseInfo })
                    {
                        if (item == null) continue;
                        try { vhd.Dismount(item.Path); } catch { }
                    }
                }
            }

            // ---- 15. VHD 导入 + 启动时自动挂载 ----
            if (VhdService.IsSupported())
            {
                VhdService? vhdSvc = null;
                string? mountedVhd = null;
                try
                {
                    var vhdSandbox = store.Create("VHD 导入", Path.Combine(root, "vhdsbx", "v.sbx"));
                    var vhdSource = Path.Combine(root, "vhd-import-src");
                    Directory.CreateDirectory(vhdSource);
                    foreach (var file in Directory.GetFiles(testClientDir))
                        File.Copy(file, Path.Combine(vhdSource, Path.GetFileName(file)), true);

                    var location = Path.Combine(root, "vhd-location");
                    var (vhdApp, vhdInfo) = service.ImportAppToVhd(vhdSandbox, vhdSource, "VHD客户端",
                        location, 512L * 1024 * 1024, null);
                    vhdSvc = new VhdService(Path.Combine(location, "SandboxLauncherVhd"));

                    Check("VHD 导入：源目录被搬进虚拟盘", !Directory.Exists(vhdSource));
                    Check("VHD 导入：记录了 VHD 路径", vhdApp.VhdPath != null && File.Exists(vhdApp.VhdPath));
                    Check("VHD 导入：程序路径在挂载盘上", vhdApp.ExecutablePath.Contains(":") && vhdApp.ExecutablePath.Contains("app"));
                    Check("VHD 导入：记录了相对路径", !string.IsNullOrWhiteSpace(vhdApp.VhdRelativeExecutable));

                    // 卸载，验证「启动沙盒时自动挂载」
                    vhdSvc.Dismount(vhdInfo.Path);
                    var session15 = service.Start(vhdSandbox);
                    var exit15 = service.WaitForExit(session15, 60_000);
                    Check("VHD 沙盒启动时自动挂载并跑起来", exit15 &&
                        SnapshotHasEntry(vhdSandbox.SnapshotPath, "fs/profile/AppData/Roaming/DemoClient/state.txt"));
                    Check("沙盒退出后自动卸载虚拟盘", WaitUntil(() => !vhdSvc.IsMounted(vhdInfo.Path), 15000));
                    mountedVhd = vhdInfo.Path;
                }
                catch (Exception ex)
                {
                    Check("VHD 导入测试异常：" + ex.Message, false);
                }
                finally
                {
                    if (mountedVhd != null && vhdSvc != null)
                    {
                        try { vhdSvc.Dismount(mountedVhd); } catch { }
                    }
                }
            }

            // ---- 16. 单实例互斥体：自动释放已运行实例的锁 ----
            var mutexName = "SbxTestMutex-" + Guid.NewGuid().ToString("N")[..8];
            var mA = store.Create("互斥体A", Path.Combine(root, "mtx", "a.sbx"));
            var mAppA = service.ImportApp(mA, testClientDir);
            mAppA.Arguments = $"--mutex {mutexName} --sleep 20000";
            service.UpdateApp(mA, mAppA);
            var sA = service.Start(mA);
            Check("实例 A 起来了", WaitUntil(() => !sA.HasExited, 5000));
            Check("A 持有命名互斥体（探针看到 EXISTS）", WaitUntil(() => ProbeMutexExists(mutexName), 8000));

            var released = MutexUnlocker.Release(sA.ProcessId, new[] { mutexName });
            Check("手动释放 A 的互斥体句柄", released.Count > 0);
            Check("释放后探针能自己创建（CREATED）", !ProbeMutexExists(mutexName));

            service.Stop(mA.Id, 15000);
            WaitUntil(() => !HasProcesses("TestClient"), 15000);

            // 自动流程：A 再起一次，B 启动时应该自动释放 A 的锁
            var mA2 = store.Create("互斥体A2", Path.Combine(root, "mtx", "a2.sbx"));
            var mAppA2 = service.ImportApp(mA2, testClientDir);
            mAppA2.Arguments = $"--mutex {mutexName} --sleep 20000";
            service.UpdateApp(mA2, mAppA2);
            var sA2 = service.Start(mA2);
            WaitUntil(() => !sA2.HasExited, 5000);
            WaitUntil(() => ProbeMutexExists(mutexName), 8000);

            var mB = store.Create("互斥体B", Path.Combine(root, "mtx", "b.sbx"));
            var mAppB = service.ImportApp(mB, testClientDir);
            mAppB.Arguments = $"--mutex {mutexName} --sleep 3000";
            service.UpdateApp(mB, mAppB);
            var sB = service.Start(mB);
            Check("B 启动时自动释放了 A 的互斥体（日志有「已释放」）",
                WaitUntil(() => _logs.Any(l => l.Contains("已释放") && l.Contains("SbxTestMutex")), 15000));
            Check("B 正常跑完", service.WaitForExit(sB, 30_000));
            service.Stop(mA2.Id, 15000);
            WaitUntil(() => !HasProcesses("TestClient"), 15000);

            // ---- 12. 体检 ----
            var finalInfo = SnapshotFile.Inspect(imported.SnapshotPath);
            Check("快照统计里有文件数", finalInfo.Manifest.Stats.FileCount > 3);
            Console.WriteLine($"  最终快照 {finalInfo.FileSizeBytes / 1024.0:F1} KB / {finalInfo.Manifest.Stats.FileCount} 个文件");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("用例异常：" + ex);
        }
        finally
        {
            CleanupRegistry();
            TryDeleteWithReadOnly(root);
        }

        Console.WriteLine();
        if (_failed > 0)
        {
            Console.WriteLine("—— 运行日志 ——");
            foreach (var line in _logs) Console.WriteLine("   " + line);
        }

        Console.WriteLine($"通过 {_passed} 项，失败 {_failed} 项");
        return _failed == 0 ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateMutexW(IntPtr attributes, bool initialOwner, string name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>探针：这个互斥体是不是已经被别人占着（EXISTS）。</summary>
    private static bool ProbeMutexExists(string name)
    {
        var handle = CreateMutexW(IntPtr.Zero, false, name);
        var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        if (handle != IntPtr.Zero) CloseHandle(handle);
        return error == 183; // ERROR_ALREADY_EXISTS
    }

    private static void Check(string name, bool condition)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  [OK]   {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {name}");
        }
    }

    /// <summary>等某个条件成立（最多 timeoutMs），返回最终结果。</summary>
    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }

        return condition();
    }

    private static bool HasProcesses(string name)
    {
        try
        {
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>测试会在宿主机真实 %APPDATA% 里留下 DemoClient（模拟「宿主上也在跑同一个程序」），收尾删掉。</summary>
    private static void CleanupRealDemoClient()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DemoClient");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch
        {
            // 删不掉就算了
        }
    }

    private static string? ReadStateFromSnapshot(string snapshotPath)
    {
        using var fs = File.OpenRead(snapshotPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry("fs/profile/AppData/Roaming/DemoClient/state.txt");
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static int ReadRegistryRunsFromSnapshot(string snapshotPath)
    {
        using var fs = File.OpenRead(snapshotPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry("registry/software_sandboxlauncherdemo.json");
        if (entry == null) return -1;
        using var reader = new StreamReader(entry.Open());
        var text = reader.ReadToEnd();
        var node = System.Text.Json.JsonSerializer.Deserialize<RegNode>(text, JsonStore.Options);
        if (node == null) return -2;
        foreach (var (name, value) in node.Values)
        {
            if (string.Equals(name, "Runs", StringComparison.OrdinalIgnoreCase))
                return (int)(value.Number ?? -3);
        }

        return -4;
    }

    private static int CountEntries(string snapshotPath, string prefix)
    {
        using var fs = File.OpenRead(snapshotPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        return zip.Entries.Count(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith('/'));
    }

    private static bool SnapshotHasEntry(string snapshotPath, string entryName)
    {
        using var fs = File.OpenRead(snapshotPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        return zip.GetEntry(entryName) != null;
    }

    private static bool MakeJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process == null) return false;
            process.WaitForExit(8000);
            return Directory.Exists(link) || process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDeleteWithReadOnly(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* ignore */ }
            }

            Directory.Delete(directory, true);
        }
        catch (Exception)
        {
            // 清理失败不影响用例结果
        }
    }

    private static void CleanupRegistry()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(DemoKey, throwOnMissingSubKey: false); } catch { }
    }
}
