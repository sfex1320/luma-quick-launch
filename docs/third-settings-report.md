# 第三轮设置页拖放实现与验证

2026-09-19。本报告仅覆盖设置管理页与项目编辑器；浮岛动画、原生 OLE、退出握手和发布包由本轮其他任务验证。

## 根因与实现

此前外部文件拖放仅由 `Dock` 接收。驾驶舱 `.cp-content`、项目卡片和 `ProjectEditor` 均未绑定外部文件事件，所以设置页拖目录/软件不会导入。

- 管理页内容区空白接收外部 `Files`：通过现有 `shell.resolveDrop` 与 WebView `postMessageWithAdditionalObjects` 解析真实 File 对象，随后复用 `addShortcuts` 添加独立快捷项。
- 现有项目卡片接收相同文件：只追加到该堆叠，不覆盖原入口；嵌套目标停止冒泡，避免卡片与管理区重复导入。
- 管理窗口的全局外部文件 `dragover/drop` 禁止默认导航；文本路径不冒充文件，浏览器预览明确提示改用桌面程序，不生成路径。
- 项目编辑弹窗支持批量追加：仅替换未修改的新项目初始空白占位；保留已有入口及 ID；按 Windows 路径大小写、斜线与末尾分隔符去重，提示跳过数。
- 200 入口上限按当前草稿计算，超限整批拒绝，不截断。现有协议单次返回至多 100 个导入项，因此单次拖入超过 100 先明确拒绝，可以分批添加至 200。
- 解析期间允许继续编辑；结果合并到最新草稿，关闭后的迟到回调忽略。保存按钮等待解析结束。入口选择器按入口 ID 定位，若等待期间该入口被修改或移除则不覆盖。
- 卡片右上增加拖动排序手柄，自定义 MIME 与外部 Files 分离；原有前后移动按钮保持可用。
- 说明文案明确：项目是逻辑分组，可绑定 5 个目录，也可混合目录、软件与文件，最多 200 个入口；单击默认首项、长按选择其他项，实际文件不会移动。
- 本次未修改原生代码、协议字段、主样式文件、水晶图标和用户配置。

## 验证证据

回归测试先观察到原缺陷：`management blank and project card drops save once without launching` 在管理区拖放后等待新项目出现超时，退出码 1。草稿辅助函数的初始实现另有 2 项失败，分别证明追加/去重与容量拒绝缺失。

最新设置相关验证：

```text
npx playwright test tests/settings-drop.spec.ts tests/native-bridge.spec.ts --grep 'management blank|editor drop|closing a draft|browser preview|editor append|project drag|dragging text|bulk file replacement' --reporter=line --output=test-results-settings
8 passed (18.8s)
```

覆盖空白独立导入、已有卡片追加 5 目录且只解析一次/不启动、草稿占位替换/去重、解析等待期间保留名称描述编辑、取消并重新打开草稿的迟到响应隔离、浏览器模式与默认导航、199→200 与超限无损、既有 ID 保留、内部拖动排序/箭头顺序控制、拒绝文本路径、原有选择器批量替换超限。

```text
npm test -- src/core/draftShortcuts.test.ts src/core/shortcuts.test.ts
Test Files 2 passed (2)
Tests 5 passed (5)

npm run build
tsc -b && vite build
exit code 0
```

## 验证边界

Playwright 使用本机 Edge 的独立上下文、仅绑定 127.0.0.1 的 Vite 服务和测试专用 WebView 桥接桩，测试文件对象由 DataTransfer 构造。它证明 React 导入、保存和错误路径，不代表 Windows 资源管理器真实 OLE、真实磁盘路径解析、双屏热区或 WebView2 宿主集成已经验收。那些结论须结合本轮原生宿主测试证据。
