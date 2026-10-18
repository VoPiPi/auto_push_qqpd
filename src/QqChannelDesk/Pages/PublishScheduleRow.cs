using QqChannelDesk.Services;
using System.ComponentModel;

namespace QqChannelDesk.Pages;

/// <summary>
/// 发布计划在表格中的行视图模型，承载选择状态和暂停/启动后的展示状态。
/// </summary>
public sealed class PublishScheduleRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public PublishScheduleRow(ScheduleExecutionRecord record) => Record = record;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ScheduleExecutionRecord Record { get; }
    public long Id => Record.Id;
    public long MaterialId => Record.MaterialId;
    public string TitleContentDisplay => Record.TitleContentDisplay;
    public string TypeLabel => Record.TypeLabel;
    public string TargetDisplay => Record.TargetDisplay;
    public string ScheduledAtDisplay => Record.ScheduledAtDisplay;
    public string PublishRecordDisplay => Record.PublishRecordDisplay;
    public bool IsPaused => Record.IsPaused;
    public bool CanToggle => Record.Status == ScheduleExecutionStatus.Pending;
    public bool CanDelete => Record.Status != ScheduleExecutionStatus.Running;
    public string ToggleLabel => IsPaused ? "启动" : "暂停";
    public string StatusLabel => Record.Status == ScheduleExecutionStatus.Pending && IsPaused
        ? "已暂停"
        : Record.StatusLabel;
    public string ErrorDisplay => Record.Status == ScheduleExecutionStatus.Pending && IsPaused
        ? "已暂停，等待手动启动"
        : Record.ErrorDisplay;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
