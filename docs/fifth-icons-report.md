# 第五轮软件原生图标实施记录

2026-09-19 22:03（Asia/Shanghai）。本子任务完成源码与针对性验证；未发布、未停止用户应用、未修改用户配置。最终真实 WebView2 UI 集成与发布验证由主任务串行执行。

## 实施

- `shell.getIcon` 仍使用 protocol=1，仅接收保存的 `projectId/itemId` 与可选 32/48/64/96 尺寸（默认64）。未知字段、raw path、错误尺寸、空ID均拒绝。返回 `{dataUrl:string|null}`；入口删除、路径无效、提取失败或超时返回null。读取不触发启动与配置保存。
- ShellIconService 以 IShellItemImageFactory 的 ICONONLY 读取 `.exe/.lnk` 原图标，包含快捷方式自定义图标。两个后台 STA 工作线程、128排队上限；4秒返回超时后仍保留实际线程占用，不因超时增生线程。HBITMAP在finally中DeleteObject，COM在finally释放。
- 原生128缓存条目及600万字符预算，绑定保存revision、project/item/path与尺寸；成功缓存5分钟、失败缓存5秒。重复任务合并；保存变更后正在提取的旧结果丢弃。
- 前端有界请求与缓存、PNG响应边界校验、路径与请求过期隔离，失败静默保留通用图标，不输出空src或破图。真实品牌图标外加轻玻璃底座、高光和阴影，使用 `.native-software-icon img`。
- 本页成功保存响应与其他页面 `app.stateChanged` 通过内部 `subscribeCommittedState` 通知派生资源刷新；不伪造协议事件。ItemIcon 用 epoch 驱动重新读取；同一入口刷新期间保留已有品牌图像，路径改变立即隔离旧图。无定时轮询。快速连续保存不会清掉未完成请求的计数。
- GroupIcon支持projectId，单项与组合缩略图传递itemId/pathKey；Dock侧身份传递与DockMessageQueue独立调度由主任务/动效子任务完成。

## 最新验证

| 验证 | 结果 |
| --- | --- |
| 原生 IconBridgeRouterTests + ShellIconServiceTests | 11/11通过，2026-09-19 22:03 |
| Edge `tests/icons.spec.ts`，独立output `test-results/fifth-icons` | 6/6通过，6.9秒 |
| Vitest icon-contracts + software-icon-cache | 3/3通过 |
| `npx tsc -b` | 退出码0 |

原生接口初始5项测试均以METHOD_NOT_FOUND等预期原因失败，补齐后通过。前端3项初始接口/DOM测试失败后通过；本页保存/跨页状态刷新2项及刷新占位闪烁1项均先观察失败再修复。前端128未完成请求在状态更新后变成129的边界也先失败再修复。

真实Windows Shell读取（不是前端mock）：

- `C:\Users\96311\Desktop\MOMO 智能画布.lnk`：96×96 PNG，7490字符。
- `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Adobe Photoshop 2026.lnk`：96×96 PNG，3946字符。
- Windows `explorer.exe`：96×96 PNG，6586字符。

均经PngBitmapDecoder实际解码。两个用户快捷方式在读取前后逐字节一致；测试保存仅写随机临时配置，未启动对应软件。Photoshop路径通过桌面与开始菜单四个已知根目录的一级文件名筛选发现，没有全盘或递归扫描。

Edge的6项测试使用模拟协议验证React交互，因此这些结果不代表真实Windows窗口集成成功；真实Shell提取与最终宿主UI验证须分开陈述。

## 官方API依据

[Microsoft IShellItemImageFactory::GetImage](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellitemimagefactory-getimage)：ICONONLY只读图标，调用方负责DeleteObject；实际提取在后台执行。
