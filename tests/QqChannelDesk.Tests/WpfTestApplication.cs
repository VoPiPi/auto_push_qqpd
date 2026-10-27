using System.Windows;

namespace QqChannelDesk.Tests;

internal static class WpfTestApplication
{
    // AppDomain 只允许存在一个 Application 实例；xUnit 并行运行多个 UI 测试类时
    // 可能已有别的线程创建过，必须复用 Application.Current 而不是再 new 一个。
    private static readonly object Gate = new();
    private static App? _application;

    public static App GetOrCreate()
    {
        lock (Gate)
        {
            if (Application.Current is App existing) return existing;
            if (_application is not null) return _application;
            _application = new App();
            _application.InitializeComponent();
            return _application;
        }
    }
}
