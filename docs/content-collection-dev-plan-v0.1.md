# QQ 频道发布台：内容采集与 AI 辅助入库 开发方案 V0.1

> 上游文档：docs/content-collection-prd-v0.1.md（PRD V0.1，已冻结，评审决策 D-01 至 D-06 已确认）
> 本方案面向当前 WPF 工程 src/QqChannelDesk，按 PRD 的 M1 至 M4 里程碑落地，不改变现有 TG 采集与发布执行路径。

## 1. 方案目标与边界

### 1.1 目标

- 在现有 WPF 工程内落地 PRD V0.1 的 P0 范围：统一来源管理、统一收件箱、规则采集（单页/RSS/网页栏目/TG）、AI 兜底、三级去重、采集记录。
- 保持现有 TG 单次采集、TG 定时采集、历史、重试、媒体入库行为不回归。
- 全部业务数据带 AccountKey 账号隔离，迁移幂等，不删除旧表、不覆盖用户数据。

### 1.2 明确不做（与 PRD 一致）

- 登录态抓取、Cookie 注入、验证码识别、反爬绕过。
- AI 自主浏览器 Agent、AI 自动入素材库、自动排期、自动发布。
- 本地模型、本地推理服务、本机模型端点接入。
- 微信公众号、抖音、小红书等非公开来源的批量采集。

### 1.3 技术约束（沿用现有工程约定）

- net8.0-windows / WPF / SQLite（Microsoft.Data.Sqlite），构建、测试、运行均在 PowerShell。
- 不新增依赖包：RSS 解析用 System.Xml.Linq，HTTP 用 HttpClient，JSON 用 System.Text.Json，AI 调用用现有 HttpClient 手写 OpenAI 兼容请求。
- API Key 使用 DPAPI（ProtectedData）加密存储，不落明文、不写日志。
- 发布、授权、采集真实执行前必须走现有 CliWorkflow 验证与用户确认流程。

## 2. 总体架构

```text
ContentCollectionPage（页面宿主，3 个页签）
├─ 收件箱   → Pages/Collection/InboxView.xaml          （来源筛选 + 队列 + 原文/候选稿）
├─ 来源管理 → Pages/Collection/SourcesView.xaml        （状态栏 + 表格 + 详情）
└─ 采集记录 → Pages/Collection/RunsView.xaml           （批次列表 + 批次详情）

Services/Collection/
├─ CollectionSourceStore          来源 CRUD、健康状态、授权确认记录
├─ CollectionRunStore             采集批次记录与统计
├─ ISourceCollector               采集器接口（单页/RSS/栏目/TG 适配）
├─ SinglePageCollector            复用 PublicArticleFetcher
├─ RssCollector                   System.Xml.Linq 解析 RSS/Atom
├─ WebColumnCollector             HtmlAgilityPack 规则解析（扩展现有规则）
├─ TelegramSourceAdapter          适配现有 TelegramCollectionScheduler
├─ CollectionSchedulerService     公开来源定时调度（SemaphoreSlim 并发 1-2）
├─ UrlNormalizer / ContentFingerprint   三级去重（来源条目 ID + 规范 URL + 内容指纹）
├─ PublicUrlGuard                 HTTPS/HTTP 白名单、私有地址拦截、大小与超时控制
├─ IAiAssistService               AI 兜底接口
└─ OpenAiCompatibleAiClient       OpenAI 兼容接口、结构化 JSON 校验、脱敏
```

原则：页面 code-behind 只做 UI 状态和事件绑定；采集、去重、调度、AI 全部下沉到 Services；页面不直接拼 SQL。

## 3. 数据模型设计

### 3.1 新建表（全部带 AccountKey）

```sql
CREATE TABLE IF NOT EXISTS CollectionSources (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    AccountKey TEXT NULL,
    Name TEXT NOT NULL,
    SourceType TEXT NOT NULL CHECK (SourceType IN ('单页','RSS','网页栏目','TG')),
    EntryUrl TEXT NOT NULL DEFAULT '',
    Enabled INTEGER NOT NULL DEFAULT 1,
    AuthConfirmed INTEGER NOT NULL DEFAULT 0,
    AuthConfirmedAt TEXT NULL,
    ParseMode TEXT NOT NULL DEFAULT '规则优先',
    FrequencyMinutes INTEGER NOT NULL DEFAULT 60,
    RuleConfigJson TEXT NOT NULL DEFAULT '{}',
    AiFallbackEnabled INTEGER NOT NULL DEFAULT 0,
    MaxItemsPerRun INTEGER NOT NULL DEFAULT 20,
    IncludeKeywords TEXT NOT NULL DEFAULT '',
    ExcludeKeywords TEXT NOT NULL DEFAULT '',
    LastAttemptAt TEXT NULL,
    LastSuccessAt TEXT NULL,
    NextRunAt TEXT NULL,
    HealthStatus TEXT NOT NULL DEFAULT '正常',
    HealthMessage TEXT NOT NULL DEFAULT '',
    LastErrorCode TEXT NOT NULL DEFAULT '',
    LastErrorSummary TEXT NOT NULL DEFAULT '',
    DeletedAt TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_CollectionSources_AccountEnabled ON CollectionSources (AccountKey, Enabled, DeletedAt);
CREATE INDEX IF NOT EXISTS IX_CollectionSources_AccountType ON CollectionSources (AccountKey, SourceType);

CREATE TABLE IF NOT EXISTS CollectionRuns (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    AccountKey TEXT NULL,
    SourceId INTEGER NOT NULL,
    Trigger TEXT NOT NULL DEFAULT '手动',
    StartedAt TEXT NOT NULL,
    EndedAt TEXT NULL,
    Result TEXT NOT NULL DEFAULT '运行中',
    FoundCount INTEGER NOT NULL DEFAULT 0,
    FetchedCount INTEGER NOT NULL DEFAULT 0,
    RuleParsedCount INTEGER NOT NULL DEFAULT 0,
    AiFallbackCount INTEGER NOT NULL DEFAULT 0,
    DuplicateCount INTEGER NOT NULL DEFAULT 0,
    PendingCount INTEGER NOT NULL DEFAULT 0,
    FailedCount INTEGER NOT NULL DEFAULT 0,
    ErrorCode TEXT NOT NULL DEFAULT '',
    ErrorSummary TEXT NOT NULL DEFAULT '',
    DurationMs INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_CollectionRuns_AccountSourceTime ON CollectionRuns (AccountKey, SourceId, StartedAt DESC);

CREATE TABLE IF NOT EXISTS CollectionSourceClaims (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    AccountKey TEXT NULL,
    SourceId INTEGER NOT NULL,
    SourceItemId TEXT NOT NULL,
    MaterialId INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_CollectionSourceClaims_AccountSourceItem ON CollectionSourceClaims (AccountKey, SourceId, SourceItemId);
```

### 3.2 扩展现有 CollectedItems（EnsureColumnAsync 兼容迁移）

在 ContentLibraryStore.InitializeAsync 中用 EnsureColumnAsync 逐列补齐，不重建表：

| 新列 | 类型 | 说明 |
| --- | --- | --- |
| SourceId | INTEGER NULL | 关联 CollectionSources.Id |
| SourceItemId | TEXT NULL | 来源条目 ID，用于一级去重 |
| CanonicalUrl | TEXT NOT NULL DEFAULT '' | 规范化 URL，用于二级去重 |
| Fingerprint | TEXT NOT NULL DEFAULT '' | 内容指纹，用于三级去重 |
| Author | TEXT NOT NULL DEFAULT '' | 作者 |
| PublishedAt | TEXT NULL | 发布时间 |
| OriginalContent | TEXT NOT NULL DEFAULT '' | 原文（迁移时把现有 Content 复制到本列） |
| AiDraft | TEXT NOT NULL DEFAULT '' | AI 候选稿，永不覆盖 OriginalContent 和用户编辑 |
| AiUsed | INTEGER NOT NULL DEFAULT 0 | 是否经过 AI 兜底 |
| AiConfidence | REAL NOT NULL DEFAULT 0 | AI 置信度 |
| RiskFlagsJson | TEXT NOT NULL DEFAULT '[]' | 风险标记数组 |
| ParseMethod | TEXT NOT NULL DEFAULT '规则' | 规则 / AI 兜底 |

现有数据迁移规则：
- 迁移时执行 UPDATE CollectedItems SET OriginalContent = Content WHERE OriginalContent = ''，现有 Content 继续作为编辑正文使用。
- SourceHost、Url、Status 等现有字段保留，状态增加 重复（PRD 5.1）。
- 所有读写仍按 AccountKey 过滤；旧数据 AccountKey 为空的记录按现有兼容策略处理，不泄露给其他账号。

### 3.3 TG 旧表映射（不迁移、不删除）

- 保留 TelegramCollectionSchedules、TelegramCollectionRunHistory、TelegramCollectionDistributionRules 不动。
- M1 至 M3 阶段 CollectionSources 中不预建 TG 来源；M4 在来源管理中为每个启用的 TG 计划生成一条 SourceType='TG' 的投影来源（EntryUrl 存频道标识，RuleConfigJson 存计划 ID），仅用于统一视图展示与操作入口，实际执行仍调用 TelegramCollectionScheduler。
- TG 水位（LastMessageId）、重试、分发规则、并发设置全部沿用旧表与旧调度器，来源管理页面只读展示，不覆盖。

## 4. 分阶段实施计划

### 4.1 M1 基础模型与迁移（对应 PRD M1）

交付物
- CollectionSourceStore、CollectionRunStore，注册进 AppDatabaseInitializer。
- CollectedItems 扩展列与旧数据兼容迁移（EnsureColumnAsync + UPDATE ... OriginalContent = Content）。
- 来源、运行记录、收件箱的账号隔离查询（使用 AccountContext.RequireAccountKey()）。

退出条件
- 旧数据库打开后所有现有 TG 计划、历史、收件箱条目仍可读。
- 账号隔离测试通过：A 账号看不到 B 账号的来源与收件箱；旧空 AccountKey 数据不泄露。
- Release 构建通过，ContentLibraryTests 无回归。

### 4.2 M2 规则采集（对应 PRD M2）

交付物
- ISourceCollector 接口与 SinglePageCollector（复用 PublicArticleFetcher）、RssCollector、WebColumnCollector。
- PublicUrlGuard：仅 HTTP/HTTPS，解析 DNS 后拒绝环回、私网、链路本地地址（含 IPv6），跟踪重定向逐跳校验，限制响应大小与超时。
- UrlNormalizer + ContentFingerprint：三级去重。
- CollectionSchedulerService：默认并发 1、最多 2（SemaphoreSlim），同一来源不并发，手动与定时共用。
- 来源管理页与采集链接对话框（试采最多 10-20 条，不写收件箱）。
- 收件箱三栏布局与状态流转（待审/保留/忽略/重复/已入素材库）。

退出条件
- 假 HTTP 服务与代表 RSS/栏目来源的自动化测试通过。
- 去重三级测试通过（来源条目 ID、规范 URL、内容指纹）。
- 试采不写入收件箱；并发 2 不产生同来源双批次。
- 401/403/404/429/500/超时错误分类展示符合 PRD 9.3。

### 4.3 M3 AI 兜底（对应 PRD M3）

交付物
- IAiAssistService + OpenAiCompatibleAiClient：HTTPS-only，拒绝 localhost/私网端点，严格 JSON Schema 校验，Prompt 注入防护，每日调用上限。
- AI 配置进入系统设置：BaseUrl、ApiKey（DPAPI 加密）、模型名、开关（默认关闭）。
- 规则失败或低置信度时触发 AI 兜底，结果写入 AiDraft，不覆盖原文。
- AI 失败保留规则结果或最低可用摘要，不无限重试。

退出条件
- 假 AI 服务测试通过：格式非法、超时、额度不足均不阻断规则链路。
- API Key 不落明文、不写日志（脱敏测试通过）。
- AI 失败时批次结果仍可归类为降级成功或部分成功。

### 4.4 M4 TG 统一（对应 PRD M4）

交付物
- TelegramSourceAdapter：把现有 TelegramCollectionScheduler 包装为统一来源视图。
- 来源管理中 TG 来源展示授权状态、最近尝试、最近成功、健康状态。
- 收件箱支持 TG 采集结果进入统一待审队列。

退出条件
- 现有 TG 定时任务、水位、重试、并发、分发规则无回归（TelegramCollectionSchedulerTests 全绿）。
- TG 计划新增/编辑/删除后旧表与来源视图同步一致。
- 多实例与账号切换场景下运行中的 TG 批次不会写入新账号（AccountIsolationTests 覆盖）。

### 4.5 M5 内测（对应 PRD M5）

- 5 至 10 名真实运营者试用，采集记录、来源健康、AI 兜底比例、重复率统计齐全。
- 按 PRD 4.3 指标收集两周脱敏数据，形成调整结论。
- 人工验证清单更新，覆盖收件箱三栏、来源管理、采集记录、试采、授权确认。

## 5. 关键服务设计

### 5.1 采集执行流程（对齐 PRD 9.1）

1. 创建 CollectionRuns 记录（状态=运行中）。
2. 校验来源启用、授权确认、未在运行（OperationLeaseTracker 模式）。
3. PublicUrlGuard 发起公开 HTTP/HTTPS 请求，执行地址、重定向、大小、超时校验。
4. 识别来源类型并获取条目列表。
5. 规则解析：标题、正文、作者、发布时间、主图 URL、原文链接、来源域。
6. 计算规范 URL 与内容指纹，账号内三级去重。
7. 应用包含词、排除词、每批上限。
8. 规则失败或低置信度时调用 AI 兜底。
9. 写入待审收件箱，保存来源、运行记录与解析依据。
10. 汇总结果分类（成功/降级成功/部分成功/失败/取消），更新来源健康状态。

### 5.2 失败与重试策略（对齐 PRD 9.3）

- 网络/超时/5xx：最多自动重试 3 次，退避递增。
- 429：按 Retry-After 等待，不立即重试。
- 401/403/404/410/验证码页：不重试，明确提示仅支持公开页面。
- 解析失败：先 AI 兜底，不直接重试请求。
- AI 失败：不无限调用，保留规则结果或最低可用摘要。
- TG 来源：沿用现有 8 次退避重试、水位保护、部分媒体成功规则。

### 5.3 并发与取消（对齐 PRD 9.4）

- 全局公开来源默认并发 1，最多 2，用 SemaphoreSlim(1,2) 控制。
- 同一来源通过 OperationLeaseTracker 保证不并发。
- 取消后已写入的待审内容保留，未完成条目标记取消。
- 程序退出时等待当前批次保存完成再退出。

### 5.4 AI 兜底设计（对齐 PRD 10）

- 输入：原文、候选字段、来源上下文；输出严格 JSON：title/body/summary/author/publishedAt/mediaUrls/tags/qqFitScore/confidence/riskFlags/reason。
- 原文与候选稿分离：AI 结果只写 AiDraft，OriginalContent 与用户编辑永不覆盖。
- 提示注入防护：来源内容作为不可信数据隔离，输出白名单校验，异常内容标记 RiskFlagsJson。
- 成本限制：每日调用上限，开关默认关闭，调用记录只保存脱敏摘要。

### 5.5 安全边界

- 公开地址校验：PublicUrlGuard 拒绝私网/环回/链路本地地址，含 IPv6 形式。
- 凭证脱敏：日志与异常继续复用 AppLogger.Redact、CliDiagnostics.Sanitize。
- AI 端点：仅 HTTPS，拒绝 localhost、私网 IP、127.0.0.0/8、::1。
- API Key：DPAPI 加密存储，不写日志、不写发布记录、不提交仓库。

## 6. UI 改造方案

### 6.1 页面结构

- ContentCollectionPage.xaml 保留为页面宿主，仅承载页签与页面生命周期。
- 新增 Pages/Collection/InboxView.xaml、SourcesView.xaml、RunsView.xaml 三个 UserControl。
- 对话框：CollectLinkWindow（采集链接）、NewSourceWindow（新建来源）、SourceEditWindow（编辑来源）、RunDetailWindow（采集记录详情）。

### 6.2 复用现有样式

- 复用 App.xaml 中的 AppButton、PrimaryButton、QuietButton、TableLinkButton、Card、DataGrid、CompactDataGridRow、DataGridCellText、StatusPill、StatusPillText、TableActionPanel。
- 沿用现有紧凑布局与 36px 行高，不引入网页式大标题、卡片套卡片或新配色。
- 收件箱三栏在 960px 最小窗口宽度下仍满足 PRD 7.5：不出现按钮文字覆盖、列内容遮挡或主要操作不可访问。

### 6.3 交互要点

- 收件箱：左侧筛选（来源、状态、内容类型、解析方式、是否 AI、时间），中间队列（搜索 + 分页），右侧原文/候选稿双视图，操作按钮（再次解析/保存修改/保留/忽略/标记重复/确认授权并加入素材库）。
- 来源管理：顶部状态栏（启用数、今日待审数、异常数、今日成功数），表格（名称、类型、地址、频率、最近尝试、最近成功、下次运行、健康状态、待审数），行操作（立即采集/暂停/编辑/试采/查看记录/删除），详情面板。
- 采集记录：批次列表（触发方式、时间、结果、数量统计、来源健康变化、错误摘要），批次详情（步骤时间线、查看条目、再次运行、暂停来源、复制脱敏诊断信息）。

## 7. 测试方案

### 7.1 新增自动化测试（tests/QqChannelDesk.Tests/）

- CollectionSourceStoreTests：来源 CRUD、软删除、健康状态、账号隔离。
- CollectionRunStoreTests：批次统计、结果分类、账号隔离。
- PublicUrlGuardTests：私网/环回/IPv6/重定向/大小限制。
- UrlNormalizerTests、ContentFingerprintTests：三级去重。
- RssCollectorTests、WebColumnCollectorTests、SinglePageCollectorTests：假 HTTP + 样本页面。
- CollectionSchedulerServiceTests：并发上限、同来源互斥、取消保留。
- OpenAiCompatibleAiClientTests：格式非法、超时、额度不足、注入防护、Key 不落日志。
- TelegramSourceAdapterTests：旧计划映射、水位保留、并发设置透传。
- AccountIsolationTests 扩展：新表跨账号读写、旧空 AccountKey 数据隐藏。
- 迁移测试：旧数据库打开后所有现有表可读、OriginalContent 回填正确。

### 7.2 运行命令

```powershell
dotnet build .\src\QqChannelDesk\QqChannelDesk.csproj -c Release
dotnet test .\tests\QqChannelDesk.Tests\QqChannelDesk.Tests.csproj -c Release
```

### 7.3 人工验证

- 按 docs/manual-validation-checklist.md 风格补充内容采集专项清单。
- 默认窗口与 960px 最小宽度下检查收件箱、来源管理、采集记录三页。
- 使用假 HTTP 服务与假 AI 服务验证试采、调度、去重、AI 兜底，不连接真实外部平台。
- 真实 TG 采集仅在用户明确指定本人测试频道后进行。

## 8. 新增与修改文件清单

新增
- docs/content-collection-dev-plan-v0.1.md（本方案）
- src/QqChannelDesk/Services/Collection/CollectionSourceStore.cs
- src/QqChannelDesk/Services/Collection/CollectionRunStore.cs
- src/QqChannelDesk/Services/Collection/ISourceCollector.cs
- src/QqChannelDesk/Services/Collection/SinglePageCollector.cs
- src/QqChannelDesk/Services/Collection/RssCollector.cs
- src/QqChannelDesk/Services/Collection/WebColumnCollector.cs
- src/QqChannelDesk/Services/Collection/TelegramSourceAdapter.cs
- src/QqChannelDesk/Services/Collection/CollectionSchedulerService.cs
- src/QqChannelDesk/Services/Collection/PublicUrlGuard.cs
- src/QqChannelDesk/Services/Collection/UrlNormalizer.cs
- src/QqChannelDesk/Services/Collection/ContentFingerprint.cs
- src/QqChannelDesk/Services/Collection/IAiAssistService.cs
- src/QqChannelDesk/Services/Collection/OpenAiCompatibleAiClient.cs
- src/QqChannelDesk/Pages/Collection/InboxView.xaml(.cs)
- src/QqChannelDesk/Pages/Collection/SourcesView.xaml(.cs)
- src/QqChannelDesk/Pages/Collection/RunsView.xaml(.cs)
- src/QqChannelDesk/Pages/Collection/CollectLinkWindow.xaml(.cs)
- src/QqChannelDesk/Pages/Collection/NewSourceWindow.xaml(.cs)
- src/QqChannelDesk/Pages/Collection/SourceEditWindow.xaml(.cs)
- src/QqChannelDesk/Pages/Collection/RunDetailWindow.xaml(.cs)
- 测试目录下对应测试文件

修改
- src/QqChannelDesk/Services/ContentLibraryStore.cs：CollectedItems 扩展列、OriginalContent 回填、账号隔离查询。
- src/QqChannelDesk/Services/AppDatabaseInitializer.cs：注册新 store。
- src/QqChannelDesk/MainWindow.xaml.cs：装配新服务、页面改为 3 页签。
- src/QqChannelDesk/Pages/ContentCollectionPage.xaml(.cs)：拆为宿主 + 3 个 UserControl。
- src/QqChannelDesk/Services/SystemSettingsStore.cs：AI 配置（BaseUrl、ApiKey、模型名、开关）。
- src/QqChannelDesk/App.xaml：如需要少量新样式，复用现有资源风格。
- docs/manual-validation-checklist.md：补充内容采集验证项。

## 9. 风险与注意事项

- 迁移必须幂等：重复启动不会重复回填 OriginalContent，不会重建表。
- TG 调度器事件订阅沿用现有模式（SubscribeSchedulerEvents/UnsubscribeSchedulerEvents），页面切换时保持订阅稳定。
- 工作区可能已有用户改动：修改前先读取目标文件和相邻模块，不回滚、不覆盖与任务无关的改动。
- 真实登录、真实采集、真实发布必须用户明确指定本人测试频道后执行，测试一律使用假 HTTP、假 AI、临时数据库。
- 日志与截图必须脱敏：Token、Cookie、二维码内容、完整媒体路径、错误堆栈均不落日志。
