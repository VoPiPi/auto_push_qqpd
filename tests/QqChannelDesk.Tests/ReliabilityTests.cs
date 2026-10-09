using Microsoft.Data.Sqlite;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class ReliabilityTests
{
    [Fact]
    public async Task Stores_EnableWalJournalMode()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-wal-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);
        await store.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = new SqliteCommand("PRAGMA journal_mode", connection);
        var mode = Convert.ToString(await command.ExecuteScalarAsync());

        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public async Task AccountContext_ConcurrentReadersNeverSeePartialIdentity()
    {
        var context = new AccountContext();
        var identityA = CurrentAccountIdentity.Create("global-a", "nick-a");
        var identityB = CurrentAccountIdentity.Create("global-b", "nick-b");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var writers = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                context.Set(identityA);
                context.Set(identityB);
                context.Clear();
            }
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // Identity is a single volatile snapshot: readers either see one
                // complete identity or none, never fields mixed across accounts.
                var snapshot = context.Identity;
                if (snapshot is null) continue;

                var matchesA = snapshot.AccountKey == identityA.AccountKey
                    && snapshot.Nickname == identityA.Nickname
                    && snapshot.GlobalNickname == identityA.GlobalNickname;
                var matchesB = snapshot.AccountKey == identityB.AccountKey
                    && snapshot.Nickname == identityB.Nickname
                    && snapshot.GlobalNickname == identityB.GlobalNickname;
                Assert.True(matchesA || matchesB, "读取到了跨账号拼接的半成品身份。");
            }
        })).ToArray();

        await Task.WhenAll(readers.Append(writers));
    }

    [Fact]
    public void Logger_SessionEntriesAreCapped()
    {
        var logger = new AppLogger(Path.Combine(Path.GetTempPath(), $"qq-channel-log-{Guid.NewGuid():N}"));

        for (var index = 0; index < 2100; index++)
            logger.Info($"entry {index}");

        var entries = logger.ReadEntries(includeDebug: true);
        Assert.Equal(2000, entries.Count);
        Assert.Contains("entry 2099", entries[^1]);
    }
}