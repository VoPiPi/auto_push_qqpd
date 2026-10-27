# QQ 频道发布台：内容采集与 AI 辅助入库 开发执行方案 V0.2

| 项目 | 内容 |
| --- | --- |
| 文档版本 | V0.2 |
| 文档状态 | 代码实现完成（含统一调度器装配启停、原文/候选稿双视图、批次详情完整功能、OriginalContent 分批回填）；M6 人工视觉验证与真实授权流程待执行 |
| 更新日期 | 2026-10-24 |
| 适用产品 | QqChannelDesk / QQ 频道发布台 |
| 上游输入 | `content-collection-prd-v0.1.md`（已冻结，D-01 至 D-06 确认） |
| 替代文档 | 本文档替代 `content-collection-dev-plan-v0.1.md`，吸收其有效架构设计，修正实现路径与复用边界 |
| 代码基线 | 当前 `src/QqChannelDesk` 工程已具备：ContentLibraryStore + CollectedItems 表、PublicArticleFetcher、TG 采集链路（TelegramCollectionStore/Scheduler/CollectorService/Materializer）、AccountContext 账号隔离、UiAsync/AppLogger/CliDiagnostics 基础设施 |

## 1. P0 范围与明确不做

### 1.1 P0 交付

- 统一来源对象（单页 / RSS / 网页栏目 / TG）及 CRUD 界面
- 收件箱三栏改造（来源筛选 + 内容队列 + 原文/候选稿双视图）
- 规则采集链路：单页复用 PublicArticleFetcher、RSS/Atom 使用 System.Xml.Linq、网页栏目复用 HtmlAgilityPack 规则
- AI 兜底：OpenAI 兼容接口、结构化 JSON 输出校验、脱敏、成本门控
- 三级去重（来源条目 ID + 规范 URL + 内容指纹）
- 采集批次记录与来源健康状态
- 公开来源定时调度（SemaphoreSlim 并发 1-2、Cron 表达式）
- TG 采集适配为统一来源视图（不改旧表、不改旧调度器内部逻辑）
- 账号隔离：新表全部带 AccountKey，旧表空 AccountKey 记录不泄露
- 幂等迁移：`EnsureColumnAsync` 模式扩展列，`OriginalContent` 回填一次

### 1.2 明确不做

- 登录态抓取、Cookie 注入、验证码识别、反爬绕过
- AI 自主浏览器 Agent、AI 自动入素材库、自动排期、自动发布
- 本地模型部署或本地推理端点
- 微信公众号、抖音、小红书等非公开来源
- 批量删除、递归删除、删表重建库
- 新增 NuGet 包（RSS 用 System.Xml.Linq，HTTP 用 HttpClient，AI 用现有 HttpClient + System.Text.Json）

## 2. 总体架构与现有代码复用边界

### 2.1 目录结构

```text
Pages/Collection/                      新建：三页签 UserControl
├─ InboxView.xaml(.cs)                 收件箱（来源筛选 + 队列 + 原文/候选稿）
├─ SourcesView.xaml(.cs)               来源管理（状态栏 + 表格 + 详情）
└─ RunsView.xaml(.cs)                  采集记录（批次列表 + 批次详情）

Services/Collection/                   新建：采集域服务
├─ CollectionSourceStore.cs            来源 CRUD、健康状态、授权确认
├─ CollectionRunStore.cs               采集批次与统计
├─ ISourceCollector.cs                 采集器接口
├─ SinglePageCollector.cs              复用 PublicArticleFetcher
├─ RssCollector.cs                     System.Xml.Linq 解 RSS/Atom
├─ WebColumnCollector.cs               HtmlAgilityPack 规则解析
├─ TelegramSourceAdapter.cs            适配 TelegramCollectionStore/Scheduler
├─ CollectionSchedulerService.cs       公开来源定时调度
├─ UrlNormalizer.cs                    URL 规范化去重
├─ ContentFingerprint.cs               内容指纹去重
├─ PublicUrlGuard.cs                   合并 PublicArticleFetcher 已有公网校验逻辑
├─ IAiAssistService.cs                 AI 兜底接口
└─ OpenAiCompatibleAiClient.cs         OpenAI 兼容 HTTP 调用
```

### 2.2 复用边界

| 现有代码 | 复用方式 | 改造范围 |
| --- | --- | --- |
| `PublicArticleFetcher` | `SinglePageCollector` 直接调用 `FetchAsync` | 不改源码 |
| `PublicArticleFetcher` 的公网校验逻辑 | `PublicUrlGuard` 提取为独立方法，供 `RssCollector`/`WebColumnCollector` 复用 | 从现有代码提取，不修改原方法 |
| `ContentLibraryStore` / `CollectedItems` 表 | 新增 `SourceId`、`OriginalContent`、`CandidateContent`、`DuplicateOfId`、`ParsedBy`、`SourceItemId` 六列；现有四状态兼容；查询增加 `AccountKey` 条件 | `EnsureColumnAsync` 模式追加，不删列不重建 |
| `TelegramCollectionStore` | `TelegramSourceAdapter` 只读旧表，将 `TelegramCollectionSchedules` 映射为 `CollectionSources` 视图模型；写入仍走旧 Store | 旧表不动、旧调度器不重构 |
| `TelegramCollectionScheduler` | 保持独立运行；`TelegramSourceAdapter` 订阅 `RunCompleted` 事件同步健康状态 | 事件订阅，不改内部逻辑 |
| `AccountContext` / `RequireAccountKey` / `AccountScope` | 新 Store 全部沿用 | 不改 |
| `UiAsync.Run` / `UiAsync.Observe` | 新页面全部沿用 | 不改 |
| `AppLogger` / `CliDiagnostics.Sanitize` | 日志与脱敏全部沿用 | 不改 |
| `App.xaml` 样式资源 | 新控件复用 `PageTitle`、`Card`、`QuietButton`、`PrimaryButton`、`CompactDataGridRow`、`StatusPill`、`DataGridCellText` | 不新增样式，除非字段长度超出现有样式能承载的范围 |
| `AppDatabaseInitializer` | 末尾追加 `CollectionSourceStore.InitializeAsync` 和 `CollectionRunStore.InitializeAsync` | 只追加两行调用 |

### 2.3 不复用、不改造的范围

- `ContentCollectionPage.xaml(.cs)`：现有 2100+ 行的 code-behind 在改造时拆为宿主 + 3 个 UserControl，旧 TG 采集页签的临时代码迁入 `InboxView` 的 TG 快采入口，旧代理池页签不动。
- 不引入 MVVM 框架，不引入 DI 容器。新服务在 `MainWindow` 构造函数中实例化并注入，沿用现有装配模式。

## 3. 数据模型与迁移

### 3.1 新建表

全部使用 `CREATE TABLE IF NOT EXISTS` 和 `CREATE INDEX IF NOT EXISTS`，带 `AccountKey TEXT NULL`。

**CollectionSources**

```sql
CREATE TABLE IF NOT EXISTS CollectionSources (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    AccountKey      TEXT NULL,
    Name            TEXT NOT NULL,
    SourceType      TEXT NOT NULL CHECK (SourceType IN ('single-page','rss','web-column','telegram')),
    EntryUrl        TEXT NOT NULL DEFAULT '',
    Enabled         INTEGER NOT NULL DEFAULT 1,
    AuthConfirmed   INTEGER NOT NULL DEFAULT 0,
    AuthConfirmedAt TEXT NULL,
    ParseMode       TEXT NOT NULL DEFAULT 'rule-first',      -- rule-first / ai-only / rule-only
    FrequencyMinutes INTEGER NOT NULL DEFAULT 60,
    CronExpression  TEXT NOT NULL DEFAULT '',                  -- 空串表示按 FrequencyMinutes 等间隔
    RuleConfigJson  TEXT NOT NULL DEFAULT '{}',               -- 网页栏目选择器、RSS 过滤等
    AiFallbackEnabled INTEGER NOT NULL DEFAULT 0,
    MaxItemsPerRun  INTEGER NOT NULL DEFAULT 20,
    IncludeKeywords TEXT NOT NULL DEFAULT '',
    ExcludeKeywords TEXT NOT NULL DEFAULT '',
    -- 健康状态
    LastAttemptAt   TEXT NULL,
    LastSuccessAt   TEXT NULL,
    NextRunAt       TEXT NULL,
    HealthStatus    TEXT NOT NULL DEFAULT 'healthy',          -- healthy / degraded / failed / disabled
    HealthMessage   TEXT NOT NULL DEFAULT '',
    LastErrorCode   TEXT NOT NULL DEFAULT '',
    LastErrorSummary TEXT NOT NULL DEFAULT '',
    -- 软删除
    DeletedAt       TEXT NULL,
    CreatedAt       TEXT NOT NULL,
    UpdatedAt       TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_CollectionSources_AccountEnabled
    ON CollectionSources (AccountKey, Enabled, DeletedAt);
CREATE INDEX IF NOT EXISTS IX_CollectionSources_AccountType
    ON CollectionSources (AccountKey, SourceType);
CREATE INDEX IF NOT EXISTS IX_CollectionSources_NextRunAt
    ON CollectionSources (NextRunAt) WHERE Enabled = 1 AND DeletedAt IS NULL;
```

**CollectionRuns**

```sql
CREATE TABLE IF NOT EXISTS CollectionRuns (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    AccountKey      TEXT NULL,
    SourceId        INTEGER NOT NULL,
    Trigger         TEXT NOT NULL DEFAULT 'manual',           -- manual / scheduled / retry
    StartedAt       TEXT NOT NULL,
    EndedAt         TEXT NULL,
    Result          TEXT NOT NULL DEFAULT 'running',          -- running / succeeded / partial / failed / ai-degraded / cancelled
    FoundCount      INTEGER NOT NULL DEFAULT 0,
    FetchedCount    INTEGER NOT NULL DEFAULT 0,
    DuplicateCount  INTEGER NOT NULL DEFAULT 0,
    AiFallbackCount INTEGER NOT NULL DEFAULT 0,
    AiFailedCount   INTEGER NOT NULL DEFAULT 0,
    NewItemCount    INTEGER NOT NULL DEFAULT 0,
    ErrorCode       TEXT NOT NULL DEFAULT '',
    ErrorSummary    TEXT NOT NULL DEFAULT '',                 -- 脱敏摘要
    CreatedAt       TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_CollectionRuns_SourceId
    ON CollectionRuns (SourceId, StartedAt DESC);
CREATE INDEX IF NOT EXISTS IX_CollectionRuns_AccountStarted
    ON CollectionRuns (AccountKey, StartedAt DESC);
```

### 3.2 CollectedItems 扩展列（EnsureColumnAsync）

| 新列 | 类型 | 用途 |
| --- | --- | --- |
| `SourceId` | INTEGER NOT NULL DEFAULT 0 | 关联 CollectionSources.Id；0 表示旧数据或一次性采集 |
| `OriginalContent` | TEXT NOT NULL DEFAULT '' | 分离原文，`Content` 改存用户编辑或 AI 候选稿 |
| `CandidateContent` | TEXT NOT NULL DEFAULT '' | AI 生成的候选稿，供用户对比选择 |
| `DuplicateOfId` | INTEGER NOT NULL DEFAULT 0 | 去重指向已存在的条目 Id |
| `ParsedBy` | TEXT NOT NULL DEFAULT '' | 解析方式：rule / ai-fallback / ai-only |
| `SourceItemId` | TEXT NOT NULL DEFAULT '' | 来源侧唯一标识（RSS guid / TG messageId），去重用 |

**OriginalContent 回填策略**：启动时检测 `OriginalContent` 列存在但 `Content` 非空且 `OriginalContent` 为空的行，一次性 `UPDATE ... SET OriginalContent = Content WHERE OriginalContent = '' AND Content <> ''`，使用 `EnsureColumnAsync` 同一次初始化中执行，幂等（重复启动不重复回填）。

### 3.3 AccountKey 隔离

- 新表所有写入必须带 `AccountContext.RequireAccountKey()`。
- 查询使用 `AccountScope("AccountKey")` 替换占位符，与现有 `ContentLibraryStore` 模式一致。
- `AccountKey` 为 NULL 的旧记录不泄露给任何已登录账号，查询条件 `WHERE AccountKey = $ak` 不会匹配 NULL。
- `CollectionSources` 和 `CollectionRuns` 的跨账号越权访问：查询和更新都强制带 `AccountKey` 条件。

### 3.4 迁移安全

- 全部使用 `CREATE TABLE IF NOT EXISTS` 和 `EnsureColumnAsync`，重复执行不报错。
- 不使用 `DROP TABLE`、`ALTER TABLE DROP COLUMN` 或任何破坏性 DDL。
- 不重建数据库、不覆盖用户文件。
- `OriginalContent` 回填只在列首次创建时执行，幂等。
- 新索引使用 `CREATE INDEX IF NOT EXISTS`。

## 4. 统一来源模型与采集器

### 4.1 ISourceCollector 接口

```csharp
public interface ISourceCollector
{
    string SourceType { get; }           // "single-page" / "rss" / "web-column" / "telegram"
    Task<CollectionResult> CollectAsync(
        CollectionSource source,
        CancellationToken cancellationToken = default);
}

public sealed record CollectionResult(
    bool Succeeded,
    string ErrorCode,                    // timeout / http-error / parse-error / ai-degraded / rate-limit / cancelled
    string ErrorSummary,                 // 脱敏摘要
    IReadOnlyList<CollectedItemDraft> Items);
```

### 4.2 单页采集器（SinglePageCollector）

- 调用 `PublicArticleFetcher.FetchAsync(url, cancellationToken)` 获取 `ArticleFetchResult`。
- 转换为 `CollectedItemDraft`：Title、Content 写入 `OriginalContent`，`ParsedBy = "rule"`。
- 解析失败且来源 `AiFallbackEnabled = 1` 时，将原始 HTML 传给 `IAiAssistService`。

### 4.3 RSS/Atom 采集器（RssCollector）

- 使用 `HttpClient` 获取 Feed XML（复用 `PublicUrlGuard` 的公网校验）。
- 使用 `System.Xml.Linq.XDocument.Parse(xml)` 解析，兼容 RSS 2.0 和 Atom 1.0 命名空间。
- 按来源的 `RuleConfigJson` 中可选的 `categoryFilter`、`sinceHours` 过滤条目。
- 每个 `<item>` 生成一个 `CollectedItemDraft`，`SourceItemId` 取 `<guid>` 或规范 URL。
- 不需要新增 NuGet 包，`System.Xml.Linq` 是 .NET BCL。

### 4.4 网页栏目采集器（WebColumnCollector）

- 调用 `PublicArticleFetcher.FetchRawHtmlAsync` 获取栏目页 HTML。
- 使用 `HtmlAgilityPack.HtmlDocument` 解析，按 `RuleConfigJson` 中的 CSS 选择器提取条目链接列表。
- 对每个条目链接再调用 `PublicArticleFetcher.FetchAsync` 获取正文。
- `MaxItemsPerRun` 限制单次采集条目数。

### 4.5 TG 来源适配器（TelegramSourceAdapter）

- **只读映射**：读取 `TelegramCollectionSchedules` 表，将每条计划映射为 `SourceType = "telegram"` 的 `CollectionSource` 视图模型。
- **不写入** `CollectionSources` 表。TG 来源的 CRUD 仍走 `TelegramCollectionStore`，统一来源视图只是适配层。
- **事件桥接**：订阅 `TelegramCollectionScheduler.RunCompleted`，收到后更新来源视图模型的健康状态（最近尝试、最近成功、连续失败）。
- **采集触发**：用户在来源管理页面点击"立即采集"时，适配器调用 `TelegramCollectionScheduler.EnqueueRunAsync(scheduleId, "manual")`。
- 历史映射：`TelegramCollectionRunHistory` 映射为 `CollectionRuns` 视图模型，供采集记录页签展示。

## 5. 规则解析与 AI 兜底

### 5.1 解析链路

```text
ISourceCollector.CollectAsync
        ↓
规则解析（SinglePage/Rss/WebColumn 各自实现）
        ↓
ParsedBy = "rule"，Items 非空 → 返回成功
        ↓
Items 为空或置信度低 AND source.AiFallbackEnabled = 1
        ↓
IAiAssistService.ExtractAsync(rawHtml, sourceType, cancellationToken)
        ↓
结构化 JSON 校验 → 通过 → ParsedBy = "ai-fallback"
        ↓
校验失败 → ParsedBy = "ai-degraded"，Result = "ai-degraded"
```

### 5.2 IAiAssistService 接口

```csharp
public interface IAiAssistService
{
    Task<AiExtractResult> ExtractAsync(
        string rawContent,               // 脱敏后的页面内容或 HTML 片段
        string sourceType,               // 用于选择提示词模板
        CancellationToken cancellationToken = default);

    Task<AiExtractResult> SummarizeAsync(
        string content,                  // 已解析的正文
        CancellationToken cancellationToken = default);
}

public sealed record AiExtractResult(
    bool Succeeded,
    string Title,
    string Content,                       // 结构化提取的正文
    string ErrorCode,                     // timeout / quota / invalid-response / injection-detected
    string ErrorSummary);
```

### 5.3 OpenAiCompatibleAiClient 实现

- 使用 `HttpClient` 发送 POST 请求到用户配置的 BaseUrl（`/v1/chat/completions`）。
- 请求体使用 `System.Text.Json` 序列化，`response_format = { "type": "json_object" }` 要求结构化输出。
- 响应体校验：必须包含 `title` 和 `content` 字段，`title` 非空且长度 <= 500，`content` 非空且长度 <= 50000。
- 超时：请求 30 秒，流式响应不使用（桌面单次调用不需要流式）。

### 5.4 安全约束

- **输入脱敏**：传入 AI 的 `rawContent` 必须经过 `CliDiagnostics.Sanitize` 处理，移除 Cookie、Token、授权链接和完整媒体路径。
- **提示注入防护**：AI 响应中如果包含要求忽略规则、调用工具、发布内容或修改来源配置的指令性文本，标记为 `injection-detected`，不采纳其输出。
- **成本门控**：每个来源每天 AI 调用次数不超过 `MaxItemsPerRun`；全局每天 AI 调用次数可配置上限（默认 100）。
- **API Key 存储**：使用 `System.Security.Cryptography.ProtectedData`（DPAPI）加密存储在 `SystemSettingsStore`，不落明文、不写日志。
- **AI 不发布**：AI 只返回结构化提取结果，不触发素材库写入或发布。所有 AI 产出必须经人工审核。

## 6. 界面实现拆分

### 6.1 宿主改造

`ContentCollectionPage.xaml` 改为宿主 `TabControl`，三个 `TabItem` 分别承载 `InboxView`、`SourcesView`、`RunsView`。

现有内容收件箱的 URL 输入框、FetchButton、ItemsGrid、详情面板迁入 `InboxView`。
现有 TG 采集页签的临时采集功能作为 `InboxView` 内的"快速 TG 采集"入口保留。
现有 TG 定时采集页签和代理池页签保留不变（不删除，不移动）。

### 6.2 收件箱三栏（InboxView）

```
┌──────────────┬──────────────────┬──────────────────────┐
│  来源筛选栏  │    内容队列栏    │   原文 / 候选稿栏   │
│  280px 固定  │    弹性 1.05*    │     弹性 1.3*        │
│              │                  │                       │
│  · 全部来源  │  · 搜索框       │  · 上半：原文        │
│  · 按来源树  │  · 状态筛选     │    （只读，Original-  │
│  · 按类型    │  · DataGrid     │     Content）         │
│  · 待审计数  │    紧凑行       │  · 下半：候选稿      │
│              │  · 分页         │    （可编辑，Candi-   │
│              │                  │     dateContent 或     │
│              │                  │     Content）         │
│              │                  │  · 操作按钮           │
└──────────────┴──────────────────┴──────────────────────┘
```

- 来源筛选栏：`TreeView` 或 `ListBox`，按来源分组显示待审计数，点击即筛选队列。
- 内容队列栏：沿用现有 `ItemsGrid` 的 `CompactDataGridRow` 和 `StatusPill`，增加"重复"状态列和 `ParsedBy` 列。
- 原文/候选稿栏：`Grid` 上下分割，各含 `TextBox`（原文只读、候选稿可编辑）+ 媒体预览 + 操作按钮（再次解析 / 保留 / 忽略 / 标记重复 / 确认授权并加入素材库）。
- 快速采集入口：顶部保留 URL 输入框 + "提取网页" 按钮，沿用现有 `FetchButton_Click` 逻辑。

### 6.3 来源管理（SourcesView）

- 顶部状态栏：四个统计数字（启用数、今日待审数、异常数、今日成功数），用 `TextBlock` + `StatusPill`。
- 表格：`DataGrid`，沿用 `CompactDataGridRow`，列：名称、类型、地址、频率、最近尝试、最近成功、下次运行、健康状态、待审数。
- 行操作：立即采集 / 暂停 / 编辑 / 试采 / 查看记录 / 删除（软删除，设置 `DeletedAt`）。
- 新建来源弹窗：`NewSourceWindow`，选择类型后填写对应字段，TG 类型跳转 TG 定时采集配置。
- 编辑来源弹窗：`SourceEditWindow`，可修改规则、频率、AI 开关等。
- 试采功能：立即采集一次但结果不写入收件箱，只展示预览和解析方式统计。

### 6.4 采集记录（RunsView）

- 批次列表：`DataGrid`，列：来源名称、触发方式、开始时间、结果（`StatusPill`）、耗时、新增数、重复数、AI 兜底数、错误摘要。
- 批次详情弹窗：步骤时间线、查看本次新增条目、再次运行、暂停来源、复制脱敏诊断信息。
- TG 历史适配：`TelegramSourceAdapter` 将 `TelegramCollectionRunHistory` 映射为统一视图模型，供此页签展示。

### 6.5 样式约束

- 新控件全部复用 `App.xaml` 现有样式：`PageTitle`、`Card`、`QuietButton`、`PrimaryButton`、`StatusPill`、`CompactDataGridRow`、`DataGridCellText`、`TableLinkButton`、`TableActionPanel`。
- 中文文案，沿用现有措辞（"待处理"/"保留"/"忽略"/"已入素材库"），新增"重复"状态。
- 紧凑桌面布局，来源筛选栏 280px 固定宽度，队列栏和详情栏弹性分配。
- 所有窗口弹窗设置 `Owner` 和 `WindowStartupLocation="CenterOwner"`。

## 7. 采集调度、去重与健康状态

### 7.1 CollectionSchedulerService

- 定时循环：每 2 分钟检查 `CollectionSources` 中 `NextRunAt <= now AND Enabled = 1 AND DeletedAt IS NULL` 的来源。
- 并发控制：`SemaphoreSlim(initialCount: 1)`，可配置为 2。同一来源同时只运行一个批次。
- Cron 表达式：使用简化 Cron 解析（分/时/日/月/周），与 TG 采集的 `TelegramCronSchedule` 复用解析逻辑。
- 触发方式：手动 / 定时 / 失败重试。
- 取消：每个运行批次持有 `CancellationTokenSource`，页面切换时通过 `IPageLifetime` 接口取消。
- 事件：`RunStarted`、`RunProgress`、`RunCompleted`，页面订阅后刷新 UI。

### 7.2 三级去重

| 级别 | 键 | 实现类 |
| --- | --- | --- |
| L1 来源条目 ID | `SourceItemId`（RSS guid / TG messageId / URL hash） | `UrlNormalizer.CanonicalItemId` |
| L2 规范 URL | `UrlNormalizer.Canonicalize(url)`：去除 fragment、排序 query、统一 scheme | `UrlNormalizer` |
| L3 内容指纹 | SHA-256 前 12 字节，取 title + content 前 5000 字符 | `ContentFingerprint.Compute` |

去重检查顺序：L1 → L2 → L3，命中任何一级即标记 `DuplicateOfId`，状态设为"重复"，不重复写入收件箱。

### 7.3 健康状态

- `healthy`：最近一次采集成功，连续失败 0 次。
- `degraded`：最近一次采集结果为 `partial` 或 `ai-degraded`。
- `failed`：连续失败 >= 3 次，暂停自动调度，用户可手动恢复。
- `disabled`：用户手动暂停。

每次采集结束更新 `LastAttemptAt`、`LastSuccessAt`、`HealthStatus`、`HealthMessage`、`LastErrorCode`、`LastErrorSummary`。

### 7.4 失败分类

| Result | 含义 | 后续动作 |
| --- | --- | --- |
| `succeeded` | 规则解析全部成功 | 更新健康 |
| `partial` | 部分条目解析失败 | 标记 degraded |
| `failed` | 整体失败（网络/超时/解析） | 连续失败计数+1 |
| `ai-degraded` | 规则失败且 AI 兜底也失败 | 标记 degraded |
| `cancelled` | 用户取消 | 不计入健康 |

## 8. 分阶段里程碑

### M1：基础模型与迁移（预计 3 天）

**交付物**：
- `CollectionSourceStore` / `CollectionRunStore`：建表、CRUD、健康状态更新、账号隔离查询
- `CollectedItems` 扩展列 + `OriginalContent` 回填
- `AppDatabaseInitializer` 注册新 Store
- 账号隔离测试 + 迁移测试

**验收标准**：
- 旧数据库打开后所有现有表可读、现有功能不回归
- 新表跨账号读写隔离，A 账号看不到 B 账号的来源
- `OriginalContent` 回填正确且幂等
- `dotnet build -c Release` 和 `dotnet test -c Release` 通过

### M2：规则采集链路（预计 5 天）

**交付物**：
- `ISourceCollector` 接口 + `SinglePageCollector` + `RssCollector` + `WebColumnCollector`
- `UrlNormalizer` + `ContentFingerprint` + `PublicUrlGuard`
- `CollectionSchedulerService` 定时调度与并发控制
- 假 HTTP 服务测试

**验收标准**：
- 假 HTTP 服务下，单页、RSS、网页栏目各至少一个来源采集成功
- 三级去重：重复条目标记"重复"，不重复写入
- 并发上限 1-2 生效，同一来源互斥
- 取消后批次状态为 `cancelled`
- `dotnet test -c Release` 通过

### M3：AI 兜底（预计 3 天）

**交付物**：
- `IAiAssistService` + `OpenAiCompatibleAiClient`
- DPAPI 加密存储 API Key
- 结构化 JSON 校验 + 提示注入防护 + 成本门控
- 假 AI 服务测试

**验收标准**：
- 规则解析成功时不触发 AI
- 规则解析失败 + AI 兜底开启时调用 AI，AI 成功则 `ParsedBy = "ai-fallback"`
- AI 超时/格式非法/额度不足时，结果为 `ai-degraded`，不阻断规则链路
- API Key 不出现在任何日志或异常消息中
- `dotnet test -c Release` 通过

### M4：TG 统一适配（预计 2 天）

**交付物**：
- `TelegramSourceAdapter`：旧表只读映射 + 事件桥接 + 历史映射
- 来源管理页签 TG 类型行操作委托到旧调度器

**验收标准**：
- 来源管理页签正确展示 TG 来源及其健康状态
- TG 立即采集触发旧调度器，完成后健康状态同步
- 旧 TG 历史在采集记录页签正确展示
- 现有 TG 采集和发布逻辑不回归

### M5：界面改造（预计 5 天）

**交付物**：
- `ContentCollectionPage` 宿主改造 + `InboxView` + `SourcesView` + `RunsView`
- `NewSourceWindow` / `SourceEditWindow` / `RunDetailWindow`
- 快速采集入口保留

**验收标准**：
- 收件箱三栏正确展示，来源筛选联动队列
- 来源管理 CRUD、试采、立即采集、暂停、删除功能正常
- 采集记录展示批次列表和详情
- 960px 和常规桌面宽度下布局不重叠、不截断
- 所有操作按钮中文文案，沿用现有状态术语

### M6：集成与手工验证（预计 2 天）

**交付物**：
- 全功能集成
- 手工验证清单

**验收标准**：
- `dotnet build -c Release` 和 `dotnet test -c Release` 通过
- 手工验证清单全部完成或记录阻塞项
- 无凭证泄露、无批量删除、无真实外部操作

## 9. 测试方案

### 9.1 新增自动化测试（tests/QqChannelDesk.Tests/）

| 测试类 | 覆盖范围 |
| --- | --- |
| `CollectionSourceStoreTests` | 来源 CRUD、软删除、健康状态更新、账号隔离 |
| `CollectionRunStoreTests` | 批次统计、结果分类、账号隔离 |
| `PublicUrlGuardTests` | 私网/环回/IPv6/重定向/大小限制 |
| `UrlNormalizerTests` | fragment 去除、query 排序、scheme 统一 |
| `ContentFingerprintTests` | 相同内容相同指纹、微改不同指纹 |
| `SinglePageCollectorTests` | 假 HTTP + 成功/失败/超时 |
| `RssCollectorTests` | 假 HTTP + RSS 2.0 + Atom 1.0 样本 |
| `WebColumnCollectorTests` | 假 HTTP + 选择器规则 + 条目限制 |
| `CollectionSchedulerServiceTests` | 并发上限、同来源互斥、取消保留、Cron 调度 |
| `OpenAiCompatibleAiClientTests` | 格式非法、超时、额度不足、注入防护、Key 不落日志 |
| `TelegramSourceAdapterTests` | 旧计划映射、水位保留、并发设置透传 |
| `AccountIsolationTests` 扩展 | 新表跨账号读写、旧空 AccountKey 数据隐藏 |
| `MigrationTests` | 旧数据库打开后所有现有表可读、OriginalContent 回填正确且幂等 |

### 9.2 假服务边界

- **假 HTTP**：`HttpClient` 传入自定义 `HttpMessageHandler`，返回预设响应，不访问真实网络。
- **假 AI**：`IAiAssistService` 的测试实现，返回固定结构化结果或模拟超时/失败，不调用真实第三方模型。
- **临时数据库**：每个测试用 `Path.GetTempPath()` + GUID 创建独立 SQLite 文件，测试结束逐文件删除（不使用递归删除）。

### 9.3 运行命令

```powershell
dotnet build .\src\QqChannelDesk\QqChannelDesk.csproj -c Release
dotnet test .\tests\QqChannelDesk.Tests\QqChannelDesk.Tests.csproj -c Release
```

## 10. UI 手工验证清单

| 编号 | 验证项 | 通过条件 |
| --- | --- | --- |
| V-01 | 收件箱三栏布局（960px） | 来源筛选栏 280px，队列和详情栏无重叠、无截断 |
| V-02 | 收件箱三栏布局（常规宽度） | 同上，弹性分配合理 |
| V-03 | 来源筛选联动 | 点击来源树节点，队列只显示该来源的条目 |
| V-04 | 原文/候选稿双视图 | 上半原文只读，下半候选稿可编辑，切换来源条目时正确刷新 |
| V-05 | 新建来源（单页） | 保存后在来源管理表格出现，类型为"单页" |
| V-06 | 新建来源（RSS） | 填写 Feed URL 后保存，试采能返回条目 |
| V-07 | 新建来源（网页栏目） | 填写栏目 URL 和选择器后保存 |
| V-08 | 来源健康状态 | 采集成功后状态为"正常"，连续失败 3 次后状态为"异常" |
| V-09 | 立即采集 | 点击后批次在采集记录中出现，状态最终为成功或失败 |
| V-10 | 试采 | 结果不写入收件箱，只展示预览和解析方式统计 |
| V-11 | 重复标记 | 同一 URL 再次采集，条目标记为"重复" |
| V-12 | AI 兜底 | 规则解析失败后 AI 生成候选稿，ParsedBy 显示"AI 兜底" |
| V-13 | 采集记录批次详情 | 展示步骤时间线和错误摘要 |
| V-14 | TG 来源适配 | 来源管理展示 TG 来源，立即采集触发旧调度器 |
| V-15 | 快速采集入口 | 顶部 URL 输入框 + 提取网页仍可用 |
| V-16 | 账号切换 | A 账号的来源和条目在 B 账号下不可见 |
| V-17 | 中文文案 | 所有新增文案中文，状态沿用现有术语 |
| V-18 | 错误提示脱敏 | 采集失败提示不含 Token、Cookie、完整路径 |

## 11. 风险与实施顺序建议

### 11.1 主要风险

| 风险 | 影响 | 缓解 |
| --- | --- | --- |
| CollectedItems 扩展列与现有写入冲突 | 旧代码 INSERT 不含新列，DEFAULT 值覆盖 | 所有新列 NOT NULL DEFAULT，旧 INSERT 不受影响 |
| OriginalContent 回填大表耗时 | 用户数据量大时启动变慢 | 回填限 5000 行/次，分批执行；记录回填水位，重启续填 |
| TG 适配层事件丢失 | 页面切换时错过 RunCompleted 事件 | 订阅在 MainWindow 层级，不随页面切换退订；页面加载时主动快照健康状态 |
| RSS Feed 格式多样 | 部分非标准 Feed 解析失败 | 优先兼容 RSS 2.0 + Atom 1.0；非标准 Feed 降级为"网页栏目"类型 |
| AI 提示注入 | 恶意网页内容诱导 AI 产出危险内容 | 输入脱敏 + 输出校验 + 提示注入检测 + AI 不直接写入素材库 |
| 并发调度与旧 TG 调度器资源竞争 | 两个调度器同时运行可能争抢代理和连接 | 公开网页调度并发默认 1，TG 调度保持现有并发设置；代理池共享 |

### 11.2 实施顺序建议

1. **M1 先行**：数据模型是所有后续阶段的基础，迁移测试必须最先通过。
2. **M2 与 M3 可并行**：规则采集和 AI 兜底是独立链路，接口契约在 M1 中定义。
3. **M4 在 M2 之后**：TG 适配依赖调度接口和健康状态模型。
4. **M5 在 M1-M4 之后**：界面改造依赖所有服务和数据模型就绪。
5. **M6 最后**：集成验证。

### 11.3 不可突破的安全底线

- 不执行真实登录、真实频道同步、真实发布。
- 不在日志中保存 Token、Cookie、授权链接、二维码内容、完整媒体路径。
- 不使用批量删除或递归删除。
- 业务数据必须带 AccountKey。
- 数据库迁移必须幂等，不删表、不重建库。
- AI 不自动入素材库、不自动排期、不自动发布。

## 12. 新增与修改文件清单

### 新增（实际落地结构）

```
docs/content-collection-dev-plan-v0.2.md             本方案
src/QqChannelDesk/Services/Collection/CollectionSourceStore.cs
src/QqChannelDesk/Services/Collection/CollectionRunStore.cs
src/QqChannelDesk/Services/Collection/CollectionModels.cs            ISourceCollector + 采集草稿/过滤模型（原计划的 ISourceCollector.cs 合并至此）
src/QqChannelDesk/Services/Collection/SinglePageCollector.cs
src/QqChannelDesk/Services/Collection/RssCollector.cs
src/QqChannelDesk/Services/Collection/WebColumnCollector.cs
src/QqChannelDesk/Services/Collection/TelegramSourceAdapter.cs
src/QqChannelDesk/Services/Collection/CollectionSchedulerService.cs
src/QqChannelDesk/Services/Collection/PublicUrlGuard.cs
src/QqChannelDesk/Services/Collection/UrlNormalizer.cs
src/QqChannelDesk/Services/Collection/ContentFingerprint.cs
src/QqChannelDesk/Services/Collection/AiAssistService.cs             IAiAssistService + OpenAiCompatibleAiClient + 设置适配（原计划拆三个文件，合并至此）
src/QqChannelDesk/Services/Collection/CollectionAiUsageStore.cs      AI 用量记录与每日门控
src/QqChannelDesk/Pages/Collection/SourcesView.xaml(.cs)             来源管理
src/QqChannelDesk/Pages/Collection/RunsView.xaml(.cs)                采集记录
src/QqChannelDesk/Pages/Collection/NewSourceWindow.xaml(.cs)         新建/编辑来源合一（含 TG 类型跳转）；原计划的 SourceEditWindow 合并至此
src/QqChannelDesk/Pages/Collection/RunDetailWindow.xaml(.cs)         批次详情（时间线、新增条目、再次运行、暂停来源、复制脱敏诊断）
tests/QqChannelDesk.Tests/Collection*.cs、MigrationTests.cs、AiAssistTests.cs、TelegramSourceAdapterTests.cs 等
```

说明：收件箱三栏未拆分为独立 `InboxView` UserControl，仍内联在 `ContentCollectionPage` 宿主页签中；原文/候选稿双视图、再次解析、标记重复、来源待审计数均已实现在该页签内。TG 定时采集与代理池保留为隐藏页签，经来源管理页"TG定时/代理池"入口展开。

### 修改

```
src/QqChannelDesk/Services/ContentLibraryStore.cs    CollectedItems 扩展列 + OriginalContent 分批回填（默认 5000 行/次，重启续填）+ 待审统计与时间窗查询
src/QqChannelDesk/Services/AppDatabaseInitializer.cs 注册新 Store
src/QqChannelDesk/MainWindow.xaml.cs                 装配新服务；统一采集调度器随账号登录启动、账号切换与退出时停止；RunCompleted 后刷新收件箱/来源/记录
src/QqChannelDesk/Pages/ContentCollectionPage.xaml(.cs)  三页签宿主 + 收件箱三栏（原文/候选稿双视图、再次解析、标记重复）
src/QqChannelDesk/Services/SystemSettingsStore.cs    AI 配置（BaseUrl、模型名、开关、每日上限、DPAPI 加密 Key）
```
