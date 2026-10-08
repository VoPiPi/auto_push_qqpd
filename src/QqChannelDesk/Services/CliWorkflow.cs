using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QqChannelDesk.Services;

public sealed class CliWorkflow
{
    private readonly AppLogger _logger;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(10);
    private readonly string? _cliEntryPoint;
    private readonly string _nodeExecutable;
    private readonly FfmpegManager _ffmpegManager;

    public CliWorkflow(
        string? cliEntryPoint = null,
        string nodeExecutable = "node",
        AppLogger? logger = null,
        FfmpegManager? ffmpegManager = null)
    {
        _cliEntryPoint = cliEntryPoint;
        _nodeExecutable = nodeExecutable;
        _logger = logger ?? AppLogger.Instance;
        _ffmpegManager = ffmpegManager ?? new FfmpegManager();
    }

    public async Task<LoginChallenge> StartLoginAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("开始 CLI 官方扫码授权；调试日志会保留脱敏后的 CLI 响应，不记录二维码、授权链接或凭证。");
        var result = await RunCliAsync(["login", "--json", "--yes"], ShortTimeout, cancellationToken);
        _logger.CliResult("启动扫码授权", result.ExitCode, result.StandardOutput, result.StandardError);
        if (result.ExitCode != 0)
        {
            var error = ParseCliError(result.StandardOutput, result.StandardError, "无法启动扫码授权");
            _logger.Warning($"CLI 扫码授权未能启动：{error}");
            throw new InvalidOperationException(error);
        }

        using var json = ParseLastJson(result.StandardOutput);
        var data = GetData(json.RootElement);
        var qr = ReadString(data, "qr_code");
        var uri = ReadString(data, "verification_uri");
        if (qr.Length == 0 || uri.Length == 0)
        {
            throw new InvalidOperationException("CLI 未返回有效的授权二维码或授权链接。");
        }

        return new LoginChallenge(qr, uri);
    }

    public async Task<LoginResult> PollLoginAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunCliAsync(["login", "poll-token", "--json"], LoginTimeout, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            _logger.Info("用户取消 CLI 扫码授权轮询。");
            return new LoginResult(false, "已取消扫码授权。", true);
        }
        if (result.TimedOut)
        {
            _logger.Warning("CLI 扫码授权等待超过 10 分钟时限。");
            return new LoginResult(false, "扫码授权等待超过 CLI 的 10 分钟时限。", false);
        }

        if (result.ExitCode == 0 && TryReadSuccess(result.StandardOutput))
        {
            _logger.Info("CLI 官方扫码授权成功。");
            return new LoginResult(true, "扫码授权成功。", false);
        }

        var message = ParseCliError(result.StandardOutput, result.StandardError, "扫码授权未完成");
        _logger.Warning($"CLI 扫码授权未完成：{message}");
        return new LoginResult(false, message, false);
    }

    public async Task<CliActionResult> LogoutAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunCliAsync(["login", "logout", "--json", "--yes"], ShortTimeout, cancellationToken);
        _logger.CliResult("退出 CLI 登录", result.ExitCode, result.StandardOutput, result.StandardError);
        if (result.ExitCode == 0 && TryReadSuccess(result.StandardOutput))
            return new CliActionResult(true, "已退出登录。");

        return new CliActionResult(false, ParseCliError(result.StandardOutput, result.StandardError, "退出登录失败"));
    }

    public async Task<IReadOnlyList<ChannelChoice>> GetGuildsAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunCliAsync(["manage", "get-my-join-guild-info", "--json"], ShortTimeout, cancellationToken);
        _logger.CliResult("读取频道列表", result.ExitCode, result.StandardOutput, result.StandardError);
        if (result.ExitCode != 0) throw new InvalidOperationException(ParseCliError(result.StandardOutput, result.StandardError, "读取频道列表失败"));
        using var json = ParseLastJson(result.StandardOutput);
        var data = GetData(json.RootElement);
        var choices = new List<ChannelChoice>();
        foreach (var key in new[] { "created_guilds", "managed_guilds", "joined_guilds" })
        {
            if (!data.TryGetProperty(key, out var guilds) || guilds.ValueKind != JsonValueKind.Array) continue;
            foreach (var guild in guilds.EnumerateArray())
            {
                var id = ReadString(guild, "guild_id");
                var name = ReadString(guild, "name");
                var role = ReadString(guild, "role");
                if (id.Length == 0 || name.Length == 0 || choices.Any(item => item.Id == id)) continue;
                choices.Add(new ChannelChoice(id, name, role));
            }
        }
        return choices;
    }

    public async Task<IReadOnlyList<ChannelChoice>> GetChannelsAsync(string guildId, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(guildId.Trim(), @"^\d+$")) throw new ArgumentException("频道 ID 无效。", nameof(guildId));
        var result = await RunCliAsync(["manage", "get-guild-channel-list", "--guild-id", guildId.Trim(), "--json"], ShortTimeout, cancellationToken);
        //_logger.CliResult("读取版块列表", result.ExitCode, result.StandardOutput, result.StandardError);
        if (result.ExitCode != 0) throw new InvalidOperationException(ParseCliError(result.StandardOutput, result.StandardError, "读取版块列表失败"));
        using var json = ParseLastJson(result.StandardOutput);
        var data = GetData(json.RootElement);
        if (!data.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Array) return [];
        return channels.EnumerateArray()
            .Select(channel => new ChannelChoice(ReadString(channel, "channel_id"), ReadString(channel, "channel_name"), ""))
            .Where(channel => channel.Id.Length > 0 && channel.Name.Length > 0)
            .ToArray();
    }

    public async Task<CurrentAccountIdentity> GetCurrentAccountAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunCliAsync(["manage", "get-user-info", "--json"], ShortTimeout, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(ParseCliError(result.StandardOutput, result.StandardError, "读取当前账号失败"));
        }

        using var json = ParseLastJson(result.StandardOutput);
        var data = GetData(json.RootElement);
        var globalNickname = ReadString(data, "global_nickname");
        var nickname = ReadString(data, "nickname");
        try
        {
            return CurrentAccountIdentity.Create(globalNickname, nickname);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public static IReadOnlyList<ChannelChoice> ParseGuildChoices(string jsonText)
    {
        using var json = ParseLastJson(jsonText);
        var data = GetData(json.RootElement);
        var choices = new List<ChannelChoice>();
        foreach (var key in new[] { "created_guilds", "managed_guilds", "joined_guilds" })
        {
            if (!data.TryGetProperty(key, out var guilds) || guilds.ValueKind != JsonValueKind.Array) continue;
            foreach (var guild in guilds.EnumerateArray())
            {
                var id = ReadString(guild, "guild_id");
                var name = ReadString(guild, "name");
                if (id.Length == 0 || name.Length == 0 || choices.Any(item => item.Id == id)) continue;
                choices.Add(new ChannelChoice(id, name, ReadString(guild, "role")));
            }
        }
        return choices;
    }

    public static IReadOnlyList<ChannelChoice> ParseChannelChoices(string jsonText)
    {
        using var json = ParseLastJson(jsonText);
        var data = GetData(json.RootElement);
        if (!data.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Array) return [];
        return channels.EnumerateArray()
            .Select(channel => new ChannelChoice(ReadString(channel, "channel_id"), ReadString(channel, "channel_name"), ""))
            .Where(channel => channel.Id.Length > 0 && channel.Name.Length > 0)
            .ToArray();
    }

    public static string ValidatePublishRequest(PublishRequest request, bool allowUnknownSources = false)
        => ValidatePublishRequestCore(request, requireExistingFiles: true, allowUnknownSources);

    public static string ValidatePublishRequestSources(PublishRequest request, bool allowUnknownSources = false)
        => ValidatePublishRequestCore(request, requireExistingFiles: false, allowUnknownSources);

    private static string ValidatePublishRequestCore(PublishRequest request, bool requireExistingFiles, bool allowUnknownSources)
    {
        if (!Regex.IsMatch(request.GuildId.Trim(), @"^\d+$")) return "频道 ID 必须为数字。";
        if (!Regex.IsMatch(request.ChannelId.Trim(), @"^\d+$")) return "版块 ID 必须为数字。";
        if (string.IsNullOrWhiteSpace(request.Content)) return "请填写帖子正文。";
        var hasTitle = !string.IsNullOrWhiteSpace(request.Title);
        var contentLimit = hasTitle ? 10000 : 1000;
        if (request.Content.Length > contentLimit)
            return hasTitle ? "文本长贴正文不能超过 10000 个字符。" : "文本短贴正文不能超过 1000 个字符。";
        var imageLimit = hasTitle ? 50 : 18;
        var videoLimit = hasTitle ? 5 : 1;
        if (request.MediaPaths.Count > 0 && request.Type == FeedType.Text) return "文本不能包含媒体文件。";
        if (request.Type == FeedType.Image && request.MediaPaths.Count == 0) return "请选择至少一张图片。";
        if (request.Type == FeedType.Image && request.MediaPaths.Count > imageLimit) return $"图片数量不能超过 {imageLimit} 张。";
        if (request.Type == FeedType.Video && request.MediaPaths.Count != 1) return "视频必须选择一个视频文件。";
        if (requireExistingFiles && request.MediaPaths.Any(path => IsNonRemoteMediaPath(path) && !File.Exists(path)))
            return "所选媒体文件不存在，请重新选择。";
        if (request.Type is FeedType.Image or FeedType.Video &&
            request.MediaPaths.Any(path => !MaterialMediaValidator.IsValidSource(
                path,
                request.Type == FeedType.Image ? "image" : "video",
                allowUnknownSources)))
            return "所选媒体文件或链接无效，请重新选择。";
        return string.Empty;
    }

    private static bool IsNonRemoteMediaPath(string path) =>
        !Uri.TryCreate(path?.Trim(), UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps);

    public async Task<PublishResult> PublishTextAsync(PublishRequest request, CancellationToken cancellationToken = default)
        => await PublishAsync(request with { Type = FeedType.Text }, cancellationToken);

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidatePublishRequest(request);
        if (validation.Length > 0) return new PublishResult(false, PublishErrorCategory.Validation, validation, null, null);

        var args = new List<string>
        {
            "feed", "publish-feed", "--guild-id", request.GuildId.Trim(), "--channel-id", request.ChannelId.Trim()
        };
        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            args.Add("--title");
            args.Add(request.Title.Trim());
        }
        args.Add("--content");
        args.Add(request.Content);
        foreach (var path in request.MediaPaths)
        {
            args.Add(request.Type == FeedType.Image ? "--image" : "--video");
            args.Add(path);
        }
        args.Add("--json");
        args.Add("--yes");
        _logger.Info($"开始发布 {request.Type} 到频道 {request.GuildId} / 版块 {request.ChannelId}。");
        var result = await RunCliAsync(args, PublishTimeout, cancellationToken);
        var safeOutput = RedactMediaPaths(result.StandardOutput, request.MediaPaths);
        var safeError = RedactMediaPaths(result.StandardError, request.MediaPaths);
        _logger.CliResult("发布", result.ExitCode, safeOutput, safeError);
        if (result.TimedOut)
        {
            return new PublishResult(false, PublishErrorCategory.Timeout,
                "发布等待超时，未自动重试。请先检查频道是否已出现帖子，再决定后续操作。", null, null);
        }

        return ParsePublishResult(result.ExitCode, safeOutput, safeError);
    }

    private static string RedactMediaPaths(string output, IReadOnlyList<string> mediaPaths)
    {
        foreach (var path in mediaPaths.Where(MaterialMediaValidator.IsLocalPath))
        {
            var fileName = Path.GetFileName(path) ?? "本地媒体";
            output = output.Replace(path, fileName, StringComparison.OrdinalIgnoreCase);
        }
        return output;
    }

    public static PublishResult ParsePublishResult(int? exitCode, string output, string error = "")
    {
        var message = ParseCliError(output, error, "CLI 未返回可识别结果");
        var parsed = TryParseJson(output);
        if (exitCode == 0 && parsed is not null && IsSuccess(parsed.RootElement))
        {
            var root = parsed.RootElement;
            var data = GetData(root);
            var url = FindString(data, "url", "share_url", "shareUrl", "permalink", "link");
            var id = FindString(data, "feed_id", "feedId", "id", "post_id", "postId");
            parsed.Dispose();
            return new PublishResult(true, PublishErrorCategory.None,
                "帖子已提交。请在目标频道确认展示结果。", url, id);
        }

        parsed?.Dispose();
        var category = ClassifyPublishError(message);
        return new PublishResult(false, category, message, null, null);
    }

    private static PublishErrorCategory ClassifyPublishError(string text)
    {
        if (Contains(text, "permission", "权限", "forbidden", "403")) return PublishErrorCategory.Permission;
        if (Contains(text, "rate limit", "频率", "限流", "too many", "429")) return PublishErrorCategory.RateLimit;
        if (Contains(text, "content", "内容", "审核", "违规", "拒绝")) return PublishErrorCategory.ContentRejected;
        if (Contains(text, "login", "未登录", "unauthorized", "授权", "token", "8011")) return PublishErrorCategory.Authentication;
        return PublishErrorCategory.Other;
    }

    private static string ParseCliError(string output, string error, string fallback)
    {
        var combined = $"{output}\n{error}";
        using var json = TryParseJson(combined);
        if (json is not null)
        {
            var root = json.RootElement;
            if (root.TryGetProperty("error", out var errorNode) && errorNode.ValueKind == JsonValueKind.Object)
            {
                var message = ReadString(errorNode, "message");
                if (message.Length > 0) return SafeText(message);
            }
            var data = GetData(root);
            var dataMessage = FindString(data, "message", "detail");
            if (dataMessage.Length > 0) return SafeText(dataMessage);
        }

        var line = combined.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(x => !x.StartsWith('{') && !x.StartsWith('['));
        return SafeText(string.IsNullOrWhiteSpace(line) ? fallback : line);
    }

    private static bool TryReadSuccess(string output)
    {
        using var json = TryParseJson(output);
        return json is not null && IsSuccess(json.RootElement);
    }

    private static bool IsSuccess(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;

    private static string SafeText(string value)
    {
        var safe = Regex.Replace(value, "(?i)(token|authorization|cookie|qr_code|verification_uri)\\s*[=:]\\s*[^\\s,;]+", "$1=[已隐藏]");
        safe = Regex.Replace(safe, @"(?i)bot:v1_[A-Za-z0-9._-]+", "[凭证已隐藏]");
        return safe.Trim();
    }

    private async Task<ProcessResult> RunCliAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var cli = _cliEntryPoint ?? await FindCliEntryPointAsync(cancellationToken);
        if (cli is null) return new ProcessResult(null, "", "未找到 tencent-channel-cli。", false);
        var info = new ProcessStartInfo
        {
            FileName = _nodeExecutable,
            WorkingDirectory = Path.GetDirectoryName(cli)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        var inheritedPath = info.Environment.TryGetValue("PATH", out var path)
            ? path
            : Environment.GetEnvironmentVariable("PATH");
        info.Environment["PATH"] = await _ffmpegManager.BuildCliPathAsync(inheritedPath, cancellationToken).ConfigureAwait(false);
        info.ArgumentList.Add(cli);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) return new ProcessResult(null, "", "无法启动 CLI。", false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(null, "", SafeText(ex.Message), false);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            return new ProcessResult(process.ExitCode, await stdout, await stderr, false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new ProcessResult(null, "", "", true);
        }
    }

    private static async Task<string?> FindCliEntryPointAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in pathEntries)
        {
            var node = Path.Combine(entry.Trim('"'), OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (!File.Exists(node)) continue;
            var npmCli = Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js");
            if (!File.Exists(npmCli)) continue;
            var root = await RunNodeAsync(npmCli, ["root", "-g"], cancellationToken);
            if (root.ExitCode == 0)
            {
                var globalRoot = root.StandardOutput.Trim();
                if (globalRoot.Length > 0) candidates.Add(Path.Combine(globalRoot, "tencent-channel-cli", "bin", "tencent-channel-cli"));
            }
        }
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "node_modules", "tencent-channel-cli", "bin", "tencent-channel-cli"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "node_modules", "tencent-channel-cli", "bin", "tencent-channel-cli"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<ProcessResult> RunNodeAsync(string script, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true };
        info.ArgumentList.Add(script);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info);
        if (process is null) return new ProcessResult(null, "", "", false);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await stdout, await stderr, false);
    }

    private static JsonDocument ParseLastJson(string output) => TryParseJson(output) ??
        throw new InvalidOperationException("CLI 授权输出格式无法识别。");

    private static JsonDocument? TryParseJson(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0) return null;
        try { return JsonDocument.Parse(output[start..]); }
        catch (JsonException) { return null; }
    }

    private static JsonElement GetData(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;

    private static string FindString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        }
        return "";
    }

    private static string ReadString(JsonElement element, string name) => FindString(element, name);
    private static bool Contains(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    public sealed record ProcessResult(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut);
}

public sealed record LoginChallenge(string QrCodeBase64, string VerificationUri);
public sealed record LoginResult(bool Succeeded, string Message, bool Cancelled);
public sealed record CliActionResult(bool Succeeded, string Message);
public sealed record ChannelChoice(string Id, string Name, string Role)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Role) ? Name : $"{Name}（{Role}）";
}
public enum FeedType { Text, Image, Video }
public sealed record PublishRequest(string GuildId, string ChannelId, string Content, string Title = "", FeedType Type = FeedType.Text, IReadOnlyList<string>? Files = null)
{
    public IReadOnlyList<string> MediaPaths => Files ?? Array.Empty<string>();
}
public enum PublishErrorCategory { None, Validation, Authentication, Permission, RateLimit, ContentRejected, Timeout, Other }
public sealed record PublishResult(bool Succeeded, PublishErrorCategory Category, string Message, string? Url, string? PostId);
