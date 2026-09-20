# 第三轮独立代码复核

日期：2026-09-19。范围：浮岛过渡与交互保持、设置页拖放与草稿追加、原生退出状态机、OLE 热区、物理命中与 DPI。只读复核实现，未操作 UI，未重复跑已交由主任务验证的测试；本报告不是实际宿主/OLE 验收结果。

## 发现：P1，退出队列中的旧 expanded 能误确认新唤出

位置：`native/Luma.Host/App.xaml.cs` 的 `IWindowHost.SyncDock`（本次阅读时 269–274 行）；`native/Luma.Host/Windows/DockVisibilityState.cs` 的 `Acknowledge` / `ShouldHide`（22–26 行）。

`AwaitingExpanded` 只拒绝先到的 collapsed，无法判断先到的 expanded 是否属于这次唤出。`DockWindow.OnWebMessage` 会串行等待每条 sync 的 `ExecuteScriptAsync(devicePixelRatio)`，所以旧消息可排队跨越原生重新进入事件。可达时序：

1. 正常 Visible 后开始 Closing；退出动画上报旧 expanded=true，再上报结束的 expanded=false，两条尚未应用。
2. 原生重新命中并 RequestShow，进入 AwaitingExpanded，发送新 visibility=true。
3. 旧 expanded=true 被处理，错误转为 Visible。
4. 旧 expanded=false 被处理，调用 CompleteDockHide。
5. 新的 expanded=true 终于到达，但状态已 Hidden，不能再次 ShowDock；用户看到重新进入后仍然消失。

现有 `QueuedCollapsedReportsCannotCancelReveal` 测试只覆盖先 false 后 true，未覆盖旧 true、旧 false、新 true 的序列，因此无法发现此问题。此项通过实际实现分支和异步队列的代码追踪确认；未宣称已在真实 UI 中复现。

建议最小兼容补充可选 `visibilityId`：每条宿主 visibility 事件递增，前端在布局 effect 的闭包里保留收到的 ID 并原样回送 window.sync；宿主在 ApplySync 前拒绝与当前代不同的消息，不能让旧帧更新区域/交互状态或完成显隐。保留本地 Esc/收起按钮使用最近 ID 的路径。回归需覆盖旧 true/旧 false 跨新唤出以及跨新退出的组合。已将发现提交主任务与原生实现负责人处理。

## 其他结论及边界

未发现其他需要阻止发布的重要缺陷：OLE IDropTarget 的方法签名与 IDataObject QueryGetData 使用、注册持有/释放配对、效果 NONE；设置嵌套目标阻止冒泡、真实 File 对象路径、等待期间草稿合并与关闭隔离、200 容量与去重；物理 region 命中及 CSS devicePixelRatio 转换；拖放/按压/导入保持和退出动画 DOM 延迟均与本轮目标一致。

仍需结合主任务最新构建、原生集成与实际 OLE 证据；特别是修复上述消息代际问题后，应重新检查该路径并更新本报告状态。

## 复核更新：P1 已在代码层面闭环

同日对 visibilityId 修复进行定向二审，结论：上述 P1 的消息代际路径已修复，未发现新增阻塞项。

- `DockVisibilityState.RequestShow`、`RequestHide`、`RequestImmediateHide` 都在发送对应事件前递增安全整数 ID。检索全部生产 `BroadcastVisibility` 调用，三处均携带当前 ID；最终 HWND Hide 不发送新事件，因此保持当前 ID 合理。
- `App.SyncDock` 在 `ApplySync` 之前调用 `AcceptsSync`，过时代既不更新窗口区域/交互状态，也不确认显示或隐藏。当前代的本地 Esc/收起 collapsed 仍然可完成隐藏。
- `Dock` 的 ID 是 React state，并列入布局 effect 依赖；每个回调使用所在提交的 ID，旧退出回调不会读取更新后的 ID。默认 0 与宿主初始 0 一致；第一次 LayoutReady 触发 show 后，初始 sync 也不会确认该次新唤出。
- 新增原生测试明确覆盖旧 show/exit 的 true 和 false 跨新 show，以及 fresh true 后继续拒绝旧 false；另覆盖显示器变更 invalidation、当前代本地 collapsed、可选旧客户端字段、边界与错误类型。协议保持 1；缺少字段的旧客户端仍按兼容路径处理，代际保护适用于本轮新版前后端组合。

验证证据：主任务已提供当前前端单测 18 通过、带 visibilityId 的 native-bridge 浏览器测试 7 通过；本次二审已阅读对应前端与原生新增断言，没有重复执行。原生全套最新数量由原生实现报告补充，本报告不把此前 120 项结果当作本次 ID 修复后的证据。真实 HWND/OLE 发布验收仍以主任务最新集成报告为准。

## 最终定向复核：显式收起后驻留指针误重开

同日再次只读检查 Dock 的小范围修复，未操作 UI 或干扰正在进行的真实宿主回归。结论：修复与已复现原因一致，未发现阻塞项。

- Esc 与把手点击都会撤销未触发的 180ms showTimer，避免显式收起后旧驻留计时器反向打开；把手同时清理 hideTimer。
- wrap 的 pointerenter 仅清理隐藏计时器，不再因为退出动画把元素移动到静止指针下而重新展开。退出中的实际 pointermove 只有 movementX/Y 非零才可反向；外部 Files dragover 和原生几何命中唤出仍保留。
- 浏览器回归覆盖把手点击后指针保持原位、超过驻留时间仍关闭，并连续执行三轮。主任务已提供修复前该测试失败、修复后 native-bridge 全 8 项通过（包含中间帧反向）的最新输出；原生实现未因此修改，原生当前全套 135 项通过。真实宿主本轮最终结果仍由主任务记录。
