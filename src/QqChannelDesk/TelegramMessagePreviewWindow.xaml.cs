using QqChannelDesk.Pages;
using QqChannelDesk.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QqChannelDesk;

public partial class TelegramMessagePreviewWindow : Window
{
    private readonly TelegramTempItem _item;
    private readonly string _cookieHeader;
    private readonly Func<TelegramTempItem, Task<bool>> _draftAction;
    private readonly Action<TelegramTempItem> _deleteAction;
    private readonly TelegramCollectorService _collector = new();

    public TelegramMessagePreviewWindow(
        TelegramTempItem item,
        string cookieHeader,
        Func<TelegramTempItem, Task<bool>> draftAction,
        Action<TelegramTempItem> deleteAction)
    {
        InitializeComponent();
        _item = item;
        _cookieHeader = cookieHeader;
        _draftAction = draftAction;
        _deleteAction = deleteAction;
        Title = $"消息预览 · {item.Channel}";
        TitleText.Text = item.TitleDisplay;
        MetaText.Text = $"频道 {item.Channel} · 发布时间 {item.PublishedAtDisplay} · {item.MediaSummary}";
        LinkText.Text = item.Url.Length > 0 ? item.Url : "（无原帖链接）";
        ContentText.Text = item.Text.Length > 0 ? item.Text : "（无正文）";
        MediaHint.Text = item.Message.Media.Count == 0
            ? "该消息没有可下载的媒体文件。"
            : $"共 {item.Message.Media.Count} 个媒体引用；图片可加载预览，视频在加入素材库时下载。";
        LoadImagesButton.IsEnabled = item.Message.Media.Any(media => media.Kind == "image");
        DraftButton.IsEnabled = item.CanMaterialize;
    }

    private void LinkText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_item.Url.Length == 0) return;
        try
        {
            Clipboard.SetText(_item.Url);
            SetStatus("原帖链接已复制。", true);
        }
        catch (Exception ex)
        {
            SetStatus($"复制失败：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
    }

    private async void LoadImagesButton_Click(object sender, RoutedEventArgs e)
    {
        var images = _item.Message.Media.Where(media => media.Kind == "image").ToArray();
        if (images.Length == 0) return;
        LoadImagesButton.IsEnabled = false;
        SetStatus("正在下载图片预览…", false);
        try
        {
            var directory = TelegramCollectorService.BuildPreviewDirectory(
                AppContext.BaseDirectory, TelegramCollectorService.ReadMessageId(_item.Url));
            var download = await _collector.DownloadMediaAsync(images, _cookieHeader, cancellationToken: default, targetDirectory: directory);
            ImagePanel.Children.Clear();
            foreach (var path in download.Paths)
            {
                if (!File.Exists(path)) continue;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                ImagePanel.Children.Add(new Image
                {
                    Source = bitmap,
                    MaxHeight = 150,
                    MaxWidth = 220,
                    Margin = new Thickness(0, 0, 10, 10),
                    Stretch = Stretch.Uniform,
                    ToolTip = Path.GetFileName(path)
                });
            }
            SetStatus(download.Failed > 0
                ? $"已加载 {download.Paths.Count} 张图片，{download.Failed} 张下载失败。"
                : $"已加载 {download.Paths.Count} 张图片。", download.Failed == 0);
        }
        catch (Exception ex)
        {
            SetStatus($"图片加载失败：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        finally
        {
            LoadImagesButton.IsEnabled = true;
        }
    }

    private async void DraftButton_Click(object sender, RoutedEventArgs e)
    {
        DraftButton.IsEnabled = false;
        try
        {
            var drafted = await _draftAction(_item);
            if (drafted)
            {
                SetStatus(_item.MaterialIdDisplay.Length > 0
                    ? $"已加入素材仓库（编号 {_item.MaterialIdDisplay}）。"
                    : "已加入素材仓库。", true);
                DraftButton.IsEnabled = false;
            }
            else
            {
                DraftButton.IsEnabled = _item.CanMaterialize;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"加入素材仓库失败：{CliDiagnostics.Sanitize(ex.Message)}", false);
            DraftButton.IsEnabled = _item.CanMaterialize;
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        _deleteAction(_item);
        Close();
    }

    private void SetStatus(string message, bool succeeded)
    {
        StatusText.Text = message;
        StatusText.Foreground = succeeded ? UiStatusBrushes.Success : UiStatusBrushes.Warning;
    }
}
