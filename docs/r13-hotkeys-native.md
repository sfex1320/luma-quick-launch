# 第十三轮快捷键原生实现记录

日期：2026-09-21。范围仅 C# 宿主、协议 DTO、原生单元测试；未启动/停止 GUI，未改用户配置，未打包或覆盖交付。

## 实现

- `Preferences.Shortcuts` 可选且最多 128 条；严格快捷键 DTO（未知字段拒绝、必需布尔字段不能遗漏），验证 code/action/scope、目标 ID、跨范围重复、Ctrl+K / Ctrl+Alt+Space 预留、全局必须修饰键和目录动作仅 panel。快捷键 ID 最长 128，目标 ID 最长 200。Win32 不区分 Enter/NumpadEnter，所以相同修饰键的两者也视为冲突。
- `ShortcutService` 通过消息 HWND + RegisterHotKey + MOD_NOREPEAT 注册；无键盘 hook/常驻轮询。替换或删除解除旧注册，不重用原生注册编号，陈旧 WM_HOTKEY 被忽略。配置变更在 Dispatcher 中重新读最新 StateStore，不闭包绑定旧状态。失败/失效状态可查询；失败不会降级成全局监听。
- 单键只由已有 Dock 焦点内前端传保存 ID 执行；宿主要求真实已接入 Dock、当前可见展开且前台 HWND 为 Dock。global 不能通过面板执行接口重放。所有入口动作共用 LaunchService 与既有窗口复用服务；至多 4 个快捷键入口操作在途，同一绑定在途和 250ms 内重复拒绝。
- 增加 `shortcut.getStatus({})`、`shortcut.execute({id})`、`shortcut.setRecording({active})`。入参严格白名单，不接受临时路径/命令/动作。目录四动作不进入 execute。panel 状态 registered=true 表示可用（并非系统全局注册）；中文 message 明确仅面板焦点时生效。
- 录制为宿主已接入且有焦点的 settings/dock 客户端所有的 30 秒租约；前端续期，录制时连同内置搜索暂停全局注册。其他客户端不能解除本客户端租约；客户端 detach、窗口 Deactivated、单次 DispatcherTimer 到期恢复。编号更新阻止暂停前排队消息恢复后执行。
- 项目/整理动作先唤出浮岛、恢复暂停文档并发送当前可见性编号，等待新 expanded 布局；只保留最新激活动作，核验保存绑定及目标，最多等待 5 秒，再仅向 Dock 发送 `shortcut.activated {id,serial}`。设置页不接收该事件；已显示的浮岛也要求新布局确认，避免与状态刷新竞态。
- Dock 原来 WS_EX_NOACTIVATE/Focusable=false 不允许点击取得键盘焦点；现允许点击激活，仍保留 ShowActivated=false 和所有 SWP_NOACTIVATE，悬停唤出不主动抢焦点。
- 新测试复现并修正共享启动服务中的慢探针竞态：文件存在检查期间删除入口后仍启动旧路径。现在 Shell 调用前重新核验当前入口路径/kind；快捷键还核验当前绑定及应用退出状态。

## 验证

先跑失败测试，再实现：

- ShortcutContractsTests 初次 8/8 失败，原因是配置被忽略、非法绑定被接受；实现后通过。
- ShortcutServiceTests 初次 5/5 失败，注册/执行/录制尚未实现；实现后通过。
- ShortcutBridgeTests 初次 3/3 失败，方法未路由、删除后仍启动旧路径；实现后通过。
- ShortcutActivationTests 初次最新激活取出失败；实现后通过。

最新定向命令（SDK 使用用户现有 `%LOCALAPPDATA%/Microsoft/dotnet/dotnet.exe`）：

```powershell
& "$env:LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test native/tests/Luma.Host.Tests/Luma.Host.Tests.csproj --no-restore --filter 'FullyQualifiedName~Shortcut|FullyQualifiedName~LaunchServiceTests|FullyQualifiedName~DockMessageQueueTests|FullyQualifiedName~ContractsTests|FullyQualifiedName~StateStoreTests|FullyQualifiedName~BridgeRouterTests|FullyQualifiedName~WindowLifecycleTests' --verbosity quiet
```

结果：178 通过，0 失败，0 跳过；耗时 990ms（测试执行时间），宿主构建成功。涵盖注册失败/解除/重复/保存目标与焦点/原始请求拒绝/录制所有权与过期/状态旧消息/队列不阻塞布局/保存和生命周期相关回归。

真实 RegisterHotKey 占用与释放、外部窗口组合键、Dock 点击焦点与面板单键、暂停恢复后的事件及真实 Shell 复用仍需主代理独立配置 GUI 集成验证；上述单元测试不能作为实际 Windows 集成成功证据。5 小时长测与 WebView 内存限制没有新增通过结论。

## 独立审查 P2 修复：STA 排队与查找期间撤销授权

审查指出：仅在文件探针后重验仍存在时间窗口，真正的 Shell 复用会在单 STA 队列内等待、识别并查找窗口；期间配置变化或退出可能导致迟到启动/激活。

修复：保存入口路径/kind 的不可变值快照，将实时授权函数从 LaunchService 经 IShellExecutor、RealShellExecutor 的排队闭包传到 WindowReuseService。工作线程开始、Identify 后、Find 后、Launch 前均重验；恢复已最小化窗口的等待期间及实际前置之前也重验。正常 shell.openItem、search、folder、recent 的旧入口和 CancellationToken 重载保留；生产仍共用一个有界 STA 队列。没有新增轮询线程或 GUI 操作。

新 RED/GREEN 证据：

- Find 内撤销授权后的新启动/已有窗口激活，以及已进入真实共享 STA 队列后撤销授权，初跑 3 失败；传递与执行实时授权后 3 通过。
- 恢复最小化窗口等待中撤销，初跑仍发生 Activate；新增等待后的授权检查后通过。
- 6 个 LaunchService→RealShellExecutor→WindowReuseService 案例，在阻塞 Find 时删除 binding、改 item 路径、设置退出状态，分别覆盖 Launch/Activate。最初路径修改两例暴露捕获可变 item 对象的问题，改为路径/kind 值快照后全通过。

最新回归：原先定向集再增加 WindowReuseServiceTests、WindowActivationTests、FolderServiceTests、SearchServiceTests、RecentProjectServiceTests，**260 通过、0 失败、0 跳过，2 秒测试执行时间**。No GUI；真实 Windows 集成结论仍由主代理补充。

## 真实快捷键 smoke 脚本（已编写，未运行 GUI）

文件：

- `native/tests/thirteenth-hotkeys-smoke.mjs`
- `native/tests/Invoke-ShortcutInput.ps1`
- `native/tests/ShortcutInputNative.cs`
- `native/tests/ShortcutInputProbe.cs`

执行入口仅接受显式 `LUMA_TEST_EXE`；不默认为 APP，不在脚本中关闭已有 Luma。主代理需先审核，再在唯一测试 EXE、其他 Luma 全部退出且无全屏应用的窗口运行。示例：

```powershell
$env:LUMA_TEST_EXE='G:\Project\快捷启动项\APP\Luma\Luma.exe'
node native/tests/thirteenth-hotkeys-smoke.mjs
```

脚本首先进行完整可执行路径空闲检查、其他 Luma 进程检查及前台非全屏检查，在每次注入输入前重复独占/全屏检查。所有窗口动作校验 PID、完整 EXE 路径、创建时间及 HWND 所有者；物理点击先核验 WindowFromPoint 命中本测试窗口。modifier 被真实用户按住时拒绝注入，并以 finally 释放本次输入的按键。GUI 只使用传入 Luma、临时编译的外部 WinForms TextBox 和同一夹具复制成的专用启动目标，不打开 Explorer、用户文件或用户软件。

预定检查：

1. 外部夹具先 RegisterHotKey 占用 Ctrl+Shift+Alt+G；真实宿主状态必须报告失败。解除占用并经真实 app.saveState 重注册后恢复；纯 Q 能由独立短时 RegisterHotKey 探针注册，证明宿主未全局占用它。
2. 外部窗口真实 SendInput 全局 G（10 次持按重复，75ms 间隔）只启动专用目标一次，后续组合键复用原窗口；外部 Q 仍记录为普通 TextBox 按键，不启动。
3. 通过实际收起按钮折叠并在宿主日志确认“浮岛 WebView 已暂停”（没有用 CDP 读取暂停中的页面来唤醒），再由外部物理组合键打开已保存组。仅观察真实宿主事件，核对持按只发一条、重新暂停唤出后 serial 增长。
4. 实际点击堆叠空白区域，检查原生前台 PID 与 document.hasFocus；面板 E 持按仅一次编辑事件，Q 启动一次新专用实例。
5. 实际进入设置的录制输入框；验证注册暂时解除，录入已绑定组合键时目标没有被激活，录制失焦后恢复。
6. 经真实保存将 G 改绑 H，核验旧注册释放、旧键留在外部窗口、新键复用保存目标。正常退出后 H/P/Q 均可重新注册。

配置/状态 RPC 使用真实 WebView 的 chrome.webview，只用于 app.getState/app.saveState/shortcut.getStatus；动作不调用 shortcut.execute、不生成 fake host event、不使用合成 DOM KeyboardEvent。事件监听器仅记录真实宿主投递。

清理只针对本测试启动/记录的进程，完整 EXE 路径和创建时间必须仍一致；先优雅退出、必要时仅终止该 PID，绝不按名称结束进程或进程树。测试目录、JSONL 日志、SHA256、结果 JSON 保留作为证据；PID 身份核验失败会保留异常并标记清理未完成。

静态验证：`node --check` 通过；PowerShell Parser AST 无错误；两份 C# 用 Windows PowerShell Framework 编译通过；WinForms 夹具 WindowsApplication EXE 成功编译但未启动，静态编译产物已删除。**没有调用任何 Win32 操作方法、没有运行该 smoke、没有 GUI 验收通过结论。** 字母单键测试要求该测试面板没有处于 IME composition，录制/面板 IME 行为按产品规则跳过，不由脚本强改用户输入语言。
