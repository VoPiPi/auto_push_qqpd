using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QqChannelDesk.Services;
using QqChannelDesk.Services.Collection;
using QqChannelDesk.Pages;
using QqChannelDesk.Pages.Collection;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class CollectionWorkspaceUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "QqChannelDeskCollectionUiTests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public CollectionWorkspaceUiTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "channels.db");
    }

    [Fact]
    public void CollectionWorkspace_ShowsThreePrimaryTabsAtMinimumWidth()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var app = WpfTestApplication.GetOrCreate();
                var context = new AccountContext();
                context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
                var content = new ContentLibraryStore(_databasePath, context);
                var sources = new CollectionSourceStore(_databasePath, context);
                var runs = new CollectionRunStore(_databasePath, context);
                var telegram = new TelegramCollectionStore(_databasePath, context);
                var scheduler = new CollectionSchedulerService(sources, runs, content, maxConcurrency: 1);
                var page = new ContentCollectionPage(
                    content,
                    new ProxyPoolStore(_databasePath),
                    telegram,
                    telegramCollectionScheduler: null,
                    channelSync: null,
                    telegramTransport: null,
                    sources,
                    runs,
                    scheduler,
                    telegramSourceAdapter: null);
                window = new Window
                {
                    Content = page,
                    Width = 960,
                    Height = 720,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                window.Show();
                DrainDispatcher();

                var visibleTabs = page.WorkspaceTabs.Items.OfType<TabItem>()
                    .Where(item => item.Visibility == Visibility.Visible)
                    .ToArray();
                Assert.Equal(4, visibleTabs.Length);
                Assert.Equal(["收件箱", "来源管理", "采集记录", "代理池"], visibleTabs.Select(item => item.Header?.ToString()));
                var filterItem = page.SourceFilterList.SelectedItem as SourceFilterItem;
                Assert.NotNull(filterItem);
                Assert.Equal(0, filterItem!.Id);
                Assert.StartsWith("全部来源", filterItem.Name, StringComparison.Ordinal);
                Assert.NotNull(page.SourcesHost.Content);
                Assert.NotNull(page.RunsHost.Content);
                Assert.True(page.ActualWidth >= 900);
                Assert.True(double.IsNaN(page.ActualWidth) == false);
                Assert.True(page.SourceFilterList.ActualWidth > 150);
                Assert.True(page.ItemsGrid.ActualWidth > 200);
                Assert.True(page.ItemContentBox.ActualWidth > 150);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void NewSourceWindow_InitializationDoesNotCrashOnSelectionChanged()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = WpfTestApplication.GetOrCreate();
                var window = new NewSourceWindow();
                Assert.Equal("rss", window.Draft.SourceType);
                var source = new CollectionSource(
                    1, "编辑来源", "web-column", "https://example.test/column", true, true, null,
                    "rule-first", 90, "", "{}", false, 12, "", "", null, null, null,
                    "healthy", "", "", "", null, DateTimeOffset.Now, DateTimeOffset.Now);
                var editWindow = new NewSourceWindow(source);
                Assert.Equal("90", editWindow.FrequencyBox.Text);
                Assert.Equal("12", editWindow.LimitBox.Text);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void NewSourceWindow_TelegramTypeHidesFormAndRequestsLegacyTab()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = WpfTestApplication.GetOrCreate();
                var window = new NewSourceWindow();
                var requested = 0;
                window.TgScheduleRequested += () => requested++;
                SelectType(window, "telegram");
                Assert.Equal(Visibility.Collapsed, window.FormPanel.Visibility);
                Assert.Equal(Visibility.Visible, window.TgHintPanel.Visibility);
                Assert.False(window.SaveButton.IsEnabled);
                window.TgJumpButton_Click(new Button(), new RoutedEventArgs());
                Assert.Equal(1, requested);

                SelectType(window, "rss");
                Assert.Equal(Visibility.Visible, window.FormPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, window.TgHintPanel.Visibility);
                Assert.True(window.SaveButton.IsEnabled);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void RunDetailWindow_ShowsTimelineAndDisablesActionsWithoutDependencies()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = WpfTestApplication.GetOrCreate();
                var record = new CollectionRunRecord(
                    1, 5, "manual", DateTimeOffset.Now.AddMinutes(-2), DateTimeOffset.Now.AddSeconds(-10), "succeeded",
                    8, 8, 1, 0, 0, 7, "", "", DateTimeOffset.Now);
                var row = new CollectionRunRow(record, "测试来源");
                var window = new RunDetailWindow(row);
                Assert.Contains("1. 批次开始", window.DetailBox.Text);
                Assert.Contains("2. 采集执行", window.DetailBox.Text);
                Assert.Contains("3. 批次结束", window.DetailBox.Text);
                Assert.False(window.RunAgainButton.IsEnabled);
                Assert.False(window.PauseSourceButton.IsEnabled);
                Assert.False(window.ViewItemsButton.IsEnabled);

                var telegramRecord = new CollectionRunRecord(
                    2, -3, "scheduled", DateTimeOffset.Now.AddMinutes(-1), null, "running",
                    0, 0, 0, 0, 0, 0, "", "", DateTimeOffset.Now);
                var telegramWindow = new RunDetailWindow(new CollectionRunRow(telegramRecord, "TG 来源"));
                Assert.False(telegramWindow.RunAgainButton.IsEnabled);
                Assert.False(telegramWindow.PauseSourceButton.IsEnabled);
                Assert.False(telegramWindow.ViewItemsButton.IsEnabled);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void TelegramScheduleRefresh_AppliesToScheduleListAfterDebounce()
    {
        // 回归：防抖刷新使用链接 token，IsPageActive 若要求 token 实例相等会把刷新全部吞掉，
        // 导致来源管理启停 TG 来源后 TG 定时页签不更新。
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                _ = WpfTestApplication.GetOrCreate();
                var context = new AccountContext();
                context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
                var telegramStore = new TelegramCollectionStore(_databasePath, context);
                var scheduleId = telegramStore.SaveAsync(new TelegramCollectionScheduleDraft(
                    null, "https://t.me/s/refresh-channel", "refresh-channel", 0, true,
                    WebProxySelection.Automatic, 20, "")).GetAwaiter().GetResult();
                var page = new ContentCollectionPage(
                    new ContentLibraryStore(_databasePath, context),
                    new ProxyPoolStore(_databasePath),
                    telegramStore,
                    telegramCollectionScheduler: null,
                    channelSync: null);
                window = new Window
                {
                    Content = page,
                    Width = 1000,
                    Height = 720,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                window.Show();
                DrainDispatcher();
                // 页面在主窗口里由数据库初始化完成后激活；测试直接触发激活。
                page.OnDatabaseReady();
                DrainDispatcher();

                // 与来源管理启停同一条数据路径：直接更新旧表，再触发防抖刷新链路。
                telegramStore.SetEnabledAsync(scheduleId, false).GetAwaiter().GetResult();
                typeof(ContentCollectionPage)
                    .GetMethod("ScheduleTelegramSchedulesRefresh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(page, null);
                // 防抖 200ms + 数据库线程池往返需要多轮消息泵才能走完整条异步链。
                for (var i = 0; i < 12; i++)
                {
                    Thread.Sleep(60);
                    DrainDispatcher();
                }

                var row = Assert.IsType<TelegramScheduleRow>(page.TgSchedulesGrid.Items.Cast<object>().Single());
                Assert.False(row.Schedule.Enabled);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void SelectType(NewSourceWindow window, string tag)
    {
        var item = window.TypeBox.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag?.ToString(), tag));
        window.TypeBox.SelectedItem = item;
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}
