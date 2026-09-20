# 浮岛前端交互修复

## 根因与实现

- 原来 `{visible && ...}` 立即卸载，没有退出阶段；现在 `visible` 控制目标，`present` 控制 DOM 保留。Web Animations 从当前计算样式反向，展开 200ms、回收 180ms；完成退出后才给原生 collapsed 确认。减弱动态效果为 0ms。
- 原生布局同步仍在 React 提交阶段安装观察器，动画中才按帧更新区域，没有增加常驻逐帧循环。
- 新增可选 `window.sync.interacting`，明确报告按压、拖放、等待文件导入、键盘焦点、堆叠/更多菜单。退出期间不报告 busy，避免与原生退出状态互相取消。
- 拖出延迟 120ms 清除拖放提示，避免跨子元素 DragLeave 的空 relatedTarget 导致闪断；松手后到导入完成保持操作状态。
- ResizeObserver 只在宽度变化时取消长按，不因提示/容器高度变化取消操作。

## 验证

2026-09-19：`npx playwright test tests/native-bridge.spec.ts tests/frontend.spec.ts` 13/13 通过，27.2s。包含：原有长按滑选、取消、窄屏与持久化；新增退出中保留 DOM 和 expanded、退出 70ms 帧反向第一帧完全相同、五次反复隐藏展开、按压/键盘/拖放显式 busy、导入 1000ms 延迟期间 busy、导入不启动。

`npm test` 当时 17/17 通过。此处为前端测试证据，宿主接口为测试替身；真实 HWND/OLE 验证另见第三轮总报告。

## 范围

用户说缩略图也可不插，本轮保留水晶图标；没有添加目录内容扫描、缩略图后台生成或新状态字段。

## 复核与原生回归后的追加

- `visibilityId` 通过 React state 绑定各次提交；旧动画回调只回传旧编号，原生不会将旧 expanded/false 组合视为新唤出。
- 真实宿主回归发现显式收起后立即重开。新增“鼠标停在收起按钮上”测试先失败（期望 0 个 dock，实际 1 个），定位为未撤销的 180ms 驻留定时器，以及动画上移触发 stationary pointerenter。
- 修复：Esc/收起按钮取消待执行驻留；wrapper 的 pointerenter 仅取消离开定时器；实际非零 pointermove 才触发前端退出反向。原生重入仍由物理区域检测和可见性事件驱动。
- 最新定向 `npx playwright test tests/native-bridge.spec.ts` 8/8 通过，20.3s；包含三次显式收起与驻留期限后仍保持隐藏。当前单元测试 18/18 通过。
