using System.Text;
using System.Text.RegularExpressions;

namespace SandboxLauncher.Core.Snapshot;

/// <summary>
/// 快照回写时要跳过的路径（相对于沙盒 <c>fs/</c> 根，用 / 分隔）。
/// 每一段支持 <c>*</c> 通配；命中某个目录时，它下面的内容都跳过。
///
/// 默认跳过的是 Windows 系统缓存目录：它们经常被系统锁住（例如
/// INetCache\Content.IE5 会直接让整次回写失败），体积大又没有保留价值。
/// 关掉「跳过系统缓存」或把模式清空，就能把沙盒内容原样保留。
/// </summary>
public sealed class PathFilter
{
    /// <summary>默认跳过的系统缓存目录（相对 fs/）。</summary>
    public static readonly string[] DefaultExcludes =
    {
        "profile/AppData/Local/Microsoft/Windows/INetCache",
        "profile/AppData/Local/Microsoft/Windows/INetCookies",
        "profile/AppData/Local/Microsoft/Windows/WebCache",
        "profile/AppData/Local/Microsoft/Windows/Explorer",
        "profile/AppData/Local/AMD",
        "profile/AppData/Local/D3DSCache",
        "profile/AppData/Local/Microsoft/Windows/Caches"
    };

    private readonly List<Regex> _regexes = new();
    private readonly List<string> _patterns = new();

    private PathFilter()
    {
    }

    /// <summary>实际生效的排除模式（用于界面展示）。</summary>
    public IReadOnlyList<string> Patterns => _patterns;

    public bool IsEmpty => _regexes.Count == 0;

    public static PathFilter Empty { get; } = new();

    public static PathFilter Create(IEnumerable<string>? userPatterns, bool includeDefaults = true)
    {
        var filter = new PathFilter();
        if (includeDefaults)
            foreach (var pattern in DefaultExcludes) filter.Add(pattern);
        if (userPatterns != null)
            foreach (var pattern in userPatterns) filter.Add(pattern);
        return filter;
    }

    private void Add(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return;
        var normalized = Normalize(pattern);
        if (normalized.Length == 0) return;
        var regex = Build(normalized);
        if (regex == null) return;
        if (_patterns.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return;
        _patterns.Add(normalized);
        _regexes.Add(regex);
    }

    public bool IsExcluded(string? relativePath)
    {
        if (_regexes.Count == 0 || string.IsNullOrEmpty(relativePath)) return false;
        var normalized = Normalize(relativePath);
        if (normalized.Length == 0) return false;
        foreach (var regex in _regexes)
            if (regex.IsMatch(normalized)) return true;
        return false;
    }

    public static string Normalize(string path) =>
        path.Replace('\\', '/').Trim().Trim('/').ToLowerInvariant();

    private static Regex? Build(string pattern)
    {
        if (pattern.Length == 0) return null;
        var sb = new StringBuilder("^");
        foreach (var ch in pattern)
        {
            if (ch == '*') sb.Append("[^/]*");
            else sb.Append(Regex.Escape(ch.ToString()));
        }

        sb.Append("(?:/.*)?$");
        try
        {
            return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
