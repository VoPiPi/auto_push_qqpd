# QQ频道图文/视频自动发帖可行性调研

> 调研日期：2026-09-30
> 目标范围：本人 QQ 账号、本人管理的频道、个人内部工具、图文和视频帖子
> 当前状态：只完成静态调研与方案设计，未保存账号凭证，未执行真实发帖

## 1. 结论

### 1.1 业务可行性

目标在技术上可行，但需要区分两个平台能力：

| 能力 | 当前证据 | 结论 |
| --- | --- | --- |
| 机器人向频道子频道发送普通消息 | 腾讯 QQ 机器人官方文档提供 `POST /channels/{channel_id}/messages` | 已确认有官方接口 |
| 机器人发送图片、视频等富媒体消息 | 官方文档提供富媒体上传和 `msg_type=7` | 已确认有官方消息能力 |
| 以用户/社区身份发布频道 Feed 帖子 | 腾讯频道社区 CLI 文档提供 `feed publish-feed` | CLI 层面已确认，但不等于机器人官方 API 已开放同样能力 |
| 图文/视频 Feed 帖子的账号权限和长期稳定性 | 需要用本人账号和本人频道实测 | 尚未确认 |

因此，“可以自动发布”基本成立；“官方机器人 API 可以直接发布同样的 Feed 帖子”目前不能从官方机器人消息文档推出，需要单独验证。

### 1.2 YTC 的真实调用链

公开的 YTC 代码显示调用链如下：

```text
YTC Python 后端
  -> 组装 guild_id、channel_id、正文和视频路径
  -> 启动 tencent-channel-cli feed publish-feed
  -> CLI 平台原生二进制
  -> 腾讯频道服务
```

YTC 的 `publish.py` 使用的命令形态是：

```text
tencent-channel-cli feed publish-feed \
  --guild-id <guild_id> \
  --channel-id <channel_id> \
  --content <content> \
  --video <video_path> \
  --json --yes
```

Python 代码还负责 Token 读取、临时凭证环境、超时、账号切换、失败分类和重试。真正的底层请求不在公开的 Python 文件里，而是在 CLI 的平台二进制中。因此，YTC 不能简单归类为“Python 直接拿浏览器 Cookie 模拟 HTTP 请求”。更准确的描述是：

> YTC 是一个使用 Token 驱动的腾讯频道 CLI 自动化工具。

### 1.3 C# 是否能实现

可以。C# 与 Python 的差别不影响这类 HTTP、文件上传、JSON、定时任务和子进程调用能力。

最现实的路径是先让 C# 调用 CLI，验证业务闭环后再决定是否需要纯 C# 实现。只有在明确掌握接口、鉴权和媒体上传协议，并确认具有授权的情况下，才考虑直接复刻内部请求。

## 2. 证据与边界

### 2.1 YTC

- [YTC README](https://github.com/ZyeAlex/YTC)：项目描述为腾讯频道自动发帖，并包含视频转发功能。
- [YTC publish.py](https://raw.githubusercontent.com/ZyeAlex/YTC/main/backend/services/publish.py)：明确调用 `tencent-channel-cli feed publish-feed`。
- [YTC CLI 包装器](https://raw.githubusercontent.com/ZyeAlex/YTC/main/skills/tencent-channel-cli/bin/tencent-channel-cli)：根据操作系统加载 `tencent-channel-cli-win32-x64` 等原生包。
- [YTC feed 参考文档](https://raw.githubusercontent.com/ZyeAlex/YTC/main/skills/tencent-channel-community/references/feed-reference.md)：记录 Feed 发布、图片/视频参数、短贴/长贴限制和错误处理。

YTC 仓库当前没有声明可直接据此再分发的开源许可证，不能默认把 YTC 或其中的二进制、配置和业务代码打包进商业产品。

### 2.2 腾讯频道社区 CLI

- [腾讯频道社区 README](https://github.com/tencent-connect/tencent-channel-community)：公开说明包含频道管理、帖子发布、图片/视频帖子和扫码登录。
- [腾讯频道社区 feed 文档](https://github.com/tencent-connect/tencent-channel-community/blob/master/references/feed-reference.md)：记录 `publish-feed`、`upload-image`、媒体限制和 Feed 规则。
- README 标注项目许可证为 MIT，但 CLI 的 NPM 主包和 Windows 原生二进制包的 NPM 元数据没有明确许可证字段。使用或再分发前必须分别核对仓库许可证、NPM 包许可证和二进制来源。

该项目文档要求从 `https://connect.qq.com/ai` 获取 Token，并通过扫码授权登录。它不是普通浏览器 Cookie 的公开替代品。

### 2.3 官方 QQ 机器人 API

- [消息收发概述](https://bot.q.qq.com/wiki/develop/api-v2/server-inter/message/overview.html)：官方 API 支持频道消息、Markdown 和富媒体消息。
- [发送子频道消息](https://bot.q.qq.com/wiki/develop/api-v2/server-inter/channel/message/send.html)：接口为 `POST /channels/{channel_id}/messages`，要求机器人在子频道有发送消息权限，并有主动消息频率限制。
- [富媒体消息概述](https://bot.q.qq.com/wiki/develop/api-v2/server-inter/message/rich-media.html)：图片、视频等媒体需要先上传获取 `file_info`，然后通过富媒体消息发送。
- [消息类型](https://bot.q.qq.com/wiki/develop/api-v2/server-inter/message/type/overview.html)：频道侧明确列出文本、Markdown、图片、视频、语音等消息能力。

官方机器人文档证明的是“频道消息”能力，不足以证明“社区 Feed 帖子”能力。产品设计必须把普通消息和 Feed 帖子作为两个不同目标处理。

## 3. 三条技术路线

### 路线 A：官方机器人 API

**适用场景**：可以接受发布为频道消息，或者官方后续确认机器人支持目标 Feed 能力。

建议技术栈：

- C# / .NET 8；
- `HttpClient` 和 `System.Text.Json`；
- `ClientWebSocket`，用于需要保持在线的事件或连接场景；
- `MultipartFormDataContent` 或官方分片上传流程处理媒体；
- `BackgroundService`；
- Quartz.NET 或 Hangfire；
- SQLite 保存配置、任务和发布记录。

优点：接口边界更清晰，长期维护和凭证管理更可控。

限制：机器人身份、主动消息频控和可用权限可能与普通用户不同；官方消息接口不应被假设为 Feed 发帖接口。

### 路线 B：C# 调用 `tencent-channel-cli`

**适用场景**：个人内部工具优先验证“本人账号能否在本人频道发布图文/视频 Feed”。

C# 负责：

- 选择正文和媒体文件；
- 校验频道 ID、版块 ID、媒体格式和大小；
- 启动 CLI 子进程；
- 传递最小必要参数和隔离环境变量；
- 读取 JSON 输出；
- 将成功、鉴权失败、权限失败、限流、内容拒绝和超时写入发布记录；
- 负责定时任务和界面。

CLI 负责：

- 登录凭证读取；
- Feed 参数校验；
- 媒体上传；
- 平台请求和返回结果。

优点：验证速度最快，避免一开始逆向原生二进制；与 YTC 已验证的发布链保持一致。

缺点：依赖 Node.js/CLI/原生二进制；CLI 变化会影响工具；二进制再分发与许可证需要单独确认；不是真正的纯 C# 方案。

### 路线 C：C# 直接复刻内部请求

**适用场景**：CLI 无法满足需求，并且已经确认有权对自有账号和自有频道做协议兼容测试。

需要先确认：

- Token 的来源、有效期和刷新机制；
- 请求域名、HTTP 方法、Headers 和 JSON 字段；
- 图片/视频预上传和最终发布的多步流程；
- 是否有签名、时间戳、设备或 IP 绑定；
- 普通消息、短贴、长贴和视频帖是否使用不同接口；
- 成功结果中的 Feed ID、分享链接和幂等行为；
- 频率限制和错误码。

对应 C# 组件：`HttpClient`、`HttpRequestMessage`、`MultipartFormDataContent`、`CookieContainer`（仅在合法授权且确实需要时）、`System.Text.Json`、`Polly` 或自定义退避策略。

风险最高：内部字段可能变化，Cookie/Token 可能过期或绑定设备，错误重试可能产生重复帖子，批量化还会增加账号风控和平台合规风险。当前不建议把它作为第一版路线。

## 4. 已知 Feed 约束

根据社区 CLI 的公开参考文档，第一版验证至少要记录这些约束：

- 指定频道发帖需要 `guild_id` 和 `channel_id`；不能未经确认使用全局作者身份发帖。
- 短贴正文不超过 1000 加权字；长贴不超过 10000 字，并需要标题。
- 短贴最多 18 张图或 1 个视频；长贴最多 50 张图或 5 个视频。
- 短贴和长贴对话题标签的支持不同，长贴不支持话题标签。
- `--content` 是纯文本模式；需要 Markdown 渲染时使用 `--markdown-content`，两者不能混用。
- 图片和视频使用不同参数；短贴图文/视频组合还存在互斥限制，必须按 CLI schema 和运行时校验执行。
- 成功后应保存帖子分享链接或平台返回的帖子标识；不能只依赖控制台输出。
- 需要识别鉴权失败、权限不足、限流、账号异常、内容拒绝和超时。

这些是 CLI 文档层面的约束，不代表所有平台版本永远不变。第一版应把限制做成可配置或集中校验，不要散落在 UI 事件里。

## 5. 推荐决策

当前推荐顺序：

1. 先用官方机器人文档验证普通频道消息和富媒体消息能力，明确它是否满足产品目标。
2. 同时在本人测试频道用 CLI 完成一次文字、一次图片、一次视频 Feed 验证。
3. 如果 CLI 稳定满足目标，让 C# 先封装 CLI，形成个人内部 MVP。
4. 只有 CLI 不满足关键需求时，才评估纯 C# 的直接请求实现。
5. 不把抓包 Cookie、账号轮换、限流规避或批量群发作为第一版功能。

### 建议的第一版形态

- Windows 本地工具；
- 单账号；
- 仅本人管理的频道；
- 立即发布和定时发布；
- 图文、视频各一个明确入口；
- 发布前校验正文、媒体格式、大小和目标版块；
- 发布历史、成功链接、原始错误分类；
- 凭证只保存在本机安全存储或 CLI 管理的位置，不写入日志；
- 默认不自动重试写入类请求，除非能够确认请求未被服务端接受。

## 6. 成本与风险判断

| 维度 | 官方 API | C# 调 CLI | 直接复刻内部请求 |
| --- | --- | --- | --- |
| 首次验证成本 | 中 | 低 | 高 |
| Feed 帖子覆盖 | 未确认 | CLI 已声明支持 | 取决于协议 |
| 普通频道消息覆盖 | 已确认 | 取决于 CLI | 可自行实现 |
| 长期维护 | 较低 | 中 | 高 |
| 是否需要 Node/原生包 | 否 | 是 | 否，但需自行维护协议 |
| 凭证稳定性 | 官方 Token | CLI Token | Cookie/内部 Token 风险较高 |
| 再分发风险 | 低于第三方 CLI | 需核许可证 | 需核授权和平台规则 |
| 第一版推荐度 | 高，前提是能力匹配 | 最高 | 低 |

## 7. 验收门槛

进入 C# MVP 前，至少满足以下条件：

- 手动确认本人账号能在目标版块发布目标类型帖子；
- CLI 成功发布一条纯文本或图片 Feed；
- CLI 成功发布一条视频 Feed；
- 能明确区分普通频道消息与 Feed 帖子；
- 成功结果可保存可访问的分享链接或稳定标识；
- 至少记录一次权限失败、凭证过期、限流或内容拒绝的错误表现；
- 连续少量测试后账号没有出现异常安全提示；
- 核对 YTC、CLI 主包和平台二进制的许可证与再分发边界；
- 确认工具只服务本人账号和本人管理的频道。

只要 Feed 视频发布仍不能稳定通过，就不应开始做定时、多任务或多账号功能。
