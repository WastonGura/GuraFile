using GuraFile.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GuraFile;

public sealed partial class MainWindow : Window
{
    private readonly ManagedRootScanner _scanner;
    private readonly FileQueryService _fileQuery;
    private readonly TagService _tags;
    private readonly ShellFileActions _shell = new();
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _fileQueryCancellation;
    private CancellationTokenSource? _detailCancellation;
    private FileSortColumn _sortColumn = FileSortColumn.Name;
    private bool _sortDescending;
    private bool _initialized;
    private bool _refreshingTags;
    private bool _changingFileTags;

    public MainWindow()
    {
        InitializeComponent();
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GuraFile",
            "index.db");
        _scanner = new(databasePath);
        _fileQuery = new(databasePath);
        _tags = new(databasePath);
        _initialized = true;
        Closed += (_, _) =>
        {
            _scanCancellation?.Cancel();
            _fileQueryCancellation?.Cancel();
            _detailCancellation?.Cancel();
        };
        RefreshRoots();
        _ = RefreshTagsAsync();
        _ = RefreshFilesAsync();
    }

    private async void AddRootButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        try
        {
            var root = _scanner.AddRoot(folder.Path);
            RefreshRoots();
            RootsList.SelectedItem = RootsList.Items.Cast<ManagedRoot>().FirstOrDefault(item => item.Id == root.Id);
            await RefreshFilesAsync();
        }
        catch (Exception exception)
        {
            ProgressText.Text = "添加根目录失败";
            FailureList.ItemsSource = new[] { exception.Message };
        }
    }

    private void RemoveRootButton_Click(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is not ManagedRoot root)
        {
            return;
        }

        _scanner.RemoveRoot(root.Id);
        RefreshRoots();
        ProgressText.Text = "根目录索引已移除；真实文件未更改";
        _ = RefreshFilesAsync();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is not ManagedRoot root || _scanCancellation is not null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        SetScanning(true);
        FailureList.ItemsSource = null;

        try
        {
            var result = await _scanner.ScanAsync(
                root.Id,
                progress: progress => DispatcherQueue.TryEnqueue(() => ShowProgress(progress)),
                cancellationToken: cancellation.Token);
            ProgressText.Text = result.Canceled
                ? $"已取消；保留 {result.CommittedFiles} 个已提交文件"
                : $"完成；新增 {result.AddedFiles}，更新 {result.UpdatedFiles}，缺失 {result.MissingFiles}，降级 {result.FallbackFiles}，失败 {result.Failures.Count}";
            FailureList.ItemsSource = result.Failures.Select(failure => $"{failure.Path}: {failure.Error}").ToList();
        }
        catch (Exception exception)
        {
            ProgressText.Text = "扫描失败";
            FailureList.ItemsSource = new[] { exception.Message };
        }
        finally
        {
            _scanCancellation = null;
            SetScanning(false);
            await RefreshFilesAsync();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
        {
            await RefreshFilesAsync(debounce: true);
        }
    }

    private async void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || !Enum.TryParse<FileSortColumn>(button.Tag?.ToString(), out var column))
        {
            return;
        }

        if (_sortColumn == column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = false;
        }

        UpdateSortLabels();
        await RefreshFilesAsync();
    }

    private async void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _detailCancellation?.Cancel();
        var selected = FilesList.SelectedItems.OfType<IndexedFile>().ToList();
        var hasSingleFile = selected.Count == 1;
        OpenFileButton.IsEnabled = hasSingleFile;
        RevealFileButton.IsEnabled = hasSingleFile;
        FileActionStatusText.Text = "";
        if (selected.Count != 1)
        {
            DetailsText.Text = selected.Count == 0 ? "未选择文件" : $"已选择 {selected.Count} 个文件";
            return;
        }

        var file = selected[0];
        var cancellation = new CancellationTokenSource();
        _detailCancellation = cancellation;
        DetailsText.Text = Describe(file, []);
        try
        {
            var tags = await Task.Run(() => _tags.ListTagsForFile(file.Id), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (ReferenceEquals(_detailCancellation, cancellation))
            {
                DetailsText.Text = Describe(file, tags);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_detailCancellation, cancellation))
            {
                DetailsText.Text = $"{Describe(file, [])}\n标签读取失败：{exception.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_detailCancellation, cancellation))
            {
                _detailCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void FilesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FindListViewItem(e.OriginalSource as DependencyObject)?.Content is not IndexedFile file)
        {
            return;
        }

        RunFileAction(file, _shell.Open, "已交给系统默认应用打开。");
    }

    private void OpenFileButton_Click(object sender, RoutedEventArgs e) =>
        RunSelectedFileAction(_shell.Open, "已交给系统默认应用打开。");

    private void RevealFileButton_Click(object sender, RoutedEventArgs e) =>
        RunSelectedFileAction(_shell.RevealInExplorer, "已在资源管理器中定位文件。");

    private void RunSelectedFileAction(Action<string> action, string successMessage)
    {
        if (FilesList.SelectedItems.OfType<IndexedFile>().ToList() is not [var file])
        {
            return;
        }

        RunFileAction(file, action, successMessage);
    }

    private void RunFileAction(IndexedFile file, Action<string> action, string successMessage)
    {
        try
        {
            action(file.Path);
            FileActionStatusText.Text = successMessage;
        }
        catch (Exception exception)
        {
            FileActionStatusText.Text = exception.Message;
        }
    }

    private static ListViewItem? FindListViewItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListViewItem item)
            {
                return item;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private async void TagsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingTags || !_initialized)
        {
            return;
        }

        var selected = SelectedTags();
        if (selected.Count == 1)
        {
            TagNameBox.Text = selected[0].Name;
        }

        if (TagFilterToggle.IsOn)
        {
            await RefreshFilesAsync();
        }
    }

    private async void TagFilterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            await RefreshFilesAsync();
        }
    }

    private async void TagMatchBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && TagFilterToggle.IsOn)
        {
            await RefreshFilesAsync();
        }
    }

    private async void CreateTagButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tag = await Task.Run(() => _tags.CreateTag(TagNameBox.Text));
            await RefreshTagsAsync(tag.Id);
            if (TagFilterToggle.IsOn)
            {
                await RefreshFilesAsync();
            }
            TagStatusText.Text = $"已创建标签“{tag.Name}”。";
        }
        catch (Exception exception)
        {
            TagStatusText.Text = exception.Message;
        }
    }

    private async void RenameTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTags() is not [var tag])
        {
            TagStatusText.Text = "请选择一个要重命名的标签。";
            return;
        }

        try
        {
            var renamed = await Task.Run(() => _tags.RenameTag(tag.Id, TagNameBox.Text));
            await RefreshTagsAsync(renamed.Id);
            if (TagFilterToggle.IsOn)
            {
                await RefreshFilesAsync();
            }
            TagStatusText.Text = $"已重命名为“{renamed.Name}”。";
        }
        catch (Exception exception)
        {
            TagStatusText.Text = exception.Message;
        }
    }

    private async void DeleteTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTags() is not [var tag])
        {
            TagStatusText.Text = "请选择一个要删除的标签。";
            return;
        }

        var dialog = new ContentDialog
        {
            Title = $"删除标签“{tag.Name}”？",
            Content = "这会移除该标签与文件的关系，但不会删除真实文件。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await Task.Run(() => _tags.DeleteTag(tag.Id));
            TagNameBox.Text = "";
            await RefreshTagsAsync();
            await RefreshFilesAsync();
            TagStatusText.Text = $"已删除标签“{tag.Name}”；真实文件未更改。";
        }
        catch (Exception exception)
        {
            TagStatusText.Text = exception.Message;
        }
    }

    private async void ApplyTagButton_Click(object sender, RoutedEventArgs e) =>
        await ChangeSelectedFileTagsAsync(add: true);

    private async void RemoveTagButton_Click(object sender, RoutedEventArgs e) =>
        await ChangeSelectedFileTagsAsync(add: false);

    private async Task ChangeSelectedFileTagsAsync(bool add)
    {
        if (_changingFileTags)
        {
            return;
        }

        if (SelectedTags() is not [var tag])
        {
            TagStatusText.Text = "请选择一个标签。";
            return;
        }

        var fileIds = FilesList.SelectedItems.OfType<IndexedFile>()
            .Select(file => file.Id)
            .Distinct()
            .ToArray();
        if (fileIds.Length == 0)
        {
            TagStatusText.Text = "请先在文件列表中选择一个或多个文件。";
            return;
        }

        try
        {
            _changingFileTags = true;
            ApplyTagButton.IsEnabled = false;
            RemoveTagButton.IsEnabled = false;
            if (add)
            {
                await Task.Run(() => _tags.AddTagToFiles(tag.Id, fileIds));
            }
            else
            {
                await Task.Run(() => _tags.RemoveTagFromFiles(tag.Id, fileIds));
            }

            await RefreshFilesAsync();
            TagStatusText.Text = add
                ? $"已给 {fileIds.Length} 个文件添加“{tag.Name}”。"
                : $"已从 {fileIds.Length} 个文件移除“{tag.Name}”。";
        }
        catch (Exception exception)
        {
            TagStatusText.Text = exception.Message;
        }
        finally
        {
            _changingFileTags = false;
            ApplyTagButton.IsEnabled = true;
            RemoveTagButton.IsEnabled = true;
        }
    }

    private async Task RefreshTagsAsync(long? selectedTagId = null)
    {
        _refreshingTags = true;
        try
        {
            var tags = await Task.Run(_tags.ListTags);
            TagsList.ItemsSource = tags;
            TagsList.SelectedItems.Clear();
            if (selectedTagId is not null
                && tags.FirstOrDefault(tag => tag.Id == selectedTagId) is { } selected)
            {
                TagsList.SelectedItems.Add(selected);
            }
        }
        catch (Exception exception)
        {
            TagStatusText.Text = $"标签加载失败：{exception.Message}";
        }
        finally
        {
            _refreshingTags = false;
        }
    }

    private IReadOnlyList<UserTag> SelectedTags() =>
        TagsList.SelectedItems.OfType<UserTag>().ToList();

    private async Task RefreshFilesAsync(bool debounce = false)
    {
        var cancellation = new CancellationTokenSource();
        var previous = _fileQueryCancellation;
        _fileQueryCancellation = cancellation;
        previous?.Cancel();

        try
        {
            if (debounce)
            {
                await Task.Delay(200, cancellation.Token);
            }

            if (!ReferenceEquals(_fileQueryCancellation, cancellation))
            {
                return;
            }

            FilesLoadingRing.IsActive = true;
            FilesLoadingRing.Visibility = Visibility.Visible;
            FilesStateText.Text = "正在加载文件…";
            var tagIds = TagFilterToggle.IsOn
                ? SelectedTags().Select(tag => tag.Id).ToArray()
                : null;
            var files = await _fileQuery.QueryAsync(
                new(
                    Search: SearchBox.Text,
                    SortBy: _sortColumn,
                    Descending: _sortDescending,
                    TagIds: tagIds,
                    TagMatch: TagMatchBox.SelectedIndex == 1 ? TagMatchMode.All : TagMatchMode.Any),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            FilesList.ItemsSource = files;
            FilesStateText.Text = files.Count == 0 ? "没有匹配的文件" : $"{files.Count:N0} 个文件";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_fileQueryCancellation, cancellation))
            {
                FilesList.ItemsSource = null;
                FilesStateText.Text = $"文件列表加载失败：{exception.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_fileQueryCancellation, cancellation))
            {
                FilesLoadingRing.IsActive = false;
                FilesLoadingRing.Visibility = Visibility.Collapsed;
                _fileQueryCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void RefreshRoots()
    {
        RootsList.ItemsSource = _scanner.ListRoots();
        RootsList.SelectedIndex = RootsList.Items.Count > 0 ? 0 : -1;
    }

    private void ShowProgress(ScanProgress progress) =>
        ProgressText.Text = $"发现 {progress.DiscoveredFiles} 个，提交 {progress.CommittedFiles} 个，失败 {progress.FailedItems} 项";

    private void SetScanning(bool scanning)
    {
        ScanProgressRing.IsActive = scanning;
        ScanProgressRing.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = scanning;
        AddRootButton.IsEnabled = !scanning;
        RemoveRootButton.IsEnabled = !scanning;
        ScanButton.IsEnabled = !scanning;
    }

    private void UpdateSortLabels()
    {
        SortNameButton.Content = SortLabel("名称", FileSortColumn.Name);
        SortPathButton.Content = SortLabel("路径", FileSortColumn.Path);
        SortExtensionButton.Content = SortLabel("扩展名", FileSortColumn.Extension);
        SortSizeButton.Content = SortLabel("大小（字节）", FileSortColumn.Size);
        SortModifiedButton.Content = SortLabel("修改时间", FileSortColumn.Modified);
    }

    private string SortLabel(string label, FileSortColumn column) =>
        _sortColumn == column ? $"{label} {(_sortDescending ? '▼' : '▲')}" : label;

    private static string Describe(IndexedFile file, IReadOnlyList<UserTag> tags)
    {
        var status = file.IsOnline ? "在线" : "离线";
        var tagNames = tags.Count == 0 ? "无" : string.Join("、", tags.Select(tag => tag.Name));
        var diagnostic = string.IsNullOrWhiteSpace(file.Diagnostic) ? "" : $"\n诊断：{file.Diagnostic}";
        return $"{file.Name}\n{file.Path}\n扩展名：{file.Extension}\n大小：{file.Size:N0} 字节\n修改时间：{file.Modified.LocalDateTime:g}\n状态：{status}\n标签：{tagNames}{diagnostic}";
    }
}
