namespace QqChannelDesk.Services;

/// <summary>
/// Aggregates one batch materialization without treating messages that failed
/// completely as "partially stored" media failures.
/// </summary>
public sealed class TelegramDraftBatchStats
{
    public int FullySucceededCount { get; private set; }

    public int FailedCount { get; private set; }

    public int PartialSucceededCount { get; private set; }

    public int PartialMissingMediaCount { get; private set; }

    public int SkippedDuplicateCount { get; private set; }

    public int SucceededCount => FullySucceededCount + PartialSucceededCount;

    public void Record(TelegramMaterializationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.SkippedDuplicate)
        {
            SkippedDuplicateCount++;
            return;
        }

        if (!result.Succeeded || result.MaterialId is null)
        {
            FailedCount++;
            return;
        }

        if (result.FailedMediaCount > 0)
        {
            PartialSucceededCount++;
            PartialMissingMediaCount += result.FailedMediaCount;
            return;
        }

        FullySucceededCount++;
    }

    public void RecordFailure() => FailedCount++;

    public string ToSummary()
    {
        var summary = $"加入素材库完成：成功 {SucceededCount} 条，失败 {FailedCount} 条";
        if (PartialSucceededCount > 0)
        {
            summary += $"；其中 {PartialSucceededCount} 条素材已入库，但有 {PartialMissingMediaCount} 个媒体缺失";
        }
        return summary + "。可在“素材库”查看。";
    }
}
