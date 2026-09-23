# Luma Quick Launch · 项目触手可及

Windows 顶边快捷启动浮岛：把常用文件夹、软件和文档收进一条磨砂面板，长按展开堆叠，滑向目标，松手打开。

A compact top-edge quick-launch dock for Windows: group folders, apps and documents into a frosted panel — long-press, slide, release to open.

## 功能

- **项目堆叠与级联目录**：项目是一组快捷引用的逻辑分组；文件夹入口可逐级展开（最多 12 层），当前目录名与返回在面板顶栏，底部固定"打开当前目录 / 新建文件夹 / 复制地址 / 移动"动作。
- **模糊搜索**：Ctrl+K 或顶栏搜索框——软件别名（AI、PS）、保存项轻度模糊、Windows 索引文件名与正文；面板内另有"筛选当前菜单"即时过滤。
- **快捷键**：单键仅面板有焦点时生效，组合键可全局；编辑模式下磁贴右下角显示快捷键徽标。
- **收藏区**：整理模式下点星收藏项目或目录项，收藏区域固定在主栏右侧。
- **手势**：单击直达主目录；长按 300ms 展开堆叠、滑选松手打开；右键拖动平移所有子菜单，原地右击打开菜单。
- **外观**：宽高、图标、圆角可调；实色 / 清透 / 强磨砂三种材质（明暗双主题，强磨砂带颗粒感）；动画速度三档，一级菜单先滑入/滑出，二级菜单随后更快滑动；回收期间移回一级或二级菜单均可唤回。
- **软件内更新**：设置与备份页底部检查 GitHub Releases，下载强制 SHA-256 校验；便携版自动替换重启，安装版运行安装程序。
- **系统集成**：托盘驻留、单实例、可选开机启动与桌面快捷方式（默认关闭）；浮岛位于普通窗口上方，不申请管理员权限。

## 下载与安装

见 [Releases](https://github.com/sfex1320/luma-quick-launch/releases)。两种版本：

- **便携版**：`luma-quick-launch-*-win-x64.zip`，完整解压后运行 `Luma.exe`（不要只复制 exe）。
- **安装版**：`luma-quick-launch-*-setup-x64.exe`，当前用户安装（无需管理员），创建桌面/开始菜单入口，卸载保留配置。

需要 Windows 10 22H2+ 与 Microsoft Edge WebView2 Runtime（安装版会自动引导安装），无需 Node.js 或 .NET 运行时。当前为**未签名测试版**。

## 开发与构建

```powershell
npm ci                                # Node.js 22.12+，package-lock 固定依赖
npm run dev                           # 前端开发预览（127.0.0.1）
npm test                              # 前端单元测试
npm run test:e2e                      # 前端 E2E（本机 Edge，独立浏览器上下文）

cd native
dotnet build Luma.sln                 # 需 .NET 10 SDK（scripts/install-dotnet10.ps1）
dotnet test Luma.sln                  # 内核回归测试

powershell -ExecutionPolicy Bypass -File scripts/package-release.ps1 -Version x.y.z
# 一键构建交付：前端 dist + 自包含内核 + ZIP/SHA256 + 解压实机冒烟 + 系统集成/生命周期检查
```

架构：React 19 + TypeScript 前端（Vite），.NET 10 WPF + WebView2 原生宿主（顶边热区、命中区域、Shell 启动、快捷键、更新）。协议 v1，桥接方法见 `docs/内核接口协议.md`。

## 状态与边界

- 常驻内存目标（50–150MB）尚未达成；长期（5 小时）稳定性验收未完成，按测试版发布。
- 软件内更新需要本仓库保持公开（匿名读取 Releases）。
- 本地测试截图、原始日志与配置不上传 Git；报告中的本地证据路径仅供原工作区查阅。
