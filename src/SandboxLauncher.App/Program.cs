namespace SandboxLauncher.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);

        try
        {
            // --root 可以指定另一套启动器元数据目录（测试/便携用）
            var store = new Core.CatalogStore(GetOption(args, "root"));
            using var service = new Core.Runtime.SandboxService(store);

            // 直接打开某个沙盒的文件浏览器：
            //   SandboxLauncher.exe --files "演示沙盒" [--path profile/Documents] [--host <宿主目录>] [--root <元数据目录>]
            var filesKey = GetOption(args, "files");
            if (!string.IsNullOrWhiteSpace(filesKey))
            {
                var record = store.Find(filesKey);
                if (record == null)
                {
                    MessageBox.Show($"找不到沙盒「{filesKey}」。", "沙盒文件", MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                Application.Run(new SandboxFilesDialog(record, store, service,
                    GetOption(args, "path"), GetOption(args, "host")));
                return;
            }

            Application.Run(new MainForm(store, service));
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            throw;
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--" + name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "-" + name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static void LogCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var path = Path.Combine(Core.AppPaths.Root, "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败就算了
        }
    }
}
