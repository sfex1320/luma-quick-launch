# 第十二轮：项目开发启动沿用原环境

日期：2026-09-21。范围仅 `ProjectTestService.cs`、对应项目服务/终端测试和本文件；不修改 MOMO，不下载依赖，不运行 MOMO GUI，不退出或替换当前 Luma。

## 根因与实际证据

用户点击 MOMO 的“打开项目软件”后，Rust 链接报 `LNK2019 / OrtGetApiBase`。这是 Luma 的启动环境改变了项目原定行为，不是识别出的命令错误。

只读检查 `G:\Project\momo智能画布`，先读了其 AGENTS.md：

- 根 `package.json` 的 `tauri` 为 `tauri`，`dev` 为 `vite`，包管理器由 `pnpm-lock.yaml` 确认；`src-tauri/tauri.conf.json` 的 `beforeDevCommand` 为 `pnpm dev`，devUrl 使用 1430 端口。既有开发命令为 `pnpm tauri dev`，与 Luma 的 `pnpm run tauri dev` 等价。
- `src-tauri/Cargo.toml` 的 `ort` 是 `2.0.0-rc.13`、启用 `directml`，没有关闭默认 features；项目注释明确默认 `download-binaries/copy-dylibs`。`Cargo.lock` 同时锁定 `ort-sys 2.0.0-rc.13`。
- 本机已缓存的 `ort-sys-2.0.0-rc.13/build/vars.rs` 把 `CARGO_NET_OFFLINE`、`ORT_SKIP_DOWNLOAD`、`ORT_OFFLINE` 都作为禁止下载配置。`build/download/mod.rs:81–85` 将 `1`/`true` 判断为禁止下载。
- 其 `build/main.rs:74–77` 在跳过下载且没有可用系统库时设置 `link_error_generic`，明确把失败延迟到链接阶段。所以“Cargo 检查过了但运行链接失败”并不矛盾。
- MOMO 已有 `src-tauri/target/debug/build/ort-sys-f0e29928a59c285b/output` 实际包含 `rerun-if-env-changed=ORT_LIB_PATH`、`ORT_LIB_LOCATION`、`CARGO_NET_OFFLINE`，最后是 `cargo:rustc-cfg=link_error_generic`，与用户报告一致。

旧 Luma 的 `BuildStartInfo` 对所有自动入口都写入 `CARGO_NET_OFFLINE=true`，同时覆写 Corepack 和 Go 环境。第十一轮增加 Tauri/dev/start 后，这套“离线测试”规则错误地覆盖了正常开发启动，导致 ort-sys 无法按项目配置准备 ONNX Runtime。

## 最小修复与授权边界

宿主内部增加 `ProjectExecutionMode.Test / Development`；不新增 JSON 字段、不修改 protocol=1、保存配置、Bridge 或前端调用参数。模式由真实清单识别分支确定，前端不能指定任意模式、命令或路径。

| 入口 | 运行环境 |
| --- | --- |
| 自动 `test`、`cargo test --offline`、Go 测试、NET 测试 | 保留既有离线测试策略及固定参数 |
| 自动识别的 `tauri dev`、`dev`、`start` | 沿用启动时的用户环境，不覆写或清除 Cargo/Corepack/Go 网络策略 |
| 用户保存的手动命令，如 `cargo run` | 保留原有 CMD 语义和用户环境，不改写命令 |

用户本轮明确要求“正常启动 MOMO”，授权恢复明确点击开发启动时的项目原定运行方式。自动开发入口点击后，终端只显示一次：

> 项目开发启动：沿用当前环境；构建脚本可能按项目配置下载依赖。

随后只执行已经识别、展示并重新验证的原命令。Luma 不额外执行 `install`、`fetch`、依赖修复、`cargo clean` 或后台联网，也不把用户显式设定的 offline 强制改为 online。若开发命令本身按项目配置准备依赖，这属于用户点击后的正常项目流程，与保存/识别时主动安装依赖不同。

测试优先顺序、根目录手动覆盖、令牌范围、清单指纹校验、两个后台工作槽、四秒启动期限、取消检查、相同 cwd 的终端去重及最多 16 个终端均不变。开发启动和手动启动不因此绕过这些边界。

本节供主任务加入协议第十二轮附录，覆盖旧文档“所有自动入口统一离线”的表述；不取消自动测试的离线限制。

## 验证证据

先在真实服务识别结果上补断言，再运行旧实现：**5 项中 4 失败、1 通过**。三个 dev/start 用例与 MOMO Tauri 用例明确失败于生成的终端脚本仍注入 `$env:`，test 用例保持通过。

修复后相关完整组合：

```text
dotnet test native/tests/Luma.Host.Tests/Luma.Host.Tests.csproj --no-restore --filter "FullyQualifiedName~ProjectTestServiceTests|FullyQualifiedName~ManualProjectLaunchTests|FullyQualifiedName~ProjectTestBridgeTests|FullyQualifiedName~ProjectTestTerminalExecutionTests" --verbosity minimal
已通过! - 失败: 0，通过: 73，已跳过: 0，总计: 73，持续时间: 9 s
```

其中新增 8 个真实子进程环境用例，执行生产生成的 PowerShell/CMD 命令体；仅去除终端留屏并设隐藏窗口。目标是临时目录中的无依赖 `.cmd` 环境记录器，不执行 Cargo/npm/MOMO，不联网。覆盖：

- Development：父环境 offline 未设置、false、true 三种状态原样保留。
- Test：即使父环境没设或设为 false，仍按旧策略设为 true，并限制 Corepack/Go。
- Manual：未设置、false、true 均保留，不误用自动测试策略。
- 全部分支保留 `ORT_LIB_PATH`、`ORT_OFFLINE` 和明确工作目录；开发/手动保留用户的 Corepack、Go 设置，参数 `run tauri dev` 到达记录器不变。仅修改子进程的测试环境，不修改用户/测试进程全局环境。
- 自动 Cargo/Go/NET 服务用例确认仍选择 Test 模式，手动覆盖及跨客户端/过期/变更令牌等已有测试继续通过。

`git diff --check` 对本分工文件通过。没有用单元测试结果代替 MOMO 实际启动验收。

## 尚需统一交付窗口验证

此修复消除了 Luma 额外注入的离线原因；MOMO 实际构建仍取决于网络、项目依赖源、已装工具链、可用 ONNX 库和 1430 端口。用户自己的 `CARGO_NET_OFFLINE`、`ORT_OFFLINE`、`ORT_SKIP_DOWNLOAD`、项目 Cargo 配置或错误的 `ORT_LIB_PATH` 仍会按原语义生效，不会被 Luma 偷偷覆盖。

ort-sys 的已有构建输出已经声明环境变化会触发重跑；因此无需为此次修复主动清空 target 或改项目文件。已打开的旧终端仍保留创建时的环境，更新 Luma 不会改变这些终端的环境。主任务应在获授权的交付窗口从更新后的入口创建新终端验证正常项目流程，遵循 MOMO 既有开发实例管理；本分工不启动第二个 MOMO 或终止其他应用。
