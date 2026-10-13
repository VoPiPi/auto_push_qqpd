using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class ComboBoxStyleTests
{
    [Fact]
    public void ComboBoxTemplate_SupportsTypedAddressesHistoryAndReadOnlyModes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            App? app = null;
            try
            {
                app = new App();
                app.InitializeComponent();
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

                var editor = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
                var selection = Assert.IsType<ContentPresenter>(combo.Template.FindName("ContentSite", combo));
                Assert.Equal(Visibility.Visible, editor.Visibility);
                Assert.Equal(Visibility.Collapsed, selection.Visibility);
                Assert.True(editor.Focusable);
                Assert.False(editor.IsReadOnly);
                Assert.False(combo.IsTabStop);
                Assert.InRange(editor.ActualHeight, 20, 26);

                editor.Text = "https://t.me/new_channel";
                DrainDispatcher();
                Assert.Equal(editor.Text, combo.Text);
                combo.SelectedIndex = 0;
                DrainDispatcher();
                Assert.Equal("https://t.me/history_channel", editor.Text);
                combo.Text = "@another_channel";
                DrainDispatcher();
                Assert.Equal("@another_channel", editor.Text);

                combo.IsReadOnly = true;
                Assert.True(editor.IsReadOnly);
                combo.IsEditable = false;
                Assert.Equal(Visibility.Collapsed, editor.Visibility);
                Assert.Equal(Visibility.Visible, selection.Visibility);
                Assert.True(combo.IsTabStop);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                app?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void DrainDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
