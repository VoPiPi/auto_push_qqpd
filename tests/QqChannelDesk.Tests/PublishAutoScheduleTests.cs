using Microsoft.Data.Sqlite;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class PublishAutoScheduleTests : IDisposable
{
    private const string GuildId = "10001";
    private const string ChannelId = "20001";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "QqChannelDeskAutoScheduleTests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly AccountContext _accountContext = new();

    public PublishAutoScheduleTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "channels.db");
        _accountContext.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
    }

    [Fact]
    public async Task Initialize_MigratesLegacyScheduleColumnsAndCanRunRepeatedly()
    {
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Materials (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL DEFAULT '',
                    Type TEXT NOT NULL DEFAULT 'text' CHECK (Type IN ('text','image','video')),
                    Content TEXT NOT NULL DEFAULT '',
                    GuildId TEXT NOT NULL DEFAULT '',
                    GuildName TEXT NOT NULL DEFAULT '',
                    ChannelId TEXT NOT NULL DEFAULT '',
                    ChannelName TEXT NOT NULL DEFAULT '',
                    PublishAt TEXT NULL,
                    Status TEXT NOT NULL DEFAULT 'waitsend' CHECK (Status IN ('waitsend','queue','published','delete')),
                    Link TEXT NOT NULL DEFAULT '',
                    MediaJson TEXT NOT NULL DEFAULT '[]',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE TABLE ScheduleExecutions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    MaterialId INTEGER NOT NULL,
                    ScheduledAt TEXT NOT NULL,
                    Status TEXT NOT NULL CHECK (Status IN ('Pending','Running','Succeeded','Failed','NeedsVerification','Cancelled')),
                    StartedAt TEXT NULL,
                    CompletedAt TEXT NULL,
                    LastError TEXT NOT NULL DEFAULT '',
                    PublishRecordId INTEGER NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    UNIQUE (MaterialId, ScheduledAt),
                    FOREIGN KEY (MaterialId) REFERENCES Materials(Id) ON DELETE CASCADE
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var library = new ContentLibraryStore(_databasePath);
        await library.InitializeAsync();
        await library.InitializeAsync();

        var columns = await ReadColumnNamesAsync(_databasePath, "ScheduleExecutions");
        Assert.Contains("AccountKey", columns);
        Assert.Contains("SchedulingMode", columns);
        Assert.Contains("RateLimitAttempts", columns);
        Assert.Contains("IsPaused", columns);

        var tables = await ReadTableNamesAsync(_databasePath);
        Assert.Contains("PublishPolicies", tables);
        Assert.Contains("PublishGuildBudgets", tables);
    }

    [Fact]
    public async Task PausedExecutionKeepsMaterialQueuedAndCannotBeClaimedUntilResumed()
    {
        var now = StableFutureNoon();
        var library = new ContentLibraryStore(_databasePath, _accountContext);
        var scheduledAt = now.AddHours(2);
        var materialId = await SaveScheduledMaterialAsync(library, scheduledAt, "暂停计划");
        var pending = Assert.Single(await library.GetScheduleExecutionsAsync());

        Assert.False(pending.IsPaused);
        var paused = await library.SetScheduleExecutionsPausedAsync([pending.Id], true);

        Assert.Equal([pending.Id], paused.AffectedIds);
        var pausedExecution = Assert.Single(await library.GetScheduleExecutionsAsync());
        Assert.True(pausedExecution.IsPaused);
        var material = await library.GetMaterialAsync(materialId);
        Assert.Equal("queue", material!.Status);
        Assert.Equal(scheduledAt.ToUniversalTime(), material.PublishAt!.Value.ToUniversalTime());
        Assert.Null(await library.TryClaimDueExecutionAsync(scheduledAt.AddSeconds(1)));

        var resumed = await library.SetScheduleExecutionsPausedAsync([pending.Id], false);

        Assert.Equal([pending.Id], resumed.AffectedIds);
        var claimed = await library.TryClaimDueExecutionAsync(scheduledAt.AddSeconds(2));
        Assert.NotNull(claimed);
        Assert.Equal(pending.Id, claimed!.Id);
    }

    [Fact]
    public async Task DeletingPendingExecutionRestoresMaterialAndDoesNotDeleteMaterial()
    {
        var library = new ContentLibraryStore(_databasePath, _accountContext);
        var scheduledAt = StableFutureNoon().AddHours(2);
        var materialId = await SaveScheduledMaterialAsync(library, scheduledAt, "删除计划");
        var pending = Assert.Single(await library.GetScheduleExecutionsAsync());

        var deleted = await library.DeleteScheduleExecutionsAsync([pending.Id]);

        Assert.Equal([pending.Id], deleted.AffectedIds);
        Assert.Empty(await library.GetScheduleExecutionsAsync());
        var material = await library.GetMaterialAsync(materialId);
        Assert.NotNull(material);
        Assert.Equal("waitsend", material!.Status);
        Assert.Null(material.PublishAt);
    }

    [Fact]
    public async Task RunningExecutionCannotBeDeleted()
    {
        var library = new ContentLibraryStore(_databasePath, _accountContext);
        var scheduledAt = DateTimeOffset.Now.AddHours(1);
        var materialId = await SaveScheduledMaterialAsync(library, scheduledAt, "运行中计划");
        var claimed = await library.TryClaimDueExecutionAsync(scheduledAt.AddSeconds(1));
        Assert.NotNull(claimed);

        var deleted = await library.DeleteScheduleExecutionsAsync([claimed!.Id]);

        Assert.Empty(deleted.AffectedIds);
        Assert.Equal([claimed.Id], deleted.RunningIds);
        Assert.Equal(ScheduleExecutionStatus.Running, Assert.Single(await library.GetScheduleExecutionsAsync()).Status);
        Assert.Equal("queue", (await library.GetMaterialAsync(materialId))!.Status);
    }

    [Fact]
    public async Task DashboardDoesNotCountPausedExecutionsAsScheduledOrOverdue()
    {
        var library = new ContentLibraryStore(_databasePath, _accountContext);
        var futureId = await SaveMaterialAsync(library, "未来暂停");
        var overdueId = await SaveMaterialAsync(library, "逾期暂停");
        await library.QueueMaterialsForPublishAsync([futureId], DateTimeOffset.Now.AddHours(2));
        await library.QueueMaterialsForPublishAsync([overdueId], DateTimeOffset.Now.AddMinutes(-2));
        var executions = await library.GetScheduleExecutionsAsync();
        var paused = await library.SetScheduleExecutionsPausedAsync(
            executions.Select(item => item.Id).ToArray(),
            true);
        Assert.Equal(2, paused.AffectedIds.Count);

        var dashboard = new DashboardService(
            library,
            new PublishHistoryStore(_databasePath, _accountContext),
            new SystemSettingsStore(_databasePath));
        var snapshot = await dashboard.GetSnapshotAsync();

        Assert.Equal(0, snapshot.Tasks.ScheduledCount);
        Assert.Equal(0, snapshot.Tasks.OverdueCount);
        Assert.Equal(0, snapshot.Tasks.RemainingCount);
        Assert.Equal("queue", (await library.GetMaterialAsync(futureId))!.Status);
        Assert.Equal("queue", (await library.GetMaterialAsync(overdueId))!.Status);
    }

    [Fact]
    public async Task PoliciesAndAutoScheduling_AreIsolatedByAccount()
    {
        var libraryA = new ContentLibraryStore(_databasePath, _accountContext);
        var policiesA = new PublishPolicyStore(_databasePath, _accountContext);
        await policiesA.SavePolicyAsync(CreatePolicy(dailyLimit: 3));

        _accountContext.Set(CurrentAccountIdentity.Create("global-b", "nickname-b"));
        var libraryB = new ContentLibraryStore(_databasePath, _accountContext);
        var policiesB = new PublishPolicyStore(_databasePath, _accountContext);

        Assert.Null(await policiesB.GetPolicyAsync(GuildId, ChannelId));
        var materialIdA = 1L;
        var blocked = await libraryB.TryQueueAutomaticMaterialAsync(
            materialIdA,
            StableFutureNoon(),
            CreatePolicyRecord(dailyLimit: 3),
            TimeSpan.FromMinutes(1));
        Assert.False(blocked.Scheduled);
        Assert.Empty(await libraryB.GetScheduleExecutionsAsync());

        await policiesB.SavePolicyAsync(CreatePolicy(dailyLimit: 7));
        Assert.Equal(7, (await policiesB.GetPolicyAsync(GuildId, ChannelId))!.DailyLimit);

        _accountContext.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        Assert.Equal(3, (await policiesA.GetPolicyAsync(GuildId, ChannelId))!.DailyLimit);
        Assert.Empty(await libraryA.GetScheduleExecutionsAsync());
    }

    [Fact]
    public async Task DisabledPolicy_DoesNotQueueMaterial()
    {
        var now = StableFutureNoon();
        var (library, policies, scheduler) = CreateServices(now);
        await policies.SavePolicyAsync(CreatePolicy(enabled: false));
        var materialId = await SaveMaterialAsync(library, "关闭自动排期");

        var result = await scheduler.TryQueueAsync(materialId, GuildId, ChannelId, 15);

        Assert.False(result.Scheduled);
        Assert.Null(result.ScheduledAt);
        Assert.Empty(await library.GetScheduleExecutionsAsync());
    }

    [Fact]
    public async Task FirstDelayAndMinimumInterval_ProduceOrderedQueue()
    {
        var now = StableFutureNoon();
        var (library, policies, scheduler) = CreateServices(now);
        await policies.SavePolicyAsync(CreatePolicy(
            minIntervalMinutes: 30,
            maxIntervalMinutes: 30,
            dailyLimit: 10));
        var firstMaterialId = await SaveMaterialAsync(library, "第一条");
        var secondMaterialId = await SaveMaterialAsync(library, "第二条");

        var first = await scheduler.TryQueueAsync(firstMaterialId, GuildId, ChannelId, 15);
        var second = await scheduler.TryQueueAsync(secondMaterialId, GuildId, ChannelId, 15);

        Assert.True(first.Scheduled);
        Assert.Equal(now.AddMinutes(15).ToUniversalTime(), first.ScheduledAt!.Value.ToUniversalTime());
        Assert.True(second.Scheduled);
        Assert.Equal(
            first.ScheduledAt.Value.AddMinutes(30).ToUniversalTime(),
            second.ScheduledAt!.Value.ToUniversalTime());
    }

    [Fact]
    public async Task DailyLimit_IsSharedAcrossQueueCallsAndMovesOverflowToNextDay()
    {
        var now = StableFutureNoon();
        var (library, policies, scheduler) = CreateServices(now);
        await policies.SavePolicyAsync(CreatePolicy(dailyLimit: 2));

        var first = await scheduler.TryQueueAsync(
            await SaveMaterialAsync(library, "额度一"), GuildId, ChannelId, 0);
        var second = await scheduler.TryQueueAsync(
            await SaveMaterialAsync(library, "额度二"), GuildId, ChannelId, 0);
        var third = await scheduler.TryQueueAsync(
            await SaveMaterialAsync(library, "额度三"), GuildId, ChannelId, 0);

        Assert.True(first.Scheduled);
        Assert.True(second.Scheduled);
        Assert.True(third.Scheduled);
        Assert.Equal(first.ScheduledAt!.Value.Date, second.ScheduledAt!.Value.Date);
        Assert.True(third.ScheduledAt!.Value.Date > second.ScheduledAt.Value.Date);
    }

    [Fact]
    public async Task GuildBudget_IsStricterThanChannelDailyLimit()
    {
        var now = StableFutureNoon();
        var (library, policies, scheduler) = CreateServices(now);
        await policies.SavePolicyAsync(CreatePolicy(dailyLimit: 20));
        await policies.SaveGuildBudgetAsync(new PublishGuildBudgetDraft(GuildId, true, 1));

        var first = await scheduler.TryQueueAsync(
            await SaveMaterialAsync(library, "频道预算一"), GuildId, ChannelId, 0);
        var second = await scheduler.TryQueueAsync(
            await SaveMaterialAsync(library, "频道预算二"), GuildId, ChannelId, 0);

        Assert.True(first.Scheduled);
        Assert.True(second.Scheduled);
        Assert.True(second.ScheduledAt!.Value.Date > first.ScheduledAt!.Value.Date);
    }

    [Fact]
    public async Task QueueLimit_RejectsAdditionalAutomaticMaterial()
    {
        var now = StableFutureNoon();
        var (library, policies, scheduler) = CreateServices(now);
        await policies.SavePolicyAsync(CreatePolicy(queueLimit: 1));

        var first = await scheduler.TryQueueAsync(
            await SaveMaterialAsync(library, "队列一"), GuildId, ChannelId, 0);
        var second = await scheduler.TryQueueAsync(
            await SaveMaterialAsync(library, "队列二"), GuildId, ChannelId, 0);

        Assert.True(first.Scheduled);
        Assert.False(second.Scheduled);
        Assert.Contains("队列已满", second.Reason, StringComparison.Ordinal);
        Assert.Single(await library.GetScheduleExecutionsAsync());
    }

    [Fact]
    public async Task SameChannelConcurrencyLimit_BlocksSecondAutomaticExecution()
    {
        var now = StableFutureNoon();
        var (library, policies, scheduler) = CreateServices(now);
        await policies.SavePolicyAsync(CreatePolicy(dailyLimit: 10, maxConcurrency: 1));
        var first = await scheduler.TryQueueAsync(
            await SaveMaterialAsync(library, "并发一"), GuildId, ChannelId, 0);
        var second = await scheduler.TryQueueAsync(
            await SaveMaterialAsync(library, "并发二"), GuildId, ChannelId, 0);

        var claimed = await library.TryClaimDueExecutionAsync(second.ScheduledAt!.Value.AddSeconds(1));
        Assert.NotNull(claimed);
        Assert.Equal(first.ScheduledAt!.Value.ToUniversalTime(), claimed!.ScheduledAt.ToUniversalTime());

        var blocked = await library.TryClaimDueExecutionAsync(second.ScheduledAt.Value.AddSeconds(2));
        Assert.Null(blocked);

        var executions = await library.GetScheduleExecutionsAsync();
        Assert.Single(executions, item => item.Status == ScheduleExecutionStatus.Running);
        Assert.Single(executions, item => item.Status == ScheduleExecutionStatus.Pending);
    }

    [Fact]
    public async Task RateLimit_RequeuesAutomaticExecutionWithBackoff()
    {
        var now = StableFutureNoon();
        var (library, policies, scheduler) = CreateServices(now);
        await policies.SavePolicyAsync(CreatePolicy(
            rateLimitBaseMinutes: 5,
            rateLimitMaxMinutes: 20));
        var queued = await scheduler.TryQueueAsync(
            await SaveMaterialAsync(library, "限流重排"), GuildId, ChannelId, 0);
        var claimed = await library.TryClaimDueExecutionAsync(queued.ScheduledAt!.Value.AddSeconds(1));
        Assert.NotNull(claimed);

        var completed = await library.CompleteScheduleExecutionAsync(
            claimed!.Id,
            null,
            new PublishResult(false, PublishErrorCategory.RateLimit, "接口限流", null, null));

        Assert.True(completed);
        var executions = await library.GetScheduleExecutionsAsync();
        Assert.Single(executions, item => item.Status == ScheduleExecutionStatus.Failed);
        var retry = Assert.Single(executions, item => item.Status == ScheduleExecutionStatus.Pending);
        Assert.Equal(ScheduleExecutionMode.Auto, retry.SchedulingMode);
        Assert.Equal(1, retry.RateLimitAttempts);
        Assert.True(retry.ScheduledAt > DateTimeOffset.Now);

        var material = await library.GetMaterialAsync(retry.MaterialId);
        Assert.Equal("queue", material!.Status);
        Assert.Equal(retry.ScheduledAt.ToUniversalTime(), material.PublishAt!.Value.ToUniversalTime());
    }

    [Fact]
    public async Task Timeout_RequiresVerificationAndDoesNotRetryAutomatically()
    {
        var now = StableFutureNoon();
        var (library, policies, scheduler) = CreateServices(now);
        await policies.SavePolicyAsync(CreatePolicy());
        var queued = await scheduler.TryQueueAsync(
            await SaveMaterialAsync(library, "超时不重试"), GuildId, ChannelId, 0);
        var claimed = await library.TryClaimDueExecutionAsync(queued.ScheduledAt!.Value.AddSeconds(1));
        Assert.NotNull(claimed);

        var completed = await library.CompleteScheduleExecutionAsync(
            claimed!.Id,
            null,
            new PublishResult(false, PublishErrorCategory.Timeout, "结果未知", null, null));

        Assert.True(completed);
        var execution = Assert.Single(await library.GetScheduleExecutionsAsync());
        Assert.Equal(ScheduleExecutionStatus.NeedsVerification, execution.Status);
        Assert.Equal(ScheduleExecutionMode.Auto, execution.SchedulingMode);
        var material = await library.GetMaterialAsync(execution.MaterialId);
        Assert.Equal("waitsend", material!.Status);
        Assert.Null(material.PublishAt);
    }

    [Fact]
    public async Task DisablingPolicyAfterQueue_DoesNotCancelExistingExecution()
    {
        var now = StableFutureNoon();
        var (library, policies, scheduler) = CreateServices(now);
        await policies.SavePolicyAsync(CreatePolicy());
        var queued = await scheduler.TryQueueAsync(
            await SaveMaterialAsync(library, "停用不取消"), GuildId, ChannelId, 0);

        await policies.SavePolicyAsync(CreatePolicy(enabled: false));

        var blocked = await library.TryClaimDueExecutionAsync(queued.ScheduledAt!.Value.AddSeconds(1));
        Assert.Null(blocked);
        var execution = Assert.Single(await library.GetScheduleExecutionsAsync());
        Assert.Equal(ScheduleExecutionStatus.Pending, execution.Status);
    }

    [Fact]
    public async Task ManualExecution_IsNotBlockedByDisabledAutomaticPolicy()
    {
        var now = StableFutureNoon();
        var (library, policies, _) = CreateServices(now);
        await policies.SavePolicyAsync(CreatePolicy(enabled: false));
        var materialId = await library.SaveMaterialAsync(new MaterialDraft(
            null,
            "手工计划",
            "text",
            "正文",
            GuildId,
            "频道",
            ChannelId,
            "版块",
            now,
            "queue",
            "",
            []));

        var claimed = await library.TryClaimDueExecutionAsync(now.AddSeconds(1));

        Assert.NotNull(claimed);
        Assert.Equal(ScheduleExecutionMode.Manual, claimed!.SchedulingMode);
        Assert.Equal(materialId, claimed.MaterialId);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        DeleteIfExists(_databasePath);
        DeleteIfExists(_databasePath + "-wal");
        DeleteIfExists(_databasePath + "-shm");
        if (Directory.Exists(_directory)) Directory.Delete(_directory, false);
    }

    private (ContentLibraryStore Library, PublishPolicyStore Policies, PublishAutoScheduleService Scheduler)
        CreateServices(DateTimeOffset now)
    {
        var library = new ContentLibraryStore(_databasePath, _accountContext);
        var policies = new PublishPolicyStore(_databasePath, _accountContext);
        return (library, policies, new PublishAutoScheduleService(library, policies, () => now));
    }

    private static PublishPolicyDraft CreatePolicy(
        bool enabled = true,
        int minIntervalMinutes = 1,
        int maxIntervalMinutes = 1,
        int dailyLimit = 20,
        int queueLimit = 100,
        int maxConcurrency = 1,
        int rateLimitBaseMinutes = 30,
        int rateLimitMaxMinutes = 120) =>
        new(
            GuildId,
            ChannelId,
            enabled,
            "00:00",
            "23:59",
            minIntervalMinutes,
            maxIntervalMinutes,
            dailyLimit,
            queueLimit,
            maxConcurrency,
            rateLimitBaseMinutes,
            rateLimitMaxMinutes);

    private static PublishPolicy CreatePolicyRecord(
        bool enabled = true,
        int minIntervalMinutes = 1,
        int maxIntervalMinutes = 1,
        int dailyLimit = 20,
        int queueLimit = 100,
        int maxConcurrency = 1,
        int rateLimitBaseMinutes = 30,
        int rateLimitMaxMinutes = 120)
    {
        var draft = CreatePolicy(
            enabled,
            minIntervalMinutes,
            maxIntervalMinutes,
            dailyLimit,
            queueLimit,
            maxConcurrency,
            rateLimitBaseMinutes,
            rateLimitMaxMinutes);
        var now = DateTimeOffset.Now;
        return new PublishPolicy(
            draft.GuildId,
            draft.ChannelId,
            draft.Enabled,
            draft.WindowStart,
            draft.WindowEnd,
            draft.MinIntervalMinutes,
            draft.MaxIntervalMinutes,
            draft.DailyLimit,
            draft.QueueLimit,
            draft.MaxConcurrency,
            draft.RateLimitBaseMinutes,
            draft.RateLimitMaxMinutes,
            now,
            now);
    }

    private static Task<long> SaveMaterialAsync(ContentLibraryStore library, string title) =>
        library.SaveMaterialAsync(new MaterialDraft(
            null,
            title,
            "text",
            "正文",
            GuildId,
            "频道",
            ChannelId,
            "版块",
            null,
            "waitsend",
            "",
            []));

    private static Task<long> SaveScheduledMaterialAsync(
        ContentLibraryStore library,
        DateTimeOffset scheduledAt,
        string title) =>
        library.SaveMaterialAsync(new MaterialDraft(
            null,
            title,
            "text",
            "正文",
            GuildId,
            "频道",
            ChannelId,
            "版块",
            scheduledAt,
            "queue",
            "",
            []));

    private static DateTimeOffset StableFutureNoon()
    {
        var localDate = DateTime.Now.Date.AddDays(1).AddHours(12);
        return new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate));
    }

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        string databasePath,
        string tableName)
    {
        var columns = new List<string>();
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<IReadOnlyList<string>> ReadTableNamesAsync(string databasePath)
    {
        var tables = new List<string>();
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        return tables;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
