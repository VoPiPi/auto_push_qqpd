namespace QqChannelDesk.Services;

public sealed record PublishHistoryFilterCriteria(
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    FeedType? FeedType = null,
    string KeywordQuery = "");

public static class PublishHistoryFilter
{
    public static IReadOnlyList<PublishRecord> Apply(
        IEnumerable<PublishRecord> records,
        PublishHistoryFilterCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(criteria);

        var startDate = criteria.StartDate?.Date;
        var endDate = criteria.EndDate?.Date;
        if (startDate.HasValue && endDate.HasValue && startDate.Value > endDate.Value)
        {
            return [];
        }

        var keywordQuery = criteria.KeywordQuery.Trim();
        return records
            .Where(record =>
            {
                var publishedDate = record.StartedAt.ToLocalTime().Date;
                if (startDate.HasValue && publishedDate < startDate.Value) return false;
                if (endDate.HasValue && publishedDate > endDate.Value) return false;
                if (criteria.FeedType.HasValue && record.FeedType != criteria.FeedType.Value) return false;
                if (keywordQuery.Length > 0 &&
                    !Contains(record.Title, keywordQuery) &&
                    !Contains(record.Summary, keywordQuery))
                {
                    return false;
                }

                return true;
            })
            .ToArray();
    }

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);
}
