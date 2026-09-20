# 第五轮动效证据与修复

2026-09-19。范围为 `useDockMotion`、原生退出兜底和异步图标请求对布局队列的影响；没有发布或重启用户的现有 Luma。

## 已确认的旧版行为

独立数据目录启动已发布的真实 Windows Host，以 CDP 读取真实 WebView2；未改系统设置，未移动物理鼠标。退出使用 WebView 键盘 Escape，再次显示使用该独立数据目录的原生单实例激活信号。测试结束关闭自身 Host，用户配置前后 SHA256 一致。

旧版采样摘要记录如下；最新发布后的完整逐帧记录见 [fifth-native-motion-after.json](fifth-native-motion-after.json)，可重跑脚本为 `native/tests/native-motion-probe.mjs`。本文件是子任务阶段记录，最终发布和双屏物理交互验收以 [第五轮修复报告](第五轮修复报告.md) 为准。

- Windows 注册表 `MinAnimate=0`，但该真实 WebView2 中 `matchMedia('(prefers-reduced-motion: reduce)').matches=false`。因此不能把系统减弱动效认定为本机“突然出现”的根因。
- 真实动画入场 360ms，出场 300ms，存在连续 transform 变化，并非完全没有动画。
- 旧版退出约 88ms 已降到 opacity≈0.50，175ms 时面板底边只剩 0.56 CSS px 在屏幕内，opacity≈0.126；后半段动画基本不可见。短时位移叠加淡出是可证实的视觉问题。
- 原代码在系统媒体查询为 reduce 时确实强制 duration=0。这个条件问题另由浏览器回归复现；不能将该模拟结果说成本机真实 WebView2 已触发。

## 修改

- 入场 650ms、出场 600ms，使用对称缓入缓出 `cubic-bezier(.42,0,.58,1)`，opacity 全程保持 1，仅纵向 transform，不缩放、不回弹。隐藏位置仍根据主面板及展开面板的完整底边计算。
- 初次原生 expanded 布局确认后，通过两个一次性 requestAnimationFrame 保留隐藏首帧再播放。反向操作取消待播帧，直接从当前显示位置反转。
- 根据本轮用户明确要求显示完整滑动，浮岛减弱动效仅由 Luma 软件内 `reducedMotion` 控制；没有更改 Windows 系统设置。软件开关为 true 仍立即完成。此为用户要求对原先系统继承策略的覆盖。
- 原生状态截止时间与 DispatcherTimer 共用 `CloseTimeoutMilliseconds=1400`，给 600ms 退出及调度/回传留出余量。正常仍由当前 visibilityId 的 collapsed 确认结束，保留过期消息守卫。
- `shell.getIcon` 与目录 IO 一样独立路由，慢图标提取不能阻塞 `window.sync`。完整协议校验仍由 BridgeRouter 执行。
- 保持既有固定扫过区域，不添加逐帧 `window.sync` 或 `SetWindowRgn`。

## 最新验证

先改测试、运行旧实现：motion 3 失败 / 2 通过（650/600ms、系统 reduce 下显式动效）；原生针对性测试 2 失败 / 32 通过（1400ms 兜底、慢图标不阻塞布局）。随后修改实现。另一次先行回归验证非对称旧曲线在 600ms 退出的 400ms 时 bottom=-6.08px 已出界，位移对称断言与 400ms 仍可见断言均失败；再改为对称缓入缓出。

- `npx playwright test tests/motion.spec.ts --output=test-results/fifth-motion --reporter=line`：5/5 通过。覆盖完整对称位移、退出 400ms 仍可见、恒定透明度、ack 等待、反转连续、无逐帧 sync、软件减弱动效、系统 reduce 时软件显式动画、宽堆叠扫过区域。等待条件明确要求动画 finished，避免把等待布局的 paused 当作完成。
- `dotnet test native/tests/Luma.Host.Tests --filter 'FullyQualifiedName~WindowLifecycleTests|FullyQualifiedName~DockMessageQueueTests' --verbosity quiet`：34/34 通过，62ms。

上述前端测试有桥接模拟，不能代替发布后的真实 Windows 集成验收。新版候选 Host 的最终真实动效证据由主任务串行验证；脚本支持 `node native/tests/native-motion-probe.mjs <候选Luma.exe> <输出JSON>`。它会记录真实 WebView2 媒体查询、入退出逐帧坐标/透明度/时间、用户配置不变结果，并清理自身进程。
