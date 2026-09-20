# 原生搜索与导入服务记录（2026-09-19）

本次新增 `native/Luma.Host/Services/SearchService.cs`、`ShortcutImportService.cs` 及三个匹配测试文件，未引入包依赖，未更改保存协议或现有 LaunchService/PathRules。窗口路由与前端由主任务集成。

## 接口

- `SearchService(StateStore, IWindowsSearchProvider? provider, IShellExecutor? shell, IFileSystemProbe? probe, Func<DateTimeOffset>? clock, TimeSpan? queryTimeout)`：后五个参数可省略，仅用于注入与测试。
- `Task<SearchResponse> QueryAsync(string clientId, string query, string scope)`。
- `LaunchOutcome Open(string clientId, string requestId, string resultId)`。
- `ShortcutImportService.Resolve(IReadOnlyList<string> paths)` 返回 `IReadOnlyList<ImportedShortcut>`，包括 Name/Path/Kind，由既有 ContractsJson camelCase 序列化。
- 查询参数非法以及导入任一路径非法、失效或不可访问时抛 `ArgumentException`，路由应映射 INVALID_REQUEST。导入整批成功才返回，不保存、不启动；重复路径合并，空数组保留取消语义。

## 行为与边界

查询最多 200 字符、返回最多 60 条；scope 为 all/shortcuts/files/content/settings。保存入口仅查询 StateStore 当前状态；中文 Windows 设置及英文关键词来自内核固定白名单，不接收前端 URI。空查询只显示入口和设置，不扫描磁盘，indexAvailable=false 并说明尚未查询索引；无需索引的范围同样给出明确说明。

Windows 索引通过 ADODB COM 与 `Search.CollatorDSO.1` 只读读取 SYSTEMINDEX。文件名按分词词首匹配；正文采用 FREETEXT 自然语言查询，不承诺所有磁盘或所有文件内容均已索引。文件名 CONTAINS 语法仅使用内核生成的双引号、AND 与星号，用户值只提取 Unicode 字母/数字词；正文 SQL 单引号转义，前端文本不能进入 CONTAINS 操作符语法。纯标点名称采用转义了百分号、下划线与方括号的 LIKE 查询，可能在较大索引上超时并返回诚实提示。

COM 连接超时 2 秒、命令超时 3 秒、服务响应等待最多 4 秒。服务与全局 provider 均采用单并发门闩；超时取消后，直到实际查询结束才释放门闩，持续键入不会积累失控 COM 工作线程。没有后台全盘递归、定时索引或轮询。

每个结果签发随机 GUID 能力 ID，绑定 clientId，5 分钟过期，缓存最多 1024 条。打开请求按 clientId/requestId 幂等，缓存最多 256 条，满时拒绝新打开请求；不提前逐出未过期请求。保存入口打开时交给既有 LaunchService，重新解析最新 projectId/itemId，不使用搜索时旧路径。索引结果打开时再次验证 PathRules 与存在性，存在性检查也有单并发及 3 秒等待界限。设置只能打开固定白名单。测试不执行真实 Shell 启动。

导入最多 100 项，使用真实 File.Exists/Directory.Exists 校验原生 picker/AdditionalObjects 提供的路径。文件夹为 folder，exe/lnk/appref-ms 为 app，其余实际文件为 file。服务本身不接受任何 JSON 路径来源授权，调用方必须保证路径来自本次原生文件对象或选择器。

## 最新验证

命令（native 目录）：

```powershell
& C:/Users/96311/AppData/Local/Microsoft/dotnet/dotnet.exe test tests/Luma.Host.Tests/Luma.Host.Tests.csproj --no-restore --filter 'FullyQualifiedName~SearchServiceTests|FullyQualifiedName~ShortcutImportServiceTests|FullyQualifiedName~WindowsSearchProviderTests' --logger 'console;verbosity=detailed'
```

12 项通过：能力隔离/过期/未知 ID、最新保存路径/已删除引用、重复打开、不可用回退、原生设置白名单、索引路径再校验、超时不积累 worker、SQL 注入与通配符转义、60 条结果及 1024 能力上限、真实文件/文件夹类型与整批非法输入、三个范围的真实 Windows Search 探针。

真实机器初版 filename `LIKE '%Windows%'` 及含此谓词的 all 查询返回 `0x80041607`，即 QUERY_E_TIMEDOUT；正文则成功。基于此证据改成分词词首 CONTAINS 后，最新真实 ADODB 查询输出：

```text
Windows Search files query available: 60 results (paths omitted).   [243 ms]
Windows Search all query available: 60 results (paths omitted).     [145 ms]
Windows Search content query available: 60 results (paths omitted). [128 ms]
测试总数: 12，通过数: 12，总时间: 1.2911 秒
```

这证明当前机器的实际 Windows 索引查询与 COM 读取可用；不等同于发布包窗口、拖放或 Shell 真实打开验收。机器索引范围、文件格式过滤器及非索引目录内容未扩大或修改。环境探针允许服务不可用/超时并记录明确输出；上述最新运行三个范围均成功，而非仅靠回退通过。最终发布后仍需主任务执行 npm run test:native。

## 官方依据

- [Windows Search SQL / AQS 与只读 OLE DB](https://learn.microsoft.com/en-us/windows/win32/search/using-sql-and-aqs-to-query-the-index)
- [LIKE 特殊字符方括号转义](https://learn.microsoft.com/en-us/windows/win32/search/-search-sql-like)
- [CONTAINS 前缀匹配](https://learn.microsoft.com/en-us/windows/win32/search/-search-sql-wildcards)
- [Windows 设置 URI](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)
