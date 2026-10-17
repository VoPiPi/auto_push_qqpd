using Microsoft.Win32;
using QqChannelDesk.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace QqChannelDesk;

public partial class MaterialImportPreviewWindow : Window
{
    private readonly IReadOnlyList<MaterialTemplateTarget> _targets;
    private readonly ObservableCollection<MaterialImportPreviewItem> _items;
    private readonly bool _templateExpired;
    private readonly IReadOnlyList<string> _resultErrors;
    private int _closed;

    public MaterialImportPreviewWindow(
        MaterialTemplateParseResult result,
        IReadOnlyList<MaterialTemplateTarget> targets,
        DateTimeOffset? now = null)
    {
        InitializeComponent();
        _targets = targets;
        _templateExpired = result.TemplateExpired;
        _resultErrors = result.Errors;
        _items = new ObservableCollection<MaterialImportPreviewItem>(
            result.Rows.Select(row => new MaterialImportPreviewItem(row, targets, now ?? DateTimeOffset.Now)));
        PreviewGrid.ItemsSource = _items;
        UpdateState();
    }

    public IReadOnlyList<MaterialTemplateImportRow> RowsToImport =>
        _items.Where(item => item.IsValid).Select(item => item.ToImportRow()).ToArray();

    public IReadOnlyList<MaterialTemplateImportRow> ErrorRows =>
        _items.Where(item => !item.IsValid).Select(item => item.ToImportRow()).ToArray();

    public int SuccessCount => RowsToImport.Count;
    public int FailureCount => ErrorRows.Count;

    private void UpdateState()
    {
        SummaryText.Text = $"共 {_items.Count} 条，成功 {SuccessCount} 条，失败 {FailureCount} 条";
        TemplateStatusText.Text = _templateExpired
            ? "模板已过期：频道或版块数据已变化，请重新下载模板。"
            : string.Join("；", _resultErrors);
        ExportErrorsButton.IsEnabled = FailureCount > 0;
        if (_templateExpired)
            HintText.Text = "请重新下载模板后再导入。当前失败项仍可导出。";
        else if (FailureCount > 0)
            HintText.Text = "确认后只导入成功项；失败项可修正后导出，也可在本页补填频道和版块。";
        else if (_items.Count > 0)
            HintText.Text = "确认后写入素材库；未来发布时间会同时创建发布计划。";
        else
            HintText.Text = _resultErrors.Count > 0 ? string.Join("；", _resultErrors) : "没有可导入的数据行。";
        ConfirmButton.IsEnabled = !_templateExpired && SuccessCount > 0;
    }

    private void GuildComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo || combo.DataContext is not MaterialImportPreviewItem item) return;
        item.RefreshAfterGuildSelection();
        ScheduleStateUpdate();
    }

    private void ChannelComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo || combo.DataContext is not MaterialImportPreviewItem) return;
        ScheduleStateUpdate();
    }

    private void ScheduleStateUpdate()
    {
        if (Volatile.Read(ref _closed) != 0 ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }

        UiAsync.Post(
            Dispatcher,
            () =>
            {
                if (Volatile.Read(ref _closed) == 0) UpdateState();
            },
            DispatcherPriority.DataBind);
    }

    private void PreviewGrid_BeginningEdit(object sender, System.Windows.Controls.DataGridBeginningEditEventArgs e)
    {
        if (e.Column is System.Windows.Controls.DataGridTextColumn) e.Cancel = true;
    }

    private void ExportErrorsButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("导出素材导入失败项", async () =>
        {
        var errors = ErrorRows;
        if (errors.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出失败项",
            Filter = "Excel 工作簿|*.xlsx",
            FileName = $"素材导入失败项-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var workbookBytes = await Task.Run(() => MaterialTemplateService.CreateErrorWorkbook(errors, _targets));
            await File.WriteAllBytesAsync(dialog.FileName, workbookBytes);
            HintText.Text = $"失败项已导出：{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"{CliDiagnostics.Sanitize(ex.Message)}\n\n导出未完成；请重试或选择其他保存位置。",
                "导出失败项", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        });

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (SuccessCount == 0) return;
        DialogResult = true;
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
    public string TypeName
    {
        get => _row.Type switch
        {
            "text" => "文本",
            "image" => "图片",
            "video" => "视频",
            _ => ""
        };
    }
    public string PublishAtDisplay => _row.PublishAtDisplay;
    public string MediaDisplay => _row.MediaDisplay;
    public IReadOnlyList<string> GuildOptions { get; }
    public ObservableCollection<string> ChannelOptions => _channelOptions;
    public bool IsValid => _row.IsValid;
    public string Error => _row.Error;

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

    public void RefreshAfterGuildSelection()
    {
        RefreshChannelOptions();
        Revalidate();
        OnPropertyChanged(nameof(ChannelName));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(IsValid));
    }

    public MaterialTemplateImportRow ToImportRow() => _row with
    {
        GuildName = _guildName,
        ChannelName = _channelName
    };

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
            _row with { GuildName = _guildName, ChannelName = _channelName }, _targets, _now);
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(IsValid));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
