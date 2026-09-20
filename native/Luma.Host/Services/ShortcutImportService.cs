using System.IO;

namespace Luma.Host.Services;

public sealed record ImportedShortcut(string Name, string Path, string Kind);

/// <summary>Only call with paths supplied by the native picker or WebView file objects.</summary>
public sealed class ShortcutImportService
{
    public IReadOnlyList<ImportedShortcut> Resolve(IReadOnlyList<string> paths)
    {
        if (paths.Count > 100) throw new ArgumentException("每次最多导入 100 个入口。", nameof(paths));
        var results = new List<ImportedShortcut>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            if (raw is null || raw.Length > 4096 || PathRules.Validate(raw) is not null)
                throw new ArgumentException("包含无效入口，请选择实际存在的文件、软件或文件夹。", nameof(paths));
            try
            {
                var path = Path.GetFullPath(raw);
                if (!seen.Add(path)) continue;
                var folder = Directory.Exists(path);
                if (!folder && !File.Exists(path))
                    throw new ArgumentException("部分入口已移动或无法访问，本次未导入，请重新选择。", nameof(paths));
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                if (string.IsNullOrEmpty(name)) name = path;
                results.Add(new ImportedShortcut(name[..Math.Min(name.Length, 120)], path, Classify(path, folder)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new ArgumentException("部分入口无法访问，本次未导入，请重新选择。", nameof(paths), ex);
            }
        }
        return results;
    }

    public static string Classify(string path, bool folder = false) => folder ? "folder" :
        Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".lnk" or ".appref-ms" ? "app" : "file";
}
