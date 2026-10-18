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
    private bool _isRunning;

    public TelegramScheduleRow(TelegramCollectionSchedule schedule) => _schedule = schedule;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TelegramCollectionSchedule Schedule => _schedule;

    public long Id => _schedule.Id;
    public string ChannelUrl => _schedule.ChannelUrl;
    public string ChannelName => _schedule.ChannelName;
    public string CronDisplay => _schedule.CronDisplay;
    public string MessageLimitDisplay => _schedule.MessageLimitDisplay;
    public string DistributionDisplay => _schedule.DistributionDisplay;
    public string EnabledLabel => _schedule.EnabledLabel;
    public string ToggleLabel => _schedule.ToggleLabel;
    public string NextRunDisplay => _schedule.NextRunDisplay;
    public string LastRunDisplay => _schedule.LastRunDisplay;
    public string ResultDisplay => _schedule.ResultDisplay;

    /// <summary>本行正在采集（含已排队等待执行）。只有这一行会被锁定。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            RaiseAll();
        }
    }

    public string RunLabel => IsRunning ? "取消采集" : "立即采集";

    /// <summary>本行始终可点击：未采集时开始，采集或排队中时取消。</summary>
    public bool CanRun => true;

    /// <summary>只锁定本行：正在采集（或排队）的配置不允许同时编辑、关闭、删除。</summary>
    public bool CanEditActions => !IsRunning;

    public void Update(TelegramCollectionSchedule schedule)
    {
        _schedule = schedule;
        RaiseAll();
    }

    private void RaiseAll([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler is null) return;
        // 行列有派生属性（RunLabel / CanRun / CanEditActions），统一广播刷新。
        handler(this, new PropertyChangedEventArgs(string.Empty));
    }
}
