# 第十三轮手势与成组入口子任务记录

2026-09-21。仅实现前端交互；没有操作实际用户 APP、安装包或用户文件，没有执行原生 GUI 验收。

## 已实现

- 新增 `useBlankPan`，组入口网格、真实目录每一层网格及软件最近列表共用左键空白抓手。纵向位移达到 7px 后滚动；抓取时显示 grabbing，松手、失焦、pointercancel、lostpointercapture 和卸载时清理捕获。按钮、链接、文本输入、可编辑内容及 draggable 入口不启动抓手；滚轮行为保留。
- 主工具条继续使用原 `useRightPan('x')`，右键水平拖动、静止右键菜单未改。子菜单不再用右键滚动，普通右键菜单保留。
- 多入口且包含至少一个软件的组：悬停仅全宽“进入”，快速单击及键盘激活直接进入组，避免启动第一个软件。单软件仍“打开软件/最近”，纯文件夹入口及纯文件夹组仍“打开/进入”。
- 移除顶栏图标角标展开按钮及编辑态组成员角标展开按钮。编辑 X、移到主面板、已有右键进入、长按与拖拽保留。
- 没有修改 650/600ms 动效、固定 88×94px 目录格、协议或原生代码。

## 文件

`src/components/useBlankPan.ts`；`Dock.tsx`；`SplitFolderTile.tsx`；`GroupEntryMenu.tsx`；`FolderBrowser.tsx`；`RecentProjects.tsx`；`folder-browser.css`。

新增 `tests/thirteenth-gestures.spec.ts` 六项 Edge 测试。现有 cascade、directory-actions、dock-folders、eleventh-shortcuts、motion 测试中的已删除箭头选择器迁移到现有 ArrowDown 入口；需检验鼠标离开收回的场景使用真实悬停进入点击。旧目录右拖和软件合组快点直接启动断言同步更新为本轮行为。

## 验证

先运行新增测试，旧实现得到 4 failed / 2 passed：确认软件组两按钮、组快点启动首软件、左键空白不滚动，以及抓手清理行为尚不存在。实现后新增六项在相关回归中全部通过。

相关回归命令：

```powershell
npx playwright test tests/thirteenth-gestures.spec.ts tests/cascade.spec.ts tests/dock-folders.spec.ts tests/eleventh-shortcuts.spec.ts tests/directory-actions.spec.ts tests/motion.spec.ts --config=playwright.r13-gestures.config.ts --reporter=line
```

首轮 71 项耗时 3.0m，68 passed，3 failed 为上述旧行为断言或选择器迁移改变输入模式导致。修正测试后针对重跑：

```powershell
npx playwright test tests/directory-actions.spec.ts tests/dock-folders.spec.ts tests/eleventh-shortcuts.spec.ts --grep 'right-button movement|removing a top-level project|an idle submenu|directory right-drag' --config=playwright.r13-gestures.config.ts --reporter=line
```

4 passed（10.9s）。此轮另保留了软件最近 ID 启动、三级长按滑选、编辑拖入/拖出、真实磁盘操作确认、固定格布局及动效相关回归。

默认 127.0.0.1:5173 在本机报 listen EACCES；临时测试配置使用 127.0.0.1:4173，可供主任务集成验证复用，最终由主任务整理。测试浏览器为独立 Edge 上下文和桥接替身，这些结果不代表真实 Windows 原生集成通过。

`npx tsc -b` 在交接时受并行快捷键协议扩展影响，仅报告 `src/useWorkspace.ts:26` 未收窄新增事件导致 `data.visible/visibilityId` 类型错误；已告知主任务处理。最终构建及全套集成验收由主任务进行。

原生测试仍引用已删除角标的四处位置已交主任务迁移：`native/tests/native-smoke.mjs:99`、`twelfth-features-smoke.mjs:145,217`、`tenth-features-smoke.mjs:44`。
