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

    /// <summary>
    /// 状态文案到语义色的统一映射。匹配顺序很重要：
    /// “不可用/不可访问/未启用”等否定词必须先于“可用/启用”判断，否则会被误判为成功色。
    /// </summary>
    private static StatusTone ResolveTone(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return StatusTone.Neutral;

        // 阻断性、需要立即处理的结果保留红色提醒。
        if (status.Contains("失败", StringComparison.Ordinal) ||
            status.Contains("不可用", StringComparison.Ordinal) ||
            status.Contains("不可访问", StringComparison.Ordinal) ||
            status.Contains("逾期", StringComparison.Ordinal) ||
            status.Contains("已删除", StringComparison.Ordinal) ||
            status.Contains("异常", StringComparison.Ordinal))
            return StatusTone.Danger;

        // 已停用、取消等非故障收尾状态使用灰色。
        if (status.Contains("未完成", StringComparison.Ordinal) ||
            status.Contains("已取消", StringComparison.Ordinal) ||
            status.Contains("已停用", StringComparison.Ordinal) ||
            status.Contains("未启用", StringComparison.Ordinal) ||
            status.Contains("未验证", StringComparison.Ordinal) ||
            status.Contains("忽略", StringComparison.Ordinal))
            return StatusTone.Neutral;

        // “部分成功”必须先于“成功”判断，否则会被误映射为绿色。
        if (status.Contains("部分成功", StringComparison.Ordinal) ||
            status.Contains("等待重试", StringComparison.Ordinal) ||
            status.Contains("处理中", StringComparison.Ordinal) ||
            status.Contains("执行中", StringComparison.Ordinal) ||
            status.Contains("发布中", StringComparison.Ordinal) ||
            status.Contains("运行中", StringComparison.Ordinal) ||
            status.Contains("已暂停", StringComparison.Ordinal) ||
            status.Contains("待核实", StringComparison.Ordinal) ||
            status.Contains("未测", StringComparison.Ordinal))
            return StatusTone.Warning;

        // 明确的成功、可用或已开启状态，使用正色。
        if (status.Contains("正常", StringComparison.Ordinal) ||
            status.Contains("成功", StringComparison.Ordinal) ||
            status.Contains("已发布", StringComparison.Ordinal) ||
            status.Contains("已启用", StringComparison.Ordinal) ||
            status.Contains("已入素材库", StringComparison.Ordinal) ||
            status.Contains("可用", StringComparison.Ordinal) ||
            status.Contains("可访问", StringComparison.Ordinal) ||
            status.Contains("保留", StringComparison.Ordinal) ||
            status.Contains("转草稿", StringComparison.Ordinal) ||
            status.Contains("通过", StringComparison.Ordinal))
            return StatusTone.Success;

        // 排队等待，使用中等强调程度。
        if (status.Contains("排队", StringComparison.Ordinal) ||
            status.Contains("待发布", StringComparison.Ordinal) ||
            status.Contains("等待发布", StringComparison.Ordinal) ||
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
