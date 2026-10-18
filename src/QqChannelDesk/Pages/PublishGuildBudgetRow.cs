using System.Globalization;
using QqChannelDesk.Services;

namespace QqChannelDesk.Pages;

/// <summary>频道预算在表格中的行视图模型。</summary>
public sealed class PublishGuildBudgetRow
{
    public PublishGuildBudgetRow(PublishGuildBudget budget, string guildDisplay)
    {
        Budget = budget;
        GuildDisplay = guildDisplay;
    }

    public PublishGuildBudget Budget { get; }
    public string GuildDisplay { get; }
    public string StatusLabel => Budget.EnabledLabel;
    public string ToggleLabel => Budget.Enabled ? "停用" : "启用";
    public string DailyLimitDisplay => $"{Budget.DailyLimit.ToString("N0", CultureInfo.InvariantCulture)} 条/日";
    public string UpdatedAtDisplay => Budget.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
