namespace QqChannelDesk.Services;

using QqChannelDesk.Services.Collection;

/// <summary>
/// Initializes the single application database before the main window starts
/// using any feature that depends on persisted data.
/// </summary>
public sealed class AppDatabaseInitializer
{
    private readonly SystemSettingsStore _settings;
    private readonly ChannelCacheStore _channels;
    private readonly ContentLibraryStore _contentLibrary;
    private readonly PublishHistoryStore _history;
    private readonly ProxyPoolStore? _proxyPool;
    private readonly TelegramCollectionStore? _telegramCollections;
    private readonly PublishPolicyStore? _publishPolicies;
    private readonly CollectionSourceStore? _collectionSources;
    private readonly CollectionRunStore? _collectionRuns;
    private readonly CollectionAiUsageStore? _collectionAiUsage;

    public AppDatabaseInitializer(
        SystemSettingsStore settings,
        ChannelCacheStore channels,
        ContentLibraryStore contentLibrary,
        PublishHistoryStore history,
        ProxyPoolStore? proxyPool = null,
        TelegramCollectionStore? telegramCollections = null,
        PublishPolicyStore? publishPolicies = null,
        CollectionSourceStore? collectionSources = null,
        CollectionRunStore? collectionRuns = null,
        CollectionAiUsageStore? collectionAiUsage = null)
    {
        _settings = settings;
        _channels = channels;
        _contentLibrary = contentLibrary;
        _history = history;
        _proxyPool = proxyPool;
        _telegramCollections = telegramCollections;
        _publishPolicies = publishPolicies;
        _collectionSources = collectionSources;
        _collectionRuns = collectionRuns;
        _collectionAiUsage = collectionAiUsage;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Keep initialization sequential because all stores share one SQLite
        // file. The channel store must migrate the legacy channel schema before
        // any other store creates tables in the same database.
        await _channels.InitializeAsync(cancellationToken);
        await _settings.EnsureDefaultsAsync(cancellationToken);
        await _contentLibrary.InitializeAsync(cancellationToken);
        await _history.InitializeAsync(cancellationToken);
        if (_proxyPool is not null) await _proxyPool.InitializeAsync(cancellationToken);
        if (_telegramCollections is not null) await _telegramCollections.InitializeAsync(cancellationToken);
        if (_publishPolicies is not null) await _publishPolicies.InitializeAsync(cancellationToken);
        if (_collectionSources is not null) await _collectionSources.InitializeAsync(cancellationToken);
        if (_collectionRuns is not null) await _collectionRuns.InitializeAsync(cancellationToken);
        if (_collectionAiUsage is not null) await _collectionAiUsage.InitializeAsync(cancellationToken);
    }
}
