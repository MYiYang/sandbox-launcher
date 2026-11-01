using SandboxLauncher.Core.Model;

namespace SandboxLauncher.Core.Runtime;

/// <summary>构造沙盒进程的环境变量：把用户目录、临时目录指到沙盒内部。</summary>
public static class EnvBuilder
{
    public static Dictionary<string, string> Build(string sessionDir, SandboxSettings settings)
    {
        var fsRoot = Path.Combine(sessionDir, "fs");
        var profile = Path.Combine(fsRoot, settings.ProfileFolderName);
        var roaming = Path.Combine(profile, "AppData", "Roaming");
        var local = Path.Combine(profile, "AppData", "Local");
        var temp = Path.Combine(profile, "Temp");

        Directory.CreateDirectory(roaming);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(temp);

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (settings.RedirectUserProfile)
        {
            var rootOfDrive = Path.GetPathRoot(profile) ?? "C:\\";
            env["USERPROFILE"] = profile;
            env["APPDATA"] = roaming;
            env["LOCALAPPDATA"] = local;
            env["TEMP"] = temp;
            env["TMP"] = temp;
            env["HOMEDRIVE"] = rootOfDrive.TrimEnd('\\');
            env["HOMEPATH"] = profile[rootOfDrive.Length..];
            env["OneDrive"] = Path.Combine(profile, "OneDrive");
            env["USERPROFILE_ORIGINAL"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            Directory.CreateDirectory(env["OneDrive"]);
        }

        env["SANDBOX_LAUNCHER"] = "1";
        env["SANDBOX_LAUNCHER_SESSION"] = sessionDir;
        env["SANDBOX_LAUNCHER_PROFILE"] = profile;

        foreach (var (key, value) in settings.ExtraEnvironment)
        {
            if (!string.IsNullOrWhiteSpace(key)) env[key] = value ?? "";
        }

        return env;
    }

    /// <summary>生成一份可读的环境差异报告（用于 CLI 自检 / GUI 展示）。</summary>
    public static string Describe(IReadOnlyDictionary<string, string> env)
    {
        string[] keys = { "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "HOMEDRIVE", "HOMEPATH" };
        var lines = new List<string>();
        foreach (var key in keys)
        {
            if (env.TryGetValue(key, out var value))
                lines.Add($"{key} = {value}   （宿主：{Environment.GetEnvironmentVariable(key)}）");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
