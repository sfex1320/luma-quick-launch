# Windows 兼容与依赖

## 交付目标

Luma 当前只发布 `win-x64` 自包含包，技术启动门槛为 Windows 10 22H2（内部版本 19045）或更高版本。应用和安装器使用同一门槛；低于该版本时在创建窗口前停止，并显示明确原因。程序不依赖用户预装 .NET，但依赖 Microsoft Edge WebView2 Evergreen Runtime。

这是一条产品技术门槛，不等于微软仍为所有满足门槛的系统提供支持。截至 2026-09-20，[.NET 10 的 Windows 支持表](https://learn.microsoft.com/en-us/dotnet/core/install/windows)只列出仍在支持期的 Windows 11，以及 Windows 10 21H2/1809/1607 的 LTSC 或 Enterprise 情形；Windows 10 22H2 消费版已经结束微软生命周期。因此：

- Windows 11 x64 是当前支持与实测主平台；本机验证环境为 Windows 11 专业版 x64，内部版本 26200。
- Windows 10 22H2 x64 保留技术兼容门槛，但不声明它仍受微软或 .NET 厂商支持；发布前仍需独立真机回归。
- Windows Server、Windows 10 早期版本、Windows 8.1 和 Windows 7 不在产品支持范围。
- 没有 ARM64 原生包。Windows on ARM 可能通过系统的 x64 模拟运行本包，但尚未实测，不作兼容承诺。

.NET 10 是 LTS，微软当前列出的支持终止日期为 2028 年 11 月；操作系统一旦结束生命周期，.NET 也会停止在该系统上测试和支持。参见 [.NET releases and support](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)。

## WebView2 交付策略

微软要求 WebView2 应用在安装或启动时确认 Runtime 存在。[官方分发文档](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)规定：64 位 Windows 的机器级 Evergreen Runtime 版本在 32 位注册表视图 `HKLM\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` 的 `pv` 值中；用户级版本位于相同路径的 HKCU。值缺失、空白或 `0.0.0.0` 均视为未安装。

发布构建通过 `scripts/get-webview2-bootstrapper.ps1` 从微软固定跳转地址下载 Evergreen Bootstrapper，并在进入交付目录前校验 Authenticode 状态及 Microsoft Corporation 发布者。输出文件固定为：

`releases/dependencies/MicrosoftEdgeWebview2Setup.exe`

打包流程应把该文件复制到发布目录根部，文件名保持 `MicrosoftEdgeWebview2Setup.exe`。安装器和便携版共用此副本：

- 当前用户安装器先查 HKCU 和 HKLM 32 位视图。仅在缺失时，在复制 Luma 文件之前以非提权身份执行 `MicrosoftEdgeWebview2Setup.exe /silent /install`；微软说明这种调用安装为当前用户范围（若设备已有机器级 Edge Updater，微软安装器可能按自己的规则转为机器级）。每次等待最多 10 分钟；超时会终止本次安装器进程树且不再并行重试，普通失败会重试一次。仍失败时 Luma 安装不会开始。
- 便携版启动时执行同样检测。缺失时先校验打包文件的 Microsoft Authenticode 签名，再询问用户是否安装。只有用户明确选择“是”才执行安装；拒绝或失败时应用退出，不创建 WebView 或后台常驻组件。
- Bootstrapper 是在线安装器，必须能连接微软下载服务。离线设备需要管理员或运维预先部署 Evergreen Standalone Installer；当前包不宣称离线自动补齐 Runtime。

微软说明 Evergreen Runtime 会独立自动更新，Windows 11 通常预装，但应用仍应检测缺失情形。官方下载入口见 [Microsoft Edge WebView2](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)。

## 发布与验证

构建依赖文件：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/get-webview2-bootstrapper.ps1
```

发布流水线在制作 ZIP 和安装器前必须检查：文件存在、Authenticode 状态为 `Valid`、签名者组织为 `Microsoft Corporation`。启用 `-RequireSignature` 时，安装器构建还会直接验证 `Luma.exe` 与 `Luma.dll` 的 Authenticode 状态、时间戳及其证书指纹是否等于 `LUMA_SIGNING_THUMBPRINT`；可编辑的 `build-info.signed` 只作附加元数据，不能代替二进制签名证据。每次构建记录实际 SHA-256；Evergreen 文件会随微软更新，因此不把某个历史哈希写死为永久可信值。

自动化覆盖版本解析、HKCU/HKLM 32 位视图、最低系统内部版本、x64 架构限制、安装重试及未签名文件拒绝。真实安装/升级/卸载测试必须串行执行，且不得通过删除现有 WebView2 Runtime 来模拟缺失；缺失场景应在干净虚拟机快照中验证。

当前未完成的外部证据：Windows 10 22H2 真机、Windows 11 干净机、Windows on ARM x64 模拟、断网与受企业策略限制的 WebView2 安装。完成这些测试前不得在发布说明中写成已验证。
