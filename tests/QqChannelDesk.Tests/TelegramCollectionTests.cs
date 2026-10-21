using Microsoft.Data.Sqlite;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class TelegramCollectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QqChannelDeskTelegramTests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public TelegramCollectionTests()
    {
        _databasePath = Path.Combine(_directory, "channels.db");
    }

    [Fact]
    public async Task CollectionStore_SavesSchedulesAndRejectsDuplicateChannelPerAccount()
    {
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
        var store = new TelegramCollectionStore(_databasePath, context);

        var draft = new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/channel", "channel", 0, true,
            WebProxySelection.Automatic, 999, "  keyword  ");
        var id = await store.SaveAsync(draft);
        var saved = Assert.Single(await store.GetAsync());

        Assert.Equal(id, saved.Id);
        Assert.Equal(24, saved.IntervalHours);
        Assert.Equal(20, saved.MessageLimit);
        Assert.Equal("keyword", saved.Keyword);
        Assert.Equal("每天一次", saved.FrequencyDisplay);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(draft));

        context.Set(CurrentAccountIdentity.Create("global-b", "account-b"));
        Assert.Empty(await store.GetAsync());
        await store.SaveAsync(draft);
        Assert.Single(await store.GetAsync());
    }

    [Fact]
    public async Task CollectionStore_PersistsIncludeAndExcludeKeywordsSeparatelyAndTrimsThem()
    {
        var store = new TelegramCollectionStore(_databasePath);
        var draft = new TelegramCollectionScheduleDraft(
            null,
            "https://t.me/s/keywords",
            "keywords",
            24,
            true,
            WebProxySelection.Automatic,
            20,
            "  广告 | 旧闻  ",
            IncludeKeyword: "  新闻 | 公告  ");

        var id = await store.SaveAsync(draft);
        var saved = Assert.Single(await store.GetAsync());

        Assert.Equal(id, saved.Id);
        Assert.Equal("广告 | 旧闻", saved.Keyword);
        Assert.Equal("新闻 | 公告", saved.IncludeKeyword);
    }

    [Fact]
    public async Task CollectionStore_PersistsTenMinutePollingCronExpression()
    {
        var store = new TelegramCollectionStore(_databasePath);
        var draft = new TelegramCollectionScheduleDraft(
            null,
            "https://t.me/s/polling",
            "polling",
            24,
            true,
            WebProxySelection.Automatic,
            20,
            "",
            CronExpression: "*/10 * * * *");

        var id = await store.SaveAsync(draft);
        var saved = Assert.Single(await store.GetAsync());

        Assert.Equal(id, saved.Id);
        Assert.Equal("*/10 * * * *", saved.CronExpression);
        Assert.Equal("*/10 * * * *", saved.CronDisplay);
    }

    [Fact]
    public async Task CollectionStore_MigratesLegacyScheduleWithoutIncludeKeywordColumn()
    {
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE TelegramCollectionSchedules (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ChannelUrl TEXT NOT NULL,
                    ChannelName TEXT NOT NULL DEFAULT '',
                    IntervalHours INTEGER NOT NULL DEFAULT 24,
                    Enabled INTEGER NOT NULL DEFAULT 1,
                    ProxySelection TEXT NOT NULL DEFAULT 'auto',
                    MessageLimit INTEGER NOT NULL DEFAULT 20,
                    Keyword TEXT NOT NULL DEFAULT '',
                    LastCollectedAt TEXT NULL,
                    LastSuccessAt TEXT NULL,
                    LastStatus TEXT NOT NULL DEFAULT '从未采集',
                    LastError TEXT NOT NULL DEFAULT '',
                    LastAddedCount INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    AccountKey TEXT NULL
                );
                INSERT INTO TelegramCollectionSchedules
                    (ChannelUrl, ChannelName, CreatedAt, UpdatedAt, Keyword, AccountKey)
                VALUES
                    ('https://t.me/s/legacy', 'legacy', '2026-10-11T00:00:00+08:00',
                     '2026-10-11T00:00:00+08:00', '旧排除词', NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new TelegramCollectionStore(_databasePath);
        var saved = Assert.Single(await store.GetAsync());

        Assert.Equal("旧排除词", saved.Keyword);
        Assert.Equal("", saved.IncludeKeyword);
    }

    [Fact]
    public async Task CollectionStore_UpdatesRunResultAndKeepsDisabledScheduleReadable()
    {
        var store = new TelegramCollectionStore(_databasePath);
        var id = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/channel", "channel", 6, false, "proxyunblocker", 20, ""));
        var collectedAt = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(8));

        await store.UpdateResultAsync(id, collectedAt, true, 3, "新增 3 条");
        var saved = Assert.Single(await store.GetAsync());

        Assert.False(saved.Enabled);
        Assert.Equal(collectedAt, saved.LastCollectedAt);
        Assert.Equal(collectedAt, saved.LastSuccessAt);
        Assert.Equal("成功", saved.LastStatus);
        Assert.Equal(3, saved.LastAddedCount);
        Assert.Contains("新增 3 条", saved.LastError);
    }

    [Fact]
    public async Task CollectionStore_ReadsRulesForMultipleSchedulesAndKeepsAccountScope()
    {
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
        var store = new TelegramCollectionStore(_databasePath, context);
        var first = await SaveScheduleAsync(store, "first");
        var second = await SaveScheduleAsync(store, "second");
        var withoutRules = await SaveScheduleAsync(store, "no-rules");
        await store.ReplaceRulesAsync(first, [Rule("text")]);
        await store.ReplaceRulesAsync(second, [Rule("image"), Rule("video")]);

        var grouped = await store.GetRulesByScheduleIdsAsync([first, second, withoutRules]);

        Assert.Equal(2, grouped.Count);
        Assert.Single(grouped[first]);
        Assert.Equal(2, grouped[second].Count);
        Assert.False(grouped.ContainsKey(withoutRules));

        // 超出单个 SQLite 绑定参数批次的查询仍需安全执行。
        var overflow = Enumerable.Range(0, 900).Select(index => 100_000L + index).ToArray();
        Assert.Empty(await store.GetRulesByScheduleIdsAsync(overflow));

        context.Set(CurrentAccountIdentity.Create("global-b", "account-b"));
        Assert.Empty(await store.GetRulesByScheduleIdsAsync([first, second]));
    }

    [Fact]
    public async Task CollectionStore_RejectsReplacingRulesForAnotherAccountsSchedule()
    {
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
        var store = new TelegramCollectionStore(_databasePath, context);
        var scheduleId = await SaveScheduleAsync(store, "account-a");
        await store.ReplaceRulesAsync(scheduleId, [Rule("text")]);

        context.Set(CurrentAccountIdentity.Create("global-b", "account-b"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ReplaceRulesAsync(scheduleId, [Rule("image")]));

        context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
        var rule = Assert.Single(await store.GetRulesAsync(scheduleId));
        Assert.Equal("text", rule.MessageType);
    }

    [Fact]
    public async Task CollectionStore_PersistsCancelledStatusWithoutOverwritingLastSuccess()
    {
        var store = new TelegramCollectionStore(_databasePath);
        var id = await SaveScheduleAsync(store, "cancelled");
        var lastSuccess = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(8));
        await store.UpdateResultAsync(id, lastSuccess, true, 3, "上次成功");
        await store.UpdateFailureAsync(id, lastSuccess.AddMinutes(1), 3, "模拟失败");

        var cancelledAt = lastSuccess.AddMinutes(10);
        await store.UpdateCancelledAsync(id, cancelledAt, 1, "已新增 1 条，已跳过 0 条");

        var saved = Assert.Single(await store.GetAsync());
        Assert.Equal("已取消", saved.LastStatus);
        Assert.Equal(lastSuccess, saved.LastCollectedAt);
        Assert.Equal(cancelledAt, saved.LastAttemptAt);
        Assert.Equal(lastSuccess, saved.LastSuccessAt);
        Assert.Equal(1, saved.LastAddedCount);
        Assert.Null(saved.NextRetryAt);
        Assert.Equal(0, saved.RetryAttempt);
    }

    [Fact]
    public async Task CollectionStore_RetriesWithGrowingBackoffAndResetsAfterSuccess()
    {
        var store = new TelegramCollectionStore(_databasePath);
        var id = await SaveScheduleAsync(store, "retry");
        var attemptedAt = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(8));
        var expectedDelays = new int?[] { 5, 15, 30, 60, 60, 60, 60, null };

        for (var attempt = 0; attempt < expectedDelays.Length; attempt++)
        {
            var currentAttempt = attemptedAt.AddHours(attempt);
            await store.UpdateFailureAsync(id, currentAttempt, attempt, $"第 {attempt + 1} 次失败");

            var saved = Assert.Single(await store.GetAsync());
            Assert.Equal(attempt + 1, saved.RetryAttempt);
            Assert.Equal(
                expectedDelays[attempt] is { } delay ? currentAttempt.AddMinutes(delay) : null,
                saved.NextRetryAt);
            Assert.Equal(
                expectedDelays[attempt] is null
                    ? TelegramCollectionStore.AutomaticRetryStoppedStatus
                    : "失败",
                saved.LastStatus);
            Assert.Equal(attempt, saved.LastAddedCount);
        }

        var exhausted = Assert.Single(await store.GetAsync());
        Assert.False(TelegramCollectionStore.IsDue(exhausted, attemptedAt.AddDays(2)));

        await store.UpdateFailureAsync(
            id,
            attemptedAt.AddHours(9),
            0,
            "手动失败",
            automaticRetry: false);
        var afterManualFailure = Assert.Single(await store.GetAsync());
        Assert.Equal(TelegramCollectionStore.MaxAutomaticRetryAttempts, afterManualFailure.RetryAttempt);
        Assert.Null(afterManualFailure.NextRetryAt);
        Assert.Equal(TelegramCollectionStore.AutomaticRetryStoppedStatus, afterManualFailure.LastStatus);

        var successAt = attemptedAt.AddHours(5);
        await store.UpdateSuccessAsync(id, successAt, 2, "成功", "120");

        var completed = Assert.Single(await store.GetAsync());
        Assert.Equal(0, completed.RetryAttempt);
        Assert.Null(completed.NextRetryAt);
        Assert.Equal(successAt, completed.LastSuccessAt);
        Assert.Equal(120, completed.LastMessageId);
    }

    [Fact]
    public async Task CollectionStore_SanitizesPersistedErrors()
    {
        var store = new TelegramCollectionStore(_databasePath);
        var id = await SaveScheduleAsync(store, "redaction");
        var attemptedAt = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(8));

        await store.UpdateFailureAsync(
            id,
            attemptedAt,
            0,
            "QQ_AI_CONNECT_TOKEN=bot:v1_secret123 authorization=Bearer-secret");

        var failed = Assert.Single(await store.GetAsync());
        Assert.DoesNotContain("secret123", failed.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer-secret", failed.LastError, StringComparison.Ordinal);
        Assert.Contains("已隐藏", failed.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("secret123", failed.ResultDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectionStore_PersistsFirstRunModeAndResetsWatermarkWhenItChanges()
    {
        var store = new TelegramCollectionStore(_databasePath);
        var draft = new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/watermark", "watermark", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "new-only");
        var id = await store.SaveAsync(draft);
        var attemptedAt = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(8));
        await store.UpdateSuccessAsync(id, attemptedAt, 0, "成功", "120");

        var saved = Assert.Single(await store.GetAsync());
        Assert.Equal("new-only", saved.FirstRunMode);
        Assert.Equal(120, saved.LastMessageId);

        await store.SaveAsync(draft with { Id = id, MessageLimit = 30 });
        saved = Assert.Single(await store.GetAsync());
        Assert.Equal(20, saved.MessageLimit);
        Assert.Equal(120, saved.LastMessageId);

        await store.SaveAsync(draft with { Id = id, FirstRunMode = "backfill" });
        saved = Assert.Single(await store.GetAsync());
        Assert.Equal("backfill", saved.FirstRunMode);
        Assert.Equal(0, saved.LastMessageId);
    }

    [Fact]
    public async Task CollectionStore_SaveWithRulesRollsBackScheduleWhenRuleIsInvalid()
    {
        var store = new TelegramCollectionStore(_databasePath);
        var id = await SaveScheduleAsync(store, "transaction");
        await store.ReplaceRulesAsync(id, [Rule("text")]);
        var original = Assert.Single(await store.GetAsync());

        var changed = new TelegramCollectionScheduleDraft(
            id, original.ChannelUrl, original.ChannelName, original.IntervalHours, original.Enabled,
            original.ProxySelection, 77, original.Keyword, original.FrequencyType, original.FrequencyValue,
            original.Weekday, original.TriggerTime, original.CronExpression, original.AutoScheduleEnabled,
            original.AutoScheduleDelayMinutes, original.AutoScheduleMaxPerRun, original.FirstRunMode);
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveWithRulesAsync(changed, [Rule("invalid")]));

        var saved = Assert.Single(await store.GetAsync());
        Assert.Equal(20, saved.MessageLimit);
        var rule = Assert.Single(await store.GetRulesAsync(id));
        Assert.Equal("text", rule.MessageType);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("2026-10-12T08:00:00+08:00", true)]
    [InlineData("2026-10-13T07:59:59+08:00", false)]
    public void CollectionStore_IsDueUsesConfiguredInterval(string? lastText, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset? last = lastText is null ? null : DateTimeOffset.Parse(lastText);

        Assert.Equal(expected, TelegramCollectionStore.IsDue(last, now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void CollectionStore_IsDueSupportsDailyEveryDaysAndWeeklySchedules()
    {
        var now = new DateTimeOffset(2026, 10, 14, 8, 30, 0, TimeSpan.FromHours(8));
        var daily = Schedule("daily", triggerTime: "08:00");
        Assert.True(TelegramCollectionStore.IsDue(daily, now));
        Assert.False(TelegramCollectionStore.IsDue(daily with { LastCollectedAt = now }, now));

        var everyThreeDays = Schedule("every-days", frequencyValue: 3, triggerTime: "08:00") with
        {
            LastCollectedAt = now.AddDays(-2)
        };
        Assert.False(TelegramCollectionStore.IsDue(everyThreeDays, now));
        Assert.True(TelegramCollectionStore.IsDue(everyThreeDays with { LastCollectedAt = now.AddDays(-3) }, now));

        var weekly = Schedule("weekly", weekday: 3, triggerTime: "08:00");
        Assert.True(TelegramCollectionStore.IsDue(weekly, now));
        Assert.False(TelegramCollectionStore.IsDue(weekly with { LastCollectedAt = now }, now));
        Assert.False(TelegramCollectionStore.IsDue(weekly with { Weekday = 4 }, now));
    }

    [Theory]
    [InlineData("* * * * *", true)]
    [InlineData("0 2 * * *", true)]
    [InlineData("*/15 * * * *", true)]
    [InlineData("0 9 * * 1-5", true)]
    [InlineData("0 9 * * 8", false)]
    [InlineData("0 9 * *", false)]
    [InlineData("61 * * * *", false)]
    public void CronSchedule_ValidatesFiveFieldExpressions(string expression, bool valid)
    {
        Assert.Equal(valid, TelegramCronSchedule.TryParse(expression, out _, out _));
    }

    [Fact]
    public void TelegramCronPresetCatalog_EveryPresetParses()
    {
        var presets = TelegramCronPresetCatalog.Options
            .Where(preset => preset.Name != TelegramCronPresetCatalog.CustomName)
            .ToArray();

        Assert.NotEmpty(presets);
        Assert.All(presets, preset =>
        {
            Assert.True(
                TelegramCronSchedule.TryParse(preset.Expression, out _, out var error),
                $"{preset.Name} 的 Cron 表达式无效：{error}");
        });
    }

    [Fact]
    public void TelegramCronPresetCatalog_OffersTenThirtyAndSixtyMinutePolling()
    {
        Assert.Contains(
            TelegramCronPresetCatalog.Options,
            preset => preset.Name == "每 10 分钟" && preset.Expression == "*/10 * * * *");
        Assert.Contains(
            TelegramCronPresetCatalog.Options,
            preset => preset.Name == "每 30 分钟" && preset.Expression == "*/30 * * * *");
        Assert.Contains(
            TelegramCronPresetCatalog.Options,
            preset => preset.Name == "每 60 分钟" && preset.Expression == "0 * * * *");
    }

    [Theory]
    [InlineData("*/10 * * * *", "每 10 分钟")]
    [InlineData("*/30 * * * *", "每 30 分钟")]
    [InlineData("0 * * * *", "每 60 分钟")]
    public void TelegramCronPresetCatalog_SelectsPollingPresetFromExpression(
        string expression,
        string expectedName)
    {
        Assert.Equal(expectedName, TelegramCronPresetCatalog.Find(expression).Name);
    }

    [Fact]
    public void CronSchedule_ComputesNextOccurrenceAndTreatsZeroAndSevenAsSunday()
    {
        var monday = new DateTime(2026, 10, 12, 8, 30, 0, DateTimeKind.Unspecified);
        var weekday = TelegramCronSchedule.Parse("*/15 9-10 * * 1-5");
        Assert.Equal(new DateTime(2026, 10, 12, 9, 0, 0), weekday.GetNextOccurrence(monday));

        var everyTenMinutes = TelegramCronSchedule.Parse("*/10 * * * *");
        Assert.Equal(
            new DateTime(2026, 10, 12, 9, 10, 0),
            everyTenMinutes.GetNextOccurrence(new DateTime(2026, 10, 12, 9, 0, 0)));

        var sundayFromZero = TelegramCronSchedule.Parse("0 9 * * 0");
        var sundayFromSeven = TelegramCronSchedule.Parse("0 9 * * 7");
        var saturday = new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(new DateTime(2026, 10, 18, 9, 0, 0), sundayFromZero.GetNextOccurrence(saturday));
        Assert.Equal(new DateTime(2026, 10, 18, 9, 0, 0), sundayFromSeven.GetNextOccurrence(saturday));
    }

    [Fact]
    public void CronSchedule_StepFromSingleValueExpandsToFieldMaximum()
    {
        var schedule = TelegramCronSchedule.Parse("5/15 * * * *");

        var occurrence = schedule.GetNextOccurrence(new DateTime(2026, 10, 17, 8, 0, 0, DateTimeKind.Unspecified));
        Assert.Equal(new DateTime(2026, 10, 17, 8, 5, 0), occurrence);

        // 5/15 必须等价于 {5,20,35,50}，不能在 5 分之后跳到下一小时。
        occurrence = schedule.GetNextOccurrence(new DateTime(2026, 10, 17, 8, 5, 0, DateTimeKind.Unspecified));
        Assert.Equal(new DateTime(2026, 10, 17, 8, 20, 0), occurrence);
        occurrence = schedule.GetNextOccurrence(new DateTime(2026, 10, 17, 8, 50, 0, DateTimeKind.Unspecified));
        Assert.Equal(new DateTime(2026, 10, 17, 9, 5, 0), occurrence);
    }

    [Fact]
    public void CronSchedule_FindsLeapDayBeyondOneYear()
    {
        var schedule = TelegramCronSchedule.Parse("0 9 29 2 *");
        var occurrence = schedule.GetNextOccurrence(new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Unspecified));

        Assert.Equal(new DateTime(2028, 2, 29, 9, 0, 0), occurrence);
    }

    [Fact]
    public void CronSchedule_DoesNotRepeatImmediatelyAfterExecution()
    {
        var schedule = TelegramCronSchedule.Parse("0 9 * * *");
        var lastRun = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.FromHours(8));

        Assert.False(schedule.IsDue(lastRun, lastRun));
        Assert.False(schedule.IsDue(lastRun, lastRun.AddMinutes(1)));
        Assert.True(schedule.IsDue(lastRun, lastRun.AddDays(1)));
    }

    [Theory]
    [InlineData("*/10 * * * *", 10)]
    [InlineData("*/30 * * * *", 30)]
    public void CollectionStore_PollingCronRunsAtIntervalWithoutRepeatingSameMinute(
        string expression,
        int intervalMinutes)
    {
        var lastRun = new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.FromHours(8));
        var dueAt = lastRun.AddMinutes(intervalMinutes);
        var schedule = Schedule("daily") with
        {
            CronExpression = expression,
            LastCollectedAt = lastRun
        };

        Assert.False(TelegramCollectionStore.IsDue(schedule, dueAt.AddMinutes(-1)));
        Assert.True(TelegramCollectionStore.IsDue(schedule, dueAt));
        Assert.False(TelegramCollectionStore.IsDue(schedule with { LastCollectedAt = dueAt }, dueAt));
    }

    [Fact]
    public void TelegramDistributionRules_MatchKeywordsAndUseFirstMatchingRule()
    {
        var message = new TelegramChannelMessage(
            "https://t.me/channel/42", "t.me", DateTimeOffset.Now, "头像公告", "请查看内容", [], "");
        var first = new TelegramDistributionRule(1, 1, "text", "头像|通知", "guild-1", "公告频道", "channel-1", "头像", 0, DateTimeOffset.Now, DateTimeOffset.Now);
        var later = first with { Id = 2, ChannelId = "channel-2", ChannelName = "其他", SortOrder = 1 };

        var matched = TelegramDistributionRuleMatcher.Match(message, [later, first]);

        Assert.Same(first, matched);
        Assert.True(TelegramDistributionRuleMatcher.MatchesKeyword("Some NOTICE", "notice|other"));
        Assert.False(TelegramDistributionRuleMatcher.MatchesKeyword("正文", "图片|视频"));
    }

    [Fact]
    public void TelegramDistributionRules_ResolveMediaTypeAndSkipWrongType()
    {
        var image = new TelegramChannelMessage("https://t.me/channel/1", "t.me", null, "", "", [
            new TelegramMediaRef("https://example.com/a.jpg", "https://example.com/a.jpg", "image")], "");
        var videoRule = new TelegramDistributionRule(1, 1, "video", "", "guild", "频道", "channel", "视频", 0, DateTimeOffset.Now, DateTimeOffset.Now);
        var imageRule = videoRule with { Id = 2, MessageType = "image" };

        Assert.Equal("image", TelegramDistributionRuleMatcher.ResolveMessageType(image));
        Assert.Null(TelegramDistributionRuleMatcher.Match(image, [videoRule]));
        Assert.Same(imageRule, TelegramDistributionRuleMatcher.Match(image, [videoRule, imageRule]));
    }

    [Theory]
    [InlineData("https://t.me/channel/123", true, 123L)]
    [InlineData("https://t.me/channel/123/", true, 123L)]
    [InlineData("https://t.me/channel/0", false, 0L)]
    [InlineData("https://t.me/channel/-2", false, 0L)]
    [InlineData("https://t.me/s/channel", false, 0L)]
    [InlineData("not-a-url", false, 0L)]
    public void TelegramCollector_ReadsOnlyPositiveNumericMessageNumbers(
        string url,
        bool expectedValid,
        long expectedNumber)
    {
        var valid = TelegramCollectorService.TryReadMessageNumber(url, out var number);

        Assert.Equal(expectedValid, valid);
        Assert.Equal(expectedNumber, number);
    }

    [Fact]
    public void PrepareScheduledMessages_FiltersWatermarkExclusionsSortsAndLimits()
    {
        var firstPublishedAt = new DateTimeOffset(2026, 10, 11, 7, 0, 0, TimeSpan.Zero);
        var messages = new[]
        {
            Message("https://t.me/channel/7", "", "普通 7", publishedAt: firstPublishedAt.AddMinutes(7)),
            Message("https://t.me/channel/1", "", "旧 1", publishedAt: firstPublishedAt.AddMinutes(1)),
            Message("https://t.me/channel/5", "广告 5", "正文", publishedAt: firstPublishedAt.AddMinutes(5)),
            Message("https://t.me/channel/3", "", "普通 3", publishedAt: firstPublishedAt.AddMinutes(3)),
            Message("https://t.me/channel/2", "", "旧 2", publishedAt: firstPublishedAt.AddMinutes(2)),
            Message("https://t.me/channel/6", "", "普通 6", publishedAt: firstPublishedAt.AddMinutes(6)),
            Message("https://t.me/channel/4", "广告 4", "正文", publishedAt: firstPublishedAt.AddMinutes(4))
        };

        var prepared = TelegramCollectionScheduler.PrepareScheduledMessages(
            messages,
            lastMessageId: 2,
            limit: 2,
            inclusionKeywords: "",
            exclusionKeywords: "广告",
            watermarkCandidate: out var watermark);

        Assert.Equal(
            ["https://t.me/channel/7", "https://t.me/channel/6"],
            prepared.Select(message => message.Url));
        Assert.Equal(7, watermark);
    }

    [Fact]
    public void PrepareScheduledMessages_DoesNotReadPastNewestLimitToFillExcludedMessages()
    {
        var firstPublishedAt = new DateTimeOffset(2026, 10, 11, 7, 0, 0, TimeSpan.Zero);
        var messages = new[]
        {
            Message("https://t.me/channel/7", "", "普通 7", publishedAt: firstPublishedAt.AddMinutes(7)),
            Message("https://t.me/channel/6", "广告 6", "正文", publishedAt: firstPublishedAt.AddMinutes(6)),
            Message("https://t.me/channel/5", "", "普通 5", publishedAt: firstPublishedAt.AddMinutes(5))
        };

        var prepared = TelegramCollectionScheduler.PrepareScheduledMessages(
            messages,
            lastMessageId: 0,
            limit: 2,
            inclusionKeywords: "",
            exclusionKeywords: "广告",
            watermarkCandidate: out var watermark);

        Assert.Equal(["https://t.me/channel/7"], prepared.Select(message => message.Url));
        Assert.Equal(7, watermark);
    }

    [Fact]
    public void PrepareScheduledMessages_AppliesInclusionBeforeExclusionAndExclusionWins()
    {
        var firstPublishedAt = new DateTimeOffset(2026, 10, 11, 7, 0, 0, TimeSpan.Zero);
        var messages = new[]
        {
            Message("https://t.me/channel/4", "新闻 4", "普通更新", publishedAt: firstPublishedAt.AddMinutes(4)),
            Message("https://t.me/channel/3", "新闻 3", "广告合作", publishedAt: firstPublishedAt.AddMinutes(3)),
            Message("https://t.me/channel/2", "其他 2", "广告", publishedAt: firstPublishedAt.AddMinutes(2)),
            Message("https://t.me/channel/1", "其他 1", "普通内容", publishedAt: firstPublishedAt.AddMinutes(1))
        };

        var prepared = TelegramCollectionScheduler.PrepareScheduledMessages(
            messages,
            lastMessageId: 0,
            limit: 20,
            inclusionKeywords: "新闻",
            exclusionKeywords: "广告",
            watermarkCandidate: out var watermark);

        Assert.Equal(["https://t.me/channel/4"], prepared.Select(message => message.Url));
        Assert.Equal(4, watermark);
    }

    [Fact]
    public void PrepareScheduledMessages_DoesNotBackfillInclusionMatchOutsideNewestLimit()
    {
        var firstPublishedAt = new DateTimeOffset(2026, 10, 11, 7, 0, 0, TimeSpan.Zero);
        var messages = new[]
        {
            Message("https://t.me/channel/3", "普通 3", "正文", publishedAt: firstPublishedAt.AddMinutes(3)),
            Message("https://t.me/channel/2", "普通 2", "正文", publishedAt: firstPublishedAt.AddMinutes(2)),
            Message("https://t.me/channel/1", "新闻 1", "较早的包含词命中", publishedAt: firstPublishedAt.AddMinutes(1))
        };

        var prepared = TelegramCollectionScheduler.PrepareScheduledMessages(
            messages,
            lastMessageId: 0,
            limit: 2,
            inclusionKeywords: "新闻",
            exclusionKeywords: "",
            watermarkCandidate: out var watermark);

        Assert.Empty(prepared);
        Assert.Equal(3, watermark);
    }

    private static TelegramCollectionSchedule Schedule(string type, int frequencyValue = 1, int weekday = 1, string triggerTime = "00:00") =>
        new(1, "https://t.me/s/channel", "channel", 24, true, "auto", 20, "", null, null, "从未采集", "", 0,
            DateTimeOffset.Now, DateTimeOffset.Now, type, frequencyValue, weekday, triggerTime);

    private static Task<long> SaveScheduleAsync(TelegramCollectionStore store, string name) =>
        store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, $"https://t.me/s/{name}", name, 24, true, WebProxySelection.Automatic, 20, ""));

    private static TelegramDistributionRuleDraft Rule(string messageType) =>
        new(messageType, "", "100", "版块", "200", "频道");

    [Fact]
    public async Task Materializer_SavesTelegramSourceAndSkipsDuplicateBeforeDownloading()
    {
        var library = new ContentLibraryStore(_databasePath);
        var calls = 0;
        var collector = new TelegramCollectorService();
        var materializer = new TelegramMaterializer(collector, library, (message, _, _) =>
        {
            calls++;
            return Task.FromResult(new TelegramDraftResult(
                Path.Combine(_directory, "temp"), ["https://example.com/image.jpg"], 0));
        });
        var message = Message("https://t.me/channel/42", "", "正文");

        var first = await materializer.MaterializeAsync(message, "");
        var second = await materializer.MaterializeAsync(message, "");
        var material = Assert.Single(await library.GetMaterialsAsync());

        Assert.True(first.Succeeded);
        Assert.True(second.SkippedDuplicate);
        Assert.Equal(first.MaterialId, second.MaterialId);
        Assert.Equal(1, calls);
        Assert.Equal("", material.Title);
        Assert.Equal("正文", material.Content);
        Assert.Equal("TG采集", material.SourceType);
        Assert.Equal(message.Url, material.SourceUrl);
    }

    [Fact]
    public async Task Materializer_DoesNotCreateEmptyMaterialWhenAllMediaFail()
    {
        var library = new ContentLibraryStore(_databasePath);
        var materializer = new TelegramMaterializer(new TelegramCollectorService(), library, (_, _, _) =>
            Task.FromResult(new TelegramDraftResult(Path.Combine(_directory, "temp"), [], 1)));
        var message = Message("https://t.me/channel/43", "", "");

        var result = await materializer.MaterializeAsync(message, "");

        Assert.True(result.SkippedEmpty);
        Assert.False(result.Succeeded);
        Assert.Empty(await library.GetMaterialsAsync());
    }

    [Fact]
    public async Task Materializer_KeepsSuccessfulMediaWhenSomeDownloadsFail()
    {
        var library = new ContentLibraryStore(_databasePath);
        var mediaRoot = Path.Combine(_directory, "partial-media");
        var settings = new SystemSettingsStore(_databasePath);
        await settings.SaveAsync(new AppSettings(MaterialStoragePath: mediaRoot));
        var mediaStorage = new MediaStorageService(settings);
        var channelDirectory = Path.Combine(_directory, "temp", "telegram", "20261013", "partial");
        Directory.CreateDirectory(channelDirectory);
        var stagedImage = Path.Combine(channelDirectory, "image.jpg");
        File.WriteAllBytes(stagedImage, [3, 3, 3]);
        var message = Message("https://t.me/channel/44", "", "需要保留正文,但媒体必须完整");
        var materializer = new TelegramMaterializer(
            new TelegramCollectorService(),
            library,
            (_, _, _) => Task.FromResult(new TelegramDraftResult(channelDirectory, [stagedImage], 1)),
            mediaStorage,
            (paths, _) =>
            {
                foreach (var path in paths)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                return Task.CompletedTask;
            });

        var result = await materializer.MaterializeAsync(message, "");

        Assert.True(result.Succeeded);
        Assert.False(result.SkippedEmpty);
        Assert.NotNull(result.MaterialId);
        Assert.Equal(1, result.FailedMediaCount);
        Assert.Contains("不自动排期", result.Error);
        var material = Assert.Single(await library.GetMaterialsAsync());
        Assert.Equal(message.Text, material.Content);
        var storedMedia = Assert.Single(material.MediaLinks);
        Assert.True(File.Exists(storedMedia));
        Assert.False(File.Exists(stagedImage));
    }

    [Fact]
    public void MaterialRecord_UsesTitleThenBodyThenMediaForDisplay()
    {
        var now = DateTimeOffset.Now;
        var title = new MaterialRecord(1, "标题", "text", "正文", "", "", "", "", null, "waitsend", "", [], now, now);
        var body = title with { Title = "", Content = "正文" };
        var images = title with { Title = "", Content = "", Type = "image", MediaLinks = ["a.jpg", "b.jpg"] };
        var video = title with { Title = "", Content = "", Type = "video", MediaLinks = ["a.mp4"] };
        var empty = title with { Title = "", Content = "", Type = "text" };

        Assert.Equal("标题", title.TitleContentDisplay);
        Assert.Equal("正文", body.TitleContentDisplay);
        Assert.Equal("媒体 · 2 张", images.TitleContentDisplay);
        Assert.Equal("媒体 · 1 个", video.TitleContentDisplay);
        Assert.Equal("（无内容）", empty.TitleContentDisplay);
        Assert.Equal("手工添加", title.SourceDisplay);
        var telegram = title with { SourceType = "TG采集", SourceUrl = "https://t.me/channel/42" };
        Assert.Equal("TG采集：https://t.me/channel/42", telegram.SourceDisplay);
    }

    [Fact]
    public async Task Materializer_DiscardsStagedMediaWhenNothingIsSaved()
    {
        var library = new ContentLibraryStore(_databasePath);
        var channelDirectory = Path.Combine(_directory, "temp", "telegram", "20261013", "channel");
        Directory.CreateDirectory(channelDirectory);
        var stagedVideo = Path.Combine(channelDirectory, "video.bin");
        File.WriteAllBytes(stagedVideo, [7, 7, 7]);
        var cleaned = new List<string>();
        var materializer = new TelegramMaterializer(
            new TelegramCollectorService(),
            library,
            (_, _, _) => Task.FromResult(new TelegramDraftResult(channelDirectory, [stagedVideo], 1)),
            cleanupStagingAsync: (paths, _) =>
            {
                cleaned.AddRange(paths);
                return Task.CompletedTask;
            });
        var message = Message("https://t.me/channel/45", "", "");

        var result = await materializer.MaterializeAsync(message, "");

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.FailedMediaCount);
        Assert.Equal([stagedVideo], cleaned);
        Assert.Empty(await library.GetMaterialsAsync());
    }

    [Theory]
    [InlineData("image", ".jpg")]
    [InlineData("video", ".mp4")]
    public async Task Materializer_PromotesDownloadedMediaToLibraryAndRemovesStaging(string type, string extension)
    {
        var library = new ContentLibraryStore(_databasePath);
        var mediaRoot = Path.Combine(_directory, "media");
        var settings = new SystemSettingsStore(_databasePath);
        await settings.SaveAsync(new AppSettings(MaterialStoragePath: mediaRoot));
        var mediaStorage = new MediaStorageService(settings);
        var channelDirectory = Path.Combine(_directory, "temp", "telegram", "20261013", "channel");
        Directory.CreateDirectory(channelDirectory);
        var stagedMedia = Path.Combine(channelDirectory, $"media{extension}");
        File.WriteAllBytes(stagedMedia, [5, 5, 5]);
        var materializer = new TelegramMaterializer(
            new TelegramCollectorService(),
            library,
            (_, _, _) => Task.FromResult(new TelegramDraftResult(channelDirectory, [stagedMedia], 0)),
            mediaStorage,
            (paths, _) =>
            {
                foreach (var path in paths)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                return Task.CompletedTask;
            });
        var message = Message($"https://t.me/channel/4{extension.Length}", "", "");

        var result = await materializer.MaterializeAsync(message, "");

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(stagedMedia));
        var material = Assert.Single(await library.GetMaterialsAsync());
        var storedMedia = Assert.Single(material.MediaLinks);
        Assert.Equal(type, material.Type);
        Assert.Equal(Path.Combine(mediaRoot, "library"), Path.GetDirectoryName(storedMedia));
        Assert.True(File.Exists(storedMedia));
        Assert.Equal(storedMedia, Assert.Single(result.MediaPaths));
    }

    [Fact]
    public async Task Materializer_UsesMediaFallbackOnlyForAutomaticContext()
    {
        var library = new ContentLibraryStore(_databasePath);
        var transport = new MediaFallbackTransport();
        var materializer = new TelegramMaterializer(
            new TelegramCollectorService(),
            library,
            transport: transport);

        var automatic = await materializer.MaterializeAsync(
            MessageWithMedia("https://t.me/channel/100"),
            TelegramTransportContext.Direct() with { AllowFallback = true });
        var explicitSelection = await materializer.MaterializeAsync(
            MessageWithMedia("https://t.me/channel/101"),
            TelegramTransportContext.Direct());

        Assert.True(automatic.Succeeded);
        Assert.True(explicitSelection.Succeeded);
        Assert.Equal(1, transport.FallbackCalls);
        Assert.Equal(1, transport.DirectCalls);
    }

    [Fact]
    public async Task Materializer_DoesNotRunASecondPassToFillMissingMedia()
    {
        var library = new ContentLibraryStore(_databasePath);
        var transport = new PartialMediaFallbackTransport();
        var materializer = new TelegramMaterializer(
            new TelegramCollectorService(),
            library,
            transport: transport);
        var message = new TelegramChannelMessage(
            "https://t.me/channel/102",
            "t.me",
            DateTimeOffset.Now,
            "",
            "多个媒体",
            [
                new TelegramMediaRef("https://example.com/one.jpg", "https://example.com/one.jpg", "image"),
                new TelegramMediaRef("https://example.com/two.jpg", "https://example.com/two.jpg", "image")
            ],
            "");

        var result = await materializer.MaterializeAsync(
            message,
            TelegramTransportContext.Direct() with { AllowFallback = true });

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.FailedMediaCount);
        Assert.Equal(["one", "one", "two"], transport.Attempts);
    }

    private static TelegramChannelMessage Message(
        string url,
        string title,
        string text,
        DateTimeOffset? publishedAt = null) =>
        new(url, "t.me", publishedAt ?? DateTimeOffset.Now, title, text, [], "");

    private static TelegramChannelMessage MessageWithMedia(string url) =>
        new(
            url,
            "t.me",
            DateTimeOffset.Now,
            "",
            "带媒体",
            [new TelegramMediaRef(
                "https://example.com/fallback.jpg",
                "https://example.com/fallback.jpg",
                "image")],
            "");

    private sealed class MediaFallbackTransport : ITelegramTransport, ITelegramMediaFallbackTransport
    {
        public int FallbackCalls { get; private set; }

        public int DirectCalls { get; private set; }

        public Task<TelegramTransportPreview> FetchChannelAsync(
            string channelUrl,
            TelegramTransportSelection selection,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> DownloadMediaAsync(
            TelegramMediaRef media,
            TelegramTransportContext context,
            string? targetDirectory = null,
            CancellationToken cancellationToken = default)
        {
            DirectCalls++;
            return Task.FromResult(media.OriginalUrl);
        }

        public Task<string> DownloadMediaWithFallbackAsync(
            TelegramMediaRef media,
            TelegramTransportContext context,
            string? targetDirectory = null,
            CancellationToken cancellationToken = default)
        {
            FallbackCalls++;
            return Task.FromResult(media.OriginalUrl);
        }
    }

    private sealed class PartialMediaFallbackTransport : ITelegramTransport, ITelegramMediaFallbackTransport
    {
        public List<string> Attempts { get; } = [];

        public Task<TelegramTransportPreview> FetchChannelAsync(
            string channelUrl,
            TelegramTransportSelection selection,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> DownloadMediaAsync(
            TelegramMediaRef media,
            TelegramTransportContext context,
            string? targetDirectory = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> DownloadMediaWithFallbackAsync(
            TelegramMediaRef media,
            TelegramTransportContext context,
            string? targetDirectory = null,
            CancellationToken cancellationToken = default)
        {
            var name = media.OriginalUrl.Contains("one", StringComparison.Ordinal) ? "one" : "two";
            Attempts.Add(name);
            return name == "one"
                ? Task.FromException<string>(new IOException("one failed"))
                : Task.FromResult(media.OriginalUrl);
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}
