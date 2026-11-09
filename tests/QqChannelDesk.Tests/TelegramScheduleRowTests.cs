using QqChannelDesk.Pages;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class TelegramScheduleRowTests
{
    [Fact]
    public void HealthLabelPrioritizesRuntimeRetryAndDisabledStates()
    {
        var row = new TelegramScheduleRow(Schedule(enabled: false));
        Assert.Equal("已停用", row.HealthLabel);

        row = new TelegramScheduleRow(Schedule());
        row.SetQueueState(isExecuting: true, queuePosition: 0);
        Assert.Equal("执行中", row.HealthLabel);
        Assert.False(row.CanEditActions);

        row.SetQueueState(isExecuting: false, queuePosition: 2);
        Assert.Equal("排队", row.HealthLabel);
        Assert.Contains("#2", row.HealthDetail, StringComparison.Ordinal);
        Assert.False(row.CanEditActions);

        row = new TelegramScheduleRow(Schedule(nextRetryAt: DateTimeOffset.Now.AddMinutes(5)));
        Assert.Equal("等待重试", row.HealthLabel);
        Assert.True(row.CanEditActions);
    }

    [Fact]
    public void HealthLabelCoversTerminalResultsAndNeverCollected()
    {
        Assert.Equal("部分成功",
            new TelegramScheduleRow(Schedule(lastStatus: "部分成功")).HealthLabel);
        Assert.Equal("失败",
            new TelegramScheduleRow(Schedule(lastStatus: "采集异常")).HealthLabel);
        Assert.Equal("已取消",
            new TelegramScheduleRow(Schedule(lastStatus: "已取消")).HealthLabel);
        Assert.Equal("从未采集",
            new TelegramScheduleRow(Schedule(lastStatus: "从未采集")).HealthLabel);
        Assert.Equal("正常",
            new TelegramScheduleRow(Schedule(lastStatus: "成功")).HealthLabel);
    }

    [Fact]
    public void HealthToolTipRedactsCredentialLikeError()
    {
        var row = new TelegramScheduleRow(Schedule(
            lastStatus: "失败",
            lastError: "QQ_AI_CONNECT_TOKEN=bot:v1_secret123 authorization=Bearer-secret"));

        Assert.Equal("失败", row.HealthLabel);
        Assert.DoesNotContain("secret123", row.HealthToolTip, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer-secret", row.HealthToolTip, StringComparison.Ordinal);
        Assert.Contains("已隐藏", row.HealthToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void ExhaustedAutomaticRetryShowsManualActionAndStaysFailed()
    {
        var row = new TelegramScheduleRow(Schedule(
            lastStatus: TelegramCollectionStore.AutomaticRetryStoppedStatus,
            lastError: "QQ_AI_CONNECT_TOKEN=bot:v1_secret123",
            retryAttempt: TelegramCollectionStore.MaxAutomaticRetryAttempts));

        Assert.Equal("失败", row.HealthLabel);
        Assert.Contains("自动重试已停止", row.HealthDetail, StringComparison.Ordinal);
        Assert.Contains("请手动采集", row.NextRunDisplay, StringComparison.Ordinal);
        Assert.DoesNotContain("secret123", row.ResultDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void IdleAndRunningRowsExposeOnlyTheirOwnActionState()
    {
        var row = new TelegramScheduleRow(Schedule());

        Assert.True(row.CanRun);
        Assert.True(row.CanEditActions);
        Assert.Equal("立即采集", row.RunLabel);

        row.SetQueueState(isExecuting: true, queuePosition: 0);
        Assert.True(row.CanRun);
        Assert.False(row.CanEditActions);
        Assert.Equal("取消采集", row.RunLabel);

        row.SetQueueState(isExecuting: false, queuePosition: 1);
        Assert.True(row.CanRun);
        Assert.False(row.CanEditActions);
        Assert.Equal("取消排队", row.RunLabel);
    }

    private static TelegramCollectionSchedule Schedule(
        bool enabled = true,
        string lastStatus = "成功",
        string lastError = "",
        DateTimeOffset? nextRetryAt = null,
        int retryAttempt = 0)
    {
        var now = DateTimeOffset.Now;
        return new TelegramCollectionSchedule(
            Id: 1,
            ChannelUrl: "https://t.me/s/example",
            ChannelName: "example",
            IntervalHours: 24,
            Enabled: enabled,
            ProxySelection: "automatic",
            MessageLimit: 20,
            Keyword: "",
            LastCollectedAt: now.AddMinutes(-2),
            LastSuccessAt: now.AddMinutes(-2),
            LastStatus: lastStatus,
            LastError: lastError,
            LastAddedCount: 2,
            CreatedAt: now.AddDays(-1),
            UpdatedAt: now,
            NextRetryAt: nextRetryAt,
            RetryAttempt: retryAttempt);
    }
}
