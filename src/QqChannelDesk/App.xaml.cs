using System.Windows;
using System.Windows.Threading;
using QqChannelDesk.Services;
using Application = System.Windows.Application;

namespace QqChannelDesk;

public partial class App : Application
{
    public App()
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
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
