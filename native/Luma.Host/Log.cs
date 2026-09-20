using System.IO;
using System.Text;

namespace Luma.Host;

/// <summary>轻量文件日志：写 %LOCALAPPDATA%\Luma\logs\host.log，保留原生失败证据。线程安全。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string baseDir)
    {
        try
        {
            var dir = Path.Combine(baseDir, "logs");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, $"host-{DateTime.Now:yyyyMMdd}.log");
            Info($"Luma 内核启动 pid={Environment.ProcessId} os={Environment.OSVersion.VersionString}");
        }
        catch
        {
            _path = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var path = _path;
        if (path is null) return;
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch
        {
            // 日志失败不能影响宿主
        }
    }
}
