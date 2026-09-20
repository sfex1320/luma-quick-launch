# 第三轮交互修复：原生实现及单测证据

日期：2026-09-19。范围：`native/Luma.Host/**`、`native/tests/Luma.Host.Tests/**`。本报告不代表新发布包已经通过真实 Explorer 拖放验收。

## 已实施

- 每个显示器的 `NativeHotspotWindow` 都注册 Windows OLE `IDropTarget`；通过 `CF_HDROP` 的 `QueryGetData` 判定文件拖入并立即唤出。`DragEnter/DragOver/Drop` 均返回 `DROPEFFECT_NONE`。热区不读取路径、接收文件、复制移动删除文件或启动入口。离开/销毁按 UI 线程配对 `RevokeDragDrop`、`OleUninitialize`；保留目标对象避免 COM 生命周期丢失。
- 自动回收不再通过 `CurrentRects.Count > 2` 推断交互。`window.sync` 新增可选 `interacting:boolean`，未提供为 false；错误类型返回 `INVALID_REQUEST`。App 和 DockWindow 保存实际交互状态。
- 浮岛可见时检查鼠标五种按键及 Shift/Ctrl/Alt/左右 Windows 键的当前高位状态；历史按下低位不构成保持。实际 HWND 区域由 `GetWindowRgn/PtInRegion` 进行物理坐标命中，避免 Explorer 拖拽图层覆盖 `WindowFromPoint` 后误收起。原来的父 HWND 命中仍为补充。
- 回收先进入 `Closing` 并广播 `visibility=false`，前端退出过渡仍使用现有 HWND；收到 `expanded=false` 后最终隐藏。前端本地 Esc/收起按钮完成动画后的 collapsed 报告仍可关闭 Visible 状态。
- 回收期间交互或重新命中会发起 fresh reveal，停止退出定时器。`AwaitingExpanded` 忽略排队的 collapsed 报告，连命中区域也不清空。只有 fresh expanded 报告确认显示。
- 设置 800ms 单次退出确认期限；重复请求不延长，旧定时器 tick 不能使新退出提前完成。若全屏避让拒绝反向展开，兜底仍完成隐藏，避免停表后卡在 Closing。显式托盘/快捷启动唤出提供 3 秒离开检测宽限。
- 显示器变化是几何失效路径：广播 false、终结旧区域、重定位并重新建立 expanded 握手，旧 collapsed 不能取消新唤出。未增加隐藏状态的快速轮询，保留原有每秒全屏检查。
- CSS → HWND 区域仍按 WebView 实际 `devicePixelRatio` 转换；未启用全宽 HWND 的 DWM backdrop，未调整 native protocol=1 或启动路径验证。

## 最新验证

运行目录 `G:\Project\快捷启动项\native`，SDK `C:\Users\96311\AppData\Local\Microsoft\dotnet\dotnet.exe`。

```powershell
& C:/Users/96311/AppData/Local/Microsoft/dotnet/dotnet.exe test tests/Luma.Host.Tests/Luma.Host.Tests.csproj --filter 'FullyQualifiedName~WindowLifecycleTests|FullyQualifiedName~windowSync|FullyQualifiedName~OleHotspotTests' --verbosity minimal
# 已通过：32；失败：0；跳过：0。此轮出现一处测试的 ref/in 编译警告，随后修正。

& C:/Users/96311/AppData/Local/Microsoft/dotnet/dotnet.exe test tests/Luma.Host.Tests/Luma.Host.Tests.csproj --verbosity minimal
# 最新：已通过 120，失败 0，跳过 0，总计 120，持续时间 706 ms，exit code 0。
# 最新全套编译无警告。
```

已观察到交互类型验证回归在实现前失败（字符串 `"true"` 被旧路由错误接收），实现后通过。新增覆盖退出确认、反向展开、800ms 超时、旧退出 tick、10 种原生按键、默认/显式交互字段、文件与非文件 OLE 行为以及圆角物理区域。

`OleHotspotTests` 在专门 STA 线程创建两个实际 Win32 HWND；第二次注册分别返回 `DRAGDROP_E_ALREADYREGISTERED`，释放其一不影响另一；通过 COM `QueryInterface` 验证 IDropTarget 接口。文件拖入行为测试直接调用目标对象，并断言不读取文件数据、所有 effect 为 NONE。几何测试使用实际 `SetWindowRgn` 和原生物理命中，覆盖圆角与窗口内透明空白。

## 集成边界

本任务没有发布或重启用户实际应用，没有执行真实 Explorer 文件拖拽或 `Ole32.DoDragDrop`，也没有把 WM_MOUSEMOVE、浏览器 fake bridge、CDP DOM 状态当作系统拖放成功。新包的真实 WebView2 双屏拖放、退出视觉及实际宿主 `npm run test:native` 由主任务和独立 OLE 回归继续验证。800ms 期限依赖正常 UI Dispatcher 调度，不能保证被完全阻塞的 UI 线程按墙钟准时执行。

主要文件：`App.xaml.cs`、`Windows/DockVisibilityState.cs`、`Windows/DockWindow.cs`、`Services/EdgeActivation.cs`、`Bridge/BridgeRouter.cs`、`Interop/NativeHotspotWindow.cs`、`Interop/OleFileDropTarget.cs`、`Interop/DockHitTest.cs`、`Interop/Win32.cs`、`Properties/AssemblyInfo.cs`；测试为 `WindowLifecycleTests.cs`、`BridgeRouterTests.cs`、`Fakes.cs`、`OleHotspotTests.cs`。

## 复核补充：可见性代号防止旧帧成对迟到

独立复核指出，上述仅凭 `AwaitingExpanded` 的保护仍不足：旧展开报告 true 能先确认新唤出，随后旧退出 false 又把窗口关掉。已按第三轮计划文末预先约定的最小协议扩展完成修复。

- `DockVisibilityState.VisibilityId` 初始为 0；每个实际原生可见性命令生成新编号。正常展开、请求收起和显示器变更的立即 false 都分配编号；最终 HWND 隐藏不额外生成编号，因为它不广播新命令。
- `window.visibility.data.visibilityId` 随每次 App 广播发送。`window.sync.params.visibilityId` 通过原生路由传入 App，合法范围为非负 JavaScript 安全整数，最多 `9007199254740991`；字段缺失保留旧 protocol=1 客户端兼容，明确 null、字符串、布尔、负数、分数与超界拒绝。
- App 在应用任何命中区域、`interacting` 或状态确认之前比较编号。旧代与未来代消息都忽略；初始布局 0 不能确认后来发出的 1。状态机的 Acknowledge/ShouldHide 也带同样检查，防止未来调用绕开状态保护。
- 接口旧客户端不回传编号时只能获得原有保护，本版前端始终回传编号，完整竞态保护依赖前后端一起发布。

验证（2026-09-19，命令及目录同上）：

```powershell
& C:/Users/96311/AppData/Local/Microsoft/dotnet/dotnet.exe test tests/Luma.Host.Tests/Luma.Host.Tests.csproj --filter 'FullyQualifiedName~windowSync_InvalidVisibilityIdDoesNotReachWindow' --verbosity minimal
# 修改前红灯：7 个非法 visibilityId 案例均失败（旧路由错误接收）。

& C:/Users/96311/AppData/Local/Microsoft/dotnet/dotnet.exe test tests/Luma.Host.Tests/Luma.Host.Tests.csproj --filter 'FullyQualifiedName~WindowLifecycleTests|FullyQualifiedName~windowSync|FullyQualifiedName~windowVisibility' --verbosity minimal
# 修改后：43 通过，0 失败，0 跳过。

& C:/Users/96311/AppData/Local/Microsoft/dotnet/dotnet.exe test tests/Luma.Host.Tests/Luma.Host.Tests.csproj --verbosity minimal
# 本报告最新全套：135 通过，0 失败，0 跳过，持续时间 681 ms，exit code 0，无编译警告。
```

新增回归：旧 show/exit 的 expanded+collapsed 对不能确认或关闭新 show；新代可以确认；旧代交互报告在应用入口不获接受；显示器 invalidation 单独换代；最终隐藏不换代；未来编号拒绝；可选旧客户端字段兼容；广播及安全整数边界。此次修复后本原生子任务仍未发布或重启应用。独立 OLE 回归已向主任务报告上一发布包的实际 DoDragDrop 双屏结果，最终合并代号版本仍需由主任务重新构建并集成验证。
