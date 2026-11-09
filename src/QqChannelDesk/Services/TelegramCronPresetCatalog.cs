namespace QqChannelDesk.Services;

public sealed record TelegramCronPreset(string Name, string Expression);

public static class TelegramCronPresetCatalog
{
    public const string CustomName = "自定义";

    public static IReadOnlyList<TelegramCronPreset> Options { get; } =
    [
        new("每 10 分钟", "*/10 * * * *"),
        new("每 15 分钟", "*/15 * * * *"),
        new("每 30 分钟", "*/30 * * * *"),
        new("每 60 分钟", "0 * * * *"),
        new("每天凌晨 2 点", "0 2 * * *"),
        new("工作日早上 9 点", "0 9 * * 1-5"),
        new("每周一早上 9 点", "0 9 * * 1"),
        new("每月 1 日早上 9 点", "0 9 1 * *"),
        new(CustomName, "")
    ];

    public static TelegramCronPreset Find(string? expression)
    {
        var normalized = NormalizeExpression(expression);
        return Options.FirstOrDefault(item =>
            item.Expression.Length > 0 &&
            string.Equals(item.Expression, normalized, StringComparison.Ordinal))
            ?? Options[^1];
    }

    public static string NormalizeExpression(string? expression)
    {
        var text = expression?.Trim() ?? "";
        if (TelegramCronSchedule.TryParse(text, out var parsed, out _))
            return parsed.Expression;
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
