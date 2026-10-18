using QqChannelDesk.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using QqChannelDesk;

namespace QqChannelDesk.Pages;

public sealed class ProxyNodeRow : INotifyPropertyChanged
{
    private bool _isSelected;
    public ProxyNodeRow(ProxyNode node)
    {
        Node = node;
        IsSelected = false;
    }

    public ProxyNode Node { get; private set; }
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnChanged(); } }
    public string Address => Node.Address;
    public string Scheme => Node.Scheme.ToUpperInvariant();
    public string Host => Node.Host;
    public int Port => Node.Port;
    public string ExitIp => string.IsNullOrWhiteSpace(Node.ExitIp) ? "-" : Node.ExitIp;
    public string Country => FirstKnown(Node.SourceCountry, Node.CountryName, Node.CountryCode) ?? "未知";
    public string Status => Node.IsAlive ? "可用" : string.IsNullOrWhiteSpace(Node.LastCheckError) ? "未测" : "不可用";
    public string TgStatus => Node.TgAccessible is true ? $"可访问 ({Node.LastTgMessageCount})" : Node.TgAccessible is false ? "不可访问" : "未验证";
    public Brush StatusBackground => Node.IsAlive ? UiStatusBrushes.SuccessSoft : Node.LastCheckedAt is null ? UiStatusBrushes.WarningSoft : UiStatusBrushes.DangerSoft;
    public Brush StatusForeground => Node.IsAlive ? UiStatusBrushes.Success : Node.LastCheckedAt is null ? UiStatusBrushes.Warning : UiStatusBrushes.Danger;
    public Brush TgStatusBackground => Node.TgAccessible is true ? UiStatusBrushes.SuccessSoft : Node.TgAccessible is false ? UiStatusBrushes.DangerSoft : UiStatusBrushes.NeutralSoft;
    public Brush TgStatusForeground => Node.TgAccessible is true ? UiStatusBrushes.Success : Node.TgAccessible is false ? UiStatusBrushes.Danger : UiStatusBrushes.Neutral;
    public string LastChecked => Node.LastCheckedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "-";
    public string Error => string.IsNullOrWhiteSpace(Node.LastCheckError) ? Node.LastTgCheckError : Node.LastCheckError;
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
