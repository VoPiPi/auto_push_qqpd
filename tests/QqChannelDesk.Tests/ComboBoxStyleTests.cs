using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace QqChannelDesk.Tests;

// 本机环境下 Application 在每个测试方法结束后会被系统消息置为关闭状态（存量问题），
// 因此 UI 场景由 CollectionWorkspaceUiTests 的聚合测试统一调用，不单独作为测试方法。
public static class ComboBoxStyleTests
{
    internal static void ComboBoxTemplate_SupportsTypedAddressesHistoryAndReadOnlyModes(string? progressLog = null)
    {
            void Mark(string step)
            {
                if (progressLog is not null)
                    File.AppendAllText(progressLog, $"{DateTime.Now:HH:mm:ss.fff} combo: {step}{Environment.NewLine}");
            }
            var app = WpfTestApplication.GetOrCreate();
            Mark("get app");
            var combo = new ComboBox
            {
                Style = (Style)app.FindResource(typeof(ComboBox)),
                IsEditable = true,
                Width = 480,
                Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            combo.Items.Add("https://t.me/history_channel");
            combo.ApplyTemplate();
            combo.Measure(new Size(480, 32));
            combo.Arrange(new Rect(0, 0, 480, 32));
            Mark("arranged");

            var editor = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
            var selection = Assert.IsType<ContentPresenter>(combo.Template.FindName("ContentSite", combo));
            Assert.Equal(Visibility.Visible, editor.Visibility);
            Assert.Equal(Visibility.Collapsed, selection.Visibility);
            Assert.True(editor.Focusable);
            Assert.False(editor.IsReadOnly);
            Assert.False(combo.IsTabStop);
            Assert.InRange(editor.ActualHeight, 20, 26);
            Mark("initial asserts done");

            Mark("before editor.Text set");
            editor.Text = "https://t.me/new_channel";
            Mark("after editor.Text set");
            DrainDispatcher();
            Mark("drain1");
            Assert.Equal(editor.Text, combo.Text);
            combo.SelectedIndex = 0;
            DrainDispatcher();
            Mark("drain2");
            Assert.Equal("https://t.me/history_channel", editor.Text);
            combo.Text = "@another_channel";
            DrainDispatcher();
            Mark("drain3");
            Assert.Equal("@another_channel", editor.Text);
            Mark("done");

            combo.IsReadOnly = true;
            Assert.True(editor.IsReadOnly);
            combo.IsEditable = false;
            Assert.Equal(Visibility.Collapsed, editor.Visibility);
            Assert.Equal(Visibility.Visible, selection.Visibility);
            Assert.True(combo.IsTabStop);
    }

    private static void DrainDispatcher()
    {
        // 同线程 Invoke 走快速路径直接执行，不经消息泵；PushFrame 泵可能等不到 Background 而卡死。
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }
}
