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
        Assert.Equal(1, defaults.MaxScheduleConcurrency);
        Assert.False(defaults.StartWithWindows);
        Assert.Equal(CloseWindowBehavior.ExitApplication, defaults.CloseWindowBehavior);
        Assert.Equal(MaterialRetentionPolicy.DeleteAfterSuccessfulPublish, defaults.MaterialRetentionPolicy);
        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "temp")), defaults.EffectiveMaterialStoragePath);

        await store.SaveAsync(new AppSettings(true, true, AutoExecuteSchedules: true, MaxScheduleConcurrency: 3,
            StartWithWindows: true, CloseWindowBehavior: CloseWindowBehavior.MinimizeToTray));

        var persisted = await new SystemSettingsStore(_databasePath).GetAsync();
        Assert.True(persisted.NotifyUpgrade);
        Assert.True(persisted.RunTasksOnStartup);
        Assert.True(persisted.AutoExecuteSchedules);
        Assert.Equal(3, persisted.MaxScheduleConcurrency);
        Assert.True(persisted.StartWithWindows);
        Assert.Equal(CloseWindowBehavior.MinimizeToTray, persisted.CloseWindowBehavior);
        Assert.Equal(MaterialRetentionPolicy.DeleteAfterSuccessfulPublish, persisted.MaterialRetentionPolicy);
    }

    [Fact]
    public async Task InvalidConcurrencyFallsBackToOne()
    {
        var store = new SystemSettingsStore(_databasePath);
        await store.SaveAsync(new AppSettings(MaxScheduleConcurrency: 9));
        Assert.Equal(1, (await new SystemSettingsStore(_databasePath).GetAsync()).MaxScheduleConcurrency);
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
    public void MachineCodeIsStableAndDoesNotExposeRawMachineGuid()
    {
        var first = MachineCodeProvider.GetMachineCode();
        var second = MachineCodeProvider.GetMachineCode();

        Assert.Equal(first, second);
        Assert.Matches("^QCD-[A-F0-9]{16}$", first);
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, false);
    }
}
