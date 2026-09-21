using System.Text.Json.Serialization;

namespace Luma.Host.Bridge;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ShortcutBinding
{
    [JsonRequired] public string Id { get; init; } = "";
    [JsonRequired] public string Code { get; init; } = "";
    [JsonRequired] public bool Ctrl { get; init; }
    [JsonRequired] public bool Shift { get; init; }
    [JsonRequired] public bool Alt { get; init; }
    [JsonRequired] public string Scope { get; init; } = "panel";
    [JsonRequired] public string Action { get; init; } = "dock";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProjectId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ItemId { get; init; }
}

public static class ShortcutRules
{
    public static readonly HashSet<string> DirectoryActions = new(StringComparer.Ordinal)
        { "openDirectory", "newFolder", "copyAddress", "runProject" };
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
        { "dock", "search", "settings", "edit", "project", "item", "openDirectory", "newFolder", "copyAddress", "runProject" };
    private static readonly Dictionary<string, uint> Keys = CreateKeys();
    public static uint VirtualKey(string code) => code is not null && Keys.TryGetValue(code, out var key) ? key : 0;
    public static uint Modifiers(ShortcutBinding binding) => (binding.Alt ? 1u : 0) | (binding.Ctrl ? 2u : 0) | (binding.Shift ? 4u : 0);
    public static IEnumerable<string> Validate(IReadOnlyList<ShortcutBinding>? bindings)
    {
        if (bindings is null) yield break;
        if (bindings.Count > 128) { yield return "快捷键最多 128 条。"; yield break; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var chords = new HashSet<(uint, uint)>();
        foreach (var b in bindings)
        {
            if (b is null) { yield return "快捷键不能为空。"; continue; }
            if (!ValidId(b.Id) || b.Id.Length > 128 || !ids.Add(b.Id)) yield return "快捷键 id 无效或重复。";
            var key = VirtualKey(b.Code); var modifiers = Modifiers(b);
            if (key == 0 || !Actions.Contains(b.Action) || b.Scope is not ("panel" or "global")) yield return "快捷键按键、动作或范围无效。";
            if (!chords.Add((key, modifiers))) yield return "快捷键组合重复。";
            if ((key == 0x20 && modifiers == 3) || (key == 0x4b && modifiers == 2)) yield return "此快捷键为系统内置搜索预留。";
            if (b.Scope == "global" && (modifiers == 0 || DirectoryActions.Contains(b.Action))) yield return "全局快捷键需要修饰键，目录动作仅支持面板内。";
            if (b.Action is "project" or "item")
            {
                if (!ValidId(b.ProjectId)) yield return "快捷键缺少有效项目 id。";
                if (b.Action == "item" && !ValidId(b.ItemId)) yield return "快捷键缺少有效入口 id。";
                if (b.Action == "project" && b.ItemId is not null) yield return "项目快捷键不能携带入口 id。";
            }
            else if (b.ProjectId is not null || b.ItemId is not null) yield return "此快捷键动作不能携带目标 id。";
        }
    }
    public static bool HasTarget(AppState state, ShortcutBinding b) => b.Action is not ("project" or "item") ||
        state.Projects.Any(p => p.Id == b.ProjectId && (b.Action == "project" || p.Items.Any(i => i.Id == b.ItemId)));
    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200;
    private static Dictionary<string, uint> CreateKeys()
    {
        var keys = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["Space"]=0x20, ["Enter"]=0x0d, ["Tab"]=9, ["Escape"]=0x1b, ["Backspace"]=8,
            ["ArrowUp"]=0x26, ["ArrowDown"]=0x28, ["ArrowLeft"]=0x25, ["ArrowRight"]=0x27,
            ["Home"]=0x24, ["End"]=0x23, ["PageUp"]=0x21, ["PageDown"]=0x22, ["Insert"]=0x2d, ["Delete"]=0x2e,
            ["NumpadAdd"]=0x6b, ["NumpadSubtract"]=0x6d, ["NumpadMultiply"]=0x6a, ["NumpadDivide"]=0x6f, ["NumpadDecimal"]=0x6e, ["NumpadEnter"]=0x0d,
            ["Backquote"]=0xc0, ["Minus"]=0xbd, ["Equal"]=0xbb, ["BracketLeft"]=0xdb, ["BracketRight"]=0xdd,
            ["Backslash"]=0xdc, ["Semicolon"]=0xba, ["Quote"]=0xde, ["Comma"]=0xbc, ["Period"]=0xbe, ["Slash"]=0xbf,
        };
        for (var i=0; i<26; i++) keys["Key" + (char)('A'+i)] = (uint)(0x41+i);
        for (var i=0; i<10; i++) { keys["Digit"+i]=(uint)(0x30+i); keys["Numpad"+i]=(uint)(0x60+i); }
        for (var i=1; i<=24; i++) keys["F"+i]=(uint)(0x6f+i);
        return keys;
    }
}
