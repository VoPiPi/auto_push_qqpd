namespace QqChannelDesk.Services.Collection;

using System.Text.Json;

public interface ISourceCollector
{
    string SourceType { get; }

    Task<CollectionResult> CollectAsync(CollectionSource source, CancellationToken cancellationToken = default);
}

public sealed record CollectionItemDraft(
    string Title,
    string Content,
    string Url = "",
    string SourceItemId = "",
    IReadOnlyList<string>? MediaList = null,
    string ParsedBy = "rule",
    string OriginalContent = "",
    string CandidateContent = "")
{
    public IReadOnlyList<string> Media => MediaList ?? [];
}

public sealed record CollectionResult(
    bool Succeeded,
    string ErrorCode,
    string ErrorSummary,
    IReadOnlyList<CollectionItemDraft> Items,
    int FailedCount = 0)
{
    public static CollectionResult Success(IReadOnlyList<CollectionItemDraft> items, int failedCount = 0) =>
        new(true, "", "", items, failedCount);

    public static CollectionResult Failure(string errorCode, string errorSummary) =>
        new(false, errorCode, errorSummary, [], 0);
}

public sealed record CollectionRuleOptions(
    IReadOnlyList<string> IncludeKeywords,
    IReadOnlyList<string> ExcludeKeywords,
    IReadOnlyList<string> Categories,
    int? SinceHours)
{
    public static CollectionRuleOptions Parse(CollectionSource source)
    {
        var include = SplitKeywords(source.IncludeKeywords);
        var exclude = SplitKeywords(source.ExcludeKeywords);
        IReadOnlyList<string> categories = [];
        int? sinceHours = null;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(source.RuleConfigJson) ? "{}" : source.RuleConfigJson);
            if (document.RootElement.ValueKind is JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("categoryFilter", out var categoriesElement) &&
                    categoriesElement.ValueKind is JsonValueKind.Array)
                {
                    categories = categoriesElement.EnumerateArray()
                        .Where(item => item.ValueKind is JsonValueKind.String)
                        .Select(item => item.GetString() ?? "")
                        .Where(item => item.Length > 0)
                        .ToArray();
                }

                if (document.RootElement.TryGetProperty("sinceHours", out var sinceElement) &&
                    sinceElement.ValueKind is JsonValueKind.Number &&
                    sinceElement.TryGetInt32(out var parsedSince) &&
                    parsedSince > 0)
                {
                    sinceHours = parsedSince;
                }
            }
        }
        catch (JsonException)
        {
            // Store validation rejects invalid JSON. Keep rule parsing tolerant here
            // so a previously valid source can still be paused and edited.
        }

        return new(include, exclude, categories, sinceHours);
    }

    private static IReadOnlyList<string> SplitKeywords(string value) => (value ?? "")
        .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static class CollectionFilter
{
    public static bool Matches(CollectionItemDraft item, CollectionRuleOptions options)
    {
        if (options.Categories.Count > 0 && !options.Categories.Any(category => item.Title.Contains(category, StringComparison.OrdinalIgnoreCase))) return false;
        if (options.ExcludeKeywords.Any(keyword => item.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) || item.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase))) return false;
        if (options.IncludeKeywords.Count > 0 && !options.IncludeKeywords.Any(keyword => item.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) || item.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }
}
