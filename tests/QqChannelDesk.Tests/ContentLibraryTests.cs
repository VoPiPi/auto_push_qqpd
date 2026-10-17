using Microsoft.Data.Sqlite;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class ContentLibraryTests
{
    [Fact]
    public void NormalizeUrl_RemovesFragmentAndDefaultPort()
    {
        var valid = PublicArticleFetcher.TryNormalizePublicUrl(" HTTPS://example.com:443/article#section ", out var uri, out _);

        Assert.True(valid);
        Assert.Equal("https://example.com/article", uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://192.168.1.2/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fe80::1]/")]
    public void NormalizeUrl_RejectsNonPublicTargets(string input)
    {
        Assert.False(PublicArticleFetcher.TryNormalizePublicUrl(input, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ParseHtml_ExtractsTitleAndReadableArticleAndRemovesScripts()
    {
        var result = PublicArticleFetcher.ParseHtml("""
            <html><head><title>页面标题</title><script>secret()</script></head>
            <body><nav>导航内容</nav><article><h1>文章标题</h1><p>第一段正文。</p><p>第二段&nbsp;正文。</p><script>bad()</script></article></body></html>
            """);

        Assert.Equal("页面标题", result.Title);
        Assert.Contains("第一段正文。", result.Content);
        Assert.Contains("第二段 正文。", result.Content);
        Assert.DoesNotContain("secret", result.Content);
        Assert.DoesNotContain("导航内容", result.Content);
    }

    [Fact]
    public async Task Store_AllowsMultipleManualItemsAndTracksDraftPublication()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-content-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);

        var firstId = await store.SaveItemAsync(new CollectedItemDraft("", "手动录入", "素材 A", "正文 A", ParseError: "无法提取"));
        var secondId = await store.SaveItemAsync(new CollectedItemDraft("", "手动录入", "素材 B", "正文 B"));
        Assert.NotEqual(firstId, secondId);

        var collectedId = await store.SaveItemAsync(new CollectedItemDraft("https://example.com/a", "example.com", "文章", "正文", TagList: ["技术", "频道"]));
        var sameId = await store.SaveItemAsync(new CollectedItemDraft("https://example.com/a", "example.com", "更新标题", "更新正文", TagList: ["技术", "频道"]));
        Assert.NotEqual(collectedId, sameId);
        Assert.Equal(4, (await store.GetItemsAsync()).Count);
        var original = await store.FindByUrlAsync("https://example.com/a");
        Assert.NotNull(original);
        await store.UpdateItemAsync(new CollectedItemDraft(original.Url, original.SourceHost, "手动修改", "新正文", original.Status, original.Notes, original.Tags, original.ParseError, original.Id));
        var edited = await store.FindByUrlAsync("https://example.com/a");
        Assert.Equal(original.CollectedAt, edited!.CollectedAt);
        Assert.Equal("手动修改", edited.Title);

        var draftId = await store.CreateDraftAsync(collectedId, "草稿标题", "草稿正文", "https://example.com/a");
        await store.MarkDraftPublishedAsync(draftId);
        var draft = Assert.Single(await store.GetDraftsAsync("草稿标题"));
        Assert.Equal(collectedId, draft.CollectedItemId);
        Assert.Equal(1, draft.PublishCount);
        Assert.NotNull(draft.LastPublishedAt);

        var items = await store.GetItemsAsync("素材", "待处理");
        Assert.Equal(2, items.Count);
        var article = await store.FindByUrlAsync("https://example.com/a");
        Assert.Equal("手动修改", article!.Title);
        Assert.Equal(new[] { "技术", "频道" }, article.Tags);

        await store.DeleteItemAsync(collectedId);
        draft = Assert.Single(await store.GetDraftsAsync("草稿标题"));
        Assert.Null(draft.CollectedItemId);
        Assert.Equal("https://example.com/a", draft.SourceUrl);
    }

    [Fact]
    public async Task MaterialStore_SavesScheduleAndSoftDeletes()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-material-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);
        var publishAt = DateTimeOffset.Now.AddHours(2);
        var id = await store.SaveMaterialAsync(new MaterialDraft(null, "图文标题", "image", "正文", "1", "频道", "2", "版块", publishAt, "queue", "", ["https://example.com/photo.jpg"]));

        var material = Assert.Single(await store.GetMaterialsAsync("图文标题", "queue"));
        Assert.Equal(id, material.Id);
        Assert.Equal("image", material.Type);
        Assert.Equal(new[] { "https://example.com/photo.jpg" }, material.MediaLinks);
        Assert.Equal("频道/版块", material.TargetDisplay);
        Assert.Equal("发布中", material.StatusLabel);
        Assert.Equal(publishAt.ToString("O"), material.PublishAt!.Value.ToString("O"));

        await store.SaveMaterialAsync(new MaterialDraft(id, "编辑后", "text", "正文已修改", "", "", "", "", null, "waitsend", "", []));
        material = Assert.Single(await store.GetMaterialsAsync("编辑后"));
        Assert.Equal("-", material.TargetDisplay);
        Assert.Equal("-", material.LinkDisplay);

        await store.UpdateMaterialStatusAsync(id, "published", "https://pd.qq.com/s/abc");
        material = Assert.Single(await store.GetMaterialsAsync("", "published"));
        Assert.Equal("已发布", material.StatusLabel);
        Assert.Equal("https://pd.qq.com/s/abc", material.Link);

        await store.UpdateMaterialStatusAsync(id, "delete");
        Assert.Empty(await store.GetMaterialsAsync());
        Assert.Null(await store.GetMaterialAsync(id));
    }

    [Fact]
    public async Task MaterialStore_SavesMediaOnlyMaterialWithoutText()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-media-only-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);

        var id = await store.SaveMaterialAsync(new MaterialDraft(
            null, "", "image", "", "guild", "频道", "channel", "版块", null, "waitsend", "",
            ["https://example.com/media-only.jpg"]));

        var material = await store.GetMaterialAsync(id);
        Assert.NotNull(material);
        Assert.Equal("", material!.Title);
        Assert.Equal("", material.Content);
        Assert.Equal("image", material.Type);
        Assert.Equal(new[] { "https://example.com/media-only.jpg" }, material.MediaLinks);
    }

    [Fact]
    public async Task MaterialSourceClaims_AreAtomicAndReleaseAfterSoftDelete()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-source-claim-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);
        var first = await store.SaveMaterialWithSourceAsync(new MaterialDraft(
            null, "第一条", "text", "正文", "", "", "", "", null, "waitsend", "", [],
            "TG采集", "https://t.me/channel/100"));
        var duplicate = await store.SaveMaterialWithSourceAsync(new MaterialDraft(
            null, "重复", "text", "重复正文", "", "", "", "", null, "waitsend", "", [],
            "TG采集", "https://t.me/channel/100"));

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.MaterialId, duplicate.MaterialId);
        Assert.Single(await store.GetMaterialsAsync());

        await store.UpdateMaterialStatusAsync(first.MaterialId, "delete");
        var reclaimed = await store.SaveMaterialWithSourceAsync(new MaterialDraft(
            null, "重新采集", "text", "新正文", "", "", "", "", null, "waitsend", "", [],
            "TG采集", "https://t.me/channel/100"));

        Assert.True(reclaimed.Created);
        Assert.NotEqual(first.MaterialId, reclaimed.MaterialId);
        Assert.Equal("重新采集", Assert.Single(await store.GetMaterialsAsync()).Title);
    }

    [Fact]
    public async Task MaterialSourceClaims_HandleConcurrentSameSourceSaves()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-source-race-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);
        await store.InitializeAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var saves = Enumerable.Range(0, 4).Select(index => Task.Run(async () =>
        {
            await start.Task;
            return await store.SaveMaterialWithSourceAsync(new MaterialDraft(
                null, $"并发 {index}", "text", "正文", "", "", "", "", null, "waitsend", "", [],
                "TG采集", "https://t.me/channel/101"));
        })).ToArray();
        start.SetResult();

        var results = await Task.WhenAll(saves);

        Assert.Single(results, result => result.Created);
        Assert.Single(results.Select(result => result.MaterialId).Distinct());
        Assert.Single(await store.GetMaterialsAsync());
    }

    [Fact]
    public async Task MaterialBatchOperations_UpdateTargetAndTimeWithoutTouchingProtectedRows()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-material-batch-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);

        var editableId = await store.SaveMaterialAsync(new MaterialDraft(
            null, "可修改", "text", "正文", "", "", "", "", null, "waitsend", "", []));
        var runningId = await store.SaveMaterialAsync(new MaterialDraft(
            null, "正在发布", "text", "正文", "old-guild", "旧频道", "old-channel", "旧版块",
            DateTimeOffset.Now.AddMinutes(1), "queue", "", []));
        var runningExecution = await store.TryClaimDueExecutionAsync(DateTimeOffset.Now.AddMinutes(2));
        Assert.NotNull(runningExecution);
        var publishedId = await store.SaveMaterialAsync(new MaterialDraft(
            null, "已发布", "text", "正文", "published-guild", "已发布频道",
            "published-channel", "已发布版块", null, "published", "https://example.com/post", []));

        var targetResult = await store.UpdateMaterialsTargetAsync(
            [editableId, runningId, publishedId, 999999],
            "new-guild",
            "新频道",
            "new-channel",
            "新版块");
        Assert.Equal([editableId], targetResult.UpdatedIds);
        Assert.Equal([runningId], targetResult.RunningIds);
        Assert.Equal([publishedId], targetResult.PublishedIds);
        Assert.Equal([999999L], targetResult.MissingIds);

        var editable = await store.GetMaterialAsync(editableId);
        Assert.Equal("new-guild", editable!.GuildId);
        Assert.Equal("新频道", editable.GuildName);
        Assert.Equal("new-channel", editable.ChannelId);
        Assert.Equal("新版块", editable.ChannelName);
        Assert.Equal("old-guild", (await store.GetMaterialAsync(runningId))!.GuildId);

        var publishAt = DateTimeOffset.Now.AddHours(3);
        var timeResult = await store.UpdateMaterialsPublishAtAsync(
            [editableId, runningId, publishedId, 999999],
            publishAt);
        Assert.Equal([editableId], timeResult.UpdatedIds);
        Assert.Equal([runningId], timeResult.RunningIds);
        Assert.Equal([publishedId], timeResult.PublishedIds);
        Assert.Equal([999999L], timeResult.MissingIds);
        editable = await store.GetMaterialAsync(editableId);
        Assert.Equal("queue", editable!.Status);
        Assert.Equal(publishAt.ToString("O"), editable.PublishAt!.Value.ToString("O"));

        var deleteResult = await store.DeleteMaterialsAsync(
            [editableId, runningId, publishedId, 999999]);
        Assert.Equal([editableId], deleteResult.DeletedIds);
        Assert.Equal([runningId], deleteResult.RunningIds);
        Assert.Equal([publishedId], deleteResult.PublishedIds);
        Assert.Equal([999999L], deleteResult.MissingIds);
        Assert.Null(await store.GetMaterialAsync(editableId));
        Assert.NotNull(await store.GetMaterialAsync(runningId));
        Assert.NotNull(await store.GetMaterialAsync(publishedId));
        Assert.Null(await store.GetActiveScheduleExecutionAsync(editableId));
        Assert.NotNull(await store.GetActiveScheduleExecutionAsync(runningId));
    }

    [Fact]
    public async Task QueueMaterialsForPublishAsync_IsAtomicAndRejectsRunningRows()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-material-queue-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);
        var firstId = await store.SaveMaterialAsync(new MaterialDraft(
            null, "第一篇", "text", "正文一", "guild", "频道", "channel", "版块", null, "waitsend", "", []));
        var secondId = await store.SaveMaterialAsync(new MaterialDraft(
            null, "第二篇", "text", "正文二", "guild", "频道", "channel", "版块", null, "waitsend", "", []));
        var publishedId = await store.SaveMaterialAsync(new MaterialDraft(
            null, "已发布", "text", "正文", "guild", "频道", "channel", "版块", null, "published", "https://example.com/post", []));
        var firstScheduledAt = DateTimeOffset.Now.AddHours(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.QueueMaterialsForPublishAsync([firstId, secondId, publishedId], firstScheduledAt));
        Assert.Equal("waitsend", (await store.GetMaterialAsync(firstId))!.Status);
        Assert.Empty(await store.GetScheduleExecutionsAsync());

        var queuedIds = await store.QueueMaterialsForPublishAsync([firstId, secondId], firstScheduledAt);
        Assert.Equal([firstId, secondId], queuedIds);
        Assert.Equal(2, (await store.GetScheduleExecutionsAsync()).Count);
        Assert.All(await store.GetScheduleExecutionsAsync(), execution =>
            Assert.Equal(ScheduleExecutionStatus.Pending, execution.Status));

        var claimed = await store.TryClaimDueExecutionAsync(firstScheduledAt.AddMinutes(1));
        Assert.NotNull(claimed);
        var secondScheduledAt = firstScheduledAt.AddHours(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.QueueMaterialsForPublishAsync([claimed!.MaterialId], secondScheduledAt));
    }

    [Fact]
    public void MaterialBatchSettingsWindow_ResolvesOnlyFutureValidTimes()
    {
        var now = new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 14)));
        var localDate = now.LocalDateTime.Date.AddDays(1);
        var expected = new DateTimeOffset(
            localDate.AddHours(9).AddMinutes(5),
            TimeZoneInfo.Local.GetUtcOffset(localDate.AddHours(9).AddMinutes(5)));

        Assert.Equal(expected, MaterialBatchSettingsWindow.ResolvePublishAt(localDate, "09:05:00", now));
        Assert.Null(MaterialBatchSettingsWindow.ResolvePublishAt(now.LocalDateTime.Date, "09:00:00", now));
        Assert.Null(MaterialBatchSettingsWindow.ResolvePublishAt(localDate, "9:5", now));
        Assert.Null(MaterialBatchSettingsWindow.ResolvePublishAt(null, "09:00:00", now));
    }

    [Fact]
    public async Task ScheduleExecution_DisplaysContentAndMediaWhenTitleIsEmpty()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-schedule-display-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);
        var scheduledAt = DateTimeOffset.Now.AddHours(1);
        var textId = await store.SaveMaterialAsync(new MaterialDraft(
            null, "", "text", "正文内容", "guild", "频道", "channel", "版块", null, "waitsend", "", []));
        var imageId = await store.SaveMaterialAsync(new MaterialDraft(
            null, "", "image", "", "guild", "频道", "channel", "版块", null, "waitsend", "", ["https://example.com/a.jpg", "https://example.com/b.jpg"]));
        await store.QueueMaterialsForPublishAsync([textId, imageId], scheduledAt);

        var executions = (await store.GetScheduleExecutionsAsync()).OrderBy(item => item.MaterialId).ToArray();

        Assert.Equal("正文内容", executions[0].TitleContentDisplay);
        Assert.Equal("媒体 · 2 张", executions[1].TitleContentDisplay);
    }
    [Fact]
    public async Task MaterialStore_ReturnsScheduledMaterialsInPublishOrderAndCanCancelSchedule()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-schedule-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);
        var later = DateTimeOffset.Now.AddHours(3);
        var earlier = DateTimeOffset.Now.AddHours(1);
        var laterId = await store.SaveMaterialAsync(new MaterialDraft(null, "较晚计划", "text", "正文 2", "guild", "频道", "channel", "版块", later, "queue", "", []));
        var earlierId = await store.SaveMaterialAsync(new MaterialDraft(null, "较早计划", "image", "正文 1", "guild", "频道", "channel", "版块", earlier, "queue", "", ["https://example.com/photo.jpg"]));
        var noScheduleId = await store.SaveMaterialAsync(new MaterialDraft(null, "待发布", "text", "正文 3", "guild", "频道", "channel", "版块", null, "waitsend", "", []));

        var schedules = await store.GetScheduledMaterialsAsync();
        Assert.Equal(new[] { earlierId, laterId }, schedules.Select(item => item.Id));

        Assert.True(await store.CancelMaterialScheduleAsync(earlierId));
        var cancelled = await store.GetMaterialAsync(earlierId);
        Assert.NotNull(cancelled);
        Assert.Equal("waitsend", cancelled.Status);
        Assert.Null(cancelled.PublishAt);
        Assert.Equal(new[] { "https://example.com/photo.jpg" }, cancelled.MediaLinks);
        Assert.False(await store.CancelMaterialScheduleAsync(earlierId));
        Assert.False(await store.CancelMaterialScheduleAsync(noScheduleId));
        Assert.DoesNotContain(await store.GetScheduledMaterialsAsync(), item => item.Id == earlierId);
    }

    [Fact]
    public async Task ScheduleExecution_CreatesPendingRowAndCanOnlyBeClaimedOnce()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-execution-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);
        var scheduledAt = DateTimeOffset.Now.AddHours(1);
        var materialId = await store.SaveMaterialAsync(new MaterialDraft(
            null, "待执行文本", "text", "计划正文", "guild", "频道", "channel", "版块",
            scheduledAt, "queue", "", []));

        var pending = Assert.Single(await store.GetScheduleExecutionsAsync());
        Assert.Equal(materialId, pending.MaterialId);
        Assert.Equal(ScheduleExecutionStatus.Pending, pending.Status);

        var claimed = await store.TryClaimDueExecutionAsync(scheduledAt.AddSeconds(1));
        Assert.NotNull(claimed);
        Assert.Equal(ScheduleExecutionStatus.Running, claimed!.Status);
        Assert.Null(await store.TryClaimDueExecutionAsync(scheduledAt.AddSeconds(1)));

        var result = new PublishResult(true, PublishErrorCategory.None, "已发布", "https://pd.qq.com/s/test", "post-1");
        Assert.True(await store.CompleteScheduleExecutionAsync(claimed.Id, 42, result));
        Assert.False(await store.CompleteScheduleExecutionAsync(claimed.Id, 43,
            new PublishResult(false, PublishErrorCategory.Other, "迟到结果", null, null)));

        var completed = Assert.Single(await store.GetScheduleExecutionsAsync());
        Assert.Equal(ScheduleExecutionStatus.Succeeded, completed.Status);
        Assert.Equal(42, completed.PublishRecordId);
        Assert.Equal("https://pd.qq.com/s/test", completed.ErrorDisplay);
        Assert.Equal("#42", completed.PublishRecordDisplay);
        var material = await store.GetMaterialAsync(materialId);
        Assert.NotNull(material);
        Assert.Equal("published", material!.Status);
        Assert.Equal("https://pd.qq.com/s/test", material.Link);
        Assert.Null(material.PublishAt);
    }

    [Fact]
    public async Task ScheduleExecution_ReschedulingKeepsOldHistoryAndCreatesNewPendingRow()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-reschedule-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);
        var firstTime = DateTimeOffset.Now.AddHours(1);
        var secondTime = firstTime.AddHours(1);
        var materialId = await store.SaveMaterialAsync(new MaterialDraft(
            null, "重新安排", "text", "正文", "guild", "频道", "channel", "版块",
            firstTime, "queue", "", []));

        await store.SaveMaterialAsync(new MaterialDraft(
            materialId, "重新安排", "text", "修改后的正文", "guild", "频道", "channel", "版块",
            secondTime, "queue", "", []));

        var executions = (await store.GetScheduleExecutionsAsync()).OrderBy(item => item.ScheduledAt).ToArray();
        Assert.Equal(2, executions.Length);
        Assert.Equal(ScheduleExecutionStatus.Cancelled, executions[0].Status);
        Assert.Equal(ScheduleExecutionStatus.Pending, executions[1].Status);
        Assert.Equal(materialId, executions[0].MaterialId);
        Assert.Equal(materialId, executions[1].MaterialId);
        Assert.Equal(secondTime.ToString("O"), executions[1].ScheduledAt.ToString("O"));
    }

    [Fact]
    public async Task ScheduleExecution_ReschedulingToSameTimeReusesPendingRowWithoutThrowing()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-reschedule-same-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);
        var scheduledAt = DateTimeOffset.Now.AddHours(1);
        var materialId = await store.SaveMaterialAsync(new MaterialDraft(
            null, "同时间重排", "text", "正文", "guild", "频道", "channel", "版块",
            scheduledAt, "queue", "", []));

        await store.SaveMaterialAsync(new MaterialDraft(
            materialId, "同时间重排", "text", "修改后的正文", "guild", "频道", "channel", "版块",
            scheduledAt, "queue", "", []));

        var executions = await store.GetScheduleExecutionsAsync();
        var execution = Assert.Single(executions);
        Assert.Equal(materialId, execution.MaterialId);
        Assert.Equal(ScheduleExecutionStatus.Pending, execution.Status);
        Assert.Equal(scheduledAt.ToString("O"), execution.ScheduledAt.ToString("O"));
    }

    [Theory]
    [InlineData(PublishErrorCategory.Permission, ScheduleExecutionStatus.Failed)]
    [InlineData(PublishErrorCategory.Timeout, ScheduleExecutionStatus.NeedsVerification)]
    public async Task ScheduleExecution_FailureReturnsMaterialToWaiting(
        PublishErrorCategory category, ScheduleExecutionStatus expectedStatus)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-execution-failure-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);
        var scheduledAt = DateTimeOffset.Now.AddHours(1);
        var materialId = await store.SaveMaterialAsync(new MaterialDraft(
            null, "失败计划", "text", "正文", "guild", "频道", "channel", "版块",
            scheduledAt, "queue", "", []));
        var claimed = await store.TryClaimDueExecutionAsync(scheduledAt.AddSeconds(1));
        Assert.NotNull(claimed);

        var result = new PublishResult(false, category, "发布结果需要人工检查", null, null);
        Assert.True(await store.CompleteScheduleExecutionAsync(claimed!.Id, 7, result));

        var execution = Assert.Single(await store.GetScheduleExecutionsAsync());
        Assert.Equal(expectedStatus, execution.Status);
        Assert.Equal(7, execution.PublishRecordId);
        Assert.Equal("发布结果需要人工检查", execution.ErrorDisplay);
        Assert.Equal("#7", execution.PublishRecordDisplay);
        var material = await store.GetMaterialAsync(materialId);
        Assert.NotNull(material);
        Assert.Equal("waitsend", material!.Status);
        Assert.Null(material.PublishAt);
    }

    [Fact]
    public async Task ScheduleExecution_RecoveryMarksRunningAsNeedsVerificationWithoutRepublishing()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-execution-recovery-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);
        var scheduledAt = DateTimeOffset.Now.AddHours(1);
        var materialId = await store.SaveMaterialAsync(new MaterialDraft(
            null, "恢复计划", "text", "正文", "guild", "频道", "channel", "版块",
            scheduledAt, "queue", "", []));
        var claimed = await store.TryClaimDueExecutionAsync(scheduledAt.AddSeconds(1));
        Assert.NotNull(claimed);

        await store.MarkRunningExecutionsAsNeedsVerificationAsync("应用异常退出");

        var execution = Assert.Single(await store.GetScheduleExecutionsAsync());
        Assert.Equal(ScheduleExecutionStatus.NeedsVerification, execution.Status);
        Assert.Contains("应用异常退出", execution.LastError);
        var material = await store.GetMaterialAsync(materialId);
        Assert.NotNull(material);
        Assert.Equal("waitsend", material!.Status);
        Assert.Null(material.PublishAt);

        Assert.False(await store.CompleteScheduleExecutionAsync(claimed!.Id, null,
            new PublishResult(true, PublishErrorCategory.None, "迟到成功", "https://example.test/post", null)));
        execution = Assert.Single(await store.GetScheduleExecutionsAsync());
        Assert.Equal(ScheduleExecutionStatus.NeedsVerification, execution.Status);
    }

    [Fact]
    public void ValidateMaterialForPublish_RequiresTargetAndMediaByType()
    {
        var noTarget = new MaterialRecord(1, "title", "text", "body", "", "", "", "", null, "waitsend", "", [], DateTimeOffset.Now, DateTimeOffset.Now);
        Assert.Contains("指定发布频道", Pages.MaterialsPage.ValidateMaterialForPublish(noTarget));

        var noImage = noTarget with { GuildId = "1", ChannelId = "2", Type = "image" };
        Assert.Contains("请至少添加一张有效图片", Pages.MaterialsPage.ValidateMaterialForPublish(noImage));

        var imageWithUnknownType = noImage with { MediaLinks = ["https://example.com/file.bin"] };
        Assert.Contains("无效链接或文件", Pages.MaterialsPage.ValidateMaterialForPublish(imageWithUnknownType));

        var validImage = noImage with { MediaLinks = ["https://example.com/photo.jpg?token=abc"] };
        Assert.Empty(Pages.MaterialsPage.ValidateMaterialForPublish(validImage));

        var noVideo = noTarget with { GuildId = "1", ChannelId = "2", Type = "video", MediaLinks = ["https://example.com/v.mp4"] };
        Assert.Empty(Pages.MaterialsPage.ValidateMaterialForPublish(noVideo));

        var mixedVideo = noVideo with { MediaLinks = ["https://example.com/v.mp4", "https://example.com/photo.jpg"] };
        Assert.Contains("请选择一个有效视频", Pages.MaterialsPage.ValidateMaterialForPublish(mixedVideo));
    }

    [Fact]
    public void ValidateMaterialForPublish_AllowsEmptyTitleWhenContentIsPresent()
    {
        var noTitle = new MaterialRecord(1, "", "text", "正文", "1", "频道", "2", "版块", null, "waitsend", "", [], DateTimeOffset.Now, DateTimeOffset.Now);
        Assert.Empty(Pages.MaterialsPage.ValidateMaterialForPublish(noTitle));

        var emptyContent = noTitle with { Content = "   " };
        Assert.Contains("请填写正文，或添加图片或视频", Pages.MaterialsPage.ValidateMaterialForPublish(emptyContent));

        var imageWithoutMedia = noTitle with { Type = "image" };
        Assert.Contains("请至少添加一张有效图片", Pages.MaterialsPage.ValidateMaterialForPublish(imageWithoutMedia));

        var tooManyImages = noTitle with
        {
            Type = "image",
            MediaLinks = Enumerable.Range(1, 19).Select(index => $"https://example.com/{index}.jpg").ToArray()
        };
        Assert.Contains("最多添加 18 张图片", Pages.MaterialsPage.ValidateMaterialForPublish(tooManyImages));

        var mediaOnly = noTitle with
        {
            Content = "",
            Type = "image",
            MediaLinks = ["https://example.com/photo.jpg"]
        };
        Assert.Empty(Pages.MaterialsPage.ValidateMaterialForPublish(mediaOnly));

        var titleOnly = noTitle with { Content = "", Title = "只有标题" };
        Assert.Contains("请填写正文，或添加图片或视频", Pages.MaterialsPage.ValidateMaterialForPublish(titleOnly));
    }

    [Fact]
    public void MaterialMediaValidator_AcceptsExistingLocalFilesAndPublicUrls()
    {
        var localImage = Path.Combine(Path.GetTempPath(), $"qq-channel-{Guid.NewGuid():N}.png");
        var wrongType = Path.ChangeExtension(localImage, ".mp4");
        File.WriteAllBytes(localImage, [1, 2, 3]);
        File.WriteAllBytes(wrongType, [1, 2, 3]);
        try
        {
            Assert.True(MaterialMediaValidator.IsValidSource(localImage, "image"));
            Assert.False(MaterialMediaValidator.IsValidSource(wrongType, "image"));
            Assert.False(MaterialMediaValidator.IsValidSource(Path.Combine(Path.GetTempPath(), "missing.png"), "image"));
            Assert.True(MaterialMediaValidator.IsValidSource("https://example.com/photo.jpg?token=abc", "image"));
            Assert.Equal("image", MaterialMediaValidator.InferType([localImage]));
            Assert.Equal("video", MaterialMediaValidator.InferType(["https://example.com/movie.mp4"]));
            Assert.Equal("text", MaterialMediaValidator.InferType([]));
            Assert.Equal("image", MaterialMediaValidator.InferType(["https://example.com/a.jpg?token=abc", "https://example.com/b.PNG"]));
            Assert.Null(MaterialMediaValidator.InferType(["https://example.com/without-extension"]));
            Assert.Null(MaterialMediaValidator.InferType(["https://example.com/a.jpg", "https://example.com/b.mp4"]));
            Assert.True(MaterialMediaValidator.IsValidSource("https://example.com/without-extension", "image", allowUnknownExtension: true));

            var localMaterial = new MaterialRecord(1, "title", "image", "body", "1", "guild", "2", "channel", null,
                "waitsend", "", [localImage], DateTimeOffset.Now, DateTimeOffset.Now);
            Assert.Empty(Pages.MaterialsPage.ValidateMaterialForPublish(localMaterial));
        }
        finally
        {
            File.Delete(localImage);
            File.Delete(wrongType);
        }
    }

    [Fact]
    public void MaterialMediaValidator_UnknownSourceCanBeAcceptedAfterManualTypeConfirmation()
    {
        Assert.False(MaterialMediaValidator.IsValidSource("https://example.com/media", "video"));
        Assert.True(MaterialMediaValidator.IsValidSource("https://example.com/media", "video", allowUnknownExtension: true));
        Assert.Contains("无效链接或文件", MaterialMediaValidator.Validate("image", "title", ["https://example.com/media"]));
        Assert.Empty(MaterialMediaValidator.Validate("image", "title", ["https://example.com/media"], allowUnknownSources: true));
    }

    [Fact]
    public void MaterialMediaValidator_RejectsInvalidUrlsAndUnsupportedTypes()
    {
        Assert.False(MaterialMediaValidator.IsValidSource("http://127.0.0.1/private.jpg", "image"));
        Assert.False(MaterialMediaValidator.IsValidSource("https://example.com/movie.mp4", "image"));
        Assert.Contains("请选择一个有效视频", MaterialMediaValidator.Validate("video", "title", [
            "https://example.com/a.mp4", "https://example.com/b.mp4"
        ]));
    }

    [Fact]
    public void MaterialStatus_OnlyQueuesFutureScheduleAndDefaultsToWaiting()
    {
        var now = DateTimeOffset.Now;

        Assert.Equal("waitsend", MaterialEditorWindow.ResolveMaterialStatus(null, null, now));
        Assert.Equal("waitsend", MaterialEditorWindow.ResolveMaterialStatus(now, null, now));
        Assert.Equal("waitsend", MaterialEditorWindow.ResolveMaterialStatus(now.AddMinutes(-1), null, now));
        Assert.Equal("queue", MaterialEditorWindow.ResolveMaterialStatus(now.AddMinutes(1), null, now));
        Assert.Equal("published", MaterialEditorWindow.ResolveMaterialStatus(null, "published", now));
    }

    [Fact]
    public void MaterialEditorChoice_UsesNameWhenCachedIdHasChanged()
    {
        ChannelChoice[] choices =
        [
            new("new-guild-id", "鲸鱼落", "管理员"),
            new("another-id", "其他频道", "")
        ];

        var selected = MaterialEditorWindow.ResolveChoice(choices, "old-guild-id", " 鲸鱼落 ");

        Assert.Same(choices[0], selected);
    }

    [Fact]
    public void MaterialEditorChoice_DoesNotSilentlySelectWrongTargetForExistingMaterial()
    {
        ChannelChoice[] choices = [new("channel-1", "默认版块", "")];

        Assert.Null(MaterialEditorWindow.ResolveChoice(choices, "missing", "已删除版块"));
        Assert.Same(choices[0], MaterialEditorWindow.ResolveChoice(choices, null, null, selectFirstWhenMissing: true));
    }

    [Fact]
    public async Task Store_RoundTripsTelegramMediaPaths()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-content-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);

        var media = new[] { Path.Combine("tools", "media-cache", "a.mp4"), Path.Combine("tools", "media-cache", "b.jpg") };
        await store.SaveItemAsync(new CollectedItemDraft("https://t.me/TGWenAnGuan/718", "t.me", "标题", "正文", MediaList: media));

        var saved = await store.FindByUrlAsync("https://t.me/TGWenAnGuan/718");
        Assert.NotNull(saved);
        Assert.Equal(media, saved.Media);
        Assert.Contains("a.mp4", saved.MediaSummary, StringComparison.Ordinal);
        Assert.Equal(media, Assert.Single(await store.GetItemsAsync()).Media);

        await store.UpdateItemAsync(new CollectedItemDraft(saved.Url, saved.SourceHost, saved.Title, saved.Content, saved.Status, saved.Notes, saved.Tags, saved.ParseError, saved.Id, saved.Media));
        Assert.Equal(media, (await store.FindByUrlAsync("https://t.me/TGWenAnGuan/718"))!.Media);

        await store.UpdateItemAsync(new CollectedItemDraft(saved.Url, saved.SourceHost, saved.Title, saved.Content, saved.Status, saved.Notes, saved.Tags, saved.ParseError, saved.Id));
        Assert.Empty((await store.FindByUrlAsync("https://t.me/TGWenAnGuan/718"))!.Media);

        await store.SaveItemAsync(new CollectedItemDraft("https://t.me/TGWenAnGuan/719", "t.me", "无媒体", "正文"));
        Assert.Empty((await store.FindByUrlAsync("https://t.me/TGWenAnGuan/719"))!.Media);
    }

    [Fact]
    public async Task Store_MigratesLegacyDatabaseWithoutMediaColumn()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-content-{Guid.NewGuid():N}.db");
        var collectedAt = DateTimeOffset.Now.ToString("O");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE CollectedItems (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Url TEXT NOT NULL DEFAULT '',
                    SourceHost TEXT NOT NULL DEFAULT '',
                    Title TEXT NOT NULL DEFAULT '',
                    Content TEXT NOT NULL DEFAULT '',
                    CollectedAt TEXT NOT NULL,
                    Status TEXT NOT NULL DEFAULT '待处理',
                    Notes TEXT NOT NULL DEFAULT '',
                    TagsJson TEXT NOT NULL DEFAULT '[]',
                    ParseError TEXT NOT NULL DEFAULT '',
                    AccountKey TEXT NULL
                );
                """;
            await create.ExecuteNonQueryAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO CollectedItems (Url, SourceHost, Title, Content, CollectedAt, Status, Notes, TagsJson, ParseError)
                VALUES ('https://t.me/TGWenAnGuan/1', 't.me', '旧库消息', '旧库正文', $at, '待处理', '来自 tg 频道', '[]', '');
                """;
            insert.Parameters.AddWithValue("$at", collectedAt);
            await insert.ExecuteNonQueryAsync();
        }

        var store = new ContentLibraryStore(databasePath);
        var items = await store.GetItemsAsync();

        var legacy = Assert.Single(items);
        Assert.Equal("旧库消息", legacy.Title);
        Assert.Equal("旧库正文", legacy.Content);
        Assert.Equal(DateTimeOffset.Parse(collectedAt), legacy.CollectedAt);
        Assert.Empty(legacy.Media);

        await store.SaveItemAsync(new CollectedItemDraft("https://t.me/TGWenAnGuan/2", "t.me", "新消息", "新正文", MediaList: ["a.mp4"]));
        var added = await store.FindByUrlAsync("https://t.me/TGWenAnGuan/2");
        Assert.Equal(new[] { "a.mp4" }, added!.Media);
    }

    [Fact]
    public async Task Store_MigratesLegacyMaterialsBeforeCreatingSourceIndex()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-material-legacy-{Guid.NewGuid():N}.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE Materials (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL DEFAULT '',
                    Type TEXT NOT NULL DEFAULT 'text',
                    Content TEXT NOT NULL DEFAULT '',
                    GuildId TEXT NOT NULL DEFAULT '',
                    GuildName TEXT NOT NULL DEFAULT '',
                    ChannelId TEXT NOT NULL DEFAULT '',
                    ChannelName TEXT NOT NULL DEFAULT '',
                    PublishAt TEXT NULL,
                    Status TEXT NOT NULL DEFAULT 'waitsend',
                    Link TEXT NOT NULL DEFAULT '',
                    MediaJson TEXT NOT NULL DEFAULT '[]',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    AccountKey TEXT NULL
                );
                INSERT INTO Materials (Title, Type, Content, CreatedAt, UpdatedAt)
                VALUES ('旧素材', 'text', '旧正文', '2026-10-13T00:00:00+08:00', '2026-10-13T00:00:00+08:00');
                """;
            await create.ExecuteNonQueryAsync();
        }

        var store = new ContentLibraryStore(databasePath);
        var material = Assert.Single(await store.GetMaterialsAsync());
        Assert.Equal("旧素材", material.Title);
        Assert.Equal("手工添加", material.SourceType);
        Assert.Empty(material.SourceUrl);

        var id = await store.SaveMaterialAsync(new MaterialDraft(
            null, "新素材", "text", "新正文", "", "", "", "", null, "waitsend", "", [], "TG采集", "https://t.me/example/1"));
        var added = await store.GetMaterialAsync(id);
        Assert.Equal("TG采集", added!.SourceType);
        Assert.Equal("https://t.me/example/1", added.SourceUrl);
    }

    [Fact]
    public async Task Store_RoundTripsDraftMediaPaths()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-content-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);

        var media = new[] { Path.Combine("tools", "media-cache", "a.mp4") };
        var collectedId = await store.SaveItemAsync(new CollectedItemDraft("https://t.me/TGWenAnGuan/736", "t.me", "这城市那么空", "正文", MediaList: media));
        var draftId = await store.CreateDraftAsync(collectedId, "草稿标题", "草稿正文", "https://t.me/TGWenAnGuan/736", media);

        var draft = Assert.Single(await store.GetDraftsAsync("草稿标题"));
        Assert.Equal(draftId, draft.Id);
        Assert.Equal(media, draft.Media);
        Assert.Contains("媒体 1 个", draft.MediaSummary, StringComparison.Ordinal);

        await store.UpdateDraftAsync(draft with { Title = "新标题", Media = [] });
        draft = Assert.Single(await store.GetDraftsAsync("新标题"));
        Assert.Empty(draft.Media);

        await store.CreateDraftAsync(null, "纯文本草稿", "正文", "");
        Assert.Empty(Assert.Single(await store.GetDraftsAsync("纯文本草稿")).Media);
    }

    [Fact]
    public async Task Store_CreatesDraftFromTemporaryTelegramMessage()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-content-{Guid.NewGuid():N}.db");
        var store = new ContentLibraryStore(databasePath);

        var media = new[]
        {
            Path.Combine("temp", "telegram", "20261011", "TGWenAnGuan_718", "a.jpg"),
            Path.Combine("temp", "telegram", "20261011", "TGWenAnGuan_718", "b.mp4")
        };
        var draftId = await store.CreateDraftAsync(
            null, "这城市那么空", "第一行正文", "https://t.me/TGWenAnGuan/718", media);

        var draft = Assert.Single(await store.GetDraftsAsync("这城市那么空"));
        Assert.Equal(draftId, draft.Id);
        Assert.Null(draft.CollectedItemId);
        Assert.Equal("https://t.me/TGWenAnGuan/718", draft.SourceUrl);
        Assert.Equal(media, draft.Media);
        Assert.Contains("媒体 2 个", draft.MediaSummary, StringComparison.Ordinal);

        await store.DeleteDraftAsync(draftId);
        Assert.Empty(await store.GetDraftsAsync("这城市那么空"));
    }
    [Fact]
    public async Task Store_MigratesLegacyDraftsWithoutMediaColumn()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-content-{Guid.NewGuid():N}.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE ContentDrafts (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CollectedItemId INTEGER NULL,
                    Title TEXT NOT NULL DEFAULT '',
                    Content TEXT NOT NULL DEFAULT '',
                    SourceUrl TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    PublishCount INTEGER NOT NULL DEFAULT 0,
                    LastPublishedAt TEXT NULL,
                    AccountKey TEXT NULL
                );
                """;
            await create.ExecuteNonQueryAsync();
            var now = DateTimeOffset.Now.ToString("O");
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO ContentDrafts (Title, Content, SourceUrl, CreatedAt, UpdatedAt)
                VALUES ('旧草稿', '旧正文', '', $now, $now);
                """;
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync();
        }

        var store = new ContentLibraryStore(databasePath);
        var legacy = Assert.Single(await store.GetDraftsAsync("旧草稿"));
        Assert.Equal("旧草稿", legacy.Title);
        Assert.Empty(legacy.Media);

        var media = new[] { Path.Combine("tools", "media-cache", "a.mp4") };
        await store.CreateDraftAsync(null, "新草稿", "新正文", "", media);
        Assert.Equal(media, Assert.Single(await store.GetDraftsAsync("新草稿")).Media);
    }
}
