namespace QqChannelDesk.Services;

public sealed class ChannelSyncService
{
    private readonly CliWorkflow _workflow;
    private readonly ChannelCacheStore _store;
    private readonly AppLogger _logger;

    public ChannelSyncService(CliWorkflow workflow, ChannelCacheStore store, AppLogger? logger = null)
    {
        _workflow = workflow;
        _store = store;
        _logger = logger ?? AppLogger.Instance;
    }

    public async Task<ChannelSyncResult> SynchronizeAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report("正在读取频道列表…");
            var guilds = await _workflow.GetGuildsAsync(cancellationToken);
            var snapshot = new List<CachedGuild>(guilds.Count);
            for (var index = 0; index < guilds.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var guild = guilds[index];
                //progress?.Report($"正在读取版块：{guild.Name}（{index + 1}/{guilds.Count}）…");
                var channels = await _workflow.GetChannelsAsync(guild.Id, cancellationToken);
                snapshot.Add(new CachedGuild(guild.Id, guild.Name, guild.Role, channels));
            }

            await _store.ReplaceSnapshotAsync(snapshot, cancellationToken);
            var channelCount = snapshot.Sum(item => item.Channels.Count);
            _logger.Info($"频道数据同步成功：{snapshot.Count} 个频道、{channelCount} 个版块。");
            progress?.Report($"同步完成：{snapshot.Count} 个频道、{channelCount} 个版块。");
            return new ChannelSyncResult(true, snapshot.Count, channelCount, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            const string message = "频道数据同步已取消；发布已禁用，旧缓存保留。";
            await TryMarkFailureAsync(message);
            _logger.Warning(message);
            progress?.Report(message);
            return new ChannelSyncResult(false, 0, 0, message);
        }
        catch (Exception ex)
        {
            var message = CliDiagnostics.Sanitize(ex.Message);
            await TryMarkFailureAsync(message);
            _logger.Error($"频道数据同步失败：{message}；旧缓存已保留，发布已禁用。");
            progress?.Report($"频道数据同步失败：{message}；发布已禁用。");
            return new ChannelSyncResult(false, 0, 0, message);
        }
    }

    public async Task<IReadOnlyList<ChannelChoice>> GetCachedGuildsAsync(CancellationToken cancellationToken = default) =>
        await _store.GetGuildsAsync(cancellationToken);

    public async Task<IReadOnlyList<ChannelChoice>> GetCachedChannelsAsync(string guildId, CancellationToken cancellationToken = default) =>
        await _store.GetChannelsAsync(guildId, cancellationToken);

    public async Task<ChannelSyncState> GetStateAsync(CancellationToken cancellationToken = default) =>
        await _store.GetSyncStateAsync(cancellationToken);

    private async Task TryMarkFailureAsync(string message)
    {
        try { await _store.MarkSyncFailedAsync(message); }
        catch (Exception storageException) { _logger.Error($"无法保存频道同步失败状态：{CliDiagnostics.Sanitize(storageException.Message)}"); }
    }
}

public sealed record ChannelSyncResult(bool Succeeded, int GuildCount, int ChannelCount, string? Error);
