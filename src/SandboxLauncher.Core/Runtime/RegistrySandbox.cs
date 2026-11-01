using System.Text.Json;
using Microsoft.Win32;

namespace SandboxLauncher.Core.Runtime;

public sealed class RegValue
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "String";
    public string? String { get; set; }
    public long? Number { get; set; }
    public string[]? MultiString { get; set; }
    public string? BinaryBase64 { get; set; }
}

public sealed class RegNode
{
    public Dictionary<string, RegValue> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RegNode> SubKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 注册表隔离：把 HKCU 下指定子树在「宿主状态」与「沙盒状态」之间切换。
/// 注意：这是顺序隔离（同一时刻只允许一个沙盒占用），不是并发隔离。
/// </summary>
public static class RegistrySandbox
{
    private static readonly RegistryView View = RegistryView.Registry64;

    public static string NormalizeKey(string key)
    {
        key = (key ?? "").Trim();
        if (key.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase)) key = key["HKEY_CURRENT_USER\\".Length..];
        else if (key.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase)) key = key["HKCU\\".Length..];
        key = key.Trim('\\');
        if (key.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("目前只支持隔离 HKEY_CURRENT_USER 下的键");
        return key;
    }

    public static RegNode Export(string subKeyPath)
    {
        subKeyPath = NormalizeKey(subKeyPath);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, View);
        using var key = root.OpenSubKey(subKeyPath);
        return key == null ? new RegNode() : ReadKey(key);
    }

    public static void Import(string subKeyPath, RegNode node)
    {
        subKeyPath = NormalizeKey(subKeyPath);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, View);
        using var key = root.CreateSubKey(subKeyPath, writable: true);
        WriteInto(key, node);
    }

    public static void DeleteTree(string subKeyPath)
    {
        subKeyPath = NormalizeKey(subKeyPath);
        if (subKeyPath.Length == 0) return;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, View);
            root.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException)
        {
            // 极少数键没有写权限，跳过即可
        }
    }

    public static bool IsEmpty(RegNode node) => node.Values.Count == 0 && node.SubKeys.Count == 0;

    public static void SaveToFile(string path, RegNode node) => JsonStore.Save(path, node);

    public static RegNode? LoadFromFile(string path) => JsonStore.Load<RegNode>(path);

    private static RegNode ReadKey(RegistryKey key)
    {
        var node = new RegNode();
        foreach (var name in key.GetValueNames())
        {
            try
            {
                var kind = key.GetValueKind(name);
                var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                node.Values[name] = ToValue(name, kind, value);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var sub in key.GetSubKeyNames())
        {
            try
            {
                using var child = key.OpenSubKey(sub);
                if (child != null) node.SubKeys[sub] = ReadKey(child);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return node;
    }

    private static RegValue ToValue(string name, RegistryValueKind kind, object? value) => kind switch
    {
        RegistryValueKind.String => new RegValue { Name = name, Kind = "String", String = value as string },
        RegistryValueKind.ExpandString => new RegValue { Name = name, Kind = "ExpandString", String = value as string },
        RegistryValueKind.MultiString => new RegValue { Name = name, Kind = "MultiString", MultiString = value as string[] },
        RegistryValueKind.DWord => new RegValue { Name = name, Kind = "DWord", Number = Convert.ToInt64(value ?? 0) },
        RegistryValueKind.QWord => new RegValue { Name = name, Kind = "QWord", Number = Convert.ToInt64(value ?? 0L) },
        RegistryValueKind.Binary => new RegValue
        {
            Name = name,
            Kind = "Binary",
            BinaryBase64 = value is byte[] bytes ? Convert.ToBase64String(bytes) : null
        },
        _ => new RegValue { Name = name, Kind = "String", String = value?.ToString() }
    };

    private static void WriteInto(RegistryKey key, RegNode node)
    {
        foreach (var value in node.Values.Values)
        {
            try
            {
                switch (value.Kind)
                {
                    case "ExpandString":
                        key.SetValue(value.Name, value.String ?? "", RegistryValueKind.ExpandString);
                        break;
                    case "DWord":
                        key.SetValue(value.Name, unchecked((int)(value.Number ?? 0)), RegistryValueKind.DWord);
                        break;
                    case "QWord":
                        key.SetValue(value.Name, value.Number ?? 0, RegistryValueKind.QWord);
                        break;
                    case "MultiString":
                        key.SetValue(value.Name, value.MultiString ?? Array.Empty<string>(), RegistryValueKind.MultiString);
                        break;
                    case "Binary":
                        key.SetValue(value.Name, value.BinaryBase64 == null ? Array.Empty<byte>() : Convert.FromBase64String(value.BinaryBase64), RegistryValueKind.Binary);
                        break;
                    default:
                        key.SetValue(value.Name, value.String ?? "", RegistryValueKind.String);
                        break;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        foreach (var (name, child) in node.SubKeys)
        {
            try
            {
                using var sub = key.CreateSubKey(name, writable: true);
                WriteInto(sub, child);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }
}
