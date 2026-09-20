# 第三轮真实 OLE 回归

## 测试入口

`node native/tests/native-ole-smoke.mjs`

可以通过 `--exe <Luma.exe 的绝对路径>` 指定尚未发布的原生构建。默认使用 `APP/native/Luma/Luma.exe`。如 .NET SDK 不在默认安装位置，设置 `DOTNET_EXE`。

`native/tests/OleDragProbe` 是独立的 Windows STA 测试进程，调用真正的 `Ole32.DoDragDrop`，通过 `CF_HDROP` 传递自己创建的临时文件夹。源窗口通过物理 `SetWindowPos` 定位，开始前验证 `WindowFromPoint` 命中自身，避免从其他应用窗口开始 OLE 或副屏 DPI 虚拟化导致测试失真。测试执行以下链路：

1. 使用独立 `LUMA_DATA_DIRECTORY` 和空配置启动真实 Luma/WebView2。
2. 枚举该宿主的每个真实 `Luma Hotzone` HWND，逐屏从隐藏状态开始。
3. OLE 数据源把指针移到热区；必须看到原生日志“热区 OLE 文件拖入”，普通悬停唤出不能通过。
4. 保持顶边拖拽至少 1400ms，验证面板仍显示。
5. 使用实际 HWND 边界和 WebView `devicePixelRatio` 计算面板物理坐标；OLE 拖入面板并保持 1800ms。
6. 验证真实页面出现拖放提示、面板未被自动隐藏、`DoDragDrop` 返回复制成功、真实配置保存对应目录，日志没有 Shell 启动。
7. 恢复原指针位置，收起，再测下一显示器；测试结束终止仅本测试的宿主。

CDP 只读取真实 WebView 页面和点击收起按钮，不注入桥接、文件路径或 `Input.dispatchDragEvent`。夹具和证据保留在 `%TEMP%/luma-native-ole-<UUID>/`，包括 `result.json` 和宿主日志。无用户配置改写，无用户入口启动。

## 证据边界

这个回归使用真实 Windows OLE 跨进程路由和真实 WebView 文件对象，覆盖普通浏览器/CDP 拖放模拟不能证明的隐藏热区路径。拖放数据源由程序控制 `IDropSource`，指针通过实际 Windows 坐标移动，未注入鼠标按键；它不等同于人工从 Explorer 按住左键拖动的完整输入链路。Esc 可中止 OLE，驱动有超时取消与外部看门狗，不持有全局鼠标按键。

## 当前验证

- `dotnet build native/tests/OleDragProbe/OleDragProbe.csproj -c Release --nologo`：通过，0 警告，0 错误。
- `node --check native/tests/native-ole-smoke.mjs`：通过。
- 2026-09-19 15:45（上海时间），最终发布包执行 `node native/tests/native-ole-smoke.mjs`：通过，退出码 0，两个实际显示器分别从隐藏状态完成整条链路。此包包含 visibilityId 握手及显式收起防误展开修复。
- 当前完整证据：`docs/native-ole-round3.json`；原始目录：`C:/WINDOWS/TEMP/luma-native-ole-bafe9379-9a89-48e0-a113-448cc3618b92/`。
- 测试宿主 `APP/native/Luma/Luma.dll` SHA-256：`6f0a4b3f4b665d607341a56373fe8745708203471f4c7661b2260ad9e47493a9`。
- 前端 `APP/native/Luma/dist/index.html` SHA-256：`3f811becf4ce88f9cd8fda862435dd3f8c646517c02a689466af12839ba54b8e`。
- 热区实际坐标分别为 `[1440,0,2400,6]`、`[-2400,0,-1440,6]`；WebView 实际 `devicePixelRatio=1.8899999856948853`。
- 两屏均记录原生 OLE enter/leave、源回调和 `DoDragDrop` 成功返回 `0x00040100`，复制效果 `1`。真实 state.json 各新增一个自己的测试目录，未触发 Shell 启动。
- 初始 helper 无源窗口，以及初次副屏 DPI 定位错误的诊断运行失败；未把普通 hover 唤出误记成 OLE 成功。修正测试源窗口后才取得上述通过证据。
- 指针已恢复到 `(-2062,1478)`，测试宿主及 OLE 数据源进程已停止。后续发布包若变更，需要重新执行后更新本节，不能沿用旧二进制哈希作新包结论。
