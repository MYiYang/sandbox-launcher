using System.Diagnostics;
using SandboxLauncher.Core.Model;
using SandboxLauncher.Core.Snapshot;

namespace SandboxLauncher.Core.Runtime;

public sealed class SessionMeta
{
    public string SandboxId { get; set; } = "";
    public string SandboxName { get; set; } = "";
    public string SnapshotPath { get; set; } = "";
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>一个沙盒的会话状态（不启动/不改变任何东西，只用于选择文件来源）。</summary>
public sealed record SandboxActivity(bool RunningHere, bool ProcessAlive, string? SessionDir, SessionMeta? Meta)
{
    public bool HasSession => !string.IsNullOrEmpty(SessionDir) && Directory.Exists(Path.Combine(SessionDir!, "fs"));
    public bool Running => RunningHere || ProcessAlive;

    public string StatusText => RunningHere ? "运行中（实时）"
        : ProcessAlive ? "运行中（另一个启动器进程托管）"
        : HasSession ? "有未回写的会话目录"
        : "未运行";
}

/// <summary>一个正在运行的沙盒会话。</summary>
public sealed class RunningSession
{
    public required SandboxRecord Record { get; init; }
    public required SandboxAppRecord App { get; init; }
    public required string SessionDir { get; init; }
    public required string SnapshotPath { get; init; }
    public required Process Process { get; init; }
    public required string LogFile { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public int ProcessId => Process.Id;
    public string AppName => App.Name;

    internal FileStream? RegistryLock { get; set; }
    internal int Finalized;
    internal int StopRequested;
    internal readonly ManualResetEventSlim ExitEvent = new(false);

    /// <summary>整棵进程树（作业对象）：启动器退出后子进程还在跑时靠它继续等。</summary>
    public ProcessTree? Tree { get; init; }

    /// <summary>启动时发现「同一个程序已经在别处运行」的提示（用于解释秒退）。</summary>
    public string? StartWarning { get; init; }

    /// <summary>退出后给用户的解释（例如「1 秒就退出，可能是单实例锁」）。</summary>
    public string? ExitHint { get; internal set; }

    public bool HasExited
    {
        get
        {
            try { return Process.HasExited; }
            catch { return true; }
        }
    }

    /// <summary>整棵进程树是否还活着（启动器自己退出了，但子进程还在跑也算活着）。</summary>
    public bool IsTreeAlive
    {
        get
        {
            if (!HasExited) return true;
            return Tree is { Valid: true } tree && tree.ActiveProcesses > 0;
        }
    }
}

/// <summary>
/// 沙盒引擎：挂载快照 → 建立隔离环境 → 启动客户端 → 退出后增量回写快照。
/// 所有沙盒共用同一个 CatalogStore（catalog.json 记录每个沙盒的快照路径等）。
/// </summary>
public sealed class SandboxService : IDisposable
{
    private const string RegistryLockFile = "registry-isolation.lock";

    public CatalogStore Store { get; }
    public event Action<string>? Log;
    public event Action<RunningSession>? SessionStarted;
    public event Action<RunningSession, int, SnapshotManifest>? SessionStopped;

    private readonly Dictionary<string, RunningSession> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public SandboxService(CatalogStore store, bool recoverStaleSessions = true)
    {
        Store = store;
        if (recoverStaleSessions) RecoverStaleSessions();
    }

    public IReadOnlyList<RunningSession> Running
    {
        get
        {
            lock (_gate) return _running.Values.ToList();
        }
    }

    public bool IsRunning(string sandboxId)
    {
        lock (_gate) return _running.ContainsKey(sandboxId);
    }

    public RunningSession? GetRunning(string sandboxId)
    {
        lock (_gate) return _running.TryGetValue(sandboxId, out var s) ? s : null;
    }

    /// <summary>
    /// 沙盒当前的会话状态：本进程在跑 / 别的启动器进程在跑 / 磁盘上留着没回写的会话目录 / 没运行。
    /// 「浏览沙盒文件」决定读哪个目录就靠它。
    /// </summary>
    public SandboxActivity GetActivity(SandboxRecord record)
    {
        var running = GetRunning(record.Id);
        if (running != null)
            return new SandboxActivity(true, true, running.SessionDir,
                JsonStore.Load<SessionMeta>(Path.Combine(running.SessionDir, "session.json")));

        var dir = Path.Combine(Store.SessionRoot, record.Id);
        if (!Directory.Exists(dir)) return new SandboxActivity(false, false, null, null);

        var meta = JsonStore.Load<SessionMeta>(Path.Combine(dir, "session.json"));
        var alive = meta != null && IsProcessAlive(meta.ProcessId, meta.ProcessName);
        return new SandboxActivity(false, alive, dir, meta);
    }

    /// <summary>打开这个沙盒的文件作用域：运行中（或有未回写会话）读会话目录，否则读 .sbx 快照。</summary>
    public Files.SandboxFileSystem OpenFiles(SandboxRecord record)
    {
        var activity = GetActivity(record);
        return Files.SandboxFileSystem.Open(record, activity.HasSession ? activity.SessionDir : null);
    }

    // ---------- 启动 / 停止 ----------

    public RunningSession Start(SandboxRecord record, SandboxAppRecord? app = null, string? extraArgs = null)
    {
        RunningSession session;
        lock (_gate)
        {
            if (_running.ContainsKey(record.Id))
                throw new InvalidOperationException($"沙盒「{record.Name}」已经在运行了");

            if (string.IsNullOrWhiteSpace(record.SnapshotPath))
                throw new InvalidOperationException("该沙盒还没有配置快照文件路径");

            if (!File.Exists(record.SnapshotPath))
            {
                SnapshotFile.Create(record.SnapshotPath, new SnapshotManifest
                {
                    SandboxId = record.Id,
                    Name = record.Name,
                    Settings = record.Settings
                });
                AppendLog(record, $"快照不存在，已新建空快照：{record.SnapshotPath}");
            }

            var sessionDir = Path.Combine(Store.SessionRoot, record.Id);
            if (Directory.Exists(sessionDir))
            {
                var staleMeta = JsonStore.Load<SessionMeta>(Path.Combine(sessionDir, "session.json"));
                if (staleMeta != null && IsProcessAlive(staleMeta.ProcessId, staleMeta.ProcessName))
                    throw new InvalidOperationException(
                        $"这个沙盒的客户端似乎还在运行（{staleMeta.AppName}，PID {staleMeta.ProcessId}）。请先停止它（sl stop / 界面里的「停止」）。");

                TryDeleteDirectory(sessionDir);
                if (Directory.Exists(sessionDir))
                {
                    // 还删不掉：常见于刚被强杀的客户端/子进程还捏着文件句柄（CefHost 之类）。
                    // 不要因此把沙盒卡死 —— 挪到一边继续挂载，残留目录稍后手动删。
                    var parked = sessionDir + ".stale-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    try
                    {
                        Directory.Move(sessionDir, parked);
                        AppendLog(record, $"上一次的会话目录里有文件被占用，已挪到一边继续启动：{parked}");
                    }
                    catch (Exception moveError)
                    {
                        throw new InvalidOperationException(
                            $"上一次的会话目录被占用，无法挂载：{sessionDir}\r\n" +
                            "可能有客户端进程（例如游戏的 CefHost/更新器）还开着。把那些进程结束掉，或等几秒再试。" +
                            $"\r\n（{moveError.Message}）");
                    }
                }
            }

            var manifest = SnapshotFile.Extract(record.SnapshotPath, sessionDir, m => AppendLog(record, m));

            // 快照是客户端的权威来源：把快照里的客户端列表同步回目录。
            // 注意：设置项以目录（record.Settings）为准，退出时再写回快照，
            // 否则用户在界面里刚改的隔离设置会被快照里的旧设置覆盖。
            if (manifest.Apps.Count > 0)
            {
                record.Apps = manifest.Apps;
            }

            var targetApp = app
                            ?? record.Apps.FirstOrDefault(a => a.Id == record.Settings.DefaultAppId)
                            ?? record.Apps.FirstOrDefault();
            if (targetApp == null)
                throw new InvalidOperationException("这个沙盒还没导入客户端程序，请先「导入客户端」");

            var fsRoot = Path.Combine(sessionDir, "fs");

            // 同一个程序的另一个实例正在跑？先把它的单实例互斥体句柄放掉，不然新实例会秒退
            if (targetApp.AutoReleaseMutex) ReleaseSiblingMutexes(record, targetApp, exePathPreview(targetApp));

            // 程序在 VHD（基础盘/差分盘）里：启动前先挂载，再把程序路径换算成当前盘符
            if (!string.IsNullOrWhiteSpace(targetApp.VhdPath))
            {
                var vhd = new VhdService();
                var letter = vhd.EnsureMounted(targetApp.VhdPath!, targetApp.VhdDriveLetter);
                var relative = string.IsNullOrWhiteSpace(targetApp.VhdRelativeExecutable)
                    ? targetApp.ExecutablePath
                    : targetApp.VhdRelativeExecutable!;
                targetApp.ExecutablePath = letter + ":\\" + relative.TrimStart('\\');
                targetApp.WorkingDirectory = Path.GetDirectoryName(targetApp.ExecutablePath)!;
                targetApp.SourceMode = AppSourceMode.Reference;
                AppendLog(record, $"VHD 已挂载：{targetApp.VhdPath} → {letter}:（程序：{targetApp.ExecutablePath}）");
            }

            var (exePath, workDir) = ResolveLaunchPaths(fsRoot, targetApp);
            var startWarning = DescribeRunningInstances(record, targetApp, exePath);

            var keys = PrepareRegistryIsolation(record, sessionDir);
            FileStream? registryLock = keys.Count > 0 ? AcquireRegistryLock(record) : null;

            try
            {
                var env = EnvBuilder.Build(sessionDir, record.Settings);
                Directory.CreateDirectory(workDir);

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = string.Join(' ', new[] { targetApp.Arguments, extraArgs }
                        .Where(s => !string.IsNullOrWhiteSpace(s))),
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };
                foreach (var (k, v) in env) psi.Environment[k] = v;
                psi.Environment["PATH"] = Path.GetDirectoryName(exePath) + ";" + Environment.GetEnvironmentVariable("PATH");

                var logFile = AppPaths.NewLogFile($"run-{record.Id}");

                // 挂起创建 → 放进作业对象 → 放行：启动器拉起来的子进程一定在 job 里，
                // 这样「启动器先退出、真正的客户端还在跑」时不会把会话提前收尾。
                Process process;
                ProcessTree? tree;
                try
                {
                    var launched = ProcessTree.Launch(psi, $"{record.Name}-{targetApp.Name}");
                    process = launched.Process;
                    tree = launched.Tree;
                }
                catch (Exception launchError)
                {
                    AppendLog(record, "进程树跟踪启动失败，退回普通启动：" + launchError.Message);
                    process = Process.Start(psi) ?? throw new InvalidOperationException("客户端进程启动失败");
                    process.EnableRaisingEvents = true;
                    tree = ProcessTree.Attach(process, $"{record.Name}-{targetApp.Name}");
                }
                var meta = new SessionMeta
                {
                    SandboxId = record.Id,
                    SandboxName = record.Name,
                    SnapshotPath = record.SnapshotPath,
                    AppId = targetApp.Id,
                    AppName = targetApp.Name,
                    ProcessId = process.Id,
                    ProcessName = process.ProcessName,
                    StartedAt = DateTimeOffset.Now
                };
                JsonStore.Save(Path.Combine(sessionDir, "session.json"), meta);

                session = new RunningSession
                {
                    Record = record,
                    App = targetApp,
                    SessionDir = sessionDir,
                    SnapshotPath = record.SnapshotPath,
                    Process = process,
                    LogFile = logFile,
                    RegistryLock = registryLock,
                    Tree = tree,
                    StartWarning = startWarning
                };

                _running[record.Id] = session;
                record.LastStartedAt = DateTimeOffset.Now;
                record.LastError = null;
                Store.Save();

                AppendLog(record, $"启动客户端：{targetApp.Name}（PID {process.Id}，参数：{psi.Arguments}）");
                AppendLog(record, tree != null
                    ? "进程树跟踪已开启（启动器退出后，子进程还在跑时沙盒会继续等）"
                    : "进程树跟踪不可用，只等启动的这个进程（若它很快退出，沙盒会立刻收尾）");
                if (startWarning != null) AppendLog(record, "注意：" + startWarning);
                if (targetApp.IsReference)
                    AppendLog(record, $"引用模式：程序文件不在沙盒里，直接运行 {exePath}；" +
                                      "程序写进自己安装目录的改动不会被隔离（用户数据已重定向进沙盒）");
                AppendLog(record, "环境变量：" + Environment.NewLine + EnvBuilder.Describe(env));
            }
            catch
            {
                registryLock?.Dispose();
                throw;
            }
        }

        SessionStarted?.Invoke(session);
        _ = Task.Run(() => WatchAsync(session));
        return session;
    }

    public bool WaitForExit(RunningSession session, int timeoutMs = Timeout.Infinite)
    {
        return session.ExitEvent.Wait(timeoutMs);
    }

    public bool WaitForExit(string sandboxId, int timeoutMs = Timeout.Infinite)
    {
        var session = GetRunning(sandboxId);
        return session != null && session.ExitEvent.Wait(timeoutMs);
    }

    public void Stop(string sandboxId, int waitMs = 20000)
    {
        var session = GetRunning(sandboxId);
        if (session == null) return;

        AppendLog(session.Record, "正在停止沙盒…");
        Interlocked.Exchange(ref session.StopRequested, 1);

        try
        {
            // 先杀整棵树：启动器可能早就退出了，真正的客户端在子进程里
            session.Tree?.KillAll();
        }
        catch (Exception ex)
        {
            AppendLog(session.Record, "结束进程树失败：" + ex.Message);
        }

        try
        {
            if (!session.HasExited) session.Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppendLog(session.Record, "结束进程失败（可能已退出）：" + ex.Message);
        }

        session.ExitEvent.Wait(waitMs);
    }

    public void StopAll()
    {
        foreach (var session in Running) Stop(session.Record.Id);
    }

    private async Task WatchAsync(RunningSession session)
    {
        try
        {
            await session.Process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // 进程已被杀
        }

        var rootExitCode = 0;
        try { rootExitCode = session.Process.ExitCode; } catch { }

        // 启动器退出 ≠ 客户端退出：很多游戏/客户端先起一个启动器，再把真正的程序作为子进程拉起来。
        // 这时会话必须继续挂着（profile 一删，刚起来的客户端就崩），等整棵树都结束再回写。
        if (session.Tree is { Valid: true } tree)
        {
            var active = tree.ActiveProcesses;
            if (active > 0)
            {
                AppendLog(session.Record,
                    $"启动器已退出（退出码 {rootExitCode}），但还有 {active} 个子进程在跑 —— 继续等它们结束再回写");
                while (Volatile.Read(ref session.StopRequested) == 0 && tree.ActiveProcesses > 0)
                    await Task.Delay(500).ConfigureAwait(false);
            }
        }

        Finalize(session, rootExitCode);
    }

    private void Finalize(RunningSession session, int exitCode)
    {
        if (Interlocked.Exchange(ref session.Finalized, 1) != 0) return;

        var record = session.Record;
        try
        {
            FinishRegistryIsolation(session);

            var manifest = File.Exists(session.SnapshotPath)
                ? SnapshotFile.ReadManifest(session.SnapshotPath)
                : new SnapshotManifest();

            manifest.SandboxId = record.Id;
            manifest.Name = record.Name;
            manifest.Apps = record.Apps;
            manifest.Settings = record.Settings;

            manifest = SnapshotFile.SyncFrom(session.SnapshotPath, session.SessionDir, manifest, m => AppendLog(record, m));

            record.Apps = manifest.Apps;
            record.LastStoppedAt = DateTimeOffset.Now;
            record.LastError = null;

            session.ExitHint = BuildExitHint(session, exitCode);
            if (session.ExitHint != null) AppendLog(record, session.ExitHint);

            if (!record.Settings.KeepSessionDirectory) TryDeleteDirectory(session.SessionDir);
            DismountUnusedVhds(record);
            AppendLog(record, $"沙盒已退出（退出码 {exitCode}），改动已写回快照");
        }
        catch (Exception ex)
        {
            record.LastError = ex.Message;
            AppendLog(record, "退出回写失败：" + ex);
            // 失败时保留会话目录，方便下次恢复
        }
        finally
        {
            record.LastStoppedAt = DateTimeOffset.Now;
            lock (_gate) _running.Remove(record.Id);
            try { session.Tree?.Dispose(); } catch { }
            try { Store.Save(); } catch { }
            try { session.Process.Dispose(); } catch { }
            session.ExitEvent.Set();
            SessionStopped?.Invoke(session, exitCode, new SnapshotManifest());
        }
    }

    /// <summary>
    /// 客户端「秒退」时给一句人话解释：最常见的是单实例锁（同一个程序已经在别的沙盒/宿主上跑着），
    /// 其次是启动器把界面交给了子进程、或者需要登录/管理员权限。
    /// </summary>
    private static string? BuildExitHint(RunningSession session, int exitCode)
    {
        var seconds = (DateTimeOffset.Now - session.StartedAt).TotalSeconds;
        if (seconds > 8) return null;

        var head = $"「{session.AppName}」启动后 {seconds:0.#} 秒就退出了（退出码 {exitCode}）";
        if (session.StartWarning != null)
            return head + "。" + session.StartWarning + " 这类启动器/客户端一般一次只能运行一个实例：先停掉另一个再启动。";

        if (exitCode == 0 && seconds < 4)
            return head + "。常见原因：① 同一个程序已经在别的沙盒或宿主上运行（单实例锁）；" +
                   "② 启动器把真正的客户端交给了子进程，自己先退出；③ 需要登录、管理员权限或图形环境。";

        return null;
    }

    // ---------- 注册表隔离 ----------

    private List<string> PrepareRegistryIsolation(SandboxRecord record, string sessionDir)
    {
        var keys = record.Settings.IsolateRegistryKeys
            .Select(RegistrySandbox.NormalizeKey)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (keys.Count == 0) return keys;

        var before = new Dictionary<string, RegNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys) before[key] = RegistrySandbox.Export(key);
        JsonStore.Save(Path.Combine(sessionDir, "registry.before.json"), before);

        var regDir = Path.Combine(sessionDir, "reg");
        Directory.CreateDirectory(regDir);

        foreach (var key in keys)
        {
            RegistrySandbox.DeleteTree(key);
            var saved = RegistrySandbox.LoadFromFile(Path.Combine(regDir, RegFile(key)));
            if (saved != null && !RegistrySandbox.IsEmpty(saved)) RegistrySandbox.Import(key, saved);
        }

        File.WriteAllText(Path.Combine(sessionDir, "registry.lock.json"),
            JsonStore.ToJson(new { sandboxId = record.Id, name = record.Name, keys, startedAt = DateTimeOffset.Now }));
        AppendLog(record, $"已切换注册表隔离：{string.Join("、", keys)}");
        return keys;
    }

    private void FinishRegistryIsolation(RunningSession session)
    {
        var keys = session.Record.Settings.IsolateRegistryKeys
            .Select(RegistrySandbox.NormalizeKey)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (keys.Count == 0) return;

        var regDir = Path.Combine(session.SessionDir, "reg");
        Directory.CreateDirectory(regDir);
        foreach (var key in keys)
            RegistrySandbox.SaveToFile(Path.Combine(regDir, RegFile(key)), RegistrySandbox.Export(key));

        var before = JsonStore.Load<Dictionary<string, RegNode>>(Path.Combine(session.SessionDir, "registry.before.json"));
        foreach (var key in keys)
        {
            RegistrySandbox.DeleteTree(key);
            if (before != null && before.TryGetValue(key, out var node) && !RegistrySandbox.IsEmpty(node))
                RegistrySandbox.Import(key, node);
        }

        session.RegistryLock?.Dispose();
        session.RegistryLock = null;
        TryDeleteFile(Path.Combine(session.SessionDir, "registry.lock.json"));
        AppendLog(session.Record, "注册表已还原");
    }

    private FileStream AcquireRegistryLock(SandboxRecord record)
    {
        var path = Path.Combine(Store.Root, RegistryLockFile);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InvalidOperationException(
                "另一个沙盒正在使用注册表隔离，同一时刻只能有一个沙盒隔离注册表。" +
                "请先关闭那个沙盒，或把本沙盒设置里的「隔离注册表键」清空。");
        }
    }

    private static string RegFile(string key) =>
        key.Replace('\\', '_').Replace('/', '_').ToLowerInvariant() + ".json";

    // ---------- 客户端导入 / 快照管理 ----------

    public SandboxAppRecord ImportApp(SandboxRecord record, string sourcePath, string? appName = null,
        Action<string>? progress = null, AppSourceMode mode = AppSourceMode.Copy)
    {
        if (IsRunning(record.Id)) throw new InvalidOperationException("请先停止这个沙盒，再导入客户端");

        var plan = AppImporter.ResolveSource(sourcePath, mode);
        var display = string.IsNullOrWhiteSpace(appName) ? Path.GetFileNameWithoutExtension(plan.ExeFullPath) : appName.Trim();
        var slug = AppImporter.MakeSlug(display);

        if (plan.Mode == AppSourceMode.Reference)
            return ImportReferenceApp(record, sourcePath, plan, display, progress);

        var relDir = $"apps/{slug}";
        var relExe = relDir + "/" + (plan.SingleFileOnly
            ? Path.GetFileName(plan.ExeFullPath)
            : Path.GetRelativePath(plan.RootDirectory, plan.ExeFullPath).Replace('\\', '/'));

        progress?.Invoke(plan.SingleFileOnly
            ? $"只导入单个程序文件：{plan.ExeFullPath}"
            : $"正在统计文件：{plan.RootDirectory}");
        var (count, bytes) = plan.SingleFileOnly
            ? (1, new FileInfo(plan.ExeFullPath).Length)
            : AppImporter.Measure(plan.RootDirectory);
        progress?.Invoke(plan.SingleFileOnly
            ? "开始写入快照…"
            : $"共 {count} 个文件，{bytes / 1024.0 / 1024.0:F1} MB，开始打包…");

        var app = new SandboxAppRecord
        {
            Name = display,
            ExecutablePath = relExe,
            WorkingDirectory = relDir,
            SourcePath = Path.GetFullPath(sourcePath),
            IconBase64 = AppImporter.ExtractIconBase64(plan.ExeFullPath),
            FileCount = count,
            SizeBytes = bytes
        };

        SnapshotFile.Mutate(
            record.SnapshotPath,
            zip =>
            {
                if (plan.SingleFileOnly)
                    SnapshotFile.AddFile(zip, SnapshotFile.FsPrefix + relExe, plan.ExeFullPath);
                else
                    SnapshotFile.AddDirectory(zip, SnapshotFile.FsPrefix + relDir + "/", plan.RootDirectory, progress);
            },
            manifest =>
            {
                manifest.Apps.Add(app);
                manifest.Name = record.Name;
                manifest.SandboxId = record.Id;
                manifest.Settings = record.Settings;
                manifest.Stats = new SnapshotStats { FileCount = count, TotalBytes = bytes, LastSyncAt = DateTimeOffset.Now };
            },
            m => progress?.Invoke(m));

        record.Apps.Add(app);
        record.Settings.DefaultAppId ??= app.Id;
        Store.Save();
        progress?.Invoke($"客户端「{display}」已导入沙盒「{record.Name}」");
        return app;
    }

    /// <summary>
    /// 引用模式导入：不把任何程序文件写进快照，只记下源 exe 的绝对路径。
    /// 几十 GB 的软件也能秒级「导入」，快照体积不增长；启动时直接在源安装目录里跑，
    /// 只把用户数据（USERPROFILE/APPDATA/LOCALAPPDATA/TEMP + 可选注册表键）搬进沙盒。
    /// </summary>
    private SandboxAppRecord ImportReferenceApp(SandboxRecord record, string sourcePath, SourcePlan plan,
        string display, Action<string>? progress)
    {
        progress?.Invoke($"引用安装目录（不复制文件）：{plan.RootDirectory}");
        var (probeBytes, probeFiles, truncated) = AppImporter.Probe(plan.RootDirectory);
        if (probeBytes > 0)
            progress?.Invoke($"源目录约 {(truncated ? "≥" : "")}{FormatSize(probeBytes)}、{probeFiles} 个文件，" +
                             "这些文件不会打进快照");

        var app = new SandboxAppRecord
        {
            Name = display,
            ExecutablePath = plan.ExeFullPath,
            WorkingDirectory = plan.RootDirectory,
            SourcePath = plan.ExeFullPath,
            SourceMode = AppSourceMode.Reference,
            IconBase64 = AppImporter.ExtractIconBase64(plan.ExeFullPath),
            FileCount = 0,   // 快照里没有程序文件
            SizeBytes = 0
        };

        SnapshotFile.Mutate(record.SnapshotPath, null, manifest =>
        {
            manifest.Apps.Add(app);
            manifest.Name = record.Name;
            manifest.SandboxId = record.Id;
            manifest.Settings = record.Settings;
        }, m => progress?.Invoke(m));

        record.Apps.Add(app);
        record.Settings.DefaultAppId ??= app.Id;
        Store.Save();
        progress?.Invoke($"客户端「{display}」已导入沙盒「{record.Name}」" +
                         $"（引用模式：启动时直接运行 {plan.ExeFullPath}，程序文件不复制）");
        return app;
    }

    /// <summary>沙盒退出后：把它的客户端用的 VHD 卸载掉（别的还在跑的沙盒用同一个盘就留着）。</summary>
    private void DismountUnusedVhds(SandboxRecord record)
    {
        try
        {
            var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var other in Store.Catalog.Sandboxes)
            {
                if (other.Id == record.Id) continue;
                var activity = GetActivity(other);
                if (!activity.ProcessAlive) continue;
                foreach (var a in other.Apps)
                {
                    if (string.IsNullOrWhiteSpace(a.VhdPath)) continue;
                    inUse.Add(a.VhdPath!);
                }
            }

            foreach (var r in Running)
            {
                if (r.Record.Id == record.Id) continue; // 自己这条会话正在收尾，不算占用
                foreach (var a in r.Record.Apps)
                {
                    if (string.IsNullOrWhiteSpace(a.VhdPath)) continue;
                    inUse.Add(a.VhdPath!);
                }
            }

            foreach (var app in record.Apps)
            {
                if (string.IsNullOrWhiteSpace(app.VhdPath) || inUse.Contains(app.VhdPath!)) continue;
                try
                {
                    var vhd = new VhdService();
                    vhd.Dismount(app.VhdPath!);
                    AppendLog(record, $"已卸载虚拟磁盘：{app.VhdPath}");
                }
                catch (Exception ex)
                {
                    AppendLog(record, "卸载虚拟磁盘失败（可手工 sl vhd dismount）：" + ex.Message);
                }
            }
        }
        catch
        {
            // 卸载失败不影响退出
        }
    }

    private static string FormatSize(long bytes)
    {
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

    /// <summary>
    /// 把一个程序目录做成「VHD 基础盘」再导入沙盒：
    /// 建 VHDX（把程序目录移进去，不额外占盘）→ 按用户指定的盘符挂载 → 登记为引用模式客户端（记 VHD 路径）。
    /// 用户自己选位置（哪个盘空间够/是不是固态）和盘符。
    /// </summary>
    public (SandboxAppRecord App, VhdInfo Vhd) ImportAppToVhd(SandboxRecord record, string sourceDirectory,
        string? appName, string vhdLocationDirectory, long sizeBytes, char? driveLetter,
        Action<string>? progress = null)
    {
        if (IsRunning(record.Id)) throw new InvalidOperationException("请先停止这个沙盒，再导入客户端");
        if (!Directory.Exists(sourceDirectory)) throw new DirectoryNotFoundException(sourceDirectory);

        var vhdRoot = string.IsNullOrWhiteSpace(vhdLocationDirectory)
            ? Path.Combine(AppPaths.Root, "vhd")
            : Path.Combine(vhdLocationDirectory, "SandboxLauncherVhd");
        var vhd = new VhdService(vhdRoot);

        var display = string.IsNullOrWhiteSpace(appName) ? new DirectoryInfo(sourceDirectory).Name : appName.Trim();
        progress?.Invoke($"正在创建虚拟磁盘（把程序搬进去，不额外占盘）：{vhdRoot}");
        var baseInfo = vhd.CreateBase(AppImporter.MakeSlug(display), sourceDirectory, sizeBytes, driveLetter);

        progress?.Invoke($"挂载虚拟磁盘：{baseInfo.Path}");
        var letter = vhd.EnsureMounted(baseInfo.Path, driveLetter);

        var mountedRoot = letter + ":\\app";
        var plan = AppImporter.ResolveSource(mountedRoot, AppSourceMode.Reference);
        var relative = Path.GetRelativePath(letter + ":\\", plan.ExeFullPath).Replace('/', '\\');

        var app = new SandboxAppRecord
        {
            Name = display,
            ExecutablePath = plan.ExeFullPath,
            WorkingDirectory = Path.GetDirectoryName(plan.ExeFullPath)!,
            SourcePath = plan.ExeFullPath,
            SourceMode = AppSourceMode.Reference,
            VhdPath = baseInfo.Path,
            VhdDriveLetter = letter,
            VhdRelativeExecutable = relative,
            IconBase64 = AppImporter.ExtractIconBase64(plan.ExeFullPath),
            FileCount = 0,
            SizeBytes = 0
        };

        SnapshotFile.Mutate(record.SnapshotPath, null, manifest =>
        {
            manifest.Apps.Add(app);
            manifest.Name = record.Name;
            manifest.SandboxId = record.Id;
            manifest.Settings = record.Settings;
        }, m => progress?.Invoke(m));

        record.Apps.Add(app);
        record.Settings.DefaultAppId ??= app.Id;
        Store.Save();
        progress?.Invoke($"客户端「{display}」已导入（VHD：{baseInfo.Path}，盘符 {letter}:）");
        return (app, baseInfo);
    }

    /// <summary>
    /// 更新客户端记录（例如启动参数）并同时写进快照清单。
    /// 注意：Start 时客户端列表以快照清单为准，只改 catalog 的话下次启动会被清单里的旧值覆盖。
    /// </summary>
    public void UpdateApp(SandboxRecord record, SandboxAppRecord app)
    {
        if (IsRunning(record.Id)) throw new InvalidOperationException("请先停止这个沙盒，再修改客户端设置");

        SnapshotFile.Mutate(record.SnapshotPath, null, manifest =>
        {
            var index = manifest.Apps.FindIndex(a => a.Id == app.Id);
            if (index >= 0) manifest.Apps[index] = app;
            else manifest.Apps.Add(app);
            manifest.Name = record.Name;
            manifest.SandboxId = record.Id;
            manifest.Settings = record.Settings;
        });

        var localIndex = record.Apps.FindIndex(a => a.Id == app.Id);
        if (localIndex >= 0) record.Apps[localIndex] = app;
        else record.Apps.Add(app);
        Store.Save();
    }

    public void RemoveApp(SandboxRecord record, string appId)
    {
        if (IsRunning(record.Id)) throw new InvalidOperationException("请先停止这个沙盒，再移除客户端");
        var app = record.Apps.FirstOrDefault(a => a.Id == appId);
        if (app == null) return;

        SnapshotFile.Mutate(
            record.SnapshotPath,
            zip =>
            {
                var prefix = SnapshotFile.FsPrefix + app.WorkingDirectory.Trim('/') + "/";
                foreach (var entry in zip.Entries
                             .Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                             .ToList())
                    entry.Delete();
            },
            manifest =>
            {
                manifest.Apps.RemoveAll(a => a.Id == appId);
                manifest.Name = record.Name;
                manifest.SandboxId = record.Id;
            });

        record.Apps.RemoveAll(a => a.Id == appId);
        if (record.Settings.DefaultAppId == appId) record.Settings.DefaultAppId = record.Apps.FirstOrDefault()?.Id;
        Store.Save();
    }

    /// <summary>登记一个已存在的快照文件（导入/读取）。</summary>
    public SandboxRecord RegisterSnapshot(string snapshotPath, string? nameOverride = null)
    {
        snapshotPath = Path.GetFullPath(snapshotPath);
        var info = SnapshotFile.Inspect(snapshotPath);

        var existing = Store.Catalog.Sandboxes.FirstOrDefault(s =>
            string.Equals(Path.GetFullPath(s.SnapshotPath), snapshotPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        var record = new SandboxRecord
        {
            Name = string.IsNullOrWhiteSpace(nameOverride) ? info.Manifest.Name : nameOverride.Trim(),
            SnapshotPath = snapshotPath,
            Apps = info.Manifest.Apps,
            Settings = info.Manifest.Settings,
            CreatedAt = info.Manifest.CreatedAt
        };
        if (string.IsNullOrWhiteSpace(record.Name)) record.Name = Path.GetFileNameWithoutExtension(snapshotPath);

        Store.Catalog.Sandboxes.Add(record);
        Store.Save();
        return record;
    }

    /// <summary>把沙盒的快照文件挪到别的位置（可跨盘）。</summary>
    public void RelocateSnapshot(SandboxRecord record, string newPath, bool moveFile = true)
    {
        if (IsRunning(record.Id)) throw new InvalidOperationException("请先停止这个沙盒，再移动快照文件");

        newPath = Path.GetFullPath(newPath);
        if (!newPath.EndsWith(SnapshotFile.Extension, StringComparison.OrdinalIgnoreCase))
            newPath += SnapshotFile.Extension;

        if (string.Equals(newPath, Path.GetFullPath(record.SnapshotPath), StringComparison.OrdinalIgnoreCase))
            return;

        var dir = Path.GetDirectoryName(newPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(newPath)) throw new IOException($"目标文件已存在：{newPath}");

        if (moveFile && File.Exists(record.SnapshotPath)) File.Move(record.SnapshotPath, newPath);
        record.SnapshotPath = newPath;
        Store.Save();
    }

    public string ExportSnapshot(SandboxRecord record, string targetPath)
    {
        if (IsRunning(record.Id)) throw new InvalidOperationException("请先停止这个沙盒，再导出快照");
        targetPath = Path.GetFullPath(targetPath);
        if (!targetPath.EndsWith(SnapshotFile.Extension, StringComparison.OrdinalIgnoreCase))
            targetPath += SnapshotFile.Extension;
        SnapshotFile.CopyFile(record.SnapshotPath, targetPath);
        return targetPath;
    }

    // ---------- 崩溃恢复 ----------

    /// <summary>列出磁盘上的会话（可能来自别的进程，比如被强杀的启动器）。</summary>
    public IReadOnlyList<(string Dir, SessionMeta Meta, bool Alive)> ListSessionsOnDisk()
    {
        var result = new List<(string, SessionMeta, bool)>();
        var root = Store.SessionRoot;
        if (!Directory.Exists(root)) return result;

        foreach (var dir in Directory.GetDirectories(root))
        {
            var meta = JsonStore.Load<SessionMeta>(Path.Combine(dir, "session.json"));
            if (meta == null) continue;
            result.Add((dir, meta, IsProcessAlive(meta.ProcessId, meta.ProcessName)));
        }

        return result;
    }

    /// <summary>收尾一个不在本进程里的会话：结束进程树 → 恢复注册表 → 写回快照。</summary>
    public bool FinalizeOrphanSession(string sandboxIdOrName)
    {
        var root = Store.SessionRoot;
        if (!Directory.Exists(root)) return false;

        foreach (var dir in Directory.GetDirectories(root))
        {
            var meta = JsonStore.Load<SessionMeta>(Path.Combine(dir, "session.json"));
            if (meta == null) continue;
            if (!string.Equals(meta.SandboxId, sandboxIdOrName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(meta.SandboxName, sandboxIdOrName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsProcessAlive(meta.ProcessId, meta.ProcessName))
            {
                try
                {
                    using var process = Process.GetProcessById(meta.ProcessId);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(15000);
                }
                catch (Exception ex)
                {
                    Log?.Invoke("结束进程失败：" + ex.Message);
                }
            }

            RecoverSessionDirectory(dir, meta);
            return true;
        }

        return false;
    }

    public void RecoverStaleSessions()
    {
        var root = Store.SessionRoot;
        if (!Directory.Exists(root)) return;

        foreach (var dir in Directory.GetDirectories(root))
        {
            var meta = JsonStore.Load<SessionMeta>(Path.Combine(dir, "session.json"));
            if (meta != null && IsProcessAlive(meta.ProcessId, meta.ProcessName)) continue;
            RecoverSessionDirectory(dir, meta);
        }
    }

    /// <summary>把一个没正常收尾的会话救回来：还原注册表 → 写回快照 → 删除会话目录。</summary>
    private void RecoverSessionDirectory(string dir, SessionMeta? meta)
    {
        var lockMarker = Path.Combine(dir, "registry.lock.json");
        try
        {
            if (File.Exists(lockMarker))
            {
                var before = JsonStore.Load<Dictionary<string, RegNode>>(Path.Combine(dir, "registry.before.json"));
                if (before != null)
                {
                    foreach (var (key, node) in before)
                    {
                        RegistrySandbox.DeleteTree(key);
                        if (!RegistrySandbox.IsEmpty(node)) RegistrySandbox.Import(key, node);
                    }
                }

                File.Delete(lockMarker);
                Log?.Invoke($"已恢复上次异常退出的注册表状态：{meta?.SandboxName ?? dir}");
            }

            var snapshotPath = meta?.SnapshotPath;
            if (!string.IsNullOrEmpty(snapshotPath) && File.Exists(snapshotPath) &&
                Directory.Exists(Path.Combine(dir, "fs")))
            {
                var manifest = SnapshotFile.ReadManifest(snapshotPath);
                if (meta != null)
                {
                    manifest.SandboxId = meta.SandboxId;
                    manifest.Name = meta.SandboxName;
                }

                SnapshotFile.SyncFrom(snapshotPath, dir, manifest, m => Log?.Invoke(m));
                Log?.Invoke($"已把未回写的会话写回快照：{meta?.SandboxName ?? dir}");
            }

            TryDeleteDirectory(dir);
        }
        catch (Exception ex)
        {
            Log?.Invoke("恢复会话失败（会话目录已保留）：" + ex.Message);
        }
    }

    private static bool IsProcessAlive(int pid, string name)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && (string.IsNullOrEmpty(name) ||
                                    string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    // ---------- 工具 ----------

    /// <summary>
    /// 算出这次要启动的 exe 和工作目录：
    ///   • Copy 模式 → 会话目录里的副本（fs/apps/...）
    ///   • Reference 模式 → 源安装目录里的原程序（不复制，改不了也删不掉沙盒外的文件）
    /// </summary>
    private static (string ExePath, string WorkDir) ResolveLaunchPaths(string fsRoot, SandboxAppRecord app)
    {
        if (app.SourceMode == AppSourceMode.Reference)
        {
            var srcExe = app.ExecutablePath;
            if (string.IsNullOrWhiteSpace(srcExe) || !File.Exists(srcExe))
                throw new InvalidOperationException(
                    "引用模式的源程序不在了：" + (string.IsNullOrWhiteSpace(srcExe) ? "（没有记录路径）" : srcExe) +
                    Environment.NewLine +
                    "安装目录可能被移动、重命名或卸载了。请重新「导入客户端」，或改用「复制进快照」方式。");

            var srcDir = !string.IsNullOrWhiteSpace(app.WorkingDirectory) && Directory.Exists(app.WorkingDirectory)
                ? app.WorkingDirectory
                : Path.GetDirectoryName(srcExe)!;
            return (srcExe, srcDir);
        }

        var exePath = ToSessionPath(fsRoot, app.ExecutablePath);
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"沙盒里找不到客户端程序：{app.ExecutablePath}（快照可能已损坏，请重新导入）");

        var workDir = string.IsNullOrWhiteSpace(app.WorkingDirectory)
            ? Path.GetDirectoryName(exePath)!
            : ToSessionPath(fsRoot, app.WorkingDirectory);
        return (exePath, workDir);
    }

    /// <summary>VHD 相对路径 -> 预判的可执行文件名（用于识别同一个程序的兄弟实例）。</summary>
    private static string exePathPreview(SandboxAppRecord app)
    {
        if (!string.IsNullOrWhiteSpace(app.VhdRelativeExecutable))
            return System.IO.Path.GetFileName(app.VhdRelativeExecutable);
        return System.IO.Path.GetFileName(app.ExecutablePath);
    }

    /// <summary>
    /// 启动前把「已经在跑的同一个程序」手里的单实例互斥体句柄释放掉（只碰我们自己启动的进程）。
    /// 名字从对方进程里现场枚举 + 过滤系统/输入法那类噪音；也可以在客户端设置里写死 MutexNames。
    /// </summary>
    private void ReleaseSiblingMutexes(SandboxRecord record, SandboxAppRecord app, string exeFileName)
    {
        try
        {
            var releasedAny = false;
            var siblingCount = 0;

            // 注意：兄弟实例可能在**别的启动器进程**里（GUI / 另一个 sl.exe），
            // 所以不能只看本进程的 _running，要从目录 + 会话元数据里找。
            foreach (var other in Store.Catalog.Sandboxes)
            {
                if (other.Id == record.Id) continue;

                var activity = GetActivity(other);
                if (!activity.ProcessAlive || activity.Meta == null) continue;

                var siblingApp = other.Apps.FirstOrDefault(a =>
                    string.Equals(a.Name, app.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(a.ExecutablePath), exeFileName, StringComparison.OrdinalIgnoreCase));
                if (siblingApp == null) continue;

                siblingCount++;
                var pid = activity.Meta.ProcessId;
                var patterns = new List<string>();
                if (app.MutexNames.Count > 0) patterns.AddRange(app.MutexNames);
                else if (siblingApp.MutexNames.Count > 0) patterns.AddRange(siblingApp.MutexNames);
                else patterns.AddRange(MutexUnlocker.GuessAppMutexNames(MutexUnlocker.EnumerateMutantNames(pid)));

                if (patterns.Count == 0) continue;

                var closed = MutexUnlocker.Release(pid, patterns);
                if (closed.Count == 0) continue;

                releasedAny = true;
                AppendLog(record, $"已释放「{other.Name}」（PID {pid}）的单实例互斥体：" + string.Join("、", closed));

                // 第一次释放成功就把名字记到客户端上，下次不用再猜
                if (!app.MutexNames.Any())
                {
                    app.MutexNames = closed.ToList();
                    try { Store.Save(); } catch { }
                }
            }

            if (!releasedAny && siblingCount > 0)
                AppendLog(record, $"注意：「{app.Name}」还有 {siblingCount} 个实例在跑，但没找到可释放的互斥体（程序可能用别的单实例机制）");
        }
        catch (Exception ex)
        {
            AppendLog(record, "释放兄弟实例互斥体失败（继续启动）：" + ex.Message);
        }
    }

    /// <summary>
    /// 启动前看看「同一个程序是不是已经在别处跑着」。很多启动器/客户端是单实例的（游戏启动器、微信……），
    /// 第二个实例会立刻退出 0，看上去就像「点了没反应」。这里只做提示、不拦截
    /// （微信按账号区分实例、多个副本同时跑是合法的）。
    /// </summary>
    private string? DescribeRunningInstances(SandboxRecord record, SandboxAppRecord app, string exePath)
    {
        var notes = new List<string>();
        var exeName = Path.GetFileName(exePath);

        try
        {
            foreach (var other in Store.Catalog.Sandboxes)
            {
                if (other.Id == record.Id) continue;
                var activity = GetActivity(other);
                if (!activity.ProcessAlive) continue;
                var meta = activity.Meta;
                var sameApp = meta != null &&
                              (string.Equals(meta.AppName, app.Name, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(meta.ProcessName, Path.GetFileNameWithoutExtension(exeName),
                                   StringComparison.OrdinalIgnoreCase));
                if (sameApp)
                    notes.Add($"沙盒「{other.Name}」（{meta!.AppName}，PID {meta.ProcessId}）");
            }
        }
        catch
        {
            // 检测失败不影响启动
        }

        try
        {
            var stem = Path.GetFileNameWithoutExtension(exePath);
            foreach (var process in Process.GetProcessesByName(stem))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    string? image = null;
                    try { image = process.MainModule?.FileName; } catch { }

                    if (image != null && string.Equals(Path.GetFullPath(image), Path.GetFullPath(exePath),
                            StringComparison.OrdinalIgnoreCase))
                        notes.Add($"宿主上直接运行的这个程序（PID {process.Id}）");
                    else if (image == null)
                        notes.Add($"宿主上有个同名进程 {stem}.exe（PID {process.Id}，读不到完整路径）");
                }
            }
        }
        catch
        {
            // 同样是提示性质的，失败就算了
        }

        if (notes.Count == 0) return null;
        return $"同一个程序已经在运行：{string.Join("；", notes.Distinct())}。";
    }

    private static string ToSessionPath(string fsRoot, string relative)
    {
        var rel = relative.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(fsRoot, rel));
    }

    private void AppendLog(SandboxRecord record, string message)
    {
        Log?.Invoke($"[{record.Name}] {message}");
        try
        {
            var file = Path.Combine(Store.Root, "logs", "service.log");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{record.Name}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    /// <summary>
    /// 删目录：先去掉只读属性（微信收到的文件、从只读介质拷来的文件都可能是只读的，
    /// 直接 Directory.Delete 会抛 Access denied，导致会话目录永远清不掉）。
    /// </summary>
    private static void TryDeleteDirectory(string path)
    {
        // 刚被强杀的进程往往还捏着几百毫秒的句柄：多试几次，别让残留目录把下次启动卡住
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                DeleteTree(path);
                if (!Directory.Exists(path)) return;
            }
            catch (Exception)
            {
                // 还被占用就等一会儿再试
            }

            Thread.Sleep(250);
        }
    }

    /// <summary>
    /// 逐层删除目录：去掉只读属性（微信收到的文件可能是只读的），
    /// junction/符号链接只删链接本身（绝不跟进目标目录）。
    /// </summary>
    private static void DeleteTree(string directory)
    {
        FileSystemInfo[] entries;
        try { entries = new DirectoryInfo(directory).GetFileSystemInfos(); }
        catch (Exception) { return; }

        foreach (var info in entries)
        {
            try
            {
                var attributes = info.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(info.FullName, false);
                    else File.Delete(info.FullName);
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    DeleteTree(info.FullName);
                    continue;
                }

                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(info.FullName, attributes & ~FileAttributes.ReadOnly);
                File.Delete(info.FullName);
            }
            catch (Exception)
            {
                // 单个文件删不掉不影响其它文件
            }
        }

        try { Directory.Delete(directory, false); } catch (Exception) { /* 还有删不掉的子项 */ }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>释放服务。注意：不会自动停止正在运行的沙盒（由界面关闭流程或调用方负责）。</summary>
    public void Dispose()
    {
    }
}
