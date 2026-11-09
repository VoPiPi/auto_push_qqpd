using QqChannelDesk.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using QqChannelDesk;

namespace QqChannelDesk.Pages;

public sealed class ProxyNodeRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isBusy;
    public ProxyNodeRow(ProxyNode node)
    {
        Node = node;
        IsSelected = false;
    }

    public ProxyNode Node { get; private set; }
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnChanged(); } }
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnChanged();
            OnChanged(nameof(CanOperate));
        }
    }

    public bool CanOperate => !IsBusy;
    public string Address => Node.Address;
    public string Scheme => Node.Scheme.ToUpperInvariant();
    public string Host => Node.Host;
    public int Port => Node.Port;
    public string ExitIp => string.IsNullOrWhiteSpace(Node.ExitIp) ? "-" : Node.ExitIp;
    public string Country => FirstKnown(Node.SourceCountry, Node.CountryName, Node.CountryCode) ?? "未知";
    public string Status => Node.IsAlive ? "可用" : string.IsNullOrWhiteSpace(Node.LastCheckError) ? "未测" : "不可用";
    public string TgStatus => Node.TgAccessible is true ? "可访问" : Node.TgAccessible is false ? "不可访问" : "未验证";
    public bool IsInvalidForBatchDelete =>
        (Node.LastCheckedAt is not null && !Node.IsAlive) ||
        (Node.LastTgCheckedAt is not null && Node.TgAccessible is false);
    public Brush StatusBackground => Node.IsAlive ? UiStatusBrushes.SuccessSoft : Node.LastCheckedAt is null ? UiStatusBrushes.WarningSoft : UiStatusBrushes.DangerSoft;
    public Brush StatusForeground => Node.IsAlive ? UiStatusBrushes.Success : Node.LastCheckedAt is null ? UiStatusBrushes.Warning : UiStatusBrushes.Danger;
    public Brush TgStatusBackground => Node.TgAccessible is true ? UiStatusBrushes.SuccessSoft : Node.TgAccessible is false ? UiStatusBrushes.DangerSoft : UiStatusBrushes.NeutralSoft;
    public Brush TgStatusForeground => Node.TgAccessible is true ? UiStatusBrushes.Success : Node.TgAccessible is false ? UiStatusBrushes.Danger : UiStatusBrushes.Neutral;
    public string LastChecked => Node.LastCheckedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "-";
    public string LastResult
    {
        get
        {
            var healthAt = Node.LastCheckedAt;
            var tgAt = Node.LastTgCheckedAt;
            if (tgAt is not null && (healthAt is null || tgAt >= healthAt))
            {
                if (Node.TgAccessible is true) return "TG 验证成功：可访问。";
                return string.IsNullOrWhiteSpace(Node.LastTgCheckError) ? "TG 验证失败：不可访问。" : Node.LastTgCheckError;
            }
            if (healthAt is null) return "";
            if (Node.IsAlive) return "测活成功：节点可用。";
            return string.IsNullOrWhiteSpace(Node.LastCheckError) ? "测活失败：节点不可用。" : Node.LastCheckError;
        }
    }
    public long Id => Node.Id;

    public void Update(ProxyNode node)
    {
        Node = node;
        OnChanged(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string? FirstKnown(params string[] values) => values.FirstOrDefault(value =>
        !string.IsNullOrWhiteSpace(value) && !string.Equals(value.Trim(), "未知", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase));
}
