using ClosedXML.Excel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QqChannelDesk.Services;

/// <summary>
/// Creates and reads the user-facing material import workbook.  The workbook
/// only exposes names; IDs are kept on the data-source sheet for diagnostics
/// and are deliberately ignored when importing.
/// </summary>
public static class MaterialTemplateService
{
    public static readonly string[] Headers =
    [
        "标题", "正文", "媒体文件或链接", "发布时间", "频道名称", "板块名称"
    ];

    private const string ImportSheetName = "素材导入";
    private const string DataSourceSheetName = "数据源";
    private const string DropdownSourceSheetName = "下拉源";
    private const string GuildNamesRange = "TemplateGuildNames";
    private const int MaximumImportRows = 10_000;
    private const string TemplateSignatureLabel = "TemplateSignature";

    public static byte[] CreateWorkbook(IReadOnlyList<MaterialTemplateTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0 || targets.All(target => target.Channels.Count == 0))
            throw new InvalidOperationException("当前账号没有可用的频道和板块，请先同步频道数据。");

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(ImportSheetName);
        var dataSource = workbook.Worksheets.Add(DataSourceSheetName);
        WriteDataSource(dataSource, targets);
        var dropdownSource = workbook.Worksheets.Add(DropdownSourceSheetName);
        dropdownSource.Visibility = XLWorksheetVisibility.VeryHidden;
        WriteDropdownSource(dropdownSource, targets);
        dropdownSource.Cell(1, 1).Value = TemplateSignatureLabel;
        dropdownSource.Cell(2, 1).Value = ComputeTargetSignature(targets);

        for (var index = 0; index < Headers.Length; index++)
        {
            var cell = sheet.Cell(1, index + 1);
            cell.Value = Headers[index];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF7");
        }

        // Keep the import area empty. The target data is only used to build
        // dropdown sources; it must not become accidental business data.
        var maximumRow = 200;

        var uniqueTargets = targets
            .GroupBy(target => target.GuildName, StringComparer.Ordinal)
            .ToArray();
        var maximumChannelCount = uniqueTargets
            .Select(target => target
                .SelectMany(item => item.Channels)
                .Select(channel => channel.ChannelName)
                .Distinct(StringComparer.Ordinal)
                .Count())
            .DefaultIfEmpty()
            .Max();
        if (maximumChannelCount == 0)
            throw new InvalidOperationException("当前账号没有可用的频道和板块，请先同步频道数据。");

        var dataSourceMatrixEndColumn = GetColumnLetter(7 + uniqueTargets.Length);
        var dataSourceMatrixEndRow = maximumChannelCount + 1;
        var dataSourceMatrix = $"'{dropdownSource.Name}'!$H$2:${dataSourceMatrixEndColumn}${dataSourceMatrixEndRow}";

        for (var row = 2; row <= maximumRow; row++)
        {
            sheet.Cell(row, 4).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
            sheet.Cell(row, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            sheet.Cell(row, 2).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            sheet.Cell(row, 3).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;

            // WPS is more reliable when the current channel index is first
            // calculated in a helper cell and the validation only resolves a
            // row-local list from that result. This avoids WPS caching an
            // INDIRECT expression from the first validation row.
            sheet.Cell(row, 7).FormulaA1 =
                $"=IF(E{row}=\"\",0,IFERROR(MATCH(E{row},{GuildNamesRange},0),0))";

            for (var channelIndex = 1; channelIndex <= maximumChannelCount; channelIndex++)
            {
                var helperColumn = 7 + channelIndex;
                sheet.Cell(row, helperColumn).FormulaA1 =
                    $"=IF($G{row}=0,\"\",IFERROR(IF(COUNTBLANK(INDEX({dataSourceMatrix},{channelIndex},$G{row}))>0,\"\",INDEX({dataSourceMatrix},{channelIndex},$G{row})),\"\"))";
            }
        }
        sheet.Columns(7, 7 + maximumChannelCount).Hide();

        // Give every row its own direct validation range. WPS otherwise keeps
        // the first row's relative reference for the whole validation range.
        for (var row = 2; row <= maximumRow; row++)
        {
            var guildValidation = sheet.Cell(row, 5).CreateDataValidation();
            guildValidation.IgnoreBlanks = true;
            guildValidation.InCellDropdown = true;
            guildValidation.List($"={GuildNamesRange}");

            var channelValidation = sheet.Cell(row, 6).CreateDataValidation();
            channelValidation.IgnoreBlanks = true;
            channelValidation.InCellDropdown = true;
            var helperStartColumn = GetColumnLetter(8);
            var helperEndColumn = GetColumnLetter(7 + maximumChannelCount);
            channelValidation.List($"=${helperStartColumn}{row}:${helperEndColumn}{row}");
        }

        sheet.SheetView.FreezeRows(1);
        sheet.AutoFilter.Clear();
        sheet.Range(1, 1, maximumRow, Headers.Length).SetAutoFilter();
        sheet.Column(1).Width = 24;
        sheet.Column(2).Width = 42;
        sheet.Column(3).Width = 36;
        sheet.Column(4).Width = 21;
        sheet.Column(5).Width = 22;
        sheet.Column(6).Width = 22;
        sheet.Row(1).Height = 24;

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    public static MaterialTemplateParseResult ParseWorkbook(
        string filePath,
        IReadOnlyList<MaterialTemplateTarget> currentTargets,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(currentTargets);
        if (!File.Exists(filePath)) throw new FileNotFoundException("导入模板不存在。", filePath);

        using var workbook = new XLWorkbook(filePath);
        var sheet = workbook.Worksheets.FirstOrDefault(item =>
            string.Equals(item.Name, ImportSheetName, StringComparison.OrdinalIgnoreCase));
        if (sheet is null)
            return new([], [$"模板缺少“{ImportSheetName}”工作表。"]);

        var headerErrors = Headers
            .Select((header, index) => (header, index))
            .Where(item => !string.Equals(sheet.Cell(1, item.index + 1).GetString().Trim(), item.header, StringComparison.Ordinal))
            .Select(item => $"第 1 行第 {item.index + 1} 列应为“{item.header}”。")
            .ToArray();
        if (headerErrors.Length > 0) return new([], headerErrors);

        var templateErrors = new List<string>();
        var dropdownSource = workbook.Worksheets.FirstOrDefault(item =>
            string.Equals(item.Name, DropdownSourceSheetName, StringComparison.OrdinalIgnoreCase));
        var storedSignature = dropdownSource?.Cell(2, 1).GetString().Trim() ?? "";
        if (storedSignature.Length == 0
            || !string.Equals(storedSignature, ComputeTargetSignature(currentTargets), StringComparison.OrdinalIgnoreCase))
        {
            templateErrors.Add("模板已过期：频道或板块数据已发生变化，请重新下载模板。");
        }

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        if (lastRow > MaximumImportRows + 1)
            return new([], [.. templateErrors, $"模板最多支持 {MaximumImportRows} 条导入记录。"], templateErrors.Count > 0);

        var rows = new List<MaterialTemplateImportRow>();
        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            var row = sheet.Row(rowNumber);
            var values = Enumerable.Range(1, Headers.Length)
                .Select(column => row.Cell(column).GetString().Trim())
                .ToArray();
            if (values.All(string.IsNullOrWhiteSpace)) continue;

            var title = values[0];
            var content = values[1];
            var mediaLinks = ParseMediaLinks(values[2]);
            var guildName = values[4];
            var channelName = values[5];
            var publishAt = TryParsePublishAt(row.Cell(4), out var parsedPublishAt)
                ? parsedPublishAt
                : null;
            var status = publishAt is { } scheduled && scheduled > now ? "queue" : "waitsend";
            var parsedRow = ValidateImportRow(new MaterialTemplateImportRow(
                rowNumber,
                title,
                "",
                content,
                mediaLinks,
                publishAt,
                status,
                guildName,
                channelName,
                "",
                "",
                "") { PublishAtInvalid = values[3].Length > 0 && publishAt is null },
                currentTargets,
                now,
                values[3].Length > 0 && publishAt is null);
            rows.Add(parsedRow);
        }

        if (rows.Count == 0) return new([], [.. templateErrors, "模板中没有可导入的数据行。"], templateErrors.Count > 0);

        var duplicateGroups = rows
            .Where(row => row.Title.Length > 0 && row.Content.Length > 0)
            .GroupBy(row => $"{row.Title}\n{row.Content}", StringComparer.Ordinal)
            .Where(group => group.Count() > 1);
        foreach (var group in duplicateGroups)
        {
            var duplicateRows = group.OrderBy(row => row.RowNumber).ToArray();
            foreach (var row in duplicateRows)
            {
                var otherRows = duplicateRows
                    .Where(other => other.RowNumber != row.RowNumber)
                    .Select(other => other.RowNumber)
                    .ToArray();
                var duplicateError = $"与第 {string.Join("、", otherRows)} 行存在相同标题和正文";
                var updated = row with { DuplicateError = duplicateError, Error = "" };
                rows[rows.IndexOf(row)] = ValidateImportRow(updated, currentTargets, now);
            }
        }

        if (templateErrors.Count > 0)
        {
            var templateErrorText = string.Join("；", templateErrors);
            rows = rows
                .Select(row => row with
                {
                    Error = string.IsNullOrWhiteSpace(row.Error)
                        ? FormatErrors(row.RowNumber, templateErrors)
                        : $"{row.Error}；{templateErrorText}"
                })
                .ToList();
        }

        return new(rows, templateErrors, templateErrors.Count > 0);
    }

    public static MaterialTemplateImportRow ValidateImportRow(
        MaterialTemplateImportRow row,
        IReadOnlyList<MaterialTemplateTarget> currentTargets,
        DateTimeOffset now,
        bool invalidPublishAt = false)
    {
        ArgumentNullException.ThrowIfNull(currentTargets);
        var errors = new List<string>();
        if (row.Title.Trim().Length == 0) errors.Add("标题不能为空");
        if (row.Content.Trim().Length == 0) errors.Add("正文不能为空");
        var media = row.MediaLinks.Where(link => !string.IsNullOrWhiteSpace(link)).ToArray();
        var detectedType = MaterialMediaValidator.InferType(media);
        var type = ResolveImportType(detectedType, media, errors);

        var target = ResolveTarget(currentTargets, row.GuildName.Trim(), row.ChannelName.Trim(), errors);
        if (type is ("text" or "image" or "video"))
        {
            var mediaError = MaterialMediaValidator.Validate(type, row.Title.Trim(), media);
            if (mediaError.Length > 0) errors.Add(mediaError);
        }

        if (invalidPublishAt || row.PublishAtInvalid) errors.Add("发布时间格式应为 yyyy-MM-dd HH:mm:ss");
        var publishAt = row.PublishAt;
        var status = publishAt is { } scheduled && scheduled > now ? "queue" : "waitsend";
        var validated = row with
        {
            Title = row.Title.Trim(),
            Content = row.Content.Trim(),
            Type = type ?? "",
            MediaLinks = media,
            GuildName = row.GuildName.Trim(),
            ChannelName = row.ChannelName.Trim(),
            GuildId = target?.GuildId ?? "",
            ChannelId = target?.Channels.SingleOrDefault()?.ChannelId ?? "",
            Status = status,
            Error = FormatErrors(row.RowNumber, errors.Concat(
                string.IsNullOrWhiteSpace(row.DuplicateError) ? [] : [row.DuplicateError]))
        };
        return validated;
    }

    private static string? ResolveImportType(
        string? detectedType,
        IReadOnlyList<string> media,
        ICollection<string> errors)
    {
        if (media.Count == 0) return "text";

        if (MaterialMediaValidator.IsMixedMedia(media))
        {
            errors.Add("媒体文件或链接同时包含图片和视频，不能混合为一种素材类型");
            return null;
        }

        if (detectedType is "image" or "video") return detectedType;
        errors.Add("无法根据媒体文件或链接自动判断类型；请使用带图片或视频扩展名的文件或链接");
        return null;
    }

    public static byte[] CreateErrorWorkbook(
        IReadOnlyList<MaterialTemplateImportRow> rows,
        IReadOnlyList<MaterialTemplateTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(targets);
        var workbookBytes = CreateWorkbook(targets);
        using var input = new MemoryStream(workbookBytes);
        using var workbook = new XLWorkbook(input);
        var sheet = workbook.Worksheet(ImportSheetName);
        var maximumChannelCount = targets
            .GroupBy(target => target.GuildName, StringComparer.Ordinal)
            .Select(target => target
                .SelectMany(item => item.Channels)
                .Select(channel => channel.ChannelName)
                .Distinct(StringComparer.Ordinal)
                .Count())
            .DefaultIfEmpty()
            .Max();
        var errorColumn = 8 + maximumChannelCount;
        sheet.Cell(1, errorColumn).Value = "错误原因";
        sheet.Cell(1, errorColumn).Style.Font.Bold = true;
        sheet.Cell(1, errorColumn).Style.Fill.BackgroundColor = XLColor.FromHtml("#FCECEC");
        sheet.Column(errorColumn).Width = 48;
        foreach (var row in rows)
        {
            var excelRow = Math.Max(2, row.RowNumber);
            sheet.Cell(excelRow, 1).Value = row.Title;
            sheet.Cell(excelRow, 2).Value = row.Content;
            sheet.Cell(excelRow, 3).Value = row.MediaDisplay;
            if (row.PublishAt is { } publishAt)
                sheet.Cell(excelRow, 4).Value = publishAt.LocalDateTime;
            sheet.Cell(excelRow, 5).Value = row.GuildName;
            sheet.Cell(excelRow, 6).Value = row.ChannelName;
            sheet.Cell(excelRow, errorColumn).Value = row.Error;
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    public static string ComputeTargetSignature(IReadOnlyList<MaterialTemplateTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var lines = targets
            .SelectMany(target => target.Channels.Select(channel =>
                string.Join("\u001f", target.GuildId, target.GuildName, channel.ChannelId, channel.ChannelName)))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).ToLowerInvariant();
    }

    public static string ComputeFileHash(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath)) throw new FileNotFoundException("导入模板不存在。", filePath);
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string FormatErrors(int rowNumber, IEnumerable<string> errors)
    {
        var values = errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToArray();
        return values.Length == 0 ? "" : $"第 {rowNumber} 行：{string.Join("；", values)}";
    }

    private static void WriteDataSource(IXLWorksheet sheet, IReadOnlyList<MaterialTemplateTarget> targets)
    {
        sheet.Cell(1, 1).Value = "频道名称";
        sheet.Cell(1, 2).Value = "板块名称";
        sheet.Range(1, 1, 1, 2).Style.Font.Bold = true;
        sheet.Range(1, 1, 1, 2).Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF7");
        var row = 2;
        foreach (var target in targets)
        {
            foreach (var channel in target.Channels)
            {
                sheet.Cell(row, 1).Value = target.GuildName;
                sheet.Cell(row, 2).Value = channel.ChannelName;
                row++;
            }
        }

        if (row > 2)
            sheet.Range(1, 1, row - 1, 2).SetAutoFilter();

        sheet.Column(1).Width = 24;
        sheet.Column(2).Width = 28;
        sheet.SheetView.FreezeRows(1);
    }

    private static void WriteDropdownSource(IXLWorksheet sheet, IReadOnlyList<MaterialTemplateTarget> targets)
    {
        sheet.Cell(1, 7).Value = "GuildName";
        var uniqueTargets = targets
            .GroupBy(target => target.GuildName, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < uniqueTargets.Length; index++)
        {
            var column = 8 + index;
            var target = uniqueTargets[index];
            sheet.Cell(index + 2, 7).Value = target.Key;
            var channelNames = target
                .SelectMany(item => item.Channels)
                .Select(channel => channel.ChannelName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            for (var channelIndex = 0; channelIndex < channelNames.Length; channelIndex++)
                sheet.Cell(channelIndex + 2, column).Value = channelNames[channelIndex];
        }

        sheet.Cell(1, 7).Value = "频道名称（下拉源）";
        sheet.Cell(1, 7).Style.Font.Bold = true;
        sheet.Cell(1, 7).Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF7");
        sheet.Column(7).Width = 24;
        sheet.SheetView.FreezeRows(1);

        if (uniqueTargets.Length == 0) return;
        var guildListEnd = uniqueTargets.Length + 1;
        sheet.Workbook.DefinedNames.Add(GuildNamesRange, $"'{sheet.Name}'!$G$2:$G${guildListEnd}");
    }

    private static MaterialTemplateTarget? ResolveTarget(
        IReadOnlyList<MaterialTemplateTarget> targets,
        string guildName,
        string channelName,
        ICollection<string> errors)
    {
        if (guildName.Length == 0)
        {
            errors.Add("频道名称不能为空");
            return null;
        }
        if (channelName.Length == 0)
        {
            errors.Add("板块名称不能为空");
            return null;
        }

        var guilds = targets.Where(item => string.Equals(item.GuildName, guildName, StringComparison.Ordinal)).ToArray();
        if (guilds.Length == 0)
        {
            errors.Add($"找不到频道“{guildName}”，请重新同步频道数据");
            return null;
        }
        if (guilds.Length > 1)
        {
            errors.Add($"频道“{guildName}”名称不唯一");
            return null;
        }

        var channels = guilds[0].Channels
            .Where(item => string.Equals(item.ChannelName, channelName, StringComparison.Ordinal))
            .ToArray();
        if (channels.Length == 0)
        {
            errors.Add($"频道“{guildName}”中找不到板块“{channelName}”");
            return null;
        }
        if (channels.Length > 1)
        {
            errors.Add($"板块“{channelName}”名称不唯一");
            return null;
        }
        return guilds[0] with { Channels = [channels[0]] };
    }

    private static string GetColumnLetter(int column)
    {
        var result = "";
        while (column > 0)
        {
            column--;
            result = (char)('A' + column % 26) + result;
            column /= 26;
        }
        return result;
    }

    private static IReadOnlyList<string> ParseMediaLinks(string value) => value
        .Split(['|', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool TryParsePublishAt(IXLCell cell, out DateTimeOffset? publishAt)
    {
        publishAt = null;
        if (cell.IsEmpty()) return true;
        DateTime date;
        try
        {
            if (cell.DataType == XLDataType.DateTime)
            {
                date = cell.GetDateTime();
            }
            else
            {
                var text = cell.GetFormattedString().Trim();
                var formats = new[]
                {
                    "yyyy-MM-dd HH:mm:ss",
                    "yyyy-M-d H:mm:ss",
                    "yyyy/M/d H:mm:ss",
                    "yyyy/M/d HH:mm:ss"
                };
                if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }

        date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        publishAt = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
        return true;
    }
}

public sealed record MaterialTemplateTarget(
    string GuildId,
    string GuildName,
    string GuildRole,
    IReadOnlyList<MaterialTemplateChannel> Channels);

public sealed record MaterialTemplateChannel(string ChannelId, string ChannelName);

public sealed record MaterialTemplateImportRow(
    int RowNumber,
    string Title,
    string Type,
    string Content,
    IReadOnlyList<string> MediaLinks,
    DateTimeOffset? PublishAt,
    string Status,
    string GuildName,
    string ChannelName,
    string GuildId,
    string ChannelId,
    string Error)
{
    public string DuplicateError { get; init; } = "";
    public bool PublishAtInvalid { get; init; }
    public bool IsValid => string.IsNullOrWhiteSpace(Error);
    public string PublishAtDisplay => PublishAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "立即";
    public string TargetDisplay => string.IsNullOrWhiteSpace(GuildName) || string.IsNullOrWhiteSpace(ChannelName)
        ? "-"
        : $"{GuildName}/{ChannelName}";
    public string MediaDisplay => string.Join(" | ", MediaLinks);
}

public sealed record MaterialTemplateParseResult(
    IReadOnlyList<MaterialTemplateImportRow> Rows,
    IReadOnlyList<string> Errors,
    bool TemplateExpired = false)
{
    public bool IsValid => !TemplateExpired && Errors.Count == 0 && Rows.Count > 0 && Rows.All(row => row.IsValid);
    public int ValidRowCount => Rows.Count(row => row.IsValid);
    public int InvalidRowCount => Rows.Count(row => !row.IsValid);
}
