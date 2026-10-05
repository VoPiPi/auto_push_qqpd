using QqChannelDesk.Services;
using System.IO;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class CliDiagnosticsTests
{
    [Fact]
    public void ClassifiesSuccessfulLogin()
    {
        var item = CliDiagnostics.ClassifyLoginStatus(0, "logged in");

        Assert.Equal(DiagnosticState.Ready, item.State);
        Assert.Equal("已登录", item.StateLabel);
    }

    [Theory]
    [InlineData("not logged in")]
    [InlineData("未登录")]
    [InlineData("retCode=8011 unauthorized")]
    [InlineData("{\"error\":{\"message\":\"未登录\",\"type\":\"internal\"},\"success\":false}")]
    public void ClassifiesMissingLogin(string output)
    {
        var item = CliDiagnostics.ClassifyLoginStatus(1, output);

        Assert.Equal(DiagnosticState.Warning, item.State);
        Assert.Equal("未登录", item.StateLabel);
        Assert.DoesNotContain("{", item.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifiesUtf8JsonLoginErrorAsMissingLogin()
    {
        var item = CliDiagnostics.ClassifyLoginStatus(
            5,
            "{\"error\":{\"message\":\"未登录\",\"type\":\"internal\"},\"success\":false}");

        Assert.Equal(DiagnosticState.Warning, item.State);
        Assert.Equal("未登录", item.StateLabel);
        Assert.Equal("请使用 CLI 官方扫码授权流程登录", item.Detail);
    }

    [Fact]
    public void DoesNotTreatFailedJsonAsLoggedInWhenExitCodeIsZero()
    {
        var item = CliDiagnostics.ClassifyLoginStatus(
            0,
            "{\"error\":{\"message\":\"服务暂不可用\"},\"success\":false}");

        Assert.Equal(DiagnosticState.Warning, item.State);
        Assert.Equal("状态不确定", item.StateLabel);
    }

    [Fact]
    public void DoesNotTreatEmptySuccessfulCommandAsConfirmedLogin()
    {
        var item = CliDiagnostics.ClassifyLoginStatus(0, "");

        Assert.Equal(DiagnosticState.Warning, item.State);
        Assert.Equal("状态不确定", item.StateLabel);
    }

    [Fact]
    public void RedactsCredentialLikeOutput()
    {
        var sanitized = CliDiagnostics.Sanitize("QQ_AI_CONNECT_TOKEN=bot:v1_secret123 authorization=Bearer-secret");

        Assert.DoesNotContain("secret123", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer-secret", sanitized, StringComparison.Ordinal);
        Assert.Contains("已隐藏", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void AppLoggerDefaultsToSummaryOnlyAndUsesDailyLogName()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new AppLogger(directory);
            var displayedEntries = new List<string>();
            logger.EntryWritten += displayedEntries.Add;
            logger.Info("普通操作摘要");
            logger.Debug("详细调试数据");

            Assert.False(logger.DebugEnabled);
            Assert.Contains(displayedEntries, entry => entry.Contains("普通操作摘要", StringComparison.Ordinal));
            Assert.DoesNotContain(displayedEntries, entry => entry.Contains("详细调试数据", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(directory, "logs", $"{DateTime.Now:yyyy-MM-dd}.log")));
        }
        finally
        {
            DeleteLoggerTestFiles(directory);
        }
    }

    [Theory]
    [InlineData("{\"success\":true,\"data\":{\"feed_id\":\"visible\",\"access_token\":\"secret-token\",\"nested\":{\"cookie\":\"secret-cookie\"}}}")]
    [InlineData("{\"qr_code\":\"base64-secret\",\"verification_uri\":\"https://private.test\"}")]
    [InlineData("CLI response: {\"success\":false,\"data\":{\"token\":\"wrapped-secret\"}}")]
    public void AppLoggerRedactsSensitiveJsonRecursively(string json)
    {
        var redacted = AppLogger.Redact(json);

        Assert.DoesNotContain("secret-token", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-cookie", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("base64-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("private.test", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("wrapped-secret", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void AppLoggerPreservesChineseTextInJson()
    {
        var redacted = AppLogger.Redact("{\"data\":{\"channel_name\":\"全部\"},\"success\":true}");

        Assert.Contains("\"channel_name\":\"全部\"", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u5168", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppLoggerPersistsDebugSetting()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            new AppLogger(directory).SetDebugEnabled(true);

            Assert.True(new AppLogger(directory).DebugEnabled);
        }
        finally
        {
            DeleteLoggerTestFiles(directory);
        }
    }

    [Fact]
    public void AppLoggerShowsSummaryByDefaultAndDebugEntriesOnlyWhenRequested()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new AppLogger(directory);
            logger.Info("环境检查完成");
            logger.Debug("CLI JSON 详细响应：{\"success\":false,\"error\":{\"code\":12345}}");

            var normalEntries = logger.ReadEntries(includeDebug: false);
            var debugEntries = logger.ReadEntries(includeDebug: true);

            Assert.Contains(normalEntries, entry => entry.Contains("环境检查完成", StringComparison.Ordinal));
            Assert.DoesNotContain(normalEntries, entry => entry.Contains("CLI JSON 详细响应", StringComparison.Ordinal));
            Assert.Contains(debugEntries, entry => entry.Contains("CLI JSON 详细响应", StringComparison.Ordinal));
            Assert.DoesNotContain(debugEntries, entry => entry.Contains("12345", StringComparison.Ordinal));
            var logFile = File.ReadAllText(Path.Combine(directory, "logs", $"{DateTime.Now:yyyy-MM-dd}.log"));
            Assert.Contains("12345", logFile, StringComparison.Ordinal);
        }
        finally
        {
            DeleteLoggerTestFiles(directory);
        }
    }

    [Fact]
    public void AppLoggerDoesNotLoadPreviousRunLogsIntoSessionView()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var logs = Path.Combine(directory, "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, $"{DateTime.Now:yyyy-MM-dd}.log"), "previous process entry");

            var logger = new AppLogger(directory);

            Assert.Empty(logger.ReadEntries(includeDebug: true));
        }
        finally
        {
            DeleteLoggerTestFiles(directory);
        }
    }

    [Fact]
    public void AppLoggerDefaultsToLogsFolderBesideApplication()
    {
        var logger = new AppLogger();

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "logs"), logger.LogDirectory);
    }

    [Fact]
    public void AppLoggerHidesErrorCodesInDisplayButKeepsThemInFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new AppLogger(directory);
            var displayed = new List<string>();
            logger.SetDebugEnabled(true);
            logger.EntryWritten += displayed.Add;
            logger.Debug("CLI result: {\"success\":false,\"error\":{\"code\":12345,\"message\":\"forbidden\"}}");

            Assert.Contains(displayed, entry => entry.Contains("forbidden", StringComparison.Ordinal));
            Assert.DoesNotContain(displayed, entry => entry.Contains("12345", StringComparison.Ordinal));
            var logFile = File.ReadAllText(Path.Combine(directory, "logs", $"{DateTime.Now:yyyy-MM-dd}.log"));
            Assert.Contains("12345", logFile, StringComparison.Ordinal);
        }
        finally
        {
            DeleteLoggerTestFiles(directory);
        }
    }

    private static void DeleteLoggerTestFiles(string directory)
    {
        var logFile = Path.Combine(directory, "logs", $"{DateTime.Now:yyyy-MM-dd}.log");
        if (File.Exists(logFile)) File.Delete(logFile);
        if (Directory.Exists(Path.GetDirectoryName(logFile)!)) Directory.Delete(Path.GetDirectoryName(logFile)!);
        var settingsFile = Path.Combine(directory, "settings.json");
        if (File.Exists(settingsFile)) File.Delete(settingsFile);
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
}

public sealed class CliWorkflowTests
{
    [Theory]
    [InlineData("abc", "123", "hello", "频道 ID 必须为数字。")]
    [InlineData("123", "abc", "hello", "版块 ID 必须为数字。")]
    [InlineData("123", "456", "", "请填写帖子正文。")]
    public void ValidatesPublishRequest(string guildId, string channelId, string content, string expected)
    {
        Assert.Equal(expected, CliWorkflow.ValidatePublishRequest(new PublishRequest(guildId, channelId, content)));
    }

    [Fact]
    public void RejectsContentOverShortPostLimit()
    {
        var result = CliWorkflow.ValidatePublishRequest(new PublishRequest("1", "2", new string('x', 1001)));
        Assert.Contains("1000", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowsLongPostBodyWhenTitleIsProvided()
    {
        var result = CliWorkflow.ValidatePublishRequest(new PublishRequest("1", "2", new string('x', 1001), "标题"));
        Assert.Empty(result);
    }

    [Fact]
    public void RejectsLongPostBodyOverLimit()
    {
        var result = CliWorkflow.ValidatePublishRequest(new PublishRequest("1", "2", new string('x', 10001), "标题"));
        Assert.Contains("10000", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresAtLeastOneImageAndAppliesShortPostCountLimit()
    {
        var missing = CliWorkflow.ValidatePublishRequest(new PublishRequest("1", "2", "body", Type: FeedType.Image));
        var tooMany = CliWorkflow.ValidatePublishRequest(new PublishRequest("1", "2", "body", Type: FeedType.Image,
            Files: Enumerable.Repeat("missing.png", 19).ToArray()));

        Assert.Contains("至少一张", missing, StringComparison.Ordinal);
        Assert.Contains("18", tooMany, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresExactlyOneVideoAndExistingMediaFiles()
    {
        var missing = CliWorkflow.ValidatePublishRequest(new PublishRequest("1", "2", "body", Type: FeedType.Video));
        var nonexistent = CliWorkflow.ValidatePublishRequest(new PublishRequest("1", "2", "body", Type: FeedType.Video, Files: ["missing.mp4"]));

        Assert.Contains("一个视频", missing, StringComparison.Ordinal);
        Assert.Contains("不存在", nonexistent, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesGuildsAcrossRoleGroupsAndDeduplicates()
    {
        const string json = "{\"data\":{\"created_guilds\":[{\"guild_id\":\"1\",\"name\":\"自建\",\"role\":\"创建者\"}],\"managed_guilds\":[{\"guild_id\":\"2\",\"name\":\"管理\",\"role\":\"管理员\"}],\"joined_guilds\":[{\"guild_id\":\"1\",\"name\":\"重复\",\"role\":\"成员\"},{\"guild_id\":\"3\",\"name\":\"加入\",\"role\":\"成员\"}]},\"success\":true}";

        var guilds = CliWorkflow.ParseGuildChoices(json);

        Assert.Equal(new[] { "1", "2", "3" }, guilds.Select(guild => guild.Id));
        Assert.Equal("自建（创建者）", guilds[0].DisplayName);
    }

    [Fact]
    public void ParsesChannelNamesAndIds()
    {
        const string json = "{\"data\":{\"channels\":[{\"channel_id\":\"9\",\"channel_name\":\"公告\",\"guild_id\":\"1\"}]},\"success\":true}";
        var channels = CliWorkflow.ParseChannelChoices(json);

        Assert.Single(channels);
        Assert.Equal("9", channels[0].Id);
        Assert.Equal("公告", channels[0].Name);
    }

    [Fact]
    public void ParsesSuccessfulPublishResultWithoutExposingRawJson()
    {
        var result = CliWorkflow.ParsePublishResult(0,
            "{\"success\":true,\"data\":{\"feed_id\":\"feed-123\",\"share_url\":\"https://example.test/post\"}}");

        Assert.True(result.Succeeded);
        Assert.Equal("feed-123", result.PostId);
        Assert.Equal("https://example.test/post", result.Url);
        Assert.DoesNotContain("{", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("无权限", PublishErrorCategory.Permission)]
    [InlineData("rate limit exceeded", PublishErrorCategory.RateLimit)]
    [InlineData("内容审核拒绝", PublishErrorCategory.ContentRejected)]
    [InlineData("未登录", PublishErrorCategory.Authentication)]
    public void ClassifiesPublishErrors(string message, PublishErrorCategory expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            success = false,
            error = new { message }
        });
        var result = CliWorkflow.ParsePublishResult(1, json);
        Assert.Equal(expected, result.Category);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task InvokesInjectedCliAndDoesNotShellInterpolateContent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-channel-cli-{Guid.NewGuid():N}.js");
        const string content = "hello & whoami ` $not-a-command";
        var script = "const a=process.argv.slice(2);const ok=a.includes('--content')&&a[a.indexOf('--content')+1]===" + System.Text.Json.JsonSerializer.Serialize(content) + ";console.log(JSON.stringify({success:ok,data:{feed_id:'fake-id'}}));process.exit(ok?0:1);";
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var workflow = new CliWorkflow(scriptPath);
            var result = await workflow.PublishTextAsync(new PublishRequest("123", "456", content));
            Assert.True(result.Succeeded);
            Assert.Equal("fake-id", result.PostId);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task StartLoginUsesYesAndWritesRedactedCliResponseToDebugFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"QqChannelLoginTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "fake-cli.js");
        var logger = new AppLogger(directory);
        var script = "const a=process.argv.slice(2);console.log(JSON.stringify({success:false,error:{message:a.includes('--yes')?'already logged in':'missing --yes'},data:{qr_code:'qr-secret',verification_uri:'https://private.test'}}));process.exit(1);";
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new CliWorkflow(scriptPath, logger: logger).StartLoginAsync());

            var logFile = File.ReadAllText(Path.Combine(directory, "logs", $"{DateTime.Now:yyyy-MM-dd}.log"));
            Assert.Contains("already logged in", logFile, StringComparison.Ordinal);
            Assert.DoesNotContain("qr-secret", logFile, StringComparison.Ordinal);
            Assert.DoesNotContain("private.test", logFile, StringComparison.Ordinal);
            Assert.Contains(logger.ReadEntries(includeDebug: true), entry => entry.Contains("already logged in", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(scriptPath);
            var logFile = Path.Combine(directory, "logs", $"{DateTime.Now:yyyy-MM-dd}.log");
            var settingsFile = Path.Combine(directory, "settings.json");
            if (File.Exists(logFile)) File.Delete(logFile);
            if (File.Exists(settingsFile)) File.Delete(settingsFile);
            var logsDirectory = Path.Combine(directory, "logs");
            if (Directory.Exists(logsDirectory)) Directory.Delete(logsDirectory);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task LogoutUsesOfficialCliCommandAndReturnsResult()
    {
        if (!OperatingSystem.IsWindows()) return;
        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-channel-cli-{Guid.NewGuid():N}.js");
        const string script = "const a=process.argv.slice(2);const ok=a[0]==='login'&&a[1]==='logout'&&a.includes('--json')&&a.includes('--yes');console.log(JSON.stringify({success:ok,data:{message:'logged out'}}));process.exit(ok?0:1);";
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var result = await new CliWorkflow(scriptPath).LogoutAsync();

            Assert.True(result.Succeeded);
            Assert.Equal("已退出登录。", result.Message);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task PassesTitleAndContentAsSeparateArguments()
    {
        if (!OperatingSystem.IsWindows()) return;
        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-channel-cli-{Guid.NewGuid():N}.js");
        const string title = "标题 & ` $special";
        const string content = "正文独立传递";
        var script = "const a=process.argv.slice(2);const ti=a.indexOf('--title');const ci=a.indexOf('--content');const ok=ti>=0&&a[ti+1]===" + System.Text.Json.JsonSerializer.Serialize(title) + "&&ci>=0&&a[ci+1]===" + System.Text.Json.JsonSerializer.Serialize(content) + ";console.log(JSON.stringify({success:ok,data:{feed_id:'fake-id'}}));process.exit(ok?0:1);";
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var workflow = new CliWorkflow(scriptPath);
            var result = await workflow.PublishTextAsync(new PublishRequest("123", "456", content, title));
            Assert.True(result.Succeeded);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Theory]
    [InlineData(FeedType.Image, "--image", "sample image & ` $file.png")]
    [InlineData(FeedType.Video, "--video", "sample video & ` $file.mp4")]
    public async Task PassesMediaPathsAsSeparateArguments(FeedType type, string mediaFlag, string fileName)
    {
        if (!OperatingSystem.IsWindows()) return;
        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-channel-cli-{Guid.NewGuid():N}.js");
        var mediaPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{fileName}");
        const string content = "caption";
        await File.WriteAllTextAsync(mediaPath, "fake media");
        var script = "const a=process.argv.slice(2);const mi=a.indexOf(" + System.Text.Json.JsonSerializer.Serialize(mediaFlag) + ");const ci=a.indexOf('--content');const ok=mi>=0&&a[mi+1]===" + System.Text.Json.JsonSerializer.Serialize(mediaPath) + "&&ci>=0&&a[ci+1]===" + System.Text.Json.JsonSerializer.Serialize(content) + ";console.log(JSON.stringify({success:ok,data:{feed_id:'fake-id'}}));process.exit(ok?0:1);";
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var workflow = new CliWorkflow(scriptPath);
            var result = await workflow.PublishAsync(new PublishRequest("123", "456", content, Type: type, Files: [mediaPath]));
            Assert.True(result.Succeeded);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(mediaPath);
        }
    }
}
