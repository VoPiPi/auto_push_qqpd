using System.Windows;
using System.Windows.Media;
using System.Globalization;
using System.Windows.Data;

namespace QqChannelDesk;

/// <summary>统一页面状态反馈和表格状态胶囊使用的语义色。</summary>
public static class UiStatusBrushes
{
    public static Brush Success => Find("Success", Color.FromRgb(0x14, 0x7D, 0x69));
    public static Brush SuccessSoft => Find("SuccessSoft", Color.FromRgb(0xE5, 0xF3, 0xEE));
    public static Brush Warning => Find("Warn", Color.FromRgb(0xA9, 0x5F, 0x13));
    public static Brush WarningSoft => Find("WarnSoft", Color.FromRgb(0xFF, 0xF3, 0xE2));
    public static Brush Danger => Find("Danger", Color.FromRgb(0xB5, 0x3D, 0x3D));
    public static Brush DangerSoft => Find("DangerSoft", Color.FromRgb(0xFC, 0xEC, 0xEC));
    public static Brush Info => Find("Info", Color.FromRgb(0x31, 0x5F, 0x78));
    public static Brush Neutral => Find("Neutral", Color.FromRgb(0x69, 0x73, 0x6E));
    public static Brush NeutralSoft => Find("NeutralSoft", Color.FromRgb(0xEE, 0xF1, 0xEF));

    private static Brush Find(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}

/// <summary>
/// Maps status labels used by the different tables to the same semantic palette.
/// Keeping this mapping in one place prevents a status from receiving different
/// colors when it appears in materials, schedules, history, or TG collection.
/// </summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public bool Soft { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value?.ToString()?.Trim() ?? string.Empty;
        var tone = ResolveTone(status);
        return tone switch
        {
            StatusTone.Success => Soft ? UiStatusBrushes.SuccessSoft : UiStatusBrushes.Success,
            StatusTone.Warning => Soft ? UiStatusBrushes.WarningSoft : UiStatusBrushes.Warning,
            StatusTone.Danger => Soft ? UiStatusBrushes.DangerSoft : UiStatusBrushes.Danger,
            StatusTone.Info => Soft ? Find("InfoSoft", UiStatusBrushes.Info) : UiStatusBrushes.Info,
            _ => Soft ? UiStatusBrushes.NeutralSoft : UiStatusBrushes.Neutral
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static StatusTone ResolveTone(string status)
    {
        if (status.Contains("失败", StringComparison.Ordinal) ||
            status.Contains("不可用", StringComparison.Ordinal) ||
            status.Contains("不可访问", StringComparison.Ordinal) ||
            status.Contains("逾期", StringComparison.Ordinal) ||
            status.Contains("删除", StringComparison.Ordinal))
            return StatusTone.Danger;

        if (status.Contains("成功", StringComparison.Ordinal) ||
            status.Contains("已发布", StringComparison.Ordinal) ||
            status.Contains("可用", StringComparison.Ordinal) ||
            status.Contains("可访问", StringComparison.Ordinal) ||
            status.Contains("保留", StringComparison.Ordinal) ||
            status.Contains("转草稿", StringComparison.Ordinal))
            return StatusTone.Success;

        if (status.Contains("处理中", StringComparison.Ordinal) ||
            status.Contains("执行中", StringComparison.Ordinal) ||
            status.Contains("发布中", StringComparison.Ordinal) ||
            status.Contains("已暂停", StringComparison.Ordinal) ||
            status.Contains("待核实", StringComparison.Ordinal) ||
            status.Contains("未测", StringComparison.Ordinal))
            return StatusTone.Warning;

        if (status.Contains("待发布", StringComparison.Ordinal) ||
            status.Contains("等待执行", StringComparison.Ordinal) ||
            status.Contains("待处理", StringComparison.Ordinal) ||
            status.Contains("未发布", StringComparison.Ordinal))
            return StatusTone.Info;

        return StatusTone.Neutral;
    }

    private static Brush Find(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private enum StatusTone
    {
        Success,
        Warning,
        Danger,
        Info,
        Neutral
    }
}
