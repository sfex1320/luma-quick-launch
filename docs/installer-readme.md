# Luma Quick Launch 安装版

安装包：`luma-quick-launch-0.2.0-setup-x64.exe`。便携版与安装版使用相同的原生程序和前端文件，安装版不依赖 Node.js 或另装 .NET；Windows 需要 WebView2 Runtime。

## 安装与系统入口

- 安装范围固定为当前用户，不申请管理员权限。默认目录为 `%LOCALAPPDATA%\Programs\Luma Quick Launch`，目标平台为 Windows x64 兼容环境。
- 默认创建桌面快捷方式，并提供开始菜单入口。两者打开 `Luma.exe --settings`；桌面快捷方式名称和描述均为 `Luma Quick Launch`。
- 如果同名快捷方式属于其他应用，安装器会保留并说明冲突，不覆盖该文件。将冲突快捷方式改名后，可重新安装或在应用“系统”页创建桌面入口。
- 安装不默认开启开机启动。请在应用“系统”页明确开启；登录启动使用 `Luma.exe --startup`，仅驻留托盘。
- 已有安装版开启的启动项在升级或更换安装目录时跟随更新。指向另一份便携版的启动项不会被安装器覆盖。Windows 任务管理器中的启动禁用状态也不会因升级被重置。
- 升级和卸载先调用目标目录的 `Luma.exe --shutdown`，通过完整程序路径对应的本地退出事件正常关闭应用，再按进程完整路径等待退出，最长 10 秒。不会按进程名称结束其他副本。退出失败时才提示从托盘退出。
- 安装器同时启用 Windows Restart Manager 的文件占用检查，不使用强制关闭模式。关闭成功后，交互安装完成页可重新打开设置；静默安装不会自动启动应用。
- 从 Windows“已安装的应用”卸载，或运行安装目录中的 `unins000.exe`。卸载保留 `%LOCALAPPDATA%\Luma` 中的项目、设置、备份等数据；只清理仍指向当前安装目录的启动项及快捷方式。
- 便携版和安装版默认共享同一份用户配置。移动便携目录后，在新位置重新创建桌面入口或重新开启该副本的开机启动。

## 构建

先由 `scripts/package-release.ps1` 生成并验证完整的 `win-x64` 发布目录，再调用：

```powershell
./scripts/build-installer.ps1 -SourceDirectory '<verified-release-directory>' -Version 0.2.0 -OutputDirectory ./releases -BootstrapCompiler
```

脚本检查 `build-info.json` 的版本和平台、必要程序与前端载荷，拒绝覆盖同名发布包。输出安装 EXE 及同名 `.sha256`。已有工具时可省略 `-BootstrapCompiler`，或用 `-CompilerPath` 指定完整 `ISCC.exe` 路径；仍须符合下列固定版本和校验。安装包自身未配置发行者代码签名，编译工具的官方签名不等于 Luma 安装包已签名。

编译器固定 **Inno Setup 6.7.3**，默认工具目录 `releases/tools/InnoSetup-6.7.3`（不纳入 Git）。引导过程使用官方 `/PORTABLE=1 /CURRENTUSER`，不创建工具卸载注册、桌面/开始菜单入口或 `.iss` 文件关联。仅在下载 SHA256 一致且 Authenticode 状态为 `Valid`、签名发布者为 `Pyrsys B.V.` 时执行。

| 文件 | SHA256 |
| --- | --- |
| innosetup-6.7.3.exe | `9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732` |
| ISCC.exe | `0a8757031b33777e4c9cbffee40f11a5062b36d25cbe144c1db73b6102b80ad7` |
| ISCmplr.dll | `85a1e3090d3a5b85319f001b7c8f9ecfad45f37eff030a67bbe29ef58b7aa2c3` |

来源：[官方 6.7.3 下载](https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe)、[官方签名与哈希文件](https://files.jrsoftware.org/is/6/innosetup-6.7.3.exe.issig)、[签名验证说明](https://jrsoftware.org/isdl-verify.php)、[官方免安装参数](https://jrsoftware.org/ishelp/topic_technotes.htm)。Inno Setup 的商业使用许可按其[官方许可说明](https://jrsoftware.org/isorder.php)执行。

## 真实安装验证

```powershell
./native/tests/installer-smoke.ps1 -InstallerPath './releases/luma-quick-launch-0.2.0-setup-x64.exe' -SourceDirectory '<verified-release-directory>' -RunNativeTests
```

此测试实际执行安装 EXE 和卸载程序，以唯一临时目录和开始菜单组隔离文件，关闭桌面任务，逐文件对比安装后的完整载荷；检查 HKCU 卸载注册、开始菜单目标、首次安装不开机启动、升级保留配置和任务管理器禁用状态、退出正在运行的安装副本、卸载保留其他副本启动项、卸载清理自己的启动项与保留用户数据。`-RunNativeTests` 对刚安装的真实程序运行 `npm run test:native`，使用真实 WebView2 和 Windows Shell。

脚本临时保存、修改、最终恢复 `LumaQuickLaunch` 启动项及对应 `StartupApproved` 值；测试时不要并行执行会修改系统入口的其他测试。如果已有正式 Luma 卸载注册，脚本会拒绝开始，避免覆盖用户安装。配置检查使用已有 `state.json` 的哈希与独立新增的验证文件，不重写现有配置。验证结束删除新增验证文件，保留 `%TEMP%\luma-installer-smoke-*\result.json` 和安装/卸载日志供复核。测试程序只在异常清理时终止本次创建的精确 PID。

参考：[当前用户权限](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)、[Restart Manager 关闭应用](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm)、[静默安装和任务参数](https://jrsoftware.org/ishelp/topic_setupcmdline.htm)。
