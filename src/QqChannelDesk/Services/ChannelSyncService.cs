namespace QqChannelDesk.Services;

public sealed class ChannelSyncService
{
    public static readonly TimeSpan AutomaticRefreshInterval = TimeSpan.FromHours(6);
    private readonly CliWorkflow _workflow;
    private readonly ChannelCacheStore _store;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

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
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            return await SynchronizeCoreAsync(progress, cancellationToken);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<ChannelSyncResult> EnsureFreshAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(cancellationToken);
        if (!_store.CanSynchronize)
            return new ChannelSyncResult(false, 0, 0, state.Error ?? "尚未确认登录账号，暂无频道同步状态", true);

        if (!IsRefreshDue(state.LastAttemptedAt ?? state.SyncedAt, DateTimeOffset.Now))
            return new ChannelSyncResult(state.Succeeded, 0, 0, state.Error, true);

        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            // Another page may have refreshed while this call was waiting.
            state = await GetStateAsync(cancellationToken);
            if (!IsRefreshDue(state.LastAttemptedAt ?? state.SyncedAt, DateTimeOffset.Now))
                return new ChannelSyncResult(state.Succeeded, 0, 0, state.Error, true);
            return await SynchronizeCoreAsync(progress, cancellationToken);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    internal static bool IsRefreshDue(DateTimeOffset? lastAttemptedAt, DateTimeOffset now) =>
        lastAttemptedAt is null || now - lastAttemptedAt.Value > AutomaticRefreshInterval;

    private async Task<ChannelSyncResult> SynchronizeCoreAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await _store.MarkSyncAttemptedAsync(cancellationToken);
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
            const string message = "同步已取消；旧频道数据保留，发布已暂停。请稍后刷新频道。";
            await TryMarkFailureAsync(message);
            _logger.Warning(message);
            progress?.Report(message);
            return new ChannelSyncResult(false, 0, 0, message);
        }
        catch (Exception ex)
        {
            var message = CliDiagnostics.Sanitize(ex.Message);
            await TryMarkFailureAsync(message);
            _logger.Error($"频道数据同步失败：{message}；旧数据保留，等待手动刷新或自动重试。");
            progress?.Report($"频道同步失败，旧频道数据已保留，发布已暂停。请刷新频道后重试。{message}");
            return new ChannelSyncResult(false, 0, 0, message);
        }
    }

    public async Task<IReadOnlyList<ChannelChoice>> GetCachedGuildsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureFreshAsync(cancellationToken: cancellationToken);
        return await _store.GetGuildsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChannelChoice>> GetCachedChannelsAsync(string guildId, CancellationToken cancellationToken = default)
    {
        await EnsureFreshAsync(cancellationToken: cancellationToken);
        return await _store.GetChannelsAsync(guildId, cancellationToken);
    }

    public async Task<ChannelSyncState> GetStateAsync(CancellationToken cancellationToken = default) =>
        await _store.GetSyncStateAsync(cancellationToken);

    private async Task TryMarkFailureAsync(string message)
    {
        try { await _store.MarkSyncFailedAsync(message); }
        catch (Exception storageException) { _logger.Error($"无法保存频道同步失败状态：{CliDiagnostics.Sanitize(storageException.Message)}"); }
    }
}

public sealed record ChannelSyncResult(bool Succeeded, int GuildCount, int ChannelCount, string? Error, bool Skipped = false);
