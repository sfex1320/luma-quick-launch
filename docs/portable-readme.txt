Luma Quick Launch 0.1.0 — Windows x64 便携版

1. 完整解压压缩包到固定目录，不要在压缩包内直接运行。
2. 双击 Start-Luma.cmd 打开管理窗口，或运行 Luma.exe 在托盘启动。
3. 拖入文件夹、软件快捷方式或文件，建立快捷项。
4. 鼠标悬停在任意显示器顶边居中位置，唤出快捷面板。
5. 单击打开；长按展开，滑到目标后松手打开，移出取消。
6. Ctrl + Alt + Space 打开独立搜索；托盘菜单可打开设置或退出。

运行条件
- Windows x64；当前实测环境为 Windows 11 双屏。
- 已包含 .NET 运行时，使用者不需要安装 Node.js 或 .NET SDK。
- 需要 Microsoft Edge WebView2 Runtime。如果提示缺失，请从微软官网安装：
  https://developer.microsoft.com/microsoft-edge/webview2/

配置与升级
- 配置保存于 %LOCALAPPDATA%\Luma，不在本压缩包中。
- 升级时先从托盘退出旧版，将新版完整解压到新目录，再启动。
- 请保留整个程序目录；不要只移动 Luma.exe。

功能边界
- 文件与正文搜索依赖 Windows 索引，不自动扫描全盘。
- 已打开的普通文件夹和可安全识别的软件窗口会优先恢复并前置。
  带参数的快捷方式和文档保留系统原有打开方式，不能保证所有软件均可复用。
- 浮岛使用前端玻璃样式，尚未启用真实桌面 Acrylic 背景模糊。
- 程序未签名，首次运行可能显示 Windows 安全提示。

项目与更新：https://github.com/sfex1320/luma-quick-launch
第三方许可随包放在 licenses 目录。
