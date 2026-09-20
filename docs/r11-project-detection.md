# 第十一轮：项目启动自动识别与窗口复用只读审查

日期：2026-09-21。本文第一阶段只修改 `ProjectTestService.cs` 与对应单元测试；后续主任务扩展授权增加最近项目服务和窗口前置确认修复，见文末。不运行或修改 MOMO，也不操作原生 GUI。

## MOMO 根因与识别规则

只读检查 `G:\Project\momo智能画布`：根 `package.json` 没有 `scripts.test`，有 `dev: vite`、`tauri: tauri` 和 `devDependencies.@tauri-apps/cli`；只有 `pnpm-lock.yaml`。`src-tauri/tauri.conf.json` 的 `build.beforeDevCommand` 为 `pnpm dev`，`devUrl` 为 `http://localhost:1430`。该项目 README 第 12 行和 AGENTS 第 37 行也明确开发运行命令为 `pnpm tauri dev`。旧服务只接受 Node 的 `scripts.test`，因此不提供操作入口。

本轮仍使用 `project.detectTest` / `project.runTest`，字段名与 protocol=1 不变：

- 已保存根目录 `launch` 继续优先，标签为“手动启动”，子目录不继承根覆盖。
- Node 保留有效 `test` 优先，返回“测试软件”。没有有效 `test` 时，依次使用明确的 Tauri 开发入口、`dev`、`start`，返回“打开项目软件”。不猜测 `build`、`preview` 或任意脚本名。
- Tauri 需同时具备精确 `scripts.tauri = tauri`、明确 CLI 依赖以及 `src-tauri/tauri.conf.json` 中有效开发配置。MOMO 因而得到 `pnpm run tauri dev`，其参数与项目文档的 `pnpm tauri dev` 等价，不会只启动 Vite。
- 包管理器仍须与锁文件和声明一致；多种项目类型无法确定唯一入口时返回空。

此行为是第十一轮用户需求对旧协议第九轮“dev/start 暂不支持”的补充；主任务需将本规则加入协议附录。本文件不替代主协议。

## 边界

识别只读固定元数据，绝不执行清单脚本；点击仍以宿主签发的 taskId 关联客户端、项目、入口和当前目录令牌。保存配置和识别均不启动程序。终端只接收宿主确定的工具与固定参数，不拼接清单里的脚本文本。

Tauri 的主配置、Windows 配置、`src-tauri/Cargo.toml`、`src-tauri/Cargo.lock` 全部参与重新识别指纹；即使首次不存在，新增也使旧令牌失效。清单限 256 KiB、锁文件限 2 MiB；`src-tauri` 父目录和清单文件都拒绝重解析点。未增加递归扫描或后台轮询，仍为 2 工作线程、4 秒默认超时、最多 128 个 2 分钟任务令牌、同工作目录最多一个活动或待启动终端。

自动终端增加 `CARGO_NET_OFFLINE=true`，并保留 Corepack 禁联网和 Go 的禁恢复配置；不会自动安装 npm 依赖或联网恢复 Cargo。缺依赖时需要用户准备环境后重试。手动命令保持用户保存的语义。上述约束不把项目脚本变成沙箱：用户明确点击后运行已有项目脚本，脚本自身的行为仍由项目定义。

## 验证

新增测试先验证旧实现失败：项目服务 33 项中 11 失败、22 通过，失败原因是没有 dev/start/Tauri 入口或旧标签。最终新增 14 个用例，覆盖 dev/start 顺序、test 保留优先、空 test 回退、MOMO Tauri 分支与工作目录、防重复、Tauri 四类元数据变更、CLI/脚本/配置要求、包管理器歧义、配置格式及大小限制。

最新完整原生单元测试：

```text
dotnet test native/tests/Luma.Host.Tests/Luma.Host.Tests.csproj --no-restore --verbosity minimal
已通过! - 失败: 0，通过: 367，已跳过: 0，总计: 367，持续时间: 4 s
```

`git diff --check` 通过。单元测试包含原有权限令牌、手动覆盖、超时不迟到执行、目录去重和隐藏终端夹具验证。本次没有运行 MOMO 项目脚本、安装依赖、调用 Luma GUI 或启动 Photoshop；这些结果不能代替真实 Windows 集成验收或 5 小时驻留验证。

## Photoshop 窗口复用只读审查

检查时 `Get-Process -Name Photoshop` 没有结果，因此未复现用户的 Photoshop 前置问题。后续获主任务授权修复独立可复现的成功判定缺口，但不声称 Photoshop 已真实验收。

- 已有正确路径：无参数 `.exe` 或指向它的 `.lnk` 使用完整进程映像路径匹配；已找到窗口时，即使 Windows 拒绝前置也不会重新启动。最小化恢复有有限等待，未设置永久置顶。
- 可核查的成功判定缺口：原 `WindowActivation.cs` 使用 `accepted || foreground()`，因此 `SetForegroundWindow` 返回 true 时，不再确认目标是否实际成为前台窗口。新增用例先得到明确失败（期望 false，实际 true），再最小修复为实际检查 foreground HWND，保留原有恢复和有限等待；这仍不是 Photoshop 问题的实测根因。
- 其他受限边界：`WindowsWindowReusePlatform.cs` 第 110 行跳过所有有 owner 的 HWND；完整路径只有词法规范化，未归一 8.3/链接别名；带参数快捷方式及启动器仍保留原 Shell 语义。没有证据表明当前 Photoshop 入口属于这些情况，应在真实目标存在时核实保存入口、实际进程路径、可见主窗口及 foreground HWND，不能凭窗口标题猜测。

## 后续授权：软件最近项目

新增 `RecentProjectService.cs` 与桥接 `shell.getRecent({projectId,itemId,limit})`、`shell.openRecent({projectId,itemId,entryId})`。get 返回 `{entries:[{id,name,path,kind}],note}`，open 返回 `{accepted}`；只接受严格规定字段，limit 必须为整数 6–10。桥接不接受文件路径或命令，构造函数新增可选依赖，原调用兼容。

生产源读取 Windows Recent 顶层最多 256 个目录项中的 `.lnk`，在这个有界样本内按快捷方式修改时间降序排列、按目标路径去重。只关联已保存 kind=app 的 `.exe` 或无参数 `.lnk` 解析出的 exe 文件名：Photoshop→psd/psb，Illustrator→ai/eps，InDesign→indd/idml，AfterFX→aep/aepx，Adobe Premiere Pro→prproj，Blender→blend，Figma→fig。不依据入口显示名或窗口标题；未知软件不扫描，不把通用 png/jpg/pdf 分配给任意软件。Affinity 的通用进程名 Designer/Photo/Publisher 不足以明确身份，本轮不猜测。

Recent 本身不是软件使用记录：这是按格式关联 Windows 最近文档，不保证“最后由此软件打开”，不读取私有 MRU、不读取 Jump Lists。note 会明确这一点及 Windows 默认文件关联的打开方式。只有文件进入结果；不能可靠归属某软件的普通文件夹不会出现。

文档文件和父目录拒绝重解析点；拒绝相对、非规范、设备、ADS、URL 路径以及带参数 Recent 快捷方式。软件 .lnk 限 1 MiB。令牌绑定客户端、项目、保存入口、保存路径、解析后的软件路径和目标路径，2 分钟过期、最多 512 个；打开时重新验证软件身份、保存配置、文档存在和路径。Detach 取消进行中的读取/打开并撤销令牌。

使用固定 2 个后台 STA、16 个排队槽、4 秒调用超时；挂起的 Shell/网络 IO 不新建替代线程。`RealShellExecutor.TryLaunch` 新增兼容取消令牌重载，仍复用唯一共享启动 STA 与窗口复用服务，超时/Detach 会传递到其最终启动前检查。get 不产生启动副作用；open 使用 Windows 文件关联，不写用户最近记录，也不拼接目标 exe 命令行。

最近项目测试包含真实 Windows COM `.lnk` 临时夹具（仅临时目录创建无执行内容的 exe/psd/lnk，不运行软件），以及格式过滤、排序去重、未知软件、256/10上限、跨客户端/入口、配置/解析目标改变、删除/过期/Detach、非法路径、读取和打开超时取消、严格桥接参数与单响应测试。完整最新计数由主任务最终验收输出记录。

本分工最后一次组合测试 **124/124 通过、0 跳过**（约 1 秒）：过滤 `RecentProject`、`WindowActivationTests`、`WindowReuseServiceTests`、`ProjectTestServiceTests`、`ManualProjectLaunchTests`、`ProjectTestBridgeTests`、`ProjectTestTerminalExecutionTests`。其中真实快捷方式夹具发现并修复新代码中 `dynamic` COM 类型信息缓存与手动释放 RCW 的冲突，改为反射调用 IDispatch 成员。并行开发期间一次全套结果为 441 项、6 失败：此 COM 失败已在上述组合验证解决，其余属于其他分工仍在修改的 SearchService / FolderMutationService；不能把第一阶段 367 通过表述为新增全部功能最终全套通过。
