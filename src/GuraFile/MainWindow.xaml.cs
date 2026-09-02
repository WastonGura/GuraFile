using System.Runtime.InteropServices;
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
    private readonly FileChangeCoordinator _fileChanges;
    private readonly FileQueryService _fileQuery;
    private readonly TagService _tags;
    private readonly UserTagBackupService _tagBackup;
    private readonly ShellFileActions _shell = new();
    private readonly IFileClipboardService _clipboard;
    private readonly FileListOperationService _fileOperations;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _fileQueryCancellation;
    private CancellationTokenSource? _detailCancellation;
    private CancellationTokenSource? _fileOpCancellation;
    private FileSortColumn _sortColumn = FileSortColumn.Name;
    private bool _sortDescending;
    private bool _initialized;
    private bool _refreshingTags;
    private bool _refreshingAutomaticTags;
    private bool _changingFileTags;
    private bool _transferringTags;
    private bool _isScanning;
    private bool _isOperating;

    public MainWindow()
    {
        InitializeComponent();
        Title = "GuraFile";
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GuraFile",
            "index.db");
        _scanner = new(databasePath);
        _fileChanges = new(
            _scanner,
            result => DispatcherQueue.TryEnqueue(() => _ = ShowRealtimeResultAsync(result)),
            exception => DispatcherQueue.TryEnqueue(() => ShowRealtimeError(exception)),
            onRootChanged: () => DispatcherQueue.TryEnqueue(RefreshRoots));
        _fileQuery = new(databasePath);
        _tags = new(databasePath);
        _tagBackup = new(databasePath);
        _clipboard = new FileClipboardService();
        _fileOperations = new FileListOperationService(new FileOperationIndexCommitter(_scanner), _scanner, _clipboard);
        _initialized = true;
        Closed += async (_, _) =>
        {
            _scanCancellation?.Cancel();
            _fileQueryCancellation?.Cancel();
            _detailCancellation?.Cancel();
            _fileOpCancellation?.Cancel();
            await _fileChanges.DisposeAsync();
        };
        RefreshRoots();
        foreach (var root in _scanner.ListRoots())
        {
            _fileChanges.Start(root);
        }
        _ = RefreshTagsAsync();
        _ = RefreshAutomaticTagsAsync();
        _ = RefreshFilesAsync();
        UpdateFileButtonsState();
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
            var root = await Task.Run(() => _scanner.AddRoot(folder.Path));
            _fileChanges.Start(root);
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

    private async void RemoveRootButton_Click(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is not ManagedRoot root)
        {
            return;
        }

        _fileChanges.Unwatch(root.Id);
        try
        {
            await Task.Run(() => _scanner.RemoveRoot(root.Id));
        }
        catch (Exception exception)
        {
            var restored = _fileChanges.Watch(root);
            ProgressText.Text = restored ? "移除根目录失败" : "移除根目录失败，且实时监听恢复失败";
            FailureList.ItemsSource = restored
                ? new[] { exception.Message }
                : new[] { exception.Message, "根目录实时监听未恢复；请确认目录可访问。" };
            return;
        }

        RefreshRoots();
        ProgressText.Text = "根目录索引已移除；真实文件未更改";
        await RefreshAutomaticTagsAsync();
        await RefreshFilesAsync();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is not ManagedRoot root || _scanCancellation is not null || _isOperating)
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
            RefreshRoots();
            await RefreshAutomaticTagsAsync();
            await RefreshFilesAsync();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    private async Task ShowRealtimeResultAsync(ScanResult result)
    {
        RefreshRoots();
        ProgressText.Text =
            $"实时更新；新增 {result.AddedFiles}，更新 {result.UpdatedFiles}，缺失 {result.MissingFiles}，失败 {result.Failures.Count}";
        FailureList.ItemsSource = result.Failures.Select(failure => $"{failure.Path}: {failure.Error}").ToList();
        await RefreshAutomaticTagsAsync();
        await RefreshFilesAsync();
    }

    private void ShowRealtimeError(Exception exception)
    {
        RefreshRoots();
        ProgressText.Text = "实时更新失败";
        FailureList.ItemsSource = new[] { exception.Message };
    }

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
        UpdateFileButtonsState();
        FileActionStatusText.Text = "";
        if (selected.Count != 1)
        {
            DetailsText.Text = selected.Count == 0 ? "未选择文件" : $"已选择 {selected.Count} 个文件";
            return;
        }

        var file = selected[0];
        var cancellation = new CancellationTokenSource();
        _detailCancellation = cancellation;
        DetailsText.Text = Describe(file, [], []);
        try
        {
            var tags = await Task.Run(
                () => (_tags.ListTagsForFile(file.Id), _tags.ListAutomaticTagsForFile(file.Id)),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (ReferenceEquals(_detailCancellation, cancellation))
            {
                DetailsText.Text = Describe(file, tags.Item1, tags.Item2);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_detailCancellation, cancellation))
            {
                DetailsText.Text = $"{Describe(file, [], [])}\n标签读取失败：{exception.Message}";
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

    private void CopyFileButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = FilesList.SelectedItems.OfType<IndexedFile>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        try
        {
            _fileOperations.CopyToClipboard(selected.Select(f => f.Path).ToList());
            FileActionStatusText.Text = $"已复制 {selected.Count} 个文件到剪贴板。";
            PasteToFileButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            FileActionStatusText.Text = $"复制失败：{exception.Message}";
        }
    }

    private void CutFileButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = FilesList.SelectedItems.OfType<IndexedFile>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        try
        {
            _fileOperations.CutToClipboard(selected.Select(f => f.Path).ToList());
            FileActionStatusText.Text = $"已剪切 {selected.Count} 个文件到剪贴板。";
            PasteToFileButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            FileActionStatusText.Text = $"剪切失败：{exception.Message}";
        }
    }

    private async void PasteToFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isOperating)
        {
            return;
        }

        var content = _fileOperations.GetClipboardContent();
        if (content is null || content.Files.Count == 0)
        {
            FileActionStatusText.Text = "剪贴板中没有文件。";
            PasteToFileButton.IsEnabled = false;
            return;
        }

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var policy = GetSelectedCollisionPolicy();
        var isMove = content.Effect == FileClipboardEffect.Move;
        var opName = isMove ? "粘贴（移动）" : "粘贴（复制）";

        await ExecuteFileOperationBatchAsync(opName, async (progress, cancellationToken) =>
        {
            return await _fileOperations.PasteFromClipboardAsync(
                folder.Path,
                policy,
                WindowNative.GetWindowHandle(this),
                progress,
                cancellationToken);
        });
    }

    private async void MoveToFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isOperating)
        {
            return;
        }

        var selected = FilesList.SelectedItems.OfType<IndexedFile>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var policy = GetSelectedCollisionPolicy();
        var sourcePaths = selected.Select(f => f.Path).ToList();

        await ExecuteFileOperationBatchAsync("移动", async (progress, cancellationToken) =>
        {
            return await _fileOperations.MoveToAsync(
                sourcePaths,
                folder.Path,
                policy,
                WindowNative.GetWindowHandle(this),
                progress,
                cancellationToken);
        });
    }

    private async void RenameFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isOperating)
        {
            return;
        }

        var selected = FilesList.SelectedItems.OfType<IndexedFile>().ToList();
        if (selected.Count != 1)
        {
            FileActionStatusText.Text = "请选择一个要重命名的文件。";
            return;
        }

        var file = selected[0];
        if (!file.IsOnline)
        {
            FileActionStatusText.Text = "无法重命名离线文件。";
            return;
        }

        var currentFileName = Path.GetFileName(file.Path);
        var textBox = new TextBox
        {
            Text = currentFileName,
            SelectionStart = 0,
            SelectionLength = Path.GetFileNameWithoutExtension(currentFileName).Length
        };

        var dialog = new ContentDialog
        {
            Title = "重命名文件",
            Content = textBox,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var newName = textBox.Text;
        try
        {
            FileListOperationService.ValidateNewFileName(newName);
        }
        catch (Exception ex)
        {
            FileActionStatusText.Text = ex.Message;
            FailureList.ItemsSource = new[] { $"{file.Path}: {ex.Message}" };
            return;
        }

        var policy = GetSelectedCollisionPolicy();

        await ExecuteFileOperationItemAsync("重命名", async cancellationToken =>
        {
            return await _fileOperations.RenameAsync(
                file.Path,
                newName,
                policy,
                WindowNative.GetWindowHandle(this),
                cancellationToken);
        });
    }

    private void CancelFileOperationButton_Click(object sender, RoutedEventArgs e) =>
        _fileOpCancellation?.Cancel();

    private async Task ExecuteFileOperationBatchAsync(
        string operationName,
        Func<Action<FileOperationProgress>, CancellationToken, Task<FileOperationCommitBatchResult>> action)
    {
        using var cancellation = new CancellationTokenSource();
        _fileOpCancellation = cancellation;
        SetOperating(true);
        FailureList.ItemsSource = null;
        FileActionStatusText.Text = $"正在执行{operationName}…";

        try
        {
            var result = await action(
                progress => DispatcherQueue.TryEnqueue(() =>
                {
                    FileActionStatusText.Text = $"正在执行{operationName}… ({progress.CompletedItems}/{progress.TotalItems}) {Path.GetFileName(progress.CurrentSourcePath)}";
                }),
                cancellation.Token);

            var summary = FileListOperationService.FormatBatchSummary(result, operationName);
            FileActionStatusText.Text = summary;

            var failures = result.Items
                .Where(i => i.Status == FileOperationItemStatus.Failed)
                .Select(i => $"{i.SourcePath}: {i.Error}")
                .ToList();

            if (failures.Count > 0)
            {
                FailureList.ItemsSource = failures;
            }
        }
        catch (Exception exception)
        {
            FileActionStatusText.Text = $"{operationName}失败：{exception.Message}";
            FailureList.ItemsSource = new[] { exception.Message };
        }
        finally
        {
            _fileOpCancellation = null;
            SetOperating(false);
            RefreshRoots();
            await RefreshTagsAsync();
            await RefreshAutomaticTagsAsync();
            await RefreshFilesAsync();
            UpdateFileButtonsState();
        }
    }

    private async Task ExecuteFileOperationItemAsync(
        string operationName,
        Func<CancellationToken, Task<FileOperationCommitItemResult>> action)
    {
        using var cancellation = new CancellationTokenSource();
        _fileOpCancellation = cancellation;
        SetOperating(true);
        FailureList.ItemsSource = null;
        FileActionStatusText.Text = $"正在执行{operationName}…";

        try
        {
            var result = await action(cancellation.Token);

            if (result.Succeeded)
            {
                FileActionStatusText.Text = $"{operationName}成功：{Path.GetFileName(result.ActualTargetPath)}";
            }
            else if (result.IsCanceled)
            {
                FileActionStatusText.Text = $"{operationName}已取消。";
            }
            else
            {
                FileActionStatusText.Text = $"{operationName}失败：{result.Error}";
                FailureList.ItemsSource = new[] { $"{result.SourcePath}: {result.Error}" };
            }
        }
        catch (Exception exception)
        {
            FileActionStatusText.Text = $"{operationName}失败：{exception.Message}";
            FailureList.ItemsSource = new[] { exception.Message };
        }
        finally
        {
            _fileOpCancellation = null;
            SetOperating(false);
            RefreshRoots();
            await RefreshTagsAsync();
            await RefreshAutomaticTagsAsync();
            await RefreshFilesAsync();
            UpdateFileButtonsState();
        }
    }

    private FileCollisionPolicy GetSelectedCollisionPolicy() =>
        CollisionPolicyBox?.SelectedIndex switch
        {
            1 => FileCollisionPolicy.Skip,
            2 => FileCollisionPolicy.Overwrite,
            _ => FileCollisionPolicy.AutoRename
        };

    private void SetOperating(bool operating)
    {
        _isOperating = operating;
        FileOperationProgressRing.IsActive = operating;
        FileOperationProgressRing.Visibility = operating ? Visibility.Visible : Visibility.Collapsed;
        CancelFileOperationButton.Visibility = operating ? Visibility.Visible : Visibility.Collapsed;
        CancelFileOperationButton.IsEnabled = operating;

        AddRootButton.IsEnabled = !operating && !_isScanning;
        RemoveRootButton.IsEnabled = !operating && !_isScanning;
        ScanButton.IsEnabled = !operating && !_isScanning;
        ApplyTagButton.IsEnabled = !operating;
        RemoveTagButton.IsEnabled = !operating;
        CreateTagButton.IsEnabled = !operating;
        RenameTagButton.IsEnabled = !operating;
        DeleteTagButton.IsEnabled = !operating;
        ExportTagsButton.IsEnabled = !operating && !_transferringTags;
        ImportTagsButton.IsEnabled = !operating && !_transferringTags;

        UpdateFileButtonsState();
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

    private async void AutomaticTagsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingAutomaticTags || !_initialized)
        {
            return;
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

    private async void ReidentifyTypeButton_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItems.OfType<IndexedFile>().ToList() is not [var file])
        {
            FileActionStatusText.Text = "请选择一个要重新识别的文件。";
            return;
        }

        ReidentifyTypeButton.IsEnabled = false;
        try
        {
            var classification = await Task.Run(() => _tags.ReclassifyFile(file.Id));
            await RefreshAutomaticTagsAsync();
            await RefreshFilesAsync();
            var diagnostic = string.IsNullOrWhiteSpace(classification.Diagnostic)
                ? ""
                : $"；{classification.Diagnostic}";
            FileActionStatusText.Text =
                $"类型已重新识别：{string.Join("、", classification.AutomaticTags)}{diagnostic}";
        }
        catch (Exception exception)
        {
            FileActionStatusText.Text = $"类型识别失败：{exception.Message}";
        }
        finally
        {
            ReidentifyTypeButton.IsEnabled = FilesList.SelectedItems.OfType<IndexedFile>().ToList() is [var selected] && selected.IsOnline;
        }
    }

    private async void ExportTagsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transferringTags)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = $"GuraFile-tags-{DateTime.Now:yyyyMMdd}"
        };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        SetTagTransfer(true);
        try
        {
            var json = await Task.Run(_tagBackup.Export);
            await File.WriteAllTextAsync(file.Path, json);
            TagStatusText.Text = $"已导出用户标签备份：{file.Name}";
        }
        catch (Exception exception)
        {
            TagStatusText.Text = $"导出失败：{exception.Message}";
        }
        finally
        {
            SetTagTransfer(false);
        }
    }

    private async void ImportTagsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transferringTags)
        {
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        SetTagTransfer(true);
        try
        {
            if (new FileInfo(file.Path).Length > UserTagBackupService.MaximumBackupBytes)
            {
                throw new InvalidDataException("备份文件过大。");
            }

            var json = await File.ReadAllTextAsync(file.Path);
            var result = await Task.Run(() => _tagBackup.Import(json));
            await RefreshTagsAsync();
            await RefreshFilesAsync();
            TagStatusText.Text =
                $"导入完成：新建 {result.CreatedTags} 个标签，复用 {result.ReusedTags} 个，恢复 {result.RestoredRelations} 条关系；" +
                $"名称冲突 {result.Conflicts.Count}，未匹配文件 {result.MissingFiles.Count}。";
        }
        catch (Exception exception)
        {
            TagStatusText.Text = $"导入失败：{exception.Message}";
        }
        finally
        {
            SetTagTransfer(false);
        }
    }

    private void SetTagTransfer(bool transferring)
    {
        _transferringTags = transferring;
        ExportTagsButton.IsEnabled = !transferring && !_isOperating;
        ImportTagsButton.IsEnabled = !transferring && !_isOperating;
    }

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

    private async Task RefreshAutomaticTagsAsync()
    {
        _refreshingAutomaticTags = true;
        try
        {
            var selectedIds = AutomaticTagsList.SelectedItems
                .OfType<AutomaticTag>()
                .Select(tag => tag.Id)
                .ToHashSet();
            var tags = await Task.Run(_tags.ListAutomaticTags);
            AutomaticTagsList.ItemsSource = tags;
            AutomaticTagsList.SelectedItems.Clear();
            foreach (var tag in tags.Where(tag => selectedIds.Contains(tag.Id)))
            {
                AutomaticTagsList.SelectedItems.Add(tag);
            }
        }
        catch (Exception exception)
        {
            TagStatusText.Text = $"自动标签加载失败：{exception.Message}";
        }
        finally
        {
            _refreshingAutomaticTags = false;
        }
    }

    private long[] SelectedFilterTagIds() =>
        SelectedTags().Select(tag => tag.Id)
            .Concat(AutomaticTagsList.SelectedItems.OfType<AutomaticTag>().Select(tag => tag.Id))
            .ToArray();

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
                ? SelectedFilterTagIds()
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
        var selectedId = (RootsList.SelectedItem as ManagedRoot)?.Id;
        var roots = _scanner.ListRoots();
        RootsList.ItemsSource = roots;
        RootsList.SelectedItem = roots.FirstOrDefault(root => root.Id == selectedId) ?? roots.FirstOrDefault();
    }

    private void ShowProgress(ScanProgress progress) =>
        ProgressText.Text = $"发现 {progress.DiscoveredFiles} 个，提交 {progress.CommittedFiles} 个，失败 {progress.FailedItems} 项";

    private void SetScanning(bool scanning)
    {
        _isScanning = scanning;
        ScanProgressRing.IsActive = scanning;
        ScanProgressRing.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = scanning;
        AddRootButton.IsEnabled = !scanning && !_isOperating;
        RemoveRootButton.IsEnabled = !scanning && !_isOperating;
        ScanButton.IsEnabled = !scanning && !_isOperating;
    }

    private void UpdateFileButtonsState()
    {
        var selected = FilesList.SelectedItems.OfType<IndexedFile>().ToList();
        var hasSingleFile = selected.Count == 1;
        OpenFileButton.IsEnabled = hasSingleFile && !_isOperating;
        RevealFileButton.IsEnabled = hasSingleFile && !_isOperating;
        ReidentifyTypeButton.IsEnabled = hasSingleFile && selected[0].IsOnline && !_isOperating;
        CopyFileButton.IsEnabled = selected.Count > 0 && !_isOperating;
        CutFileButton.IsEnabled = selected.Count > 0 && !_isOperating;
        PasteToFileButton.IsEnabled = _fileOperations.CanPasteFromClipboard() && !_isOperating;
        MoveToFileButton.IsEnabled = selected.Count > 0 && !_isOperating;
        RenameFileButton.IsEnabled = hasSingleFile && selected[0].IsOnline && !_isOperating;
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

    private static string Describe(
        IndexedFile file,
        IReadOnlyList<UserTag> userTags,
        IReadOnlyList<AutomaticTag> automaticTags)
    {
        var status = file.IsOnline ? "在线" : "离线";
        var userTagNames = userTags.Count == 0 ? "无" : string.Join("、", userTags.Select(tag => tag.Name));
        var automaticTagNames = automaticTags.Count == 0
            ? "无"
            : string.Join("、", automaticTags.Select(tag => tag.Name));
        var diagnostic = string.IsNullOrWhiteSpace(file.Diagnostic) ? "" : $"\n诊断：{file.Diagnostic}";
        return $"{file.Name}\n{file.Path}\n扩展名：{file.Extension}\n大小：{file.Size:N0} 字节\n修改时间：{file.Modified.LocalDateTime:g}\n状态：{status}\n用户标签：{userTagNames}\n自动标签：{automaticTagNames}{diagnostic}";
    }
}
