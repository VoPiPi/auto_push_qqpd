using QqChannelDesk.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace QqChannelDesk.Pages;

/// <summary>
/// 「TG采集」表格的一行：消息快照 + 多选状态 + 入库状态。数据仅存于内存，不落库。
/// </summary>
public sealed class TelegramTempItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status = "待处理";
    private long? _materialId;
    private string _materialDirectory = "";

    public TelegramTempItem(TelegramChannelMessage message)
    {
        Message = message;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private int _displayIndex;

    public TelegramChannelMessage Message { get; }

    /// <summary>表格里的展示序号，随列表排序重新编号。</summary>
    public int DisplayIndex
    {
        get => _displayIndex;
        set
        {
            if (_displayIndex == value) return;
            _displayIndex = value;
            OnPropertyChanged();
        }
    }

    public string Url => Message.Url;

    public string Title => Message.Title;

    public string TitleDisplay => string.IsNullOrWhiteSpace(Title) ? "（无标题）" : Title;

    public string Text => Message.Text;

    public DateTimeOffset? PublishedAt => Message.PublishedAt;

    public string PublishedAtDisplay => Message.PublishedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "-";

    public string Channel => TelegramCollectorService.ReadMessageChannel(Message.Url);

    public string Summary
    {
        get
        {
            var text = Flatten(Message.Text);
            if (text.Length == 0) return "（无正文）";
            return text.Length > 80 ? text[..80] + "..." : text;
        }
    }

    public string MediaSummary
    {
        get
        {
            var images = Message.Media.Count(media => media.Kind == "image");
            var videos = Message.Media.Count(media => media.Kind == "video");
            if (images == 0 && videos == 0) return "-";
            var parts = new List<string>();
            if (images > 0) parts.Add($"图 {images}");
            if (videos > 0) parts.Add($"视 {videos}");
            return string.Join(" · ", parts);
        }
    }

    public string StatusLabel => _status;

    /// <summary>只有待处理的行可以加入素材仓库，已入库的行禁用按钮防止重复入库。</summary>
    public bool CanMaterialize => _status == "待处理";

    public long? MaterialId => _materialId;

    public string MaterialIdDisplay => _materialId?.ToString() ?? "";

    public string MaterialDirectory => _materialDirectory;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public void MarkMaterialized(long materialId, string directory)
    {
        _materialId = materialId;
        _materialDirectory = directory;
        _status = "已入素材库";
        _isSelected = false;
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(CanMaterialize));
        OnPropertyChanged(nameof(MaterialIdDisplay));
        OnPropertyChanged(nameof(MaterialDirectory));
        OnPropertyChanged(nameof(IsSelected));
    }

    /// <summary>把正文里的换行压平成空格，方便在表格单元格里展示。</summary>
    private static string Flatten(string text)
    {
        var flattened = text.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
        return flattened.Trim();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
