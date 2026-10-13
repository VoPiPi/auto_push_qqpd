using QqChannelDesk.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

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
    public string SourceCountry => string.IsNullOrWhiteSpace(Node.SourceCountry) ? "未知" : Node.SourceCountry;
    public string ExitIp => string.IsNullOrWhiteSpace(Node.ExitIp) ? "-" : Node.ExitIp;
    public string Country => string.IsNullOrWhiteSpace(Node.CountryName) ? "未知" : Node.CountryName;
    public string Status => Node.IsAlive ? "可用" : string.IsNullOrWhiteSpace(Node.LastCheckError) ? "未测" : "不可用";
    public string TgStatus => Node.TgAccessible is true ? $"可访问 ({Node.LastTgMessageCount})" : Node.TgAccessible is false ? "不可访问" : "未验证";
    public Brush StatusBackground => Node.IsAlive ? Brushes.Honeydew : Node.LastCheckedAt is null ? Brushes.LemonChiffon : Brushes.MistyRose;
    public Brush StatusForeground => Node.IsAlive ? Brushes.SeaGreen : Node.LastCheckedAt is null ? Brushes.DarkGoldenrod : Brushes.Firebrick;
    public Brush TgStatusBackground => Node.TgAccessible is true ? Brushes.Honeydew : Node.TgAccessible is false ? Brushes.MistyRose : Brushes.GhostWhite;
    public Brush TgStatusForeground => Node.TgAccessible is true ? Brushes.SeaGreen : Node.TgAccessible is false ? Brushes.Firebrick : Brushes.SlateGray;
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
}
