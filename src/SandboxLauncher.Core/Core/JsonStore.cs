using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SandboxLauncher.Core;

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 枚举写成 "Reference" 而不是 1 —— 快照/目录都是给人看的；读的时候仍然接受数字（旧文件兼容）
        Converters = { new JsonStringEnumConverter(allowIntegerValues: true) }
    };

    public static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<T>(text, Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Options);
}
