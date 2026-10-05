using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class ChannelSyncServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"QqChannelDeskSyncTests-{Guid.NewGuid():N}");
    private readonly ChannelCacheStore _store;

    public ChannelSyncServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new ChannelCacheStore(Path.Combine(_directory, "channels.db"));
    }

    [Fact]
    public async Task SynchronizeAsync_LoadsEveryGuildBeforePersistingSnapshot()
    {
        var script = await CreateCliAsync("""
            const a=process.argv.slice(2);
            if(a[0]==='manage'&&a[1]==='get-my-join-guild-info') console.log(JSON.stringify({success:true,data:{created_guilds:[{guild_id:'1',name:'自建',role:'创建者'}],managed_guilds:null,joined_guilds:[{guild_id:'2',name:'加入',role:'成员'}]}}));
            else if(a[0]==='manage'&&a[1]==='get-guild-channel-list') { const id=a[a.indexOf('--guild-id')+1]; console.log(JSON.stringify({success:true,data:{channels:[{channel_id:id+'1',channel_name:'版块'+id}]}})); }
            else process.exit(2);
            """);
        var service = CreateService(script);

        var result = await service.SynchronizeAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.GuildCount);
        Assert.Equal(2, result.ChannelCount);
        Assert.Equal(new[] { "1", "2" }, (await _store.GetGuildsAsync()).Select(item => item.Id).OrderBy(id => id));
        Assert.Equal("版块2", Assert.Single(await _store.GetChannelsAsync("2")).Name);
    }

    [Fact]
    public async Task SynchronizeAsync_WhenAnyGuildFails_PreservesOldSnapshotAndMarksFailure()
    {
        await _store.ReplaceSnapshotAsync([
            new CachedGuild("old", "旧频道", "管理员", [new ChannelChoice("old1", "旧版块", "")])
        ]);
        var script = await CreateCliAsync("""
            const a=process.argv.slice(2);
            if(a[0]==='manage'&&a[1]==='get-my-join-guild-info') console.log(JSON.stringify({success:true,data:{created_guilds:[{guild_id:'1',name:'正常',role:'成员'},{guild_id:'2',name:'失败',role:'成员'}]}}));
            else if(a[0]==='manage'&&a[1]==='get-guild-channel-list') { const id=a[a.indexOf('--guild-id')+1]; if(id==='2'){console.error('channel lookup failed');process.exit(1);} console.log(JSON.stringify({success:true,data:{channels:[]}})); }
            else process.exit(2);
            """);
        var service = CreateService(script);

        var result = await service.SynchronizeAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("old", Assert.Single(await _store.GetGuildsAsync()).Id);
        Assert.Equal("旧版块", Assert.Single(await _store.GetChannelsAsync("old")).Name);
        Assert.False((await _store.GetSyncStateAsync()).Succeeded);
    }

    private ChannelSyncService CreateService(string script) =>
        new(new CliWorkflow(script), _store, new AppLogger(_directory));

    private async Task<string> CreateCliAsync(string body)
    {
        var path = Path.Combine(_directory, $"fake-{Guid.NewGuid():N}.js");
        await File.WriteAllTextAsync(path, body);
        return path;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
