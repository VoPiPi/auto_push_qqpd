using System.Globalization;
using QqChannelDesk.Services;

namespace QqChannelDesk.Pages;

/// <summary>
/// 版块发布策略在表格中的行视图模型：把策略记录和频道名称展示组合在一起，
/// 名称缓存不可用时显示编号，不改变底层记录。
/// </summary>
public sealed class PublishPolicyRow
{
    public PublishPolicyRow(PublishPolicy policy, string guildDisplay, string channelDisplay)
    {
        Policy = policy;
        GuildDisplay = guildDisplay;
        ChannelDisplay = channelDisplay;
    }

    public PublishPolicy Policy { get; }
    public string GuildDisplay { get; }
    public string ChannelDisplay { get; }
    public string TargetDisplay => $"{GuildDisplay} / {ChannelDisplay}";
    public string StatusLabel => Policy.EnabledLabel;
    public string ToggleLabel => Policy.Enabled ? "停用" : "启用";
    public string WindowDisplay => Policy.WindowDisplay;
    public string IntervalDisplay => Policy.IntervalDisplay;
    public string DailyLimitDisplay => $"{Policy.DailyLimit.ToString("N0", CultureInfo.InvariantCulture)} 条/日";
    public string ConcurrencyDisplay => $"{Policy.MaxConcurrency} 路";
    public string QueueLimitDisplay => $"{Policy.QueueLimit.ToString("N0", CultureInfo.InvariantCulture)} 条";
    public string UpdatedAtDisplay => Policy.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
