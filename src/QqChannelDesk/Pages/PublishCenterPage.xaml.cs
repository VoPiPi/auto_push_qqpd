using QqChannelDesk.Services;

namespace QqChannelDesk.Pages;

/// <summary>
/// 发布中心：聚合计划队列、自动规则和发布记录。
/// 子页面仍然独立加载，避免切到发布中心时一次性读取三张表。
/// </summary>
public partial class PublishCenterPage : System.Windows.Controls.UserControl
{
    private readonly PublishSchedulePage _schedulePage;
    private readonly PublishPolicyPage _publishPolicyPage;
    private readonly HistoryPage _historyPage;

    public PublishCenterPage(
        PublishSchedulePage schedulePage,
        PublishPolicyPage publishPolicyPage,
        HistoryPage historyPage)
    {
        _schedulePage = schedulePage;
        _publishPolicyPage = publishPolicyPage;
        _historyPage = historyPage;

        InitializeComponent();

        PlanQueueHost.Content = _schedulePage;
        AutoRulesHost.Content = _publishPolicyPage;
        PublishRecordsHost.Content = _historyPage;

        Loaded += (_, _) => RefreshSelectedTabIfNeeded();
    }

    private void PublishCenterTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.Source == PublishCenterTabs) RefreshSelectedTabIfNeeded();
    }

    private void RefreshSelectedTabIfNeeded()
    {
        // 计划队列和自动规则在 Loaded 时自己刷新；发布记录不会在 Loaded 自动读库。
        if (IsLoaded && PublishCenterTabs.SelectedItem == PublishRecordsTabItem)
            UiAsync.Run("刷新发布记录", _historyPage.LoadRecordsAsync);
    }
}
