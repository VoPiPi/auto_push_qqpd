namespace QqChannelDesk.Services;

public sealed record TelegramDistributionRule(
    long Id,
    long ScheduleId,
    string MessageType,
    string Keyword,
    string GuildId,
    string GuildName,
    string ChannelId,
    string ChannelName,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string MessageTypeLabel => MessageType switch { "image" => "图片", "video" => "视频", _ => "文本" };
    public string TargetDisplay => string.IsNullOrWhiteSpace(GuildName) || string.IsNullOrWhiteSpace(ChannelName) ? "未指定" : $"{GuildName}/{ChannelName}";
    public string KeywordDisplay => string.IsNullOrWhiteSpace(Keyword) ? "全部" : Keyword;
}

public sealed record TelegramDistributionRuleDraft(
    string MessageType,
    string Keyword,
    string GuildId,
    string GuildName,
    string ChannelId,
    string ChannelName,
    int SortOrder = 0);

public static class TelegramDistributionRuleMatcher
{
    public static TelegramDistributionRule? Match(
        TelegramChannelMessage message,
        IReadOnlyList<TelegramDistributionRule> rules)
    {
        var type = ResolveMessageType(message);
        foreach (var rule in rules.OrderBy(item => item.SortOrder).ThenBy(item => item.Id))
        {
            if (!string.Equals(rule.MessageType, type, StringComparison.OrdinalIgnoreCase)) continue;
            if (MatchesKeyword($"{message.Title}\n{message.Text}", rule.Keyword)) return rule;
        }
        return null;
    }

    public static string ResolveMessageType(TelegramChannelMessage message)
    {
        if (message.Media.Any(item => string.Equals(item.Kind, "video", StringComparison.OrdinalIgnoreCase))) return "video";
        if (message.Media.Any(item => string.Equals(item.Kind, "image", StringComparison.OrdinalIgnoreCase))) return "image";
        return "text";
    }

    public static bool MatchesKeyword(string text, string? keyword)
    {
        var parts = TelegramCollectorService.SplitKeywords(keyword);
        return parts.Count == 0 || parts.Any(item => text.Contains(item, StringComparison.OrdinalIgnoreCase));
    }
}
