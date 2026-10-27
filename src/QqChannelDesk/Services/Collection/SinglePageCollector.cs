using QqChannelDesk.Services;

namespace QqChannelDesk.Services.Collection;

public sealed class SinglePageCollector : ISourceCollector
{
    private readonly Func<string, CancellationToken, Task<ArticleFetchResult>> _fetchPage;
    private readonly Func<string, string?, CancellationToken, Task<ArticleFetchResult>>? _fetchPageWithSelector;
    private readonly IAiAssistService? _aiAssist;
    private readonly AiUsageGate? _aiUsage;

    public SinglePageCollector(
        Func<string, CancellationToken, Task<ArticleFetchResult>> fetchPage,
        IAiAssistService? aiAssist = null,
        AiUsageGate? aiUsage = null,
        Func<string, string?, CancellationToken, Task<ArticleFetchResult>>? fetchPageWithSelector = null)
    {
        _fetchPage = fetchPage;
        _fetchPageWithSelector = fetchPageWithSelector;
        _aiAssist = aiAssist;
        _aiUsage = aiUsage;
    }

    public string SourceType => "single-page";

    public async Task<CollectionResult> CollectAsync(CollectionSource source, CancellationToken cancellationToken = default)
    {
        if (!PublicUrlGuard.TryNormalize(source.EntryUrl, out var url, out var error))
            return CollectionResult.Failure("data-error", error);
        try
        {
            var contentSelector = ReadContentSelector(source.RuleConfigJson);
            var result = contentSelector is null || _fetchPageWithSelector is null
                ? await _fetchPage(url.AbsoluteUri, cancellationToken)
                : await _fetchPageWithSelector(url.AbsoluteUri, contentSelector, cancellationToken);
            if (result.Succeeded)
            {
                return CollectionResult.Success([new CollectionItemDraft(
                    result.Title,
                    result.Content,
                    CanonicalUrl(result.Url.Length > 0 ? result.Url : url.AbsoluteUri),
                    CanonicalItemId(result.Url.Length > 0 ? result.Url : url.AbsoluteUri),
                    OriginalContent: result.Content)]);
            }

            if (!source.AiFallbackEnabled || _aiAssist is null || result.RawHtml.Length == 0)
                return CollectionResult.Failure(Classify(result.Error), result.Error);
            if (_aiUsage is not null && !await _aiUsage.TryReserveAsync(source, cancellationToken))
                return CollectionResult.Failure("ai-degraded", "AI 兜底未执行：全局或来源调用额度已用完。");
            var aiResult = await _aiAssist.ExtractAsync(result.RawHtml, "single-page", cancellationToken);
            if (!aiResult.Succeeded)
                return CollectionResult.Failure("ai-degraded", $"AI 兜底失败：{aiResult.ErrorSummary}");
            return CollectionResult.Success([new CollectionItemDraft(
                aiResult.Title,
                aiResult.Content,
                CanonicalUrl(url.AbsoluteUri),
                CanonicalItemId(url.AbsoluteUri),
                ParsedBy: "ai-fallback",
                CandidateContent: aiResult.Content)]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CollectionResult.Failure("http-error", CliDiagnostics.Sanitize(exception.Message));
        }
    }

    private static string Classify(string error) =>
        error.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase) ? "rate-limit" :
        error.Contains("超时", StringComparison.OrdinalIgnoreCase) ? "timeout" : "http-error";

    private static string CanonicalItemId(string url) =>
        UrlNormalizer.CanonicalItemId(url);

    private static string CanonicalUrl(string url) =>
        UrlNormalizer.Canonicalize(url);

    /// <summary>从解析规则 JSON 中读取可选的自定义正文选择器（支持 CSS 与 XPath）。</summary>
    internal static string? ReadContentSelector(string? ruleConfigJson)
    {
        if (string.IsNullOrWhiteSpace(ruleConfigJson)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(ruleConfigJson);
            if (document.RootElement.ValueKind is not System.Text.Json.JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("contentSelector", out var selector) ||
                selector.ValueKind is not System.Text.Json.JsonValueKind.String)
            {
                return null;
            }

            var value = selector.GetString()?.Trim() ?? "";
            return value.Length == 0 ? null : value;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
