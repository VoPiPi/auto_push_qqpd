using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

namespace QqChannelDesk.Tests;

internal static class WpfTestApplication
{
    // AppDomain 只允许存在一个 Application 实例；xUnit 并行运行多个 UI 测试类时
    // 可能已有别的线程创建过，必须复用 Application.Current 而不是再 new 一个。
    // Application 和所有 WPF 测试体都必须运行在同一个常驻 STA 线程上：
    // 1) 测试线程退出会关闭其 Dispatcher，Application 随之进入“正在关闭”状态，
    //    后续测试的 LoadComponent 全部失败；
    // 2) StaticResource 解析有线程亲和性，在别的线程创建窗口会找不到 App 资源。
    private static readonly object Gate = new();
    private static App? _application;
    internal static string? LastExitStack;
    private static readonly System.Reflection.FieldInfo? IsShuttingDownField =
        typeof(Application).GetField("_isShuttingDown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

    public static App GetOrCreate()
    {
        lock (Gate)
        {
            if (_application is { Dispatcher.HasShutdownStarted: false }) return _application;
            // 服务器/远程会话没有可用 GPU，WPF 渲染线程失败会把 Application 置为关闭状态；
            // 测试只用布局与资源，强制软件渲染。
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                var app = new App();
                app.InitializeComponent();
                // 测试进程没有真正的主窗口：WPF 会把首个 Show 的窗口当作 MainWindow，
                // 测试结束关闭它会触发 Application 关闭，必须改成显式关闭模式。
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                // 本机（Windows Server / 远程会话）会向 testhost 进程广播 WM_QUERYENDSESSION，
                // WPF 默认响应该消息关闭 Application，导致同进程后续 UI 测试全部无法加载资源。
                // 测试进程里的 Application 只是资源宿主，不响应系统会话结束；真正关机时进程仍会被系统结束。
                app.SessionEnding += (_, args) => args.Cancel = true;
                _application = app;
                ready.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            ready.Wait();
            return _application;
        }
    }

    /// <summary>在常驻 UI 线程上同步执行测试体，异常原样抛回调用方。</summary>
    public static void Run(Action action)
    {
        var dispatcher = GetOrCreate().Dispatcher;
        if (LastExitStack is not null || IsShuttingDownField?.GetValue(null) is true)
            throw new InvalidOperationException($"WPF 测试 Application 已关闭。{DiagnoseState()}");
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static string? DiagnoseState() =>
        $"Session/Exit记录={LastExitStack ?? "无"}，标志位={IsShuttingDownField?.GetValue(null)}";

    internal static object? IsShuttingDownNow => IsShuttingDownField?.GetValue(null);
}
