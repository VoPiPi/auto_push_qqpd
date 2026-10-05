namespace QqChannelDesk.Services;

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

    public AppDatabaseInitializer(
        SystemSettingsStore settings,
        ChannelCacheStore channels,
        ContentLibraryStore contentLibrary,
        PublishHistoryStore history)
    {
        _settings = settings;
        _channels = channels;
        _contentLibrary = contentLibrary;
        _history = history;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Keep initialization sequential because all stores share one SQLite
        // file and the channel store may perform a legacy schema migration.
        await _settings.EnsureDefaultsAsync(cancellationToken);
        await _channels.InitializeAsync(cancellationToken);
        await _contentLibrary.InitializeAsync(cancellationToken);
        await _history.InitializeAsync(cancellationToken);
    }
}
