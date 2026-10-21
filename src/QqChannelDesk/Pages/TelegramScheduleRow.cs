using QqChannelDesk.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace QqChannelDesk.Pages;

/// <summary>
/// 定时采集配置在表格中的行视图模型，负责承载“单行采集 / 取消采集”的交互状态，
/// 避免采集期间禁用整张表格或页面顶部的编辑控件。
/// </summary>
public sealed class TelegramScheduleRow : INotifyPropertyChanged
{
    private TelegramCollectionSchedule _schedule;
    private bool _isExecuting;
    private bool _isQueued;
    private int _queuePosition;

    public TelegramScheduleRow(TelegramCollectionSchedule schedule) => _schedule = schedule;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TelegramCollectionSchedule Schedule => _schedule;

    public long Id => _schedule.Id;
    public string ChannelUrl => _schedule.ChannelUrl;
    public string ChannelName => _schedule.ChannelName;
    public string CronDisplay => _schedule.CronDisplay;
    public string MessageLimitDisplay => _schedule.MessageLimitDisplay;
    public string DistributionDisplay => _schedule.DistributionDisplay;
    public string AutoScheduleDisplay => _schedule.AutoScheduleDisplay;
    public string EnabledLabel => _schedule.EnabledLabel;
    public string ToggleLabel => _schedule.ToggleLabel;
    public string NextRunDisplay => _schedule.NextRunDisplay;
    public string LastRunDisplay => _schedule.LastRunDisplay;
    public string ResultDisplay => _schedule.ResultDisplay;
    public string LastAttemptDisplay => _schedule.LastAttemptDisplay;
    public string LastSuccessDisplay => _schedule.LastSuccessDisplay;

    public string HealthLabel
    {
        get
        {
            if (!_schedule.Enabled) return "已停用";
            if (IsExecuting) return "执行中";
            if (IsQueued) return "排队";
            if (TelegramCollectionStore.IsAutomaticRetryStopped(_schedule)) return "失败";
            if (_schedule.NextRetryAt is not null) return "等待重试";
            if (string.Equals(_schedule.LastStatus, "已取消", StringComparison.Ordinal)) return "已取消";
            if (string.Equals(_schedule.LastStatus, "部分成功", StringComparison.Ordinal)) return "部分成功";
            if (_schedule.LastStatus.StartsWith("失败", StringComparison.Ordinal) ||
                _schedule.LastStatus.Contains("异常", StringComparison.Ordinal))
                return "失败";
            if (string.IsNullOrWhiteSpace(_schedule.LastStatus) ||
                string.Equals(_schedule.LastStatus, "从未采集", StringComparison.Ordinal))
                return "从未采集";
            return "正常";
        }
    }

    public string HealthDetail
    {
        get
        {
            if (!_schedule.Enabled) return "定时采集已关闭";
            if (IsExecuting) return "正在采集，可单独取消本行";
            if (IsQueued)
                return QueuePosition > 0 ? $"队列位置 #{QueuePosition}" : "等待空闲执行位";
            if (TelegramCollectionStore.IsAutomaticRetryStopped(_schedule))
                return "自动重试已停止，请手动采集";
            if (_schedule.NextRetryAt is { } retryAt)
                return $"将在 {retryAt.ToLocalTime():MM-dd HH:mm} 自动重试";

            var error = SanitizedError;
            return HealthLabel switch
            {
                "已取消" => error.Length > 0 ? error : "上次采集已取消",
                "部分成功" => error.Length > 0
                    ? error
                    : $"已新增 {_schedule.LastAddedCount} 条，部分内容未完成",
                "失败" => error.Length > 0 ? error : "上次采集失败，请查看历史",
                "从未采集" => "尚未执行过定时采集",
                _ => error.Length > 0 ? error : $"最近采集正常，新增 {_schedule.LastAddedCount} 条"
            };
        }
    }

    public string HealthToolTip
    {
        get
        {
            var error = SanitizedError;
            var detail = $"健康状态：{HealthLabel}（{HealthDetail}）\n" +
                         $"最近尝试：{LastAttemptDisplay}\n" +
                         $"最近成功：{LastSuccessDisplay}";
            if (error.Length > 0) detail += $"\n最近错误：{error}";
            return detail.Length <= 700 ? detail : detail[..700] + "…";
        }
    }

    public bool IsExecuting => _isExecuting;
    public bool IsQueued => _isQueued;
    public bool IsRunning => IsExecuting || IsQueued;
    public int QueuePosition => _queuePosition;

    public string RunStatusDisplay => IsExecuting
        ? "执行中"
        : IsQueued
            ? QueuePosition > 0 ? $"排队 #{QueuePosition}" : "排队中"
            : "空闲";
    public string RunLabel => IsExecuting ? "取消采集" : IsQueued ? "取消排队" : "立即采集";

    /// <summary>本行始终可点击：未采集时开始，采集或排队中时取消。</summary>
    public bool CanRun => true;

    /// <summary>只锁定本行：正在采集（或排队）的配置不允许同时编辑、关闭、删除。</summary>
    public bool CanEditActions => !IsRunning;

    public void Update(TelegramCollectionSchedule schedule)
    {
        _schedule = schedule;
        RaiseAll();
    }

    public void SetQueueState(bool isExecuting, int queuePosition)
    {
        var isQueued = !isExecuting && queuePosition > 0;
        if (_isExecuting == isExecuting && _isQueued == isQueued && _queuePosition == queuePosition)
            return;
        _isExecuting = isExecuting;
        _isQueued = isQueued;
        _queuePosition = isQueued ? queuePosition : 0;
        RaiseAll();
    }

    private string SanitizedError => CliDiagnostics.Sanitize(_schedule.LastError).Trim();

    private void RaiseAll([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler is null) return;
        // 行列有派生属性（RunLabel / CanRun / CanEditActions），统一广播刷新。
        handler(this, new PropertyChangedEventArgs(string.Empty));
    }
}
