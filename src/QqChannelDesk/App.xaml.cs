using System.Windows;
using System.Windows.Threading;
using QqChannelDesk.Services;
using Application = System.Windows.Application;

namespace QqChannelDesk;

public partial class App : Application
{
    private const string SingleInstanceApplicationId = "QqChannelDesk.Desktop";
    private SingleInstanceCoordinator? _singleInstanceCoordinator;

    public App()
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _singleInstanceCoordinator = new SingleInstanceCoordinator(SingleInstanceApplicationId);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.WriteException("初始化单实例保护失败", ex);
            MessageBox.Show(
                "程序无法建立单实例保护，为避免计划任务被重复执行，本次不会启动。\n\n请重启系统后重试。",
                "程序启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (!_singleInstanceCoordinator.TryStartPrimary(() =>
            {
                var mainWindow = new MainWindow();
                MainWindow = mainWindow;
                mainWindow.Show();
                _singleInstanceCoordinator.StartActivationListener(() =>
                    UiAsync.Post(
                        mainWindow.Dispatcher,
                        mainWindow.ActivateFromAnotherInstance,
                        DispatcherPriority.Send));
            }))
        {
            try
            {
                _singleInstanceCoordinator.NotifyPrimaryInstance();
                AppLogger.Instance.Debug("检测到程序已运行，已请求唤醒现有主窗口。");
            }
            finally
            {
                _singleInstanceCoordinator.Dispose();
                _singleInstanceCoordinator = null;
                Shutdown(0);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceCoordinator?.Dispose();
        _singleInstanceCoordinator = null;
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var terminating = e.Exception is not OperationCanceledException;
        // Only cancellation is an expected control-flow exception. Other UI exceptions may have
        // left the visual tree or application state inconsistent, so let WPF terminate normally.
        AppLogger.Instance.WriteException("UI 线程未处理异常", e.Exception, terminating);
        e.Handled = !terminating;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            AppLogger.Instance.WriteException("程序未处理异常", exception, e.IsTerminating);
        else
            AppLogger.Instance.Error($"程序未处理异常：{AppLogger.Redact(e.ExceptionObject?.ToString() ?? "未知异常")}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Instance.WriteException("后台 Task 未观察异常", e.Exception);
        e.SetObserved();
    }
}
