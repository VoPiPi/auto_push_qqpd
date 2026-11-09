using ClosedXML.Excel;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class MaterialTemplateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QqChannelDeskTemplateTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateWorkbook_LeavesImportAreaEmptyAndHidesDictionary()
    {
        var targets = CreateTargets();
        var path = Path.Combine(_directory, "template.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(targets));

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("素材导入");
        Assert.Equal(MaterialTemplateService.Headers, Enumerable.Range(1, MaterialTemplateService.Headers.Length)
            .Select(column => sheet.Cell(1, column).GetString()).ToArray());
        Assert.Empty(sheet.Cell(2, 5).GetString());
        Assert.Empty(sheet.Cell(2, 6).GetString());
        Assert.Empty(sheet.Cell(3, 5).GetString());
        Assert.Empty(sheet.Cell(3, 6).GetString());
        Assert.Empty(sheet.Cell(4, 5).GetString());
        Assert.Empty(sheet.Cell(4, 6).GetString());
        Assert.All(Enumerable.Range(2, 199), row =>
        {
            Assert.All(Enumerable.Range(1, MaterialTemplateService.Headers.Length), column =>
                Assert.Empty(sheet.Cell(row, column).GetString()));
        });
        var dataSource = workbook.Worksheet("数据源");
        Assert.Equal(XLWorksheetVisibility.Visible, dataSource.Visibility);
        Assert.Equal("频道名称", dataSource.Cell(1, 1).GetString());
        Assert.Equal("版块名称", dataSource.Cell(1, 2).GetString());
        Assert.Equal("频道一", dataSource.Cell(2, 1).GetString());
        Assert.Equal("板块一", dataSource.Cell(2, 2).GetString());
        Assert.Equal(2, dataSource.LastColumnUsed()!.ColumnNumber());
        Assert.Equal(2, dataSource.AutoFilter.Range.RangeAddress.LastAddress.ColumnNumber);

        var dropdownSource = workbook.Worksheet("下拉源");
        Assert.Equal(XLWorksheetVisibility.VeryHidden, dropdownSource.Visibility);
        Assert.Equal("板块一", dropdownSource.Cell(2, 8).GetString());
        Assert.Equal("板块二", dropdownSource.Cell(3, 8).GetString());
        Assert.Equal("板块三", dropdownSource.Cell(2, 9).GetString());
        Assert.Equal("", dropdownSource.Cell(3, 9).GetString());

        var boardValidation = sheet.Cell(2, 6).GetDataValidation();
        Assert.DoesNotContain("INDIRECT", boardValidation.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TemplateChannels_", boardValidation.Value, StringComparison.Ordinal);
        Assert.Equal("=$H2:$I2", boardValidation.Value);
        Assert.Equal("=$H3:$I3", sheet.Cell(3, 6).GetDataValidation().Value);
        Assert.Equal("IF(E2=\"\",0,IFERROR(MATCH(E2,TemplateGuildNames,0),0))", sheet.Cell(2, 7).FormulaA1);
        Assert.Equal("IF($G2=0,\"\",IFERROR(IF(COUNTBLANK(INDEX('下拉源'!$H$2:$I$3,1,$G2))>0,\"\",INDEX('下拉源'!$H$2:$I$3,1,$G2)),\"\"))", sheet.Cell(2, 8).FormulaA1);
        Assert.Equal("IF($G2=0,\"\",IFERROR(IF(COUNTBLANK(INDEX('下拉源'!$H$2:$I$3,2,$G2))>0,\"\",INDEX('下拉源'!$H$2:$I$3,2,$G2)),\"\"))", sheet.Cell(2, 9).FormulaA1);
        Assert.Equal("IF($G3=0,\"\",IFERROR(IF(COUNTBLANK(INDEX('下拉源'!$H$2:$I$3,1,$G3))>0,\"\",INDEX('下拉源'!$H$2:$I$3,1,$G3)),\"\"))", sheet.Cell(3, 8).FormulaA1);
        Assert.Equal("IF($G4=0,\"\",IFERROR(IF(COUNTBLANK(INDEX('下拉源'!$H$2:$I$3,1,$G4))>0,\"\",INDEX('下拉源'!$H$2:$I$3,1,$G4)),\"\"))", sheet.Cell(4, 8).FormulaA1);
        Assert.Equal("IF($G4=0,\"\",IFERROR(IF(COUNTBLANK(INDEX('下拉源'!$H$2:$I$3,2,$G4))>0,\"\",INDEX('下拉源'!$H$2:$I$3,2,$G4)),\"\"))", sheet.Cell(4, 9).FormulaA1);
    }

    [Fact]
    public async Task ParseWorkbook_UsesLatestCacheIdsAndCreatesFutureScheduleStatus()
    {
        var path = Path.Combine(_directory, "import.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("素材导入");
            sheet.Cell(2, 1).Value = "标题";
            sheet.Cell(2, 2).Value = "正文";
            sheet.Cell(2, 4).Value = DateTime.Now.AddHours(1);
            sheet.Cell(2, 5).Value = "频道一";
            sheet.Cell(2, 6).Value = "板块一";
            workbook.SaveAs(path);
        }

        MaterialTemplateTarget[] currentTargets = [
            new MaterialTemplateTarget("1001", "频道一", "成员", [
                new MaterialTemplateChannel("2001", "板块一"),
                new MaterialTemplateChannel("2002", "板块二")
            ]),
            new MaterialTemplateTarget("1002", "频道二", "成员", [new MaterialTemplateChannel("2003", "板块三")])
        ];
        var result = MaterialTemplateService.ParseWorkbook(path, currentTargets, DateTimeOffset.Now);

        Assert.True(result.IsValid, string.Join("；", result.Errors.Concat(result.Rows.Select(row => row.Error))));
        var row = Assert.Single(result.Rows);
        Assert.Equal("1001", row.GuildId);
        Assert.Equal("2001", row.ChannelId);
        Assert.Equal("queue", row.Status);
    }

    [Fact]
    public async Task ParseWorkbook_AcceptsLegacyBoardHeader()
    {
        var path = Path.Combine(_directory, "legacy-header.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("素材导入");
            sheet.Cell(1, 6).Value = "板块名称";
            sheet.Cell(2, 1).Value = "旧模板标题";
            sheet.Cell(2, 2).Value = "旧模板正文";
            sheet.Cell(2, 5).Value = "频道一";
            sheet.Cell(2, 6).Value = "板块一";
            workbook.SaveAs(path);
        }

        var result = MaterialTemplateService.ParseWorkbook(path, CreateTargets(), DateTimeOffset.Now);

        Assert.True(result.IsValid, string.Join("；", result.Errors.Concat(result.Rows.Select(row => row.Error))));
        var row = Assert.Single(result.Rows);
        Assert.Equal("2001", row.ChannelId);
        Assert.Equal("板块一", row.ChannelName);
    }

    [Fact]
    public async Task ParseWorkbook_InfersTypesAndReportsManualCorrections()
    {
        var path = Path.Combine(_directory, "types.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("素材导入");
            sheet.Cell(2, 1).Value = "文本";
            sheet.Cell(2, 2).Value = "正文";
            sheet.Cell(2, 5).Value = "频道一";
            sheet.Cell(2, 6).Value = "板块一";
            sheet.Cell(3, 1).Value = "图片";
            sheet.Cell(3, 2).Value = "正文";
            sheet.Cell(3, 3).Value = "https://example.com/photo.jpg?token=1";
            sheet.Cell(3, 5).Value = "频道一";
            sheet.Cell(3, 6).Value = "板块一";
            sheet.Cell(4, 1).Value = "未知";
            sheet.Cell(4, 2).Value = "正文";
            sheet.Cell(4, 3).Value = "https://example.com/media";
            sheet.Cell(4, 5).Value = "频道一";
            sheet.Cell(4, 6).Value = "板块一";
            sheet.Cell(5, 1).Value = "未知二";
            sheet.Cell(5, 2).Value = "正文";
            sheet.Cell(5, 3).Value = "https://example.com/media";
            sheet.Cell(5, 5).Value = "频道一";
            sheet.Cell(5, 6).Value = "板块一";
            sheet.Cell(6, 1).Value = "混合";
            sheet.Cell(6, 2).Value = "正文";
            sheet.Cell(6, 3).Value = "https://example.com/a.jpg|https://example.com/b.mp4";
            sheet.Cell(6, 5).Value = "频道一";
            sheet.Cell(6, 6).Value = "板块一";
            workbook.SaveAs(path);
        }

        var result = MaterialTemplateService.ParseWorkbook(path, CreateTargets(), DateTimeOffset.Now);

        Assert.Equal("text", result.Rows.Single(row => row.RowNumber == 2).Type);
        Assert.Equal("image", result.Rows.Single(row => row.RowNumber == 3).Type);
        Assert.Contains("无法判断媒体类型", result.Rows.Single(row => row.RowNumber == 4).Error);
        Assert.Contains("无法判断媒体类型", result.Rows.Single(row => row.RowNumber == 5).Error);
        Assert.Contains("不能同时包含图片和视频", result.Rows.Single(row => row.RowNumber == 6).Error);
    }

    [Fact]
    public async Task ParseWorkbook_RejectsStaleAndAmbiguousTargets()
    {
        var path = Path.Combine(_directory, "invalid.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("素材导入");
            sheet.Cell(2, 1).Value = "标题";
            sheet.Cell(2, 2).Value = "正文";
            sheet.Cell(2, 5).Value = "已删除频道";
            sheet.Cell(2, 6).Value = "板块一";
            sheet.Cell(3, 1).Value = "标题 2";
            sheet.Cell(3, 2).Value = "正文 2";
            sheet.Cell(3, 5).Value = "频道一";
            sheet.Cell(3, 6).Value = "板块一";
            workbook.SaveAs(path);
        }

        MaterialTemplateTarget[] currentTargets = [
            new MaterialTemplateTarget("1001", "频道一", "成员", [new MaterialTemplateChannel("2001", "板块一")]),
            new MaterialTemplateTarget("1002", "频道一", "成员", [new MaterialTemplateChannel("2002", "板块一")])
        ];
        var result = MaterialTemplateService.ParseWorkbook(path, currentTargets, DateTimeOffset.Now);

        Assert.False(result.IsValid);
        Assert.Contains(result.Rows, row => row.Error.Contains("找不到频道"));
        Assert.Contains(result.Rows, row => row.Error.Contains("名称不唯一"));
    }

    [Fact]
    public async Task ParseWorkbook_RejectsBoardFromAnotherGuild()
    {
        var path = Path.Combine(_directory, "cross-guild.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("素材导入");
            sheet.Cell(2, 1).Value = "标题";
            sheet.Cell(2, 2).Value = "正文";
            sheet.Cell(2, 5).Value = "频道一";
            sheet.Cell(2, 6).Value = "板块三";
            workbook.SaveAs(path);
        }

        var result = MaterialTemplateService.ParseWorkbook(path, CreateTargets(), DateTimeOffset.Now);

        Assert.False(result.IsValid);
        Assert.Contains(result.Rows, row => row.Error.Contains("找不到版块"));
    }

    [Fact]
    public async Task ParseWorkbook_ReportsRequiredFieldsAndDuplicateRows()
    {
        var path = Path.Combine(_directory, "validation.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("素材导入");
            sheet.Cell(2, 1).Value = "缺频道和版块";
            sheet.Cell(3, 1).Value = "重复标题";
            sheet.Cell(3, 2).Value = "重复正文";
            sheet.Cell(3, 5).Value = "频道一";
            sheet.Cell(3, 6).Value = "板块一";
            sheet.Cell(4, 1).Value = "重复标题";
            sheet.Cell(4, 2).Value = "重复正文";
            sheet.Cell(4, 5).Value = "频道一";
            sheet.Cell(4, 6).Value = "板块一";
            workbook.SaveAs(path);
        }

        var result = MaterialTemplateService.ParseWorkbook(path, CreateTargets(), DateTimeOffset.Now);

        Assert.False(result.IsValid);
        var emptyFieldError = result.Rows.Single(row => row.RowNumber == 2).Error;
        Assert.Contains("第 2 行", emptyFieldError);
        Assert.Contains("B 列正文", emptyFieldError);
        Assert.Contains("E 列频道名称", emptyFieldError);
        Assert.Contains("F 列版块名称", emptyFieldError);
        Assert.Contains("请填写正文，或添加图片或视频", emptyFieldError);
        Assert.Contains("A/B/C 列内容与第 4 行重复", result.Rows.Single(row => row.RowNumber == 3).Error);
        Assert.Contains("A/B/C 列内容与第 3 行重复", result.Rows.Single(row => row.RowNumber == 4).Error);
    }

    [Fact]
    public async Task ParseWorkbook_AllowsMediaOnlyRows()
    {
        var path = Path.Combine(_directory, "media-only.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("素材导入");
            sheet.Cell(2, 3).Value = "https://example.com/photo.jpg";
            sheet.Cell(2, 5).Value = "频道一";
            sheet.Cell(2, 6).Value = "板块一";
            workbook.SaveAs(path);
        }

        var result = MaterialTemplateService.ParseWorkbook(path, CreateTargets(), DateTimeOffset.Now);

        Assert.True(result.IsValid, string.Join("；", result.Errors.Concat(result.Rows.Select(row => row.Error))));
        var row = Assert.Single(result.Rows);
        Assert.Equal("image", row.Type);
        Assert.Empty(row.Title);
        Assert.Empty(row.Content);
    }

    [Fact]
    public async Task ParseWorkbook_WarnsForChangedTemplateAndStillValidatesRows()
    {
        var path = Path.Combine(_directory, "expired.xlsx");
        var errorPath = Path.Combine(_directory, "errors.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            workbook.Worksheet("下拉源").Cell(2, 1).Value = "changed-signature";
            var sheet = workbook.Worksheet("素材导入");
            sheet.Cell(2, 1).Value = "标题";
            sheet.Cell(2, 2).Value = "正文";
            sheet.Cell(2, 5).Value = "频道一";
            sheet.Cell(2, 6).Value = "板块一";
            sheet.Cell(3, 1).Value = "缺版块";
            sheet.Cell(3, 2).Value = "正文";
            sheet.Cell(3, 5).Value = "频道一";
            workbook.SaveAs(path);
        }

        var result = MaterialTemplateService.ParseWorkbook(path, CreateTargets(), DateTimeOffset.Now);
        Assert.True(result.TemplateChanged);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("按当前频道数据继续检查"));
        Assert.True(result.Rows.Single(row => row.RowNumber == 2).IsValid);
        Assert.Contains("F 列版块名称", result.Rows.Single(row => row.RowNumber == 3).Error);
        await File.WriteAllBytesAsync(
            errorPath,
            MaterialTemplateService.CreateErrorWorkbook(result.Rows.Where(row => !row.IsValid).ToArray(), CreateTargets()));
        using var errorWorkbook = new XLWorkbook(errorPath);
        var errorSheet = errorWorkbook.Worksheet("素材导入");
        Assert.Equal("待修正原因", errorSheet.Cell(1, 10).GetString());
        Assert.Empty(errorSheet.Cell(2, 10).GetString());
        Assert.Contains("第 3 行", errorSheet.Cell(3, 10).GetString());
        Assert.Contains("F 列版块名称", errorSheet.Cell(3, 10).GetString());
    }

    [Fact]
    public async Task ParseWorkbook_ReportsAllRowIssuesWithExcelColumnNames()
    {
        var path = Path.Combine(_directory, "all-issues.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("素材导入");
            sheet.Cell(2, 2).Value = new string('x', 1001);
            workbook.SaveAs(path);
        }

        var result = MaterialTemplateService.ParseWorkbook(path, CreateTargets(), DateTimeOffset.Now);

        var row = Assert.Single(result.Rows);
        Assert.Equal(0, result.ValidRowCount);
        Assert.Equal(1, result.InvalidRowCount);
        Assert.Contains(row.Issues, issue => issue.StartsWith("B 列正文", StringComparison.Ordinal));
        Assert.Contains(row.Issues, issue => issue.StartsWith("E 列频道名称", StringComparison.Ordinal));
        Assert.Contains(row.Issues, issue => issue.StartsWith("F 列版块名称", StringComparison.Ordinal));
        Assert.Contains("1000", row.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseWorkbook_AlignsContentAndMediaRulesWithPublishValidation()
    {
        var path = Path.Combine(_directory, "publish-limits.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("素材导入");

            SetImportRow(sheet, 2, content: new string('x', 1001));

            SetImportRow(sheet, 3, title: "长贴", content: new string('x', 10001));

            SetImportRow(
                sheet,
                4,
                content: "正文",
                media: string.Join("|", Enumerable.Range(1, 19).Select(index => $"https://example.com/{index}.jpg")));

            SetImportRow(
                sheet,
                5,
                title: "图片长贴",
                content: "正文",
                media: string.Join("|", Enumerable.Range(1, 51).Select(index => $"https://example.com/{index}.jpg")));

            SetImportRow(
                sheet,
                6,
                title: "视频",
                content: "正文",
                media: "https://example.com/a.mp4|https://example.com/b.mp4");

            workbook.SaveAs(path);
        }

        var result = MaterialTemplateService.ParseWorkbook(path, CreateTargets(), DateTimeOffset.Now);

        Assert.Contains("1000", RowAt(result, 2).Error, StringComparison.Ordinal);
        Assert.Contains("10000", RowAt(result, 3).Error, StringComparison.Ordinal);
        Assert.Contains("18", RowAt(result, 4).Error, StringComparison.Ordinal);
        Assert.Contains("50", RowAt(result, 5).Error, StringComparison.Ordinal);
        Assert.Contains("一个有效视频", RowAt(result, 6).Error, StringComparison.Ordinal);
        Assert.All(result.Rows, row => Assert.False(row.IsValid));
    }

    [Fact]
    public async Task ParseWorkbook_ReportsMixedValidAndInvalidRows()
    {
        var path = Path.Combine(_directory, "mixed-result.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("素材导入");
            SetImportRow(sheet, 2, title: "可导入", content: "正文");
            sheet.Cell(3, 1).Value = "缺频道";
            sheet.Cell(3, 2).Value = "正文";
            workbook.SaveAs(path);
        }

        var result = MaterialTemplateService.ParseWorkbook(path, CreateTargets(), DateTimeOffset.Now);

        Assert.Equal(1, result.ValidRowCount);
        Assert.Equal(1, result.InvalidRowCount);
        Assert.True(RowAt(result, 2).IsValid);
        Assert.Contains("E 列频道名称", RowAt(result, 3).Error);
        Assert.Contains("F 列版块名称", RowAt(result, 3).Error);
    }

    [Fact]
    public async Task ParseWorkbook_RequiresNumericTargetIds()
    {
        var path = Path.Combine(_directory, "numeric-ids.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("素材导入");
            SetImportRow(sheet, 2, title: "标题", content: "正文");
            workbook.SaveAs(path);
        }

        MaterialTemplateTarget[] targets = [
            new MaterialTemplateTarget(
                "guild-a",
                "频道一",
                "成员",
                [new MaterialTemplateChannel("channel-a", "板块一")])
        ];
        var result = MaterialTemplateService.ParseWorkbook(path, targets, DateTimeOffset.Now);

        var row = Assert.Single(result.Rows);
        Assert.Contains(row.Issues, issue => issue.StartsWith("E 列频道名称", StringComparison.Ordinal));
        Assert.Contains(row.Issues, issue => issue.StartsWith("F 列版块名称", StringComparison.Ordinal));
        Assert.Contains("频道 ID 必须为数字", row.Error, StringComparison.Ordinal);
        Assert.Contains("版块 ID 必须为数字", row.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseWorkbook_DuplicateKeyIncludesTargetAndMediaCombination()
    {
        var path = Path.Combine(_directory, "duplicate-key.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("素材导入");
            SetImportRow(sheet, 2, title: "相同", content: "正文", channel: "板块一");
            SetImportRow(sheet, 3, title: "相同", content: "正文", channel: "板块二");
            SetImportRow(sheet, 4, title: "相同", content: "正文", channel: "板块一", media: "https://example.com/a.jpg");
            SetImportRow(sheet, 5, title: "相同", content: "正文", channel: "板块一", media: "https://example.com/a.jpg");
            workbook.SaveAs(path);
        }

        var result = MaterialTemplateService.ParseWorkbook(path, CreateTargets(), DateTimeOffset.Now);

        Assert.True(RowAt(result, 2).IsValid);
        Assert.True(RowAt(result, 3).IsValid);
        Assert.Contains("与第 5 行重复", RowAt(result, 4).Error, StringComparison.Ordinal);
        Assert.Contains("与第 4 行重复", RowAt(result, 5).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyDuplicateErrors_RecomputesMarkersAfterTargetChange()
    {
        var path = Path.Combine(_directory, "duplicate-recheck.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("素材导入");
            SetImportRow(sheet, 2, title: "相同", content: "正文", channel: "板块一");
            SetImportRow(sheet, 3, title: "相同", content: "正文", channel: "板块二");
            workbook.SaveAs(path);
        }

        var now = DateTimeOffset.Now;
        var result = MaterialTemplateService.ParseWorkbook(path, CreateTargets(), now);
        Assert.True(RowAt(result, 2).IsValid);
        Assert.True(RowAt(result, 3).IsValid);

        var conflicting = result.Rows
            .Select(row => row.RowNumber == 3 ? row with { ChannelName = "板块一" } : row)
            .ToArray();
        var conflictingResult = MaterialTemplateService.ApplyDuplicateErrors(conflicting, CreateTargets(), now);
        Assert.Contains("与第 3 行重复", RowAt(new MaterialTemplateParseResult(conflictingResult, []), 2).Error);
        Assert.Contains("与第 2 行重复", RowAt(new MaterialTemplateParseResult(conflictingResult, []), 3).Error);

        var corrected = conflictingResult
            .Select(row => row.RowNumber == 3 ? row with { ChannelName = "板块二" } : row)
            .ToArray();
        var correctedResult = MaterialTemplateService.ApplyDuplicateErrors(corrected, CreateTargets(), now);
        Assert.True(RowAt(new MaterialTemplateParseResult(correctedResult, []), 2).IsValid);
        Assert.True(RowAt(new MaterialTemplateParseResult(correctedResult, []), 3).IsValid);
    }

    [Fact]
    public async Task ParseWorkbook_AcceptsMinutePrecisionPublishTime()
    {
        var path = Path.Combine(_directory, "minute-time.xlsx");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(path, MaterialTemplateService.CreateWorkbook(CreateTargets()));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("素材导入");
            SetImportRow(sheet, 2, title: "定时", content: "正文");
            sheet.Cell(2, 4).SetValue("2026-10-22 02:20");
            workbook.SaveAs(path);
        }

        var result = MaterialTemplateService.ParseWorkbook(
            path,
            CreateTargets(),
            new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.FromHours(8)));

        var row = Assert.Single(result.Rows);
        Assert.True(row.IsValid, row.Error);
        Assert.NotNull(row.PublishAt);
    }

    [Fact]
    public async Task SaveMaterialsBatch_IsAtomicAndCreatesScheduleRows()
    {
        var databasePath = Path.Combine(_directory, "materials.db");
        Directory.CreateDirectory(_directory);
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new ContentLibraryStore(databasePath, context);
        var scheduledAt = DateTimeOffset.Now.AddHours(1);

        var ids = await store.SaveMaterialsAsync([
            new MaterialDraft(null, "文本一", "text", "正文一", "guild", "频道", "channel", "版块", scheduledAt, "queue", "", []),
            new MaterialDraft(null, "文本二", "text", "正文二", "guild", "频道", "channel", "版块", null, "waitsend", "", [])
        ]);
        Assert.Equal(2, ids.Length);
        Assert.Equal(2, (await store.GetMaterialsAsync()).Count);
        Assert.Single(await store.GetScheduleExecutionsAsync());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveMaterialsAsync([
            new MaterialDraft(null, "有效", "text", "正文", "guild", "频道", "channel", "版块", null, "waitsend", "", []),
            new MaterialDraft(null, "无效", "bad", "正文", "guild", "频道", "channel", "版块", null, "waitsend", "", [])
        ]));
        Assert.Equal(2, (await store.GetMaterialsAsync()).Count);
    }

    [Fact]
    public async Task ImportedFileHash_IsUniquePerAccountAndAtomic()
    {
        var databasePath = Path.Combine(_directory, "import-history.db");
        Directory.CreateDirectory(_directory);
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new ContentLibraryStore(databasePath, context);
        var material = new MaterialDraft(null, "导入素材", "text", "导入正文", "guild", "频道", "channel", "版块", null, "waitsend", "", []);

        await store.SaveMaterialsAsync([material], "same-file");
        Assert.True(await store.HasImportedFileAsync("same-file"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveMaterialsAsync([material with { Title = "重复文件" }], "same-file"));
        Assert.Single(await store.GetMaterialsAsync());

        context.Set(CurrentAccountIdentity.Create("global-other", "nickname-other"));
        Assert.False(await store.HasImportedFileAsync("same-file"));
        await store.SaveMaterialsAsync([material], "same-file");
        Assert.Single(await store.GetMaterialsAsync());
    }

    private static MaterialTemplateTarget[] CreateTargets() =>
    [
        new MaterialTemplateTarget("1001", "频道一", "管理员", [
            new MaterialTemplateChannel("2001", "板块一"),
            new MaterialTemplateChannel("2002", "板块二")
        ]),
        new MaterialTemplateTarget("1002", "频道二", "成员", [
            new MaterialTemplateChannel("2003", "板块三")
        ])
    ];

    private static MaterialTemplateImportRow RowAt(MaterialTemplateParseResult result, int rowNumber) =>
        result.Rows.Single(row => row.RowNumber == rowNumber);

    private static void SetImportRow(
        IXLWorksheet sheet,
        int rowNumber,
        string title = "",
        string content = "",
        string guild = "频道一",
        string channel = "板块一",
        string media = "")
    {
        sheet.Cell(rowNumber, 1).Value = title;
        sheet.Cell(rowNumber, 2).Value = content;
        sheet.Cell(rowNumber, 3).Value = media;
        sheet.Cell(rowNumber, 5).Value = guild;
        sheet.Cell(rowNumber, 6).Value = channel;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var templatePath = Path.Combine(_directory, "template.xlsx");
        var importPath = Path.Combine(_directory, "import.xlsx");
        var invalidPath = Path.Combine(_directory, "invalid.xlsx");
        var crossGuildPath = Path.Combine(_directory, "cross-guild.xlsx");
        var validationPath = Path.Combine(_directory, "validation.xlsx");
        var expiredPath = Path.Combine(_directory, "expired.xlsx");
        var errorsPath = Path.Combine(_directory, "errors.xlsx");
        var databasePath = Path.Combine(_directory, "materials.db");
        var importHistoryPath = Path.Combine(_directory, "import-history.db");
        var typesPath = Path.Combine(_directory, "types.xlsx");
        var mediaOnlyPath = Path.Combine(_directory, "media-only.xlsx");
        var legacyHeaderPath = Path.Combine(_directory, "legacy-header.xlsx");
        var allIssuesPath = Path.Combine(_directory, "all-issues.xlsx");
        var publishLimitsPath = Path.Combine(_directory, "publish-limits.xlsx");
        var numericIdsPath = Path.Combine(_directory, "numeric-ids.xlsx");
        var duplicateKeyPath = Path.Combine(_directory, "duplicate-key.xlsx");
        var duplicateRecheckPath = Path.Combine(_directory, "duplicate-recheck.xlsx");
        var mixedResultPath = Path.Combine(_directory, "mixed-result.xlsx");
        var minuteTimePath = Path.Combine(_directory, "minute-time.xlsx");
        if (File.Exists(templatePath)) File.Delete(templatePath);
        if (File.Exists(importPath)) File.Delete(importPath);
        if (File.Exists(invalidPath)) File.Delete(invalidPath);
        if (File.Exists(crossGuildPath)) File.Delete(crossGuildPath);
        if (File.Exists(validationPath)) File.Delete(validationPath);
        if (File.Exists(expiredPath)) File.Delete(expiredPath);
        if (File.Exists(errorsPath)) File.Delete(errorsPath);
        if (File.Exists(databasePath)) File.Delete(databasePath);
        if (File.Exists(importHistoryPath)) File.Delete(importHistoryPath);
        if (File.Exists(typesPath)) File.Delete(typesPath);
        if (File.Exists(mediaOnlyPath)) File.Delete(mediaOnlyPath);
        if (File.Exists(legacyHeaderPath)) File.Delete(legacyHeaderPath);
        if (File.Exists(allIssuesPath)) File.Delete(allIssuesPath);
        if (File.Exists(publishLimitsPath)) File.Delete(publishLimitsPath);
        if (File.Exists(numericIdsPath)) File.Delete(numericIdsPath);
        if (File.Exists(duplicateKeyPath)) File.Delete(duplicateKeyPath);
        if (File.Exists(duplicateRecheckPath)) File.Delete(duplicateRecheckPath);
        if (File.Exists(mixedResultPath)) File.Delete(mixedResultPath);
        if (File.Exists(minuteTimePath)) File.Delete(minuteTimePath);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, false);
    }
}
