using Microsoft.Data.Sqlite;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class SystemSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QqChannelDeskSettingsTests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public SystemSettingsTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "channels.db");
    }

    [Fact]
    public async Task DefaultsAreCreatedAndValuesPersist()
    {
        var store = new SystemSettingsStore(_databasePath);

        var defaults = await store.GetAsync();
        Assert.False(defaults.NotifyUpgrade);
        Assert.False(defaults.RunTasksOnStartup);
        Assert.True(defaults.AutoExecuteSchedules);
        Assert.True(defaults.TelegramCollectionSchedulerEnabled);
        Assert.True(defaults.NotifyTelegramCollectionFailure);
        Assert.Equal(1, defaults.TelegramCollectionConcurrency);
        Assert.Equal(1, defaults.MaxScheduleConcurrency);
        Assert.False(defaults.StartWithWindows);
        Assert.Equal(CloseWindowBehavior.ExitApplication, defaults.CloseWindowBehavior);
        Assert.Equal(MaterialRetentionPolicy.DeleteAfterSuccessfulPublish, defaults.MaterialRetentionPolicy);
        Assert.Null(defaults.FfmpegPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "temp")), defaults.EffectiveMaterialStoragePath);

        await store.SaveAsync(new AppSettings(true, true, AutoExecuteSchedules: true,
            TelegramCollectionSchedulerEnabled: false, NotifyTelegramCollectionFailure: false,
            TelegramCollectionConcurrency: 2, MaxScheduleConcurrency: 3,
            StartWithWindows: true, CloseWindowBehavior: CloseWindowBehavior.MinimizeToTray));

        var persisted = await new SystemSettingsStore(_databasePath).GetAsync();
        Assert.True(persisted.NotifyUpgrade);
        Assert.True(persisted.RunTasksOnStartup);
        Assert.True(persisted.AutoExecuteSchedules);
        Assert.False(persisted.TelegramCollectionSchedulerEnabled);
        Assert.False(persisted.NotifyTelegramCollectionFailure);
        Assert.Equal(2, persisted.TelegramCollectionConcurrency);
        Assert.Equal(3, persisted.MaxScheduleConcurrency);
        Assert.True(persisted.StartWithWindows);
        Assert.Equal(CloseWindowBehavior.MinimizeToTray, persisted.CloseWindowBehavior);
        Assert.Equal(MaterialRetentionPolicy.DeleteAfterSuccessfulPublish, persisted.MaterialRetentionPolicy);
    }

    [Fact]
    public async Task InvalidConcurrencyFallsBackToOne()
    {
        var store = new SystemSettingsStore(_databasePath);
        await store.SaveAsync(new AppSettings(
            MaxScheduleConcurrency: 9,
            TelegramCollectionConcurrency: 9));
        var persisted = await new SystemSettingsStore(_databasePath).GetAsync();
        Assert.Equal(1, persisted.MaxScheduleConcurrency);
        Assert.Equal(1, persisted.TelegramCollectionConcurrency);
    }

    [Fact]
    public async Task InvalidCloseWindowBehaviorFallsBackToExit()
    {
        var store = new SystemSettingsStore(_databasePath);
        await store.SaveAsync(new AppSettings(CloseWindowBehavior: (CloseWindowBehavior)999));

        Assert.Equal(CloseWindowBehavior.ExitApplication,
            (await new SystemSettingsStore(_databasePath).GetAsync()).CloseWindowBehavior);
    }

    [Theory]
    [InlineData("C:\\Apps\\QqChannelDesk\\QqChannelDesk.exe", "\"C:\\Apps\\QqChannelDesk\\QqChannelDesk.exe\"")]
    [InlineData("C:\\Program Files\\Qq Channel Desk\\QqChannelDesk.exe", "\"C:\\Program Files\\Qq Channel Desk\\QqChannelDesk.exe\"")]
    public void StartupCommandQuotesExecutablePath(string path, string expected)
    {
        Assert.Equal(expected, StartupManager.BuildRunCommand(path));
        Assert.DoesNotContain("--", StartupManager.BuildRunCommand(path));
        Assert.DoesNotContain("token", StartupManager.BuildRunCommand(path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", StartupManager.BuildRunCommand(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MaterialStoragePathAndRetentionPolicyPersist()
    {
        var storagePath = Path.Combine(_directory, "media-root");
        var store = new SystemSettingsStore(_databasePath);

        await store.SaveAsync(new AppSettings(
            MaterialStoragePath: storagePath,
            MaterialRetentionPolicy: MaterialRetentionPolicy.KeepSevenDays));

        var persisted = await new SystemSettingsStore(_databasePath).GetAsync();
        Assert.Equal(Path.GetFullPath(storagePath), persisted.EffectiveMaterialStoragePath);
        Assert.Equal(MaterialRetentionPolicy.KeepSevenDays, persisted.MaterialRetentionPolicy);

        File.Delete(_databasePath);
        Assert.False(File.Exists(_databasePath));
    }

    [Fact]
    public async Task FfmpegPathPersistsAsNormalizedAbsolutePath()
    {
        var relativePath = Path.Combine("ffmpeg", "ffmpeg.exe");
        var store = new SystemSettingsStore(_databasePath);

        await store.SaveAsync(new AppSettings(FfmpegPath: relativePath));

        var persisted = await new SystemSettingsStore(_databasePath).GetAsync();
        Assert.Equal(Path.GetFullPath(relativePath), persisted.FfmpegPath);

        await store.SaveAsync(new AppSettings());
        Assert.Null((await new SystemSettingsStore(_databasePath).GetAsync()).FfmpegPath);
    }

    [Fact]
    public async Task LegacySettingsTableRemainsCompatibleAndUpdatePreferencesDefaultSafely()
    {
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE AppSettings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                INSERT INTO AppSettings (Key, Value) VALUES ('notify_upgrade', 'true');
                INSERT INTO AppSettings (Key, Value) VALUES ('max_schedule_concurrency', '3');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new SystemSettingsStore(_databasePath);
        var settings = await store.GetAsync();
        var preferences = await store.GetUpdatePreferencesAsync();

        Assert.True(settings.NotifyUpgrade);
        Assert.Equal(3, settings.MaxScheduleConcurrency);
        Assert.True(settings.NotifyTelegramCollectionFailure);
        Assert.Equal(1, settings.TelegramCollectionConcurrency);
        Assert.Null(preferences.LastCheckedAt);
        Assert.Null(preferences.SkippedVersionTag);
    }

    [Fact]
    public async Task UpdatePreferencesPersistWithoutOverwritingOtherSettings()
    {
        var checkedAt = new DateTimeOffset(2026, 10, 20, 8, 30, 45, TimeSpan.FromHours(8));
        var store = new SystemSettingsStore(_databasePath);
        await store.SaveAsync(new AppSettings(
            NotifyUpgrade: true,
            AutoExecuteSchedules: false,
            MaxScheduleConcurrency: 2));

        await store.SaveLastUpdateCheckAsync(checkedAt);
        await store.SaveSkippedUpdateVersionAsync(" v1.1.0 ");

        var preferences = await new SystemSettingsStore(_databasePath).GetUpdatePreferencesAsync();
        Assert.Equal(checkedAt.ToUniversalTime(), preferences.LastCheckedAt);
        Assert.Equal("v1.1.0", preferences.SkippedVersionTag);

        await store.SaveAsync(new AppSettings(
            NotifyUpgrade: false,
            RunTasksOnStartup: true,
            AutoExecuteSchedules: true,
            MaxScheduleConcurrency: 3));

        var persistedSettings = await new SystemSettingsStore(_databasePath).GetAsync();
        var persistedPreferences = await new SystemSettingsStore(_databasePath).GetUpdatePreferencesAsync();
        Assert.False(persistedSettings.NotifyUpgrade);
        Assert.True(persistedSettings.RunTasksOnStartup);
        Assert.True(persistedSettings.AutoExecuteSchedules);
        Assert.Equal(3, persistedSettings.MaxScheduleConcurrency);
        Assert.Equal(checkedAt.ToUniversalTime(), persistedPreferences.LastCheckedAt);
        Assert.Equal("v1.1.0", persistedPreferences.SkippedVersionTag);

        await store.SaveSkippedUpdateVersionAsync(null);
        Assert.Null((await store.GetUpdatePreferencesAsync()).SkippedVersionTag);
    }

    [Fact]
    public async Task InvalidUpdatePreferenceValuesFallBackSafely()
    {
        var store = new SystemSettingsStore(_databasePath);
        await store.GetAsync();

        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE AppSettings SET Value = 'not-a-date' WHERE Key = 'last_update_check_at';
                UPDATE AppSettings SET Value = '   ' WHERE Key = 'skipped_update_version';
                """;
            await command.ExecuteNonQueryAsync();
        }

        var preferences = await new SystemSettingsStore(_databasePath).GetUpdatePreferencesAsync();
        Assert.Null(preferences.LastCheckedAt);
        Assert.Null(preferences.SkippedVersionTag);
    }

    [Fact]
    public void MachineCodeIsStableAndDoesNotExposeRawMachineGuid()
    {
        var first = MachineCodeProvider.GetMachineCode();
        var second = MachineCodeProvider.GetMachineCode();

        Assert.Equal(first, second);
        Assert.Matches("^QCD-[A-F0-9]{16}$", first);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        var writeAheadLogPath = _databasePath + "-wal";
        if (File.Exists(writeAheadLogPath)) File.Delete(writeAheadLogPath);
        var sharedMemoryPath = _databasePath + "-shm";
        if (File.Exists(sharedMemoryPath)) File.Delete(sharedMemoryPath);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, false);
    }
}
