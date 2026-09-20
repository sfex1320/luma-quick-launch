using System.Text.RegularExpressions;

namespace Luma.Host.Services;

/// <summary>
/// 协议 v1 的路径合法性规则：接受本地绝对路径与 UNC；拒绝内嵌 NUL、URL、任意命令串。
/// 校验只做静态判断，不触碰文件系统（存在性检查在 LaunchService 的后台任务里做）。
/// </summary>
public static partial class PathRules
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*://")]
    private static partial Regex UrlSchemeRegex();

    private static readonly string[] CommandShells = { "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe", "wt", "wt.exe", "start" };
    private static readonly string[] CommandMarkers = { " /c ", " /k ", " -command", " -encodedcommand", "&&", "||", " 2>", " >", " ^|", "taskkill", "reg add" };

    /// <summary>返回 null 表示合法；否则为中文错误信息（INVALID_REQUEST 语义）。</summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "路径不能为空。";
        if (path.Contains('\0')) return "路径包含非法字符（内嵌 NUL）。";
        var trimmed = path.Trim();
        if (UrlSchemeRegex().IsMatch(trimmed)) return "不支持 URL 地址，请填写本地或网络路径。";
        if (LooksLikeCommandLine(trimmed)) return "不支持命令行字符串，请填写实际路径；需要带参数时请使用 .lnk 快捷方式。";
        if (!IsAcceptableRoot(trimmed)) return "仅支持本地绝对路径（如 D:\\Projects）或 UNC 网络路径（\\\\server\\share）。";
        return null;
    }

    private static bool IsAcceptableRoot(string path)
    {
        // 本地盘符根：X:\...；UNC：\\server\share...
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) return true;
        return path.StartsWith(@"\\", StringComparison.Ordinal) && path.Length > 3;
    }

    private static bool LooksLikeCommandLine(string path)
    {
        var lower = path.ToLowerInvariant();
        var firstToken = lower.Split(' ', '\t')[0];
        if (CommandShells.Contains(firstToken)) return true;
        foreach (var marker in CommandMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal)) return true;
        }
        if (path.StartsWith('"')) return true;
        return false;
    }
}
