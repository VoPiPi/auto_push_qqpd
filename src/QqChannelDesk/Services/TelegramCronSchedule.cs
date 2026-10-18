using System.Globalization;

namespace QqChannelDesk.Services;

/// <summary>Small dependency-free five-field cron parser for local-time collection schedules.</summary>
public sealed class TelegramCronSchedule
{
    // 必须覆盖完整闰年周期：像 2 月 29 日这类表达式可能间隔 8 年才有下一次匹配，
    // 一年窗口会让合法的年度配置被误判为“永远不会触发”。
    private const int SearchWindowDays = 3660;

    private readonly CronField _minute;
    private readonly CronField _hour;
    private readonly CronField _dayOfMonth;
    private readonly CronField _month;
    private readonly CronField _dayOfWeek;

    private TelegramCronSchedule(string expression, CronField minute, CronField hour, CronField dayOfMonth, CronField month, CronField dayOfWeek)
    {
        Expression = expression;
        _minute = minute;
        _hour = hour;
        _dayOfMonth = dayOfMonth;
        _month = month;
        _dayOfWeek = dayOfWeek;
    }

    public string Expression { get; }

    public static bool TryParse(string? expression, out TelegramCronSchedule schedule, out string error)
    {
        schedule = null!;
        error = "";
        var text = expression?.Trim() ?? "";
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 5)
        {
            error = "计划表达式必须是 5 段：分 时 日 月 周。";
            return false;
        }

        var ranges = new[] { (0, 59, "分钟"), (0, 23, "小时"), (1, 31, "日期"), (1, 12, "月份"), (0, 7, "星期") };
        var fields = new CronField[5];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!CronField.TryParse(parts[index], ranges[index].Item1, ranges[index].Item2, out fields[index], out var fieldError))
            {
                error = $"{ranges[index].Item3}配置无效：{fieldError}";
                return false;
            }
        }

        schedule = new TelegramCronSchedule(string.Join(' ', parts), fields[0], fields[1], fields[2], fields[3], fields[4]);
        return true;
    }

    public static TelegramCronSchedule Parse(string expression) =>
        TryParse(expression, out var schedule, out var error)
            ? schedule
            : throw new FormatException(error);

    /// <summary>Returns the first matching local minute strictly after the supplied local time.</summary>
    public DateTime? GetNextOccurrence(DateTime localAfter)
    {
        var candidate = new DateTime(localAfter.Year, localAfter.Month, localAfter.Day, localAfter.Hour, localAfter.Minute, 0, DateTimeKind.Unspecified)
            .AddMinutes(1);
        var end = candidate.AddDays(SearchWindowDays);
        for (; candidate <= end; candidate = candidate.AddMinutes(1))
        {
            if (!Matches(candidate)) continue;
            return candidate;
        }
        return null;
    }

    /// <summary>Returns the latest matching local minute at or before the supplied local time.</summary>
    public DateTime? GetPreviousOccurrence(DateTime localAt)
    {
        var candidate = new DateTime(localAt.Year, localAt.Month, localAt.Day, localAt.Hour, localAt.Minute, 0, DateTimeKind.Unspecified);
        var start = candidate.AddDays(-SearchWindowDays);
        for (; candidate >= start; candidate = candidate.AddMinutes(-1))
        {
            if (Matches(candidate)) return candidate;
        }
        return null;
    }

    public bool IsDue(DateTimeOffset? lastCollectedAt, DateTimeOffset now)
    {
        var localNow = now.ToLocalTime().DateTime;
        if (lastCollectedAt is null)
            return GetPreviousOccurrence(localNow) is not null;

        var next = GetNextOccurrence(lastCollectedAt.Value.ToLocalTime().DateTime);
        return next is not null && next.Value <= localNow;
    }

    private bool Matches(DateTime candidate)
    {
        if (!_month.Contains(candidate.Month) || !_hour.Contains(candidate.Hour) || !_minute.Contains(candidate.Minute)) return false;
        var dayOfMonthMatches = _dayOfMonth.Contains(candidate.Day);
        var dayOfWeekMatches = _dayOfWeek.Contains(CronDayOfWeek(candidate.DayOfWeek));
        return MatchesDay(dayOfMonthMatches, dayOfWeekMatches);
    }

    private bool MatchesDay(bool dayOfMonthMatches, bool dayOfWeekMatches)
    {
        // Cron uses OR when both calendar day fields are restricted, while a wildcard
        // leaves the other field in control.
        var dayOfMonthWildcard = _dayOfMonth.IsWildcard;
        var dayOfWeekWildcard = _dayOfWeek.IsWildcard;
        if (dayOfMonthWildcard && dayOfWeekWildcard) return true;
        if (dayOfMonthWildcard) return dayOfWeekMatches;
        if (dayOfWeekWildcard) return dayOfMonthMatches;
        return dayOfMonthMatches || dayOfWeekMatches;
    }

    private static int CronDayOfWeek(DayOfWeek day) => day == DayOfWeek.Sunday ? 0 : (int)day;

    private sealed class CronField
    {
        private readonly HashSet<int> _values;
        private CronField(HashSet<int> values, bool wildcard) { _values = values; IsWildcard = wildcard; }
        public bool IsWildcard { get; }
        public bool Contains(int value)
        {
            if (_values.Contains(value)) return true;
            // Cron accepts both 0 and 7 for Sunday. Treat either spelling as
            // the same value when evaluating the weekday field.
            return value is 0 or 7 && (_values.Contains(0) || _values.Contains(7));
        }

        public static bool TryParse(string text, int minimum, int maximum, out CronField field, out string error)
        {
            field = null!;
            error = "";
            var values = new HashSet<int>();
            var wildcard = text == "*";
            foreach (var rawPart in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (rawPart.Length == 0) { error = "存在空项目。"; return false; }
                var slashParts = rawPart.Split('/', StringSplitOptions.TrimEntries);
                if (slashParts.Length > 2 || slashParts.Any(string.IsNullOrWhiteSpace)) { error = "步长格式无效。"; return false; }
                var step = 1;
                if (slashParts.Length == 2 && (!int.TryParse(slashParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0))
                { error = "步长必须是正整数。"; return false; }

                var rangeText = slashParts[0];
                var hasStep = slashParts.Length == 2;
                int start;
                int end;
                if (rangeText == "*")
                {
                    start = minimum;
                    end = maximum;
                }
                else
                {
                    var range = rangeText.Split('-', StringSplitOptions.TrimEntries);
                    if (range.Length > 2 || !int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out start))
                    { error = "范围格式无效。"; return false; }
                    // Cron 的 "5/15" 等价于 "5-{最大值}/15"，而不是只匹配 5 本身。
                    end = hasStep && range.Length == 1 ? maximum : start;
                    if (range.Length == 2 && !int.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out end))
                    { error = "范围格式无效。"; return false; }
                }
                if (start < minimum || start > maximum || end < minimum || end > maximum || start > end)
                { error = $"取值必须在 {minimum} 到 {maximum} 之间。"; return false; }
                for (var value = start; value <= end; value += step) values.Add(value);
            }
            if (values.Count == 0) { error = "至少需要一个有效取值。"; return false; }
            field = new CronField(values, wildcard);
            return true;
        }
    }
}
