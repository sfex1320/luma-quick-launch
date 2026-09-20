# 第十二轮：按软件归属的最近文件

## 调查证据

旧实现枚举 Windows Recent 顶层最多 256 个快捷方式，再按扩展名推测软件归属。它只能说明“Windows 最近记录里有同扩展名文件”，不能证明该文件由保存的软件打开或编辑，因此不能作为 Photoshop 的真实最近文件列表。

本机只读调查发现 Adobe MediaBrowser 为不同应用维护独立 MRU：

- `HKCU\Software\Adobe\MediaBrowser\MRU\Photoshop\FileList`：100 个时间戳子键；
- 同路径 `illustrator`：31 个；
- 同路径 `indesign`：7 个。

每个时间戳子键只有一个默认字符串值。调查仅统计数量、类型和长度，没有输出私人文件路径。对最多 256 项作只读验证时，Photoshop 有 30 项 PSD/PSB 格式记录，Illustrator 有 25 项 AI/EPS，InDesign 有 7 项 INDD/IDML；最终产品仍会逐项验证路径、普通文件、父目录重解析点并去重。

Adobe 官方说明 Photoshop 的 Recent File List Contains 可配置为 0–100，证实 Photoshop 自身维护最近文件列表：[Photoshop Home screen](https://helpx.adobe.com/photoshop/desktop/get-started/learn-the-basics/homescreen-overview.html)。Adobe 也明确 Windows 首选项保存在用户 AppData 的 Photoshop Settings 目录：[Reset Photoshop preferences](https://helpx.adobe.com/photoshop/desktop/get-started/settings-and-preferences/reset-preferences.html)。

Windows 官方提供 `IApplicationDocumentLists` 按 AppUserModelID 读取应用 Recent/Frequent Jump List；该列表由 `SHAddToRecentDocs` 或公共文件对话框生成：[IApplicationDocumentLists](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-iapplicationdocumentlists)、[SHAddToRecentDocs](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shaddtorecentdocs)。本轮没有解析 `.automaticDestinations-ms`：其文件格式不是上述 API 合同，直接猜测二进制结构不可维护，也无法可靠绑定保存的 exe 与 AppUserModelID。

## 实现边界

`WindowsAppRecentSource` 先从保存的 exe 或无参数快捷方式确认真实 exe 身份，再仅选择对应 Adobe MRU：Photoshop、Illustrator、InDesign。每次最多读取 256 个注册表子键，不递归磁盘、不启动 Adobe 软件、不修改 MRU。

`RecentProjectService` 对结果按 MRU 时间降序排列，按不区分大小写的规范路径去重，只保留该软件明确格式和当前存在的普通文件，再限制为用户请求的 6–10 项。返回的随机令牌继续绑定客户端、项目、入口、保存的软件路径、解析后的 exe 和文档路径；打开前重新验证配置、软件身份、扩展名、文件与令牌时效。

After Effects、Premiere、Blender、Figma 目前返回“该软件暂未接入可靠的专属最近文件来源”，不会再用全局扩展名样本冒充软件历史。后续可在能可靠取得 exe 对应 AppUserModelID 时通过官方 `IApplicationDocumentLists` 扩展，不应直接解析 AutomaticDestinations 文件。

## 验证

专项命令：

`dotnet test native/tests/Luma.Host.Tests/Luma.Host.Tests.csproj --filter "FullyQualifiedName~RecentProject" --no-restore`

结果：32/32 通过，0 跳过。覆盖应用身份到 Adobe MRU 的映射、未知软件不读取共享历史、256 项上限、排序去重、6–10 条限制、路径与令牌复核、超时和取消不产生迟到打开。

这不是 Adobe 云文档历史，也不声称覆盖应用内部未写入 MediaBrowser MRU 的记录。本轮没有启动 Photoshop、Illustrator、InDesign 或第二个 Luma 实例。
