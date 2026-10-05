using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class PublishHistoryFilterTests
{
    [Fact]
    public void Apply_WithEmptyCriteria_ReturnsAllRecords()
    {
        var records = new[]
        {
            CreateRecord(1, new DateTime(2026, 10, 1, 9, 0, 0)),
            CreateRecord(2, new DateTime(2026, 10, 2, 10, 0, 0))
        };

        var filtered = PublishHistoryFilter.Apply(records, new PublishHistoryFilterCriteria());

        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public void Apply_DateRange_IsInclusiveByLocalCalendarDay()
    {
        var records = new[]
        {
            CreateRecord(1, new DateTime(2026, 10, 1, 23, 59, 59)),
            CreateRecord(2, new DateTime(2026, 10, 2, 0, 0, 0)),
            CreateRecord(3, new DateTime(2026, 10, 2, 23, 59, 59)),
            CreateRecord(4, new DateTime(2026, 10, 3, 0, 0, 0))
        };
        var day = new DateTime(2026, 10, 2);

        var filtered = PublishHistoryFilter.Apply(
            records,
            new PublishHistoryFilterCriteria(StartDate: day, EndDate: day));

        Assert.Equal(new long[] { 2, 3 }, filtered.Select(record => record.Id));
    }

    [Fact]
    public void Apply_Type_UsesExactType()
    {
        var records = new[]
        {
            CreateRecord(1, new DateTime(2026, 10, 2), FeedType.Image, "频道 A", "News Room"),
            CreateRecord(2, new DateTime(2026, 10, 2), FeedType.Text, "频道 A", "News Room"),
            CreateRecord(3, new DateTime(2026, 10, 2), FeedType.Image, "频道 B", "公告")
        };

        var filtered = PublishHistoryFilter.Apply(
            records,
            new PublishHistoryFilterCriteria(FeedType: FeedType.Image));

        Assert.Equal(new long[] { 1, 3 }, filtered.Select(record => record.Id));
    }

    [Fact]
    public void Apply_Keyword_SearchesTitleAndSummaryCaseInsensitively()
    {
        var records = new[]
        {
            CreateRecord(1, new DateTime(2026, 10, 2), title: "Release Notes", summary: "完成"),
            CreateRecord(2, new DateTime(2026, 10, 2), title: "其他", summary: "RELEASE completed"),
            CreateRecord(3, new DateTime(2026, 10, 2), title: "其他", summary: "未命中")
        };

        var filtered = PublishHistoryFilter.Apply(
            records,
            new PublishHistoryFilterCriteria(KeywordQuery: "release"));

        Assert.Equal(new long[] { 1, 2 }, filtered.Select(record => record.Id));
    }

    [Fact]
    public void Apply_CombinedCriteria_UsesAndSemantics()
    {
        var records = new[]
        {
            CreateRecord(1, new DateTime(2026, 10, 2), FeedType.Image, "频道 A", "公告", "周报", "已发布"),
            CreateRecord(2, new DateTime(2026, 10, 2), FeedType.Video, "频道 A", "公告", "周报", "已发布"),
            CreateRecord(3, new DateTime(2026, 10, 3), FeedType.Image, "频道 A", "公告", "周报", "已发布")
        };

        var filtered = PublishHistoryFilter.Apply(
            records,
            new PublishHistoryFilterCriteria(
                StartDate: new DateTime(2026, 10, 2),
                EndDate: new DateTime(2026, 10, 2),
                FeedType: FeedType.Image,
                KeywordQuery: "周报"));

        Assert.Equal(1L, Assert.Single(filtered).Id);
    }

    [Fact]
    public void Apply_NoMatches_AndInvalidDateRange_ReturnEmpty()
    {
        var records = new[] { CreateRecord(1, new DateTime(2026, 10, 2)) };

        Assert.Empty(PublishHistoryFilter.Apply(
            records,
            new PublishHistoryFilterCriteria(KeywordQuery: "不存在")));
        Assert.Empty(PublishHistoryFilter.Apply(
            records,
            new PublishHistoryFilterCriteria(
                StartDate: new DateTime(2026, 10, 3),
                EndDate: new DateTime(2026, 10, 2))));
    }

    private static PublishRecord CreateRecord(
        long id,
        DateTime startedAt,
        FeedType feedType = FeedType.Text,
        string guildName = "测试频道",
        string channelName = "测试版块",
        string title = "标题",
        string summary = "结果")
    {
        var started = new DateTimeOffset(DateTime.SpecifyKind(startedAt, DateTimeKind.Local));
        return new PublishRecord(
            id,
            started,
            null,
            feedType,
            "1",
            guildName,
            "2",
            channelName,
            title,
            [],
            PublishRecordStatus.Succeeded,
            "",
            summary,
            null,
            null);
    }
}
