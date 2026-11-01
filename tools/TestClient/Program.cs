using System.Text.Json;
using Microsoft.Win32;

// 模拟一个「客户端软件」：往用户目录、临时目录、注册表里写东西，
// 用来验证沙盒的隔离与快照回写是否真的生效。

var sleepMs = 200;
var spawnChildMs = 0;
var childMarker = false;
var mutexName = "";
var argv = Environment.GetCommandLineArgs();
for (var i = 1; i < argv.Length; i++)
{
    if (argv[i] is "--sleep" && i + 1 < argv.Length && int.TryParse(argv[i + 1], out var parsed))
        sleepMs = parsed;
    if (argv[i] is "--spawn-child" && i + 1 < argv.Length && int.TryParse(argv[i + 1], out var childMs))
        spawnChildMs = childMs;
    if (argv[i] is "--child-marker")
        childMarker = true;
    if (argv[i] is "--mutex" && i + 1 < argv.Length)
        mutexName = argv[i + 1];
}

var envAppData = Environment.GetEnvironmentVariable("APPDATA") ?? "";
var envLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
var envTemp = Environment.GetEnvironmentVariable("TEMP") ?? "";
var envProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";

// 注意：Known Folder API 读的是注册表里的用户 Shell 文件夹，不认环境变量，
// 这里把它也打印出来，用来说明「环境变量重定向」的边界。
var knownAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
var knownProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

var stateDir = Path.Combine(envAppData, "DemoClient");
Directory.CreateDirectory(stateDir);
var stateFile = Path.Combine(stateDir, "state.txt");

var runs = 0;
if (File.Exists(stateFile) && int.TryParse(File.ReadAllText(stateFile).Trim(), out var previous))
    runs = previous;
runs++;
File.WriteAllText(stateFile, runs.ToString());

// 「子进程」模式：只写一个标记文件，用来证明启动器拉起来的真正客户端确实在沙盒里跑起来了
if (childMarker)
{
    File.WriteAllText(Path.Combine(stateDir, "child.done"),
        $"child pid {Environment.ProcessId} at {DateTime.Now:O}");
}

var tempFile = Path.Combine(envTemp, $"demo-temp-{runs}.tmp");
Directory.CreateDirectory(envTemp);
File.WriteAllText(tempFile, "temp file written at " + DateTime.Now.ToString("O"));

var regRuns = 0;
using (var key = Registry.CurrentUser.CreateSubKey(@"Software\SandboxLauncherDemo", writable: true))
{
    regRuns = Convert.ToInt32(key.GetValue("Runs", 0) ?? 0);
    regRuns++;
    key.SetValue("Runs", regRuns, RegistryValueKind.DWord);
    key.SetValue("LastRun", DateTime.Now.ToString("O"), RegistryValueKind.String);
}

// 「启动器」模式：把自己作为子进程拉起来，然后立刻退出（模拟游戏启动器把界面交给真正的客户端）
int? childPid = null;
if (spawnChildMs > 0)
{
    var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        Arguments = $"--sleep {spawnChildMs} --child-marker",
        UseShellExecute = false,
        WorkingDirectory = AppContext.BaseDirectory
    });
    childPid = child?.Id;
}

// 模拟「单实例程序」：持有一个固定名字的互斥体
bool? mutexCreated = null;
Mutex? heldMutex = null;
if (!string.IsNullOrWhiteSpace(mutexName))
{
    try
    {
        heldMutex = new Mutex(false, mutexName, out var createdNew);
        mutexCreated = createdNew;
    }
    catch
    {
        mutexCreated = null;
    }
}

if (sleepMs > 0) Thread.Sleep(sleepMs);
heldMutex?.Dispose();

Console.WriteLine(JsonSerializer.Serialize(new
{
    envAppData,
    envLocalAppData,
    envTemp,
    envProfile,
    knownAppData,
    knownProfile,
    stateFile,
    tempFile,
    runs,
    regRuns,
    pid = Environment.ProcessId,
    sandbox = Environment.GetEnvironmentVariable("SANDBOX_LAUNCHER"),
    childPid,
    mutexName,
    mutexCreated
}, new JsonSerializerOptions { WriteIndented = true }));

return 0;
