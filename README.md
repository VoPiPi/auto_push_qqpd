# QQ 频道发布台

Windows 本地 WPF 工具，用于通过官方 CLI 在 QQ 频道发布内容。

## 功能特性

- **环境诊断**：自动检测 Node.js、CLI 和 FFmpeg 状态，支持一键安装或手动重试。
- **扫码授权**：通过 CLI 官方流程完成账号登录授权。
- **频道同步**：同步账号可访问的频道及版块到本地 SQLite 数据库。
- **内容发布**：支持发布文本、图片、视频 Feed，带预览和二次确认。
- **素材仓库**：手动管理文本、图片或视频素材，指定发布目标。
- **定时计划**：支持设置未来发布时间，自动执行发布任务。
- **内容采集**：从公开 HTTP/HTTPS 网页提取标题与正文，保存为草稿。
- **系统设置**：开机自启动、最小化到托盘、关闭行为配置。

## 开发环境

- Windows 10 或更新版本
- .NET 8 SDK / Windows Desktop Runtime
- Node.js
- `tencent-channel-cli`

## 运行

```powershell
dotnet run --project .\src\QqChannelDesk\QqChannelDesk.csproj
```

## 测试

```powershell
dotnet test .\tests\QqChannelDesk.Tests\QqChannelDesk.Tests.csproj
```

## 许可证

请遵守 GPL 许可证关于 FFmpeg 的使用要求。