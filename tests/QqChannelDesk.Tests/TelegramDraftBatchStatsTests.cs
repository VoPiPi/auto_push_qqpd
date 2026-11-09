using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class TelegramDraftBatchStatsTests
{
    [Fact]
    public void SummarySeparatesCompleteFailuresFromStoredPartialMedia()
    {
        var stats = new TelegramDraftBatchStats();
        stats.Record(Result(succeeded: true, failedMedia: 0));
        stats.Record(Result(succeeded: true, failedMedia: 2, materialId: 2));
        stats.Record(Result(succeeded: false, failedMedia: 5, materialId: null));

        Assert.Equal(1, stats.FullySucceededCount);
        Assert.Equal(1, stats.PartialSucceededCount);
        Assert.Equal(1, stats.FailedCount);
        Assert.Equal(2, stats.PartialMissingMediaCount);
        Assert.Contains("成功 2 条，失败 1 条", stats.ToSummary(), StringComparison.Ordinal);
        Assert.Contains("1 条素材已入库，但有 2 个媒体缺失", stats.ToSummary(), StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteMediaFailureDoesNotCountAsStoredPartialMedia()
    {
        var stats = new TelegramDraftBatchStats();
        stats.Record(Result(succeeded: false, failedMedia: 4, materialId: null));
        stats.RecordFailure();

        Assert.Equal(2, stats.FailedCount);
        Assert.Equal(0, stats.SucceededCount);
        Assert.Equal(0, stats.PartialMissingMediaCount);
        Assert.DoesNotContain("媒体缺失", stats.ToSummary(), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateMaterializationIsNotCountedAsNewSuccess()
    {
        var stats = new TelegramDraftBatchStats();
        stats.Record(new TelegramMaterializationResult(
            3, "", [], 0, false, "已在素材库", true, SkippedDuplicate: true));

        Assert.Equal(1, stats.SkippedDuplicateCount);
        Assert.Equal(0, stats.SucceededCount);
        Assert.Equal(0, stats.FailedCount);
    }

    private static TelegramMaterializationResult Result(
        bool succeeded,
        int failedMedia,
        long? materialId = 1) =>
        new(materialId, "", [], failedMedia, false, "", succeeded);
}
