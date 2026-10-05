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
        var id = await store.SaveMaterialAsync(new MaterialDraft(null, "图文标题", "image", "正文", "1", "频道", "2", "板块", publishAt, "queue", "", ["https://example.com/photo.jpg"]));

        var material = Assert.Single(await store.GetMaterialsAsync("图文标题", "queue"));
        Assert.Equal(id, material.Id);
        Assert.Equal("image", material.Type);
        Assert.Equal(new[] { "https://example.com/photo.jpg" }, material.MediaLinks);
        Assert.Equal("频道/板块", material.TargetDisplay);
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
        Assert.Contains("有效的图片链接或本地文件", Pages.MaterialsPage.ValidateMaterialForPublish(noImage));

        var imageWithUnknownType = noImage with { MediaLinks = ["https://example.com/file.bin"] };
        Assert.Contains("无效链接或文件", Pages.MaterialsPage.ValidateMaterialForPublish(imageWithUnknownType));

        var validImage = noImage with { MediaLinks = ["https://example.com/photo.jpg?token=abc"] };
        Assert.Empty(Pages.MaterialsPage.ValidateMaterialForPublish(validImage));

        var noVideo = noTarget with { GuildId = "1", ChannelId = "2", Type = "video", MediaLinks = ["https://example.com/v.mp4"] };
        Assert.Empty(Pages.MaterialsPage.ValidateMaterialForPublish(noVideo));

        var mixedVideo = noVideo with { MediaLinks = ["https://example.com/v.mp4", "https://example.com/photo.jpg"] };
        Assert.Contains("一个视频链接或本地文件", Pages.MaterialsPage.ValidateMaterialForPublish(mixedVideo));
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
    public void MaterialMediaValidator_RejectsInvalidUrlsAndUnsupportedTypes()
    {
        Assert.False(MaterialMediaValidator.IsValidSource("http://127.0.0.1/private.jpg", "image"));
        Assert.False(MaterialMediaValidator.IsValidSource("https://example.com/movie.mp4", "image"));
        Assert.Contains("需要一个视频", MaterialMediaValidator.Validate("video", "title", [
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
}
