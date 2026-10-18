namespace QqChannelDesk.Services;

/// <summary>
/// Places newly collected materials into the automatic publishing queue.
/// Missing or disabled policies are treated as a normal skip, not a collection failure.
/// </summary>
public sealed class PublishAutoScheduleService
{
    private readonly ContentLibraryStore _library;
    private readonly PublishPolicyStore _policies;
    private readonly Func<DateTimeOffset> _now;

    public PublishAutoScheduleService(
        ContentLibraryStore library,
        PublishPolicyStore policies,
        Func<DateTimeOffset>? now = null)
    {
        _library = library;
        _policies = policies;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public async Task<PublishAutoScheduleResult> TryQueueAsync(
        long materialId,
        string guildId,
        string channelId,
        int firstDelayMinutes,
        CancellationToken cancellationToken = default)
    {
        if (materialId <= 0) return PublishAutoScheduleResult.NotScheduled("素材编号无效。");
        if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(channelId))
            return PublishAutoScheduleResult.NotScheduled("素材尚未匹配有效的频道和版块。");

        PublishPolicy? policy;
        try
        {
            policy = await _policies.GetPolicyAsync(guildId, channelId, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return PublishAutoScheduleResult.NotScheduled("素材目标不是有效的频道和版块。");
        }

        if (policy is null)
            return PublishAutoScheduleResult.NotScheduled("该频道和版块尚未配置发布策略。");
        if (!policy.Enabled)
            return PublishAutoScheduleResult.Disabled();

        var delayMinutes = TelegramCollectionStore.NormalizeAutoScheduleDelay(firstDelayMinutes);
        var notBefore = _now().AddMinutes(delayMinutes);
        var intervalMinutes = NextIntervalMinutes(policy.MinIntervalMinutes, policy.MaxIntervalMinutes);
        return await _library.TryQueueAutomaticMaterialAsync(
            materialId,
            notBefore,
            policy,
            TimeSpan.FromMinutes(intervalMinutes),
            cancellationToken).ConfigureAwait(false);
    }

    private static int NextIntervalMinutes(int minMinutes, int maxMinutes)
    {
        var min = Math.Clamp(minMinutes, 1, 7 * 24 * 60);
        var max = Math.Clamp(maxMinutes, min, 7 * 24 * 60);
        return max == min ? min : Random.Shared.Next(min, max + 1);
    }
}
