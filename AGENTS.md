# AGENTS.md

本文件适用于本仓库的全部目录。若以后在子目录中增加更具体的 `AGENTS.md`，以离目标文件最近的说明为准。

## 项目定位

- `QqChannelDesk` 是面向 Windows 的 WPF 桌面应用，使用 C#、.NET 8、WPF 和 SQLite。
- 程序通过本机 Node.js 和 `tencent-channel-cli` 完成 QQ 频道扫码授权、频道同步和 Feed 发布。
- 发布、授权、频道同步会对真实账号和外部平台产生副作用，不能把这些操作当成普通单元测试。
- 界面文案默认使用中文，代码标识符、类型名和文件名使用英文。
- 当前仓库是 Windows 项目，构建、测试和运行都在 PowerShell 中进行，不要按 Linux、WSL 或跨平台项目处理。

## 最高优先级规则

### 1. 真实外部操作

- 除非用户明确要求，并且明确指定使用本人管理的测试频道，否则不要执行真实登录、真实发布、真实频道同步或任何会修改 QQ 频道内容/账号状态的操作。
- 自动化测试必须使用临时目录、临时数据库、假 CLI 或可控的测试替身，不得连接真实账号，不得下载真实媒体后发布。
- 如果必须验证真实发布流程，先停止操作并向用户确认账号、频道、版块、内容和验证范围。
- 发布超时、进程异常退出或结果不明确时，不得自动重试。先检查频道是否已经出现帖子，再决定后续动作。

### 2. 凭证、隐私和日志

- 不采集、不保存、不提交 Token、Cookie、授权链接、二维码内容、`authorization`、`verification_uri` 或类似凭证。
- 日志、异常信息、测试输出、截图和提交内容都必须脱敏。
- 修改日志相关代码时，优先复用 `AppLogger.Redact`、`CliDiagnostics.Sanitize` 和现有的 `AppLogger` 方法，不要自行编写不完整的脱敏逻辑。
- 发布记录只保存标题、正文摘要或媒体名称等最少信息，不保存媒体文件完整路径，也不保存授权凭证。
- 网页采集不能读取用户 Cookie、绕过验证码、绕过反爬或访问需要登录的页面。

### 3. 文件和目录删除

- 禁止批量删除文件或目录。
- 禁止使用 `del /s`、`rd /s`、`rmdir /s`、`Remove-Item -Recurse`、`rm -rf`。
- 需要删除文件时，一次只删除一个明确的绝对路径或仓库内明确路径的文件。
- 需要批量清理构建产物、缓存或临时目录时，停止操作，并让用户手动删除。
- 构建脚本和测试清理逻辑也要遵守这条规则。测试只能清理自己创建的、路径明确的临时文件。

### 4. 现有工作区

- 仓库可能已有未提交修改。不要回滚、覆盖或重写与当前任务无关的用户改动。
- 修改代码前先读取目标文件和相邻模块，沿用仓库现有的服务、日志、数据库和 UI 模式。
- 不要为了完成任务顺手做无关重构、格式化或依赖升级。

## 目录职责

- `src/QqChannelDesk/`：应用程序源码。
- `src/QqChannelDesk/Pages/`：主窗口中的独立功能页面。
- `src/QqChannelDesk/Services/`：CLI、SQLite、采集、媒体、发布计划、日志和诊断服务。
- `src/QqChannelDesk/MainWindow.xaml.cs`：应用服务装配、页面导航、启动和关闭流程。
- `src/QqChannelDesk/App.xaml`：应用级资源和 WPF 样式。
- `tests/QqChannelDesk.Tests/`：xUnit 自动化测试。
- `docs/`：人工验证清单和方案文档。
- `scripts/`：构建和发布脚本。
- `src/QqChannelDesk/tools/`、`temp/`、`logs/`、`media-cache/`：运行期本地数据或按需安装的工具，不提交。

不要提交以下内容：

- `channels.db`、`channels.db-*`、其他 `*.db` 文件。
- `channels.sync.json`、`account.session.json`、`settings.json`。
- `logs/`、`temp/`、`media-cache/`。
- FFmpeg、Node.js、CLI 的本地安装目录或压缩包。
- `artifacts/`、`bin/`、`obj/`、用户配置和编辑器临时文件。

## 常用命令

所有命令都在仓库根目录使用 PowerShell 执行。

```powershell
dotnet restore
dotnet build .\src\QqChannelDesk\QqChannelDesk.csproj -c Release
dotnet test .\tests\QqChannelDesk.Tests\QqChannelDesk.Tests.csproj -c Release
dotnet run --project .\src\QqChannelDesk\QqChannelDesk.csproj
```

制作完整发布包：

```powershell
.\scripts\Build-UpdateZip.ps1 -Version 1.0.1
```

发布版本号使用 `主版本.次版本.修订版本`，必要时可带第四段数字。不要手动修改脚本中的版本号来覆盖已有发布包。

## 技术栈和依赖

- 目标框架：`net8.0-windows`。
- UI：WPF，允许使用 `UseWindowsForms` 仅用于托盘 `NotifyIcon`。
- 测试：xUnit。
- 数据库：SQLite，使用 `Microsoft.Data.Sqlite`。
- 网页解析：`HtmlAgilityPack`。
- 压缩包处理：`SharpCompress`。
- 表格：`ClosedXML`。
- 内置浏览器：`Microsoft.Web.WebView2`。
- 不要重新引入已移除的 HandyControl。当前 UI 使用 `App.xaml` 中的 WPF 原生样式。
- 不要因为“方便”随意增加 NuGet 包。新增依赖前确认现有代码或标准库无法完成，并说明对发布包大小、许可证和离线运行的影响。

## 代码约定

- 使用文件范围命名空间、nullable 和现有隐式 using 配置。
- 核心业务逻辑放在 `Services/`，页面和窗口 code-behind 负责 UI 状态、事件绑定和用户交互，不要继续把业务规则堆进 XAML code-behind。
- 手工发布和计划发布必须共享发布执行路径。发布逻辑优先复用 `IPublishExecutor` 和 `PublishExecutionService`。
- 异步方法接受并传递 `CancellationToken`。不要吞掉取消异常，除非调用点明确需要转换成业务结果。
- UI 线程外不要直接更新 WPF 控件。界面触发的后台任务使用现有的 `UiAsync.Run`、`UiAsync.Observe` 或明确的调度器模式；只有事件处理器可以使用 `async void`。
- 文件路径使用 `Path.Combine`、`Path.GetFullPath` 和 `AppContext.BaseDirectory`，不要手工拼接 Windows 路径。
- 调用外部进程使用 `ProcessStartInfo.ArgumentList`，不要拼接整条命令字符串。必须设置超时、取消处理和退出后的读取，必要时终止整个进程树。
- CLI 返回值优先解析 JSON，并复用 `CliWorkflow` 的验证、错误分类和脱敏规则。
- 不要在日志中打印媒体文件完整路径。发布前需要脱敏为文件名或安全摘要。

## 数据库和账号隔离

- 所有业务数据存储在本地 SQLite，默认路径是程序目录下的 `channels.db`。
- 数据库初始化必须保持幂等。使用 `CREATE TABLE IF NOT EXISTS`、`CREATE INDEX IF NOT EXISTS` 或现有的 `EnsureColumnAsync` 模式兼容旧数据库。
- 不要用删除表、删除数据库、重建数据库或覆盖用户文件的方式做迁移。需要清理数据时交还给用户处理。
- 新表、新查询和新写入凡是属于业务数据的，必须带 `AccountKey` 账号作用域。
- 读取时使用 `AccountContext` 的当前账号键；未登录时不得把账号隔离数据伪装成公共数据。
- 旧数据中 `AccountKey` 为空的记录不得泄露给任意已登录账号，必须保持隐藏或按现有兼容策略处理。
- 修改账号隔离、表结构或查询条件时，必须补充或更新 `tests/QqChannelDesk.Tests/AccountIsolationTests.cs` 这类测试。
- 数据库连接使用现有参数、WAL 设置和短事务模式。多个 store 共享同一数据库文件时，初始化顺序仍由 `AppDatabaseInitializer` 统一控制。

## 发布、计划和采集

- 发布前必须通过 `CliWorkflow.ValidatePublishRequest` 或 `ValidatePublishRequestSources` 的验证。
- 当前规则包括：短贴正文最多 1000 字，带标题长贴最多 10000 字；图片数量有上限；视频只能一个；内容或有效媒体至少存在一个。
- 目标频道和版块 ID 必须是数字。不要绕过输入验证直接把参数传给 CLI。
- 发布结果按 `PublishErrorCategory` 分类。鉴权、权限、限流、内容拒绝和超时不能被笼统合并成“失败”。
- 计划任务启动、停止、并发执行和程序退出时，不能让同一素材被重复发布。
- Telegram 采集依赖 WebView2 和代理页面。保留按 `t.me` 消息链接去重、媒体缓存、失败重试和用户可见错误提示的现有行为。
- 公开网页采集只面向公开 HTTP/HTTPS 页面。不要为采集加入 Cookie 注入、登录态抓取或反爬绕过。
- 更新检查只读取公开 Release 元数据并提示用户，不自动下载、安装、替换或回滚程序文件。

## WPF 界面约定

- 用户可见文案使用中文，尽量沿用现有页面的措辞和状态术语。
- 新增控件样式优先复用 `App.xaml` 中的 `AppButton`、`PrimaryButton`、`Card`、`DataGrid`、`StatusPill` 等资源。
- 修改 XAML 时保持现有紧凑的桌面布局，不要引入网页式大标题、卡片套卡片或与现有配色不一致的主题。
- 长文本、长路径和动态状态文字必须考虑截断、换行和局部放大，避免覆盖相邻控件。
- 涉及 UI 的改动至少完成构建和自动化测试；无法自动验证的交互要补充手工验证说明。

## 测试要求

- 修复行为或新增功能时优先增加可重复的 xUnit 测试。
- 测试使用 `Path.GetTempPath()` 加 GUID 创建独立目录，只清理测试自己创建的文件。
- 禁止在测试中调用真实 CLI、真实网络、真实 WebView2 登录或真实发布。
- 网络、进程和外部服务要替换成可控的假对象或接口实现。
- 与账号隔离相关的改动必须验证跨账号读取、写入和遗留空账号数据。
- 与数据库迁移相关的改动必须验证旧表结构和已有数据仍可读取。
- 与日志或错误处理相关的改动必须验证凭证、Token、Cookie、二维码和错误码不会泄露。
- 至少运行：

```powershell
dotnet build .\src\QqChannelDesk\QqChannelDesk.csproj -c Release
dotnet test .\tests\QqChannelDesk.Tests\QqChannelDesk.Tests.csproj -c Release
```

如果某项验证无法运行，要在最终说明中明确写出未验证的内容和原因，不要声称已经验证。

## 构建和发布

- `Build-UpdateZip.ps1` 使用自包含、单文件发布，禁止启用 `PublishTrimmed`，因为 WPF、ClosedXML 等组件依赖反射。
- 发布 ZIP 必须排除用户数据库、会话文件、同步状态、设置、日志、临时目录和媒体缓存。
- FFmpeg 不随发布 ZIP 分发，由用户在程序内按需在线安装。安装时使用现有校验和、下载进度和取消逻辑。
- 程序只提取需要的 FFmpeg 可执行文件，不修改系统 PATH，不要求管理员权限。
- 不要声称更新包会自动覆盖旧版本。当前更新策略只提示用户并打开 Release 页面。

## Git 和提交

- 提交信息保持具体、简短，沿用仓库风格，例如 `feat: 完善TG采集流程`、`fix: 修复发布超时状态`、`docs: 更新验证清单`。
- 提交前确认没有把数据库、日志、缓存、凭证、构建产物或本地工具目录加入暂存区。
- 不要提交与当前任务无关的用户修改。
- 需要创建代理分支时使用 `codex/` 前缀。

## 完成标准

交付前确认：

- 代码能通过 Release 构建。
- 相关自动化测试已运行并通过。
- 没有新增凭证、Cookie、Token、二维码或完整媒体路径泄露。
- 没有引入递归删除、批量删除或覆盖用户数据的逻辑。
- 账号隔离、数据库兼容和发布安全边界没有被破坏。
- 行为、命令、路径或用户流程发生变化时，README、docs 或本文件已同步更新。
