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
        Assert.Equal(500, saved.MessageLimit);
        Assert.Equal("keyword", saved.Keyword);
        Assert.Equal("每天一次", saved.FrequencyDisplay);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(draft));

        context.Set(CurrentAccountIdentity.Create("global-b", "account-b"));
        Assert.Empty(await store.GetAsync());
        await store.SaveAsync(draft);
        Assert.Single(await store.GetAsync());
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
    public void CronSchedule_ComputesNextOccurrenceAndTreatsZeroAndSevenAsSunday()
    {
        var monday = new DateTime(2026, 10, 12, 8, 30, 0, DateTimeKind.Unspecified);
        var weekday = TelegramCronSchedule.Parse("*/15 9-10 * * 1-5");
        Assert.Equal(new DateTime(2026, 10, 12, 9, 0, 0), weekday.GetNextOccurrence(monday));

        var sundayFromZero = TelegramCronSchedule.Parse("0 9 * * 0");
        var sundayFromSeven = TelegramCronSchedule.Parse("0 9 * * 7");
        var saturday = new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(new DateTime(2026, 10, 18, 9, 0, 0), sundayFromZero.GetNextOccurrence(saturday));
        Assert.Equal(new DateTime(2026, 10, 18, 9, 0, 0), sundayFromSeven.GetNextOccurrence(saturday));
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

    private static TelegramCollectionSchedule Schedule(string type, int frequencyValue = 1, int weekday = 1, string triggerTime = "00:00") =>
        new(1, "https://t.me/s/channel", "channel", 24, true, "auto", 50, "", null, null, "从未采集", "", 0,
            DateTimeOffset.Now, DateTimeOffset.Now, type, frequencyValue, weekday, triggerTime);

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
    public async Task Materializer_DoesNotSaveMaterialWhenAnyMediaDownloadFails()
    {
        var library = new ContentLibraryStore(_databasePath);
        var message = Message("https://t.me/channel/44", "", "需要保留正文,但媒体必须完整");
        var materializer = new TelegramMaterializer(new TelegramCollectorService(), library, (_, _, _) =>
            Task.FromResult(new TelegramDraftResult(
                Path.Combine(_directory, "temp"),
                [Path.Combine(_directory, "temp", "image.jpg")],
                1)));

        var result = await materializer.MaterializeAsync(message, "");

        Assert.False(result.Succeeded);
        Assert.False(result.SkippedEmpty);
        Assert.Null(result.MaterialId);
        Assert.Equal(1, result.FailedMediaCount);
        Assert.Contains("未创建素材", result.Error);
        Assert.Empty(await library.GetMaterialsAsync());
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
        var stagedVideo = Path.Combine(channelDirectory, "video.mp4");
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
        var message = Message("https://t.me/channel/45", "", "正文");

        var result = await materializer.MaterializeAsync(message, "");

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.FailedMediaCount);
        Assert.Equal([stagedVideo], cleaned);
        Assert.Empty(await library.GetMaterialsAsync());
    }
    private static TelegramChannelMessage Message(string url, string title, string text) =>
        new(url, "t.me", DateTimeOffset.Now, title, text, [], "");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}
