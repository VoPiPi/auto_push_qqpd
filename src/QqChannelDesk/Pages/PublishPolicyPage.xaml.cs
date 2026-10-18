using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QqChannelDesk.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace QqChannelDesk.Pages;

/// <summary>
/// 发布策略页：管理自动排期的版块策略和频道预算。
/// 页面只读写本地 SQLite；频道名称属于展示补充，缓存不可用时回退显示编号。
/// </summary>
public partial class PublishPolicyPage : UserControl
{
    private readonly PublishPolicyStore _store;
    private readonly ChannelSyncService _channelSync;
    private readonly Dictionary<string, string> _guildNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _channelNames = new(StringComparer.Ordinal);
    private CancellationTokenSource? _pageLifetimeCts;
    private int _loadVersion;
    private IReadOnlyList<PublishPolicy> _policies = [];
    private IReadOnlyList<PublishGuildBudget> _budgets = [];

    public PublishPolicyPage(PublishPolicyStore store, ChannelSyncService channelSync)
    {
        InitializeComponent();
        _store = store;
        _channelSync = channelSync;
        Loaded += (_, _) => StartPageLifetime();
        Unloaded += (_, _) => EndPageLifetime();
    }

    private CancellationToken CurrentPageToken =>
        _pageLifetimeCts?.Token ?? new CancellationToken(canceled: true);

    private bool IsPageActive(CancellationToken token) =>
        IsLoaded
        && !token.IsCancellationRequested
        && _pageLifetimeCts is { IsCancellationRequested: false }
        && _pageLifetimeCts.Token == token;

    private void StartPageLifetime()
    {
        var previous = _pageLifetimeCts;
        var lifetime = new CancellationTokenSource();
        _pageLifetimeCts = lifetime;
        previous?.Cancel();

        UiAsync.Observe("发布策略初始化", LoadPoliciesAsync(lifetime.Token));
    }

    private void EndPageLifetime()
    {
        _loadVersion++;
        var lifetime = _pageLifetimeCts;
        _pageLifetimeCts = null;
        lifetime?.Cancel();
    }

    public Task LoadPoliciesAsync() => LoadPoliciesAsync(CurrentPageToken);

    private async Task LoadPoliciesAsync(CancellationToken cancellationToken)
    {
        if (!IsPageActive(cancellationToken)) return;
        var version = ++_loadVersion;
        StatusText.Text = "正在读取发布策略…";
        StatusText.Foreground = (Brush)FindResource("Muted");
        HideNameHint();

        try
        {
            _policies = await _store.GetPoliciesAsync(cancellationToken);
            _budgets = await _store.GetGuildBudgetsAsync(cancellationToken);
            if (!IsPageActive(cancellationToken) || version != _loadVersion) return;

            // 先用编号渲染一次，让数据尽快可见；名称解析可能触发频道同步。
            Render();

            await RefreshNameCacheAsync(cancellationToken);
            if (!IsPageActive(cancellationToken) || version != _loadVersion) return;
            Render();
            LastRefreshText.Text = $"刷新于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsPageActive(cancellationToken) || version != _loadVersion) return;
            _policies = [];
            _budgets = [];
            Render();
            StatusText.Text = $"读取发布策略失败：{CliDiagnostics.Sanitize(ex.Message)}";
            StatusText.Foreground = UiStatusBrushes.Danger;
            LastRefreshText.Text = "刷新未完成";
        }
    }

    /// <summary>
    /// 尽力解析频道和版块名称。任何失败都只降级为编号显示，不能影响策略页本身。
    /// </summary>
    private async Task RefreshNameCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            var guilds = await _channelSync.GetCachedGuildsAsync(cancellationToken);
            foreach (var guild in guilds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(guild.Name)) _guildNames[guild.Id] = guild.Name;
                try
                {
                    var channels = await _channelSync.GetCachedChannelsAsync(guild.Id, cancellationToken);
                    foreach (var channel in channels)
                    {
                        if (!string.IsNullOrWhiteSpace(channel.Name))
                            _channelNames[ChannelKey(guild.Id, channel.Id)] = channel.Name;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    // 单个频道的版块读取失败时保留已有名称，行内继续显示编号。
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ShowNameHint($"频道名称暂不可用，表格先显示频道和版块编号：{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }

    private void Render()
    {
        var policyRows = new List<PublishPolicyRow>(_policies.Count);
        foreach (var policy in _policies)
        {
            policyRows.Add(new PublishPolicyRow(
                policy,
                ResolveGuildDisplay(policy.GuildId),
                ResolveChannelDisplay(policy.GuildId, policy.ChannelId)));
        }

        PoliciesGrid.ItemsSource = policyRows;
        PolicyCountText.Text = $"共 {policyRows.Count} 条策略";
        PoliciesEmptyState.Visibility = policyRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var budgetRows = new List<PublishGuildBudgetRow>(_budgets.Count);
        foreach (var budget in _budgets)
            budgetRows.Add(new PublishGuildBudgetRow(budget, ResolveGuildDisplay(budget.GuildId)));

        BudgetsGrid.ItemsSource = budgetRows;
        BudgetCountText.Text = $"共 {budgetRows.Count} 条预算";
        BudgetsEmptyState.Visibility = budgetRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        StatusText.Text = $"版块策略 {policyRows.Count} 条，频道预算 {budgetRows.Count} 条";
        StatusText.Foreground = (Brush)FindResource("Muted");
    }

    private string ResolveGuildDisplay(string guildId) =>
        _guildNames.TryGetValue(guildId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? $"{name}（{guildId}）"
            : guildId;

    private string ResolveChannelDisplay(string guildId, string channelId) =>
        _channelNames.TryGetValue(ChannelKey(guildId, channelId), out var name) && !string.IsNullOrWhiteSpace(name)
            ? $"{name}（{channelId}）"
            : channelId;

    private static string ChannelKey(string guildId, string channelId) => $"{guildId}|{channelId}";

    private void ShowNameHint(string message)
    {
        NameHintText.Text = message;
        NameHintBorder.Visibility = Visibility.Visible;
    }

    private void HideNameHint()
    {
        NameHintText.Text = "";
        NameHintBorder.Visibility = Visibility.Collapsed;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("刷新发布策略", () => LoadPoliciesAsync(CurrentPageToken));

    private void NewPolicyButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("新增发布策略", async () =>
        {
            var token = CurrentPageToken;
            if (!IsPageActive(token)) return;
            var dialog = new PublishPolicyEditorWindow(_channelSync) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Policy is null) return;
            await SavePolicyAsync(dialog.Policy, token);
        });

    private void EditPolicyButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("编辑发布策略", async () =>
        {
            var token = CurrentPageToken;
            if (!IsPageActive(token)) return;
            if ((sender as FrameworkElement)?.DataContext is not PublishPolicyRow row) return;
            var dialog = new PublishPolicyEditorWindow(_channelSync, row.Policy.ToDraft()) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Policy is null) return;
            await SavePolicyAsync(dialog.Policy, token);
        });

    private void TogglePolicyButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("切换发布策略状态", async () =>
        {
            var token = CurrentPageToken;
            if (!IsPageActive(token)) return;
            if ((sender as FrameworkElement)?.DataContext is not PublishPolicyRow row) return;
            await SavePolicyAsync(row.Policy.ToDraft() with { Enabled = !row.Policy.Enabled }, token);
        });

    private void DeletePolicyButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("删除发布策略", async () =>
        {
            var token = CurrentPageToken;
            if (!IsPageActive(token)) return;
            if ((sender as FrameworkElement)?.DataContext is not PublishPolicyRow row) return;
            var owner = Window.GetWindow(this);
            if (MessageBox.Show(owner,
                    $"确定删除“{row.TargetDisplay}”的发布策略吗？\n\n删除后该版块的新素材不再自动排队，已排期的任务保留。",
                    "删除发布策略", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                var deleted = await _store.DeletePolicyAsync(row.Policy.GuildId, row.Policy.ChannelId, token);
                if (!IsPageActive(token)) return;
                if (!deleted)
                {
                    MessageBox.Show(owner, "策略已被其他操作删除，请刷新后查看。", "未能删除策略", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                await LoadPoliciesAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (IsPageActive(token))
                    MessageBox.Show(owner, $"未能删除策略：{CliDiagnostics.Sanitize(ex.Message)}", "未能删除策略", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });

    private async Task SavePolicyAsync(PublishPolicyDraft draft, CancellationToken token)
    {
        try
        {
            await _store.SavePolicyAsync(draft, token);
            if (!IsPageActive(token)) return;
            await LoadPoliciesAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsPageActive(token))
                MessageBox.Show(Window.GetWindow(this), $"未能保存发布策略：{CliDiagnostics.Sanitize(ex.Message)}", "未能保存", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void NewBudgetButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("新增频道预算", async () =>
        {
            var token = CurrentPageToken;
            if (!IsPageActive(token)) return;
            var dialog = new PublishGuildBudgetEditorWindow(_channelSync) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Budget is null) return;
            await SaveBudgetAsync(dialog.Budget, token);
        });

    private void EditBudgetButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("编辑频道预算", async () =>
        {
            var token = CurrentPageToken;
            if (!IsPageActive(token)) return;
            if ((sender as FrameworkElement)?.DataContext is not PublishGuildBudgetRow row) return;
            var dialog = new PublishGuildBudgetEditorWindow(_channelSync, row.Budget.ToDraft()) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Budget is null) return;
            await SaveBudgetAsync(dialog.Budget, token);
        });

    private void ToggleBudgetButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("切换频道预算状态", async () =>
        {
            var token = CurrentPageToken;
            if (!IsPageActive(token)) return;
            if ((sender as FrameworkElement)?.DataContext is not PublishGuildBudgetRow row) return;
            await SaveBudgetAsync(row.Budget.ToDraft() with { Enabled = !row.Budget.Enabled }, token);
        });

    private void DeleteBudgetButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("删除频道预算", async () =>
        {
            var token = CurrentPageToken;
            if (!IsPageActive(token)) return;
            if ((sender as FrameworkElement)?.DataContext is not PublishGuildBudgetRow row) return;
            var owner = Window.GetWindow(this);
            if (MessageBox.Show(owner,
                    $"确定删除“{row.GuildDisplay}”的频道预算吗？\n\n删除后该频道只受各版块策略的每日额度约束。",
                    "删除频道预算", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                var deleted = await _store.DeleteGuildBudgetAsync(row.Budget.GuildId, token);
                if (!IsPageActive(token)) return;
                if (!deleted)
                {
                    MessageBox.Show(owner, "频道预算已被其他操作删除，请刷新后查看。", "未能删除预算", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                await LoadPoliciesAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (IsPageActive(token))
                    MessageBox.Show(owner, $"未能删除预算：{CliDiagnostics.Sanitize(ex.Message)}", "未能删除预算", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });

    private async Task SaveBudgetAsync(PublishGuildBudgetDraft draft, CancellationToken token)
    {
        try
        {
            await _store.SaveGuildBudgetAsync(draft, token);
            if (!IsPageActive(token)) return;
            await LoadPoliciesAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsPageActive(token))
                MessageBox.Show(Window.GetWindow(this), $"未能保存频道预算：{CliDiagnostics.Sanitize(ex.Message)}", "未能保存", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
