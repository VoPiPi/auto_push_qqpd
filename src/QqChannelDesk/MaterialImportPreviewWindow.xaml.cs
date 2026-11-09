using Microsoft.Win32;
using QqChannelDesk.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace QqChannelDesk;

public partial class MaterialImportPreviewWindow : Window
{
    private readonly IReadOnlyList<MaterialTemplateTarget> _targets;
    private readonly ObservableCollection<MaterialImportPreviewItem> _items;
    private readonly ListCollectionView _view;
    private readonly Func<IReadOnlyList<MaterialTemplateImportRow>, Task<int>>? _importAsync;
    private readonly DateTimeOffset _now;
    private readonly bool _templateChanged;
    private readonly IReadOnlyList<string> _resultErrors;
    private string _activeFilter = "all";
    private int _closed;
    private int _stateUpdatePending;
    private bool _updatingPreviewState;
    private bool _busy;
    private bool _fileImported;
    private int _importedCount;

    public MaterialImportPreviewWindow(
        MaterialTemplateParseResult result,
        IReadOnlyList<MaterialTemplateTarget> targets,
        DateTimeOffset? now = null,
        Func<IReadOnlyList<MaterialTemplateImportRow>, Task<int>>? importAsync = null)
    {
        InitializeComponent();
        _targets = targets;
        _templateChanged = result.TemplateChanged;
        _resultErrors = result.Errors;
        _importAsync = importAsync;
        _now = now ?? DateTimeOffset.Now;
        _items = new ObservableCollection<MaterialImportPreviewItem>(
            result.Rows.Select(row => new MaterialImportPreviewItem(row, targets, _now)));
        foreach (var item in _items)
            item.PropertyChanged += PreviewItem_PropertyChanged;
        _view = new ListCollectionView(_items)
        {
            Filter = FilterPreviewItem
        };
        PreviewGrid.ItemsSource = _view;
        UpdateFilterHeaders();
        if (FailureCount > 0)
            InvalidFilterButton.IsChecked = true;
        else
            AllFilterButton.IsChecked = true;
        UpdateState();
    }

    public int SuccessCount => _items.Count(item => item.IsValid);
    public int FailureCount => _items.Count(item => !item.IsValid);
    public int ImportedCount => _importedCount;
    public int RemainingCount => _items.Count;

    private IReadOnlyList<MaterialTemplateImportRow> RowsToImport =>
        _items.Where(item => item.IsValid).Select(item => item.ToImportRow()).ToArray();

    private IReadOnlyList<MaterialTemplateImportRow> ErrorRows =>
        _items.Where(item => !item.IsValid).Select(item => item.ToImportRow()).ToArray();

    private bool FilterPreviewItem(object item)
    {
        if (item is not MaterialImportPreviewItem previewItem) return false;
        return _activeFilter switch
        {
            "valid" => previewItem.IsValid,
            "invalid" => !previewItem.IsValid,
            _ => true
        };
    }

    private void FilterButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string filter }) return;
        _activeFilter = filter;
        RefreshViewPreservingSelection();
    }

    private void UpdateState()
    {
        UpdateFilterHeaders();
        RefreshViewPreservingSelection();

        SummaryText.Text = $"共 {_items.Count} 条：可导入 {SuccessCount} 条，待修正 {FailureCount} 条";
        var notices = new List<string>();
        if (_templateChanged)
            notices.Add("模板中的频道/版块快照与当前账号不一致，将按当前频道数据继续检查。请在导入前确认频道和版块。");
        notices.AddRange(_resultErrors.Where(error => !notices.Contains(error, StringComparer.Ordinal)));
        TemplateStatusText.Text = string.Join("\n", notices);
        TemplateStatusPanel.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ExportErrorsButton.IsEnabled = !_busy && FailureCount > 0;
        ConfirmButton.IsEnabled = !_busy && !_fileImported && SuccessCount > 0;
        ConfirmButton.Content = _fileImported
            ? "本次导入已处理"
            : FailureCount > 0
                ? $"跳过并导入 {SuccessCount} 条"
                : $"导入 {SuccessCount} 条";
        PreviewGrid.IsEnabled = !_busy;
        AllFilterButton.IsEnabled = !_busy;
        ValidFilterButton.IsEnabled = !_busy;
        InvalidFilterButton.IsEnabled = !_busy;

        if (_busy)
            HintText.Text = "正在写入素材库，请稍候。";
        else if (_fileImported)
            HintText.Text = FailureCount > 0
                ? $"已导入 {_importedCount} 条，保留 {FailureCount} 条待修正项。请导出后在 Excel 修正，再重新导入。"
                : $"已导入 {_importedCount} 条。";
        else if (_items.Count == 0)
            HintText.Text = _resultErrors.Count > 0 ? "没有可导入的数据行，请根据提示检查工作簿。" : "没有可导入的数据行。";
        else if (FailureCount > 0)
            HintText.Text = "可返回 Excel 修正待修正项，也可先补填频道和版块，再跳过剩余项导入。";
        else
            HintText.Text = "全部数据检查通过；未来发布时间会同时创建发布计划。";

        UpdateSelectionDetails();
    }

    private void UpdateFilterHeaders()
    {
        AllFilterButton.Content = $"全部 {_items.Count}";
        ValidFilterButton.Content = $"可导入 {SuccessCount}";
        InvalidFilterButton.Content = $"待修正 {FailureCount}";
    }

    private void RefreshViewPreservingSelection()
    {
        var selectedRow = (PreviewGrid.SelectedItem as MaterialImportPreviewItem)?.RowNumber;
        _view.Refresh();
        var visibleItems = _view.Cast<MaterialImportPreviewItem>().ToArray();
        if (visibleItems.Length == 0)
        {
            PreviewGrid.SelectedItem = null;
            return;
        }

        PreviewGrid.SelectedItem = visibleItems.FirstOrDefault(item => item.RowNumber == selectedRow)
            ?? visibleItems[0];
    }

    private void PreviewGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSelectionDetails();

    private void UpdateSelectionDetails()
    {
        if (PreviewGrid.SelectedItem is not MaterialImportPreviewItem item)
        {
            SelectedRowTitleText.Text = "选择一行查看问题";
            SelectedRowMetaText.Text = "完整问题会固定显示在这里。";
            SelectedIssuesList.ItemsSource = null;
            SelectedIssuesList.Visibility = Visibility.Collapsed;
            SelectedIssuesEmptyText.Visibility = Visibility.Visible;
            SelectedIssuesEmptyText.Text = "当前筛选下没有数据行。";
            SelectedCorrectionHintText.Text = "";
            return;
        }

        SelectedRowTitleText.Text = string.IsNullOrWhiteSpace(item.Title)
            ? $"第 {item.RowNumber} 行 · 未填写标题"
            : $"第 {item.RowNumber} 行 · {item.Title}";
        SelectedRowMetaText.Text = $"{item.TypeName} · {item.TargetDisplay} · {item.PublishAtDisplay}";
        SelectedIssuesList.ItemsSource = item.Issues;
        SelectedIssuesList.Visibility = item.HasIssues ? Visibility.Visible : Visibility.Collapsed;
        SelectedIssuesEmptyText.Visibility = item.HasIssues ? Visibility.Collapsed : Visibility.Visible;
        SelectedIssuesEmptyText.Text = item.HasIssues ? "" : "该行检查通过，可直接导入。";
        SelectedIssuesEmptyText.Foreground = item.HasIssues
            ? FindResource("Danger") as System.Windows.Media.Brush
            : FindResource("Success") as System.Windows.Media.Brush;
        SelectedCorrectionHintText.Text = item.CorrectionHint;
    }

    private void PreviewItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updatingPreviewState &&
            e.PropertyName is nameof(MaterialImportPreviewItem.GuildName)
            or nameof(MaterialImportPreviewItem.ChannelName)
            or nameof(MaterialImportPreviewItem.IsValid))
        {
            ScheduleStateUpdate();
        }
    }

    private void ScheduleStateUpdate()
    {
        if (Volatile.Read(ref _closed) != 0 ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished ||
            Interlocked.Exchange(ref _stateUpdatePending, 1) != 0)
        {
            return;
        }

        UiAsync.Post(
            Dispatcher,
            () =>
            {
                Interlocked.Exchange(ref _stateUpdatePending, 0);
                if (Volatile.Read(ref _closed) == 0)
                {
                    _updatingPreviewState = true;
                    try
                    {
                        RecomputeDuplicateErrors();
                        UpdateState();
                    }
                    finally
                    {
                        _updatingPreviewState = false;
                    }
                }
            },
            DispatcherPriority.DataBind);
    }

    private void RecomputeDuplicateErrors()
    {
        var updatedRows = MaterialTemplateService.ApplyDuplicateErrors(
            _items.Select(item => item.ToImportRow()).ToArray(),
            _targets,
            _now);
        for (var index = 0; index < updatedRows.Count; index++)
            _items[index].ReplaceRow(updatedRows[index]);
    }

    private void PreviewGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Column is DataGridTextColumn) e.Cancel = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ExportErrorsButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("导出素材导入待修正项", async () =>
        {
            var errors = ErrorRows;
            if (errors.Count == 0) return;
            var dialog = new SaveFileDialog
            {
                Title = "导出待修正项",
                Filter = "Excel 工作簿|*.xlsx",
                FileName = $"素材导入待修正项-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var workbookBytes = await Task.Run(() => MaterialTemplateService.CreateErrorWorkbook(errors, _targets));
                await File.WriteAllBytesAsync(dialog.FileName, workbookBytes);
                HintText.Text = $"待修正项已导出：{Path.GetFileName(dialog.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"{CliDiagnostics.Sanitize(ex.Message)}\n\n导出未完成；请重试或选择其他保存位置。",
                    "导出待修正项",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        });

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _fileImported) return;
        var rows = RowsToImport;
        if (rows.Count == 0) return;

        if (FailureCount > 0)
        {
            var confirmation = new MaterialImportConfirmationWindow(rows.Count, FailureCount)
            {
                Owner = this
            };
            if (confirmation.ShowDialog() != true) return;
        }

        _busy = true;
        UpdateState();
        try
        {
            var importedCount = _importAsync is null
                ? rows.Count
                : await _importAsync(rows);
            if (importedCount != rows.Count)
                throw new InvalidOperationException("导入结果与待写入数量不一致，请刷新素材库后检查。");

            foreach (var row in rows)
            {
                var item = _items.FirstOrDefault(candidate => candidate.RowNumber == row.RowNumber);
                if (item is not null) _items.Remove(item);
            }
            _importedCount += importedCount;
            _fileImported = true;
            _busy = false;

            if (_items.Count == 0)
            {
                UpdateState();
                DialogResult = true;
                return;
            }

            InvalidFilterButton.IsChecked = true;
            UpdateState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"导入未完成，素材库未写入本批次数据。\n\n{CliDiagnostics.Sanitize(ex.Message)}",
                "导入未完成",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            if (Volatile.Read(ref _closed) == 0) UpdateState();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Volatile.Write(ref _closed, 1);
        base.OnClosed(e);
    }
}

public sealed class MaterialImportPreviewItem : INotifyPropertyChanged
{
    private readonly IReadOnlyList<MaterialTemplateTarget> _targets;
    private readonly DateTimeOffset _now;
    private readonly ObservableCollection<string> _channelOptions = [];
    private MaterialTemplateImportRow _row;
    private string _guildName;
    private string _channelName;

    public MaterialImportPreviewItem(MaterialTemplateImportRow row, IReadOnlyList<MaterialTemplateTarget> targets, DateTimeOffset now)
    {
        _row = row;
        _targets = targets;
        _now = now;
        _guildName = row.GuildName;
        _channelName = row.ChannelName;
        GuildOptions = targets.Select(target => target.GuildName).Distinct(StringComparer.Ordinal).ToArray();
        RefreshChannelOptions();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public int RowNumber => _row.RowNumber;
    public string Title => _row.Title;
    public string Content => _row.Content;
    public string TypeName => _row.Type switch
    {
        "text" => "文本",
        "image" => "图片",
        "video" => "视频",
        _ => "未识别"
    };
    public string PublishAtDisplay => _row.PublishAtDisplay;
    public string MediaDisplay => _row.MediaDisplay;
    public string TargetDisplay => _row.TargetDisplay;
    public IReadOnlyList<string> GuildOptions { get; }
    public ObservableCollection<string> ChannelOptions => _channelOptions;
    public bool IsValid => _row.IsValid;
    public string Error => _row.Error;
    public IReadOnlyList<string> Issues => _row.Issues;
    public bool HasIssues => Issues.Count > 0;
    public string StatusLabel => IsValid ? "可导入" : "待修正";

    public string CorrectionHint
    {
        get
        {
            if (!HasIssues) return "该行检查通过，可随批次一起导入。";
            var hasTargetFix = Issues.Any(IsTargetIssue);
            var hasExcelFix = Issues.Any(issue => !IsTargetIssue(issue));
            return (hasTargetFix, hasExcelFix) switch
            {
                (true, true) => "频道和版块可在左侧直接补填；正文、媒体、发布时间或重复项需回到 Excel 修正。",
                (true, false) => "可直接在左侧选择频道和版块，补填后该行会重新检查。",
                _ => "需回到 Excel 修正该行；可从窗口底部导出待修正项，保留原行号继续处理。"
            };
        }
    }

    public string GuildName
    {
        get => _guildName;
        set
        {
            var valueToUse = value?.Trim() ?? "";
            if (string.Equals(_guildName, valueToUse, StringComparison.Ordinal)) return;
            _guildName = valueToUse;
            RefreshChannelOptions();
            Revalidate();
            OnPropertyChanged(nameof(ChannelName));
            OnPropertyChanged();
        }
    }

    public string ChannelName
    {
        get => _channelName;
        set
        {
            var valueToUse = value?.Trim() ?? "";
            if (string.Equals(_channelName, valueToUse, StringComparison.Ordinal)) return;
            _channelName = valueToUse;
            Revalidate();
            OnPropertyChanged();
        }
    }

    public void ReplaceRow(MaterialTemplateImportRow row)
    {
        _row = row;
        NotifyRowChanged();
    }

    public MaterialTemplateImportRow ToImportRow() => _row;

    private static bool IsTargetIssue(string issue) =>
        issue.StartsWith("E 列", StringComparison.Ordinal) ||
        issue.StartsWith("F 列", StringComparison.Ordinal);

    private void RefreshChannelOptions()
    {
        var names = _targets
            .Where(target => string.Equals(target.GuildName, _guildName, StringComparison.Ordinal))
            .SelectMany(target => target.Channels)
            .Select(channel => channel.ChannelName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _channelOptions.Clear();
        foreach (var name in names) _channelOptions.Add(name);
        if (!_channelOptions.Contains(_channelName, StringComparer.Ordinal)) _channelName = "";
        OnPropertyChanged(nameof(ChannelOptions));
    }

    private void Revalidate()
    {
        _row = MaterialTemplateService.ValidateImportRow(
            _row with { GuildName = _guildName, ChannelName = _channelName },
            _targets,
            _now);
        NotifyRowChanged();
    }

    private void NotifyRowChanged()
    {
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(Issues));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(TargetDisplay));
        OnPropertyChanged(nameof(CorrectionHint));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
