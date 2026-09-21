using System.Text.Json;
using System.Text.Json.Serialization;

namespace Luma.Host.Bridge;

/// <summary>AppState / Project / LaunchItem / Preferences DTO。字段名与 docs/contracts/state.schema.json 对齐（camelCase），校验规则同 schema 外加 ID 唯一性。</summary>
public sealed class AppState
{
    [JsonRequired, JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonRequired, JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonRequired, JsonPropertyName("projects")]
    public List<Project> Projects { get; set; } = new();

    [JsonRequired, JsonPropertyName("preferences")]
    public Preferences Preferences { get; set; } = new();
}

public sealed class Project
{
    [JsonRequired, JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonRequired, JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonRequired, JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonRequired, JsonPropertyName("color")] public string Color { get; set; } = "";
    [JsonRequired, JsonPropertyName("pinned")] public bool Pinned { get; set; }
    [JsonPropertyName("favorite"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Favorite { get; set; }
    [JsonRequired, JsonPropertyName("items")] public List<LaunchItem> Items { get; set; } = new();
}

public sealed class LaunchItem
{
    [JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Note { get; set; }
    [JsonPropertyName("websiteIcon"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? WebsiteIcon { get; set; }
    [JsonRequired, JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonRequired, JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonRequired, JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonRequired, JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("launch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LaunchConfiguration? Launch { get; set; }
}

public sealed record LaunchConfiguration
{
    [JsonRequired, JsonPropertyName("command")] public string Command { get; init; } = "";
    [JsonRequired, JsonPropertyName("workingDirectory")] public string WorkingDirectory { get; init; } = "";
}

public sealed class Preferences
{
    [JsonPropertyName("shortcuts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ShortcutBinding>? Shortcuts { get; set; }
    [JsonPropertyName("recentLimit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? RecentLimit { get; set; }
    [JsonRequired, JsonPropertyName("width")] public double Width { get; set; } = 640;
    [JsonRequired, JsonPropertyName("height")] public double Height { get; set; } = 88;
    [JsonRequired, JsonPropertyName("iconSize")] public double IconSize { get; set; } = 40;
    [JsonRequired, JsonPropertyName("radius")] public double Radius { get; set; } = 22;
    [JsonRequired, JsonPropertyName("material")] public string Material { get; set; } = "frost";
    [JsonRequired, JsonPropertyName("theme")] public string Theme { get; set; } = "light";
    [JsonRequired, JsonPropertyName("reducedMotion")] public bool ReducedMotion { get; set; }
    [JsonRequired, JsonPropertyName("autoHide")] public bool AutoHide { get; set; } = true;
}

public static class ContractsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        IncludeFields = false,
    };
}

public static class StateValidator
{
    public static readonly HashSet<string> Colors = new(StringComparer.Ordinal) { "mint", "blue", "violet", "peach", "gold" };
    public static readonly HashSet<string> Materials = new(StringComparer.Ordinal) { "frost", "soft", "solid" };
    public static readonly HashSet<string> Themes = new(StringComparer.Ordinal) { "light", "dark" };
    public static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "folder", "file", "app", "url" };

    /// <summary>新安装的初始空状态，与 docs/contracts/empty-state.json 逐字节一致（由测试保证）。</summary>
    public static AppState EmptyState() => new()
    {
        SchemaVersion = 1,
        Revision = 0,
        Projects = new List<Project>(),
        Preferences = new Preferences(),
    };

    /// <summary>校验状态合法性，返回中文错误列表；空列表表示合法。包含 schema 范围与 ID 唯一性（schema 表达不了的部分）。</summary>
    public static List<string> Validate(AppState state)
    {
        var errors = new List<string>();
        if (state is null) { errors.Add("状态不能为空。"); return errors; }
        if (state.SchemaVersion != 1) errors.Add("schemaVersion 必须为 1。");
        if (state.Revision < 0) errors.Add("revision 不能为负数。");
        if (state.Projects is null) { errors.Add("projects 不能为空列表字段。"); return errors; }
        if (state.Projects.Count > 100) errors.Add("项目数量最多 100 个。");

        var projectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in state.Projects)
        {
            if (project is null) { errors.Add("项目不能为空对象。"); continue; }
            var label = string.IsNullOrEmpty(project.Id) ? "(无 id)" : project.Id;
            if (string.IsNullOrEmpty(project.Id)) errors.Add($"项目 {label}: id 不能为空。");
            else if (!projectIds.Add(project.Id)) errors.Add($"项目 {label}: id 重复。");
            if (string.IsNullOrEmpty(project.Name) || project.Name.Length > 60) errors.Add($"项目 {label}: 名称长度需在 1–60 字之间。");
            if (project.Description is null || project.Description.Length > 160) errors.Add($"项目 {label}: 描述必须是字符串且最长 160 字。");
            if (!Colors.Contains(project.Color)) errors.Add($"项目 {label}: 颜色枚举非法。");
            if (project.Items is null || project.Items.Count == 0) errors.Add($"项目 {label}: 至少需要 1 个入口。");
            else if (project.Items.Count > 200) errors.Add($"项目 {label}: 入口数量最多 200。");
            else
            {
                var itemIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in project.Items)
                {
                    if (item is null) { errors.Add($"项目 {label}: 入口不能为空对象。"); continue; }
                    var itemLabel = string.IsNullOrEmpty(item.Id) ? "(无 id)" : item.Id;
                    if (string.IsNullOrEmpty(item.Id)) errors.Add($"项目 {label} 入口 {itemLabel}: id 不能为空。");
                    else if (!itemIds.Add(item.Id)) errors.Add($"项目 {label} 入口 {itemLabel}: id 重复。");
                    if (string.IsNullOrEmpty(item.Name) || item.Name.Length > 120) errors.Add($"项目 {label} 入口 {itemLabel}: 名称长度需在 1–120 字之间。");
                    if (string.IsNullOrEmpty(item.Path) || item.Path.Length > 4096) errors.Add($"项目 {label} 入口 {itemLabel}: 路径长度需在 1–4096 字之间。");
                    if (!Kinds.Contains(item.Kind)) errors.Add($"项目 {label} 入口 {itemLabel}: kind 枚举非法。");
                    if (item.Kind == "url" && !Services.WebsiteService.IsUrl(item.Path)) errors.Add("网址只支持有效的 HTTP(S) 地址。");
                    if (item.Note?.Length > 240) errors.Add("入口备注最多 240 字。");
                    if (item.WebsiteIcon is { } icon && (item.Kind != "url" || !Services.WebsiteService.IsIconDataUrl(icon))) errors.Add("网站图标必须为已验证的小尺寸 PNG。");
                    if (item.Launch is { } launch)
                    {
                        if (item.Kind != "folder") errors.Add($"项目 {label} 入口 {itemLabel}: 只有文件夹可以保存启动命令。");
                        if (string.IsNullOrWhiteSpace(launch.Command) || launch.Command.Length > 1000 || launch.Command.IndexOfAny(['\r', '\n', '\0']) >= 0)
                            errors.Add($"项目 {label} 入口 {itemLabel}: 启动命令必须为单行非空文本，最长 1000 字。");
                        if (string.IsNullOrWhiteSpace(launch.WorkingDirectory) || launch.WorkingDirectory.Length > 4096 ||
                            launch.WorkingDirectory.IndexOfAny(['\r', '\n', '\0']) >= 0 || !System.IO.Path.IsPathFullyQualified(launch.WorkingDirectory))
                            errors.Add($"项目 {label} 入口 {itemLabel}: 工作目录必须为绝对路径，最长 4096 字。");
                    }
                }
            }
        }

        var prefs = state.Preferences;
        if (prefs is null) errors.Add("preferences 不能为空。");
        else
        {
            if (prefs.Width < 320 || prefs.Width > 1120) errors.Add("外观宽度需在 320–1120 之间。");
            if (prefs.Height < 64 || prefs.Height > 160) errors.Add("外观高度需在 64–160 之间。");
            if (prefs.IconSize < 24 || prefs.IconSize > 64) errors.Add("图标大小需在 24–64 之间。");
            if (prefs.Radius < 12 || prefs.Radius > 32) errors.Add("圆角需在 12–32 之间。");
            if (!Materials.Contains(prefs.Material)) errors.Add("材质枚举非法。");
            if (!Themes.Contains(prefs.Theme)) errors.Add("主题枚举非法。");
            if (prefs.RecentLimit is < 6 or > 10) errors.Add("最近项目数量为 6–10 条。");
            errors.AddRange(ShortcutRules.Validate(prefs.Shortcuts));
        }
        return errors;
    }
}
