using System.Runtime.InteropServices;
using GuraFile.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
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
    private readonly GraphSnapshotService _graphSnapshotService;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _fileQueryCancellation;
    private CancellationTokenSource? _detailCancellation;
    private CancellationTokenSource? _fileOpCancellation;
    private CancellationTokenSource? _graphRefreshCancellation;
    private readonly GraphInteractionCoordinator _graphInteractionCoordinator = new();
    private FileSortColumn _sortColumn = FileSortColumn.Name;
    private bool _sortDescending;
    private bool _initialized;
    private bool _refreshingTags;
    private bool _refreshingAutomaticTags;
    private bool _changingFileTags;
    private bool _transferringTags;
    private bool _isScanning;
    private bool _isOperating;
    private List<string>? _draggedFilePaths;
    private bool _webViewInitialized;
    private bool _webPageReady;
    private bool _isSyncingSelectionFromGraph;
    private IReadOnlyList<IndexedFile> _currentFiles = [];
    private GraphSnapshot? _pendingSnapshot;

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
        _graphSnapshotService = new(databasePath);
        _clipboard = new FileClipboardService();
        _fileOperations = new FileListOperationService(new FileOperationIndexCommitter(_scanner), _scanner, _clipboard);
        _initialized = true;
        Closed += async (_, _) =>
        {
            _scanCancellation?.Cancel();
            _fileQueryCancellation?.Cancel();
            _detailCancellation?.Cancel();
            _fileOpCancellation?.Cancel();
            _graphRefreshCancellation?.Cancel();
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

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _detailCancellation?.Cancel();
        var selected = FilesList.SelectedItems.OfType<IndexedFile>().ToList();
        UpdateFileButtonsState();
        FileActionStatusText.Text = "";

        if (!_isSyncingSelectionFromGraph && ViewModeBox.SelectedIndex == 1 && _webPageReady && GraphWebView.CoreWebView2 is not null)
        {
            var selectedFile = selected.Count == 1 ? selected[0] : null;
            var nodeId = selectedFile is not null ? $"file:{selectedFile.Id}" : null;
            GraphWebView.CoreWebView2.PostWebMessageAsJson(GraphMessageSerializer.SerializeSelectNode(nodeId));
        }

        if (selected.Count != 1)
        {
            var model = FileDetailsPresenter.Create(selected, [], []);
            UpdateDetailsView(model);
            return;
        }

        _ = LoadFileDetailsAsync(selected[0]);
    }

    private async Task LoadFileDetailsAsync(IndexedFile file)
    {
        _detailCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _detailCancellation = cancellation;
        var initialModel = FileDetailsPresenter.Create([file], [], []);
        UpdateDetailsView(initialModel);
        try
        {
            var tags = await Task.Run(
                () => (_tags.ListTagsForFile(file.Id), _tags.ListAutomaticTagsForFile(file.Id)),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (ReferenceEquals(_detailCancellation, cancellation))
            {
                var loadedModel = FileDetailsPresenter.Create([file], tags.Item1, tags.Item2);
                UpdateDetailsView(loadedModel);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_detailCancellation, cancellation))
            {
                FileActionStatusText.Text = $"标签读取失败：{exception.Message}";
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

    private void UpdateDetailsView(FileDetailsModel model)
    {
        DetailsTitleText.Text = model.Title;
        DetailsText.Text = model.Title;

        if (model.IsTagSelected)
        {
            DetailsPathText.Visibility = Visibility.Collapsed;
            DetailsMetaText.Text = $"类型：{model.TagTypeSummary}";
            DetailsMetaText.Visibility = Visibility.Visible;
            DetailsStatusText.Visibility = Visibility.Collapsed;
            DetailsIdentityText.Visibility = Visibility.Collapsed;
            DetailsUserTagsText.Visibility = Visibility.Collapsed;
            DetailsAutoTagsText.Visibility = Visibility.Collapsed;
            DetailsDiagnosticText.Visibility = Visibility.Collapsed;
        }
        else if (model.IsSingleFileSelected)
        {
            DetailsPathText.Text = model.Path ?? "";
            DetailsPathText.Visibility = Visibility.Visible;

            DetailsMetaText.Text = $"扩展名：{model.Extension} | 大小：{model.SizeText} | 修改时间：{model.ModifiedText}";
            DetailsMetaText.Visibility = Visibility.Visible;

            DetailsStatusText.Text = $"在线状态：{model.StatusText}";
            DetailsStatusText.Visibility = Visibility.Visible;

            DetailsIdentityText.Text = $"身份状态：{model.IdentityStateText}";
            DetailsIdentityText.Visibility = Visibility.Visible;

            DetailsUserTagsText.Text = $"用户标签：{model.UserTagsText}";
            DetailsUserTagsText.Visibility = Visibility.Visible;

            DetailsAutoTagsText.Text = $"自动标签：{model.AutomaticTagsText}";
            DetailsAutoTagsText.Visibility = Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(model.Diagnostic))
            {
                DetailsDiagnosticText.Text = $"诊断：{model.Diagnostic}";
                DetailsDiagnosticText.Visibility = Visibility.Visible;
            }
            else
            {
                DetailsDiagnosticText.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            DetailsPathText.Visibility = Visibility.Collapsed;
            DetailsMetaText.Visibility = Visibility.Collapsed;
            DetailsStatusText.Visibility = Visibility.Collapsed;
            DetailsIdentityText.Visibility = Visibility.Collapsed;
            DetailsUserTagsText.Visibility = Visibility.Collapsed;
            DetailsAutoTagsText.Visibility = Visibility.Collapsed;
            DetailsDiagnosticText.Visibility = Visibility.Collapsed;
        }

        OpenFileButton.IsEnabled = model.CanOpen && !_isOperating;
        RevealFileButton.IsEnabled = model.CanReveal && !_isOperating;
        ReidentifyTypeButton.IsEnabled = model.CanReidentify && !_isOperating;
        CopyPathButton.IsEnabled = model.CanCopyPath && !_isOperating;
    }

    private void CopyPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItems.OfType<IndexedFile>().ToList() is not [var file])
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(file.Path);
            Clipboard.SetContent(package);
            FileActionStatusText.Text = "已复制文件完整路径到剪贴板。";
        }
        catch (Exception exception)
        {
            FileActionStatusText.Text = $"复制路径失败：{exception.Message}";
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

    private async void DeleteFileButton_Click(object sender, RoutedEventArgs e)
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

        var representativeName = selected[0].Name;
        var message = selected.Count == 1
            ? $"文件：“{representativeName}”\n已删除的项目可从 Windows 回收站恢复。"
            : $"已选择 {selected.Count} 个文件（例如：“{representativeName}”等）。\n已删除的项目可从 Windows 回收站恢复。";

        var dialog = new ContentDialog
        {
            Title = selected.Count == 1 ? $"将“{representativeName}”移入回收站？" : $"将选中的 {selected.Count} 个文件移入回收站？",
            Content = message,
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var sourcePaths = selected.Select(f => f.Path).ToList();

        await ExecuteFileOperationBatchAsync("删除", async (progress, cancellationToken) =>
        {
            return await _fileOperations.DeleteToRecycleBinAsync(
                sourcePaths,
                WindowNative.GetWindowHandle(this),
                progress,
                cancellationToken);
        });
    }

    private void CancelFileOperationButton_Click(object sender, RoutedEventArgs e) =>
        _fileOpCancellation?.Cancel();

    private void FilesList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot);
        var isTextInput = focused is TextBox;

        var isShift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) &
                       Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        var isCtrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) &
                      Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        var shortcutKey = e.Key switch
        {
            Windows.System.VirtualKey.C => FileShortcutKey.C,
            Windows.System.VirtualKey.X => FileShortcutKey.X,
            Windows.System.VirtualKey.V => FileShortcutKey.V,
            Windows.System.VirtualKey.A => FileShortcutKey.A,
            Windows.System.VirtualKey.F2 => FileShortcutKey.F2,
            Windows.System.VirtualKey.F5 => FileShortcutKey.F5,
            Windows.System.VirtualKey.Delete => FileShortcutKey.Delete,
            _ => FileShortcutKey.None
        };

        var cmd = FileKeyboardShortcutRouter.Evaluate(shortcutKey, isCtrl, isShift, isTextInput);

        switch (cmd)
        {
            case FileShortcutCommand.Copy:
                if (CopyFileButton.IsEnabled)
                {
                    CopyFileButton_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
                break;
            case FileShortcutCommand.Cut:
                if (CutFileButton.IsEnabled)
                {
                    CutFileButton_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
                break;
            case FileShortcutCommand.Paste:
                if (PasteToFileButton.IsEnabled)
                {
                    PasteToFileButton_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
                break;
            case FileShortcutCommand.Rename:
                if (RenameFileButton.IsEnabled)
                {
                    RenameFileButton_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
                break;
            case FileShortcutCommand.Delete:
                if (DeleteFileButton.IsEnabled)
                {
                    DeleteFileButton_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
                break;
            case FileShortcutCommand.SelectAll:
                FilesList.SelectAll();
                e.Handled = true;
                break;
            case FileShortcutCommand.Refresh:
                _ = RefreshFilesAsync();
                RefreshRoots();
                e.Handled = true;
                break;
        }

        if (isShift && e.Key == Windows.System.VirtualKey.Delete)
        {
            e.Handled = true;
        }
    }

    private void FilesList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (_isOperating)
        {
            e.Cancel = true;
            return;
        }

        var items = e.Items.OfType<IndexedFile>().ToList();
        if (items.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        _draggedFilePaths = items.Select(f => f.Path).ToList();
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private ManagedRoot? GetTargetRootFromDragEvent(DragEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ManagedRoot root)
        {
            return root;
        }

        return RootsList.SelectedItem as ManagedRoot;
    }

    private void RootsList_DragOver(object sender, DragEventArgs e)
    {
        if (_isOperating)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var targetRoot = GetTargetRootFromDragEvent(e);
        if (targetRoot == null || targetRoot.Status != ManagedRootStatus.Online)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.Caption = "目标根目录不可用或已离线";
            e.DragUIOverride.IsCaptionVisible = true;
            return;
        }

        if (_draggedFilePaths != null && _draggedFilePaths.Count > 0)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = $"移动到 {targetRoot.DisplayName}";
            e.DragUIOverride.IsCaptionVisible = true;
        }
        else if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = $"复制到 {targetRoot.DisplayName}";
            e.DragUIOverride.IsCaptionVisible = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void RootsList_Drop(object sender, DragEventArgs e)
    {
        if (_isOperating)
        {
            return;
        }

        var targetRoot = GetTargetRootFromDragEvent(e);
        if (targetRoot == null || targetRoot.Status != ManagedRootStatus.Online)
        {
            FileActionStatusText.Text = "拖放目标必须为在线管理根目录。";
            return;
        }

        var isInternal = _draggedFilePaths != null && _draggedFilePaths.Count > 0;
        List<string> sourcePaths = new();

        if (isInternal)
        {
            sourcePaths.AddRange(_draggedFilePaths!);
            _draggedFilePaths = null;
        }
        else if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (item is Windows.Storage.StorageFile file)
                {
                    sourcePaths.Add(file.Path);
                }
                else if (item is Windows.Storage.StorageFolder folder)
                {
                    FileActionStatusText.Text = $"不支持拖入文件夹“{folder.Name}”，仅支持拖入文件。";
                    return;
                }
            }
        }

        if (sourcePaths.Count == 0)
        {
            return;
        }

        var policy = GetSelectedCollisionPolicy();
        await ExecuteFileOperationBatchAsync(
            isInternal ? "移动" : "复制",
            async (progress, cancellationToken) =>
            {
                return await _fileOperations.ExecuteDropAsync(
                    sourcePaths,
                    targetRoot.Path,
                    isInternal,
                    policy,
                    WindowNative.GetWindowHandle(this),
                    progress,
                    cancellationToken);
            });
    }

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
        var generation = _graphInteractionCoordinator.BeginQuery();
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

            if (!_graphInteractionCoordinator.CanCommitQuery(generation) || !ReferenceEquals(_fileQueryCancellation, cancellation))
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

            if (!_graphInteractionCoordinator.CommitQuery(generation, files) || !ReferenceEquals(_fileQueryCancellation, cancellation))
            {
                return;
            }

            _currentFiles = files;
            FilesList.ItemsSource = files;
            FilesStateText.Text = files.Count == 0 ? "没有匹配的文件" : $"{files.Count:N0} 个文件";
            if (ViewModeBox.SelectedIndex == 1)
            {
                await RefreshGraphAsync();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_graphInteractionCoordinator.CanCommitQuery(generation) && ReferenceEquals(_fileQueryCancellation, cancellation))
            {
                _graphInteractionCoordinator.CommitQuery(generation, []);
                _currentFiles = [];
                FilesList.ItemsSource = null;
                FilesStateText.Text = $"文件列表加载失败：{exception.Message}";
                if (ViewModeBox.SelectedIndex == 1)
                {
                    UpdateGraphState(GraphViewState.Error(exception.Message));
                }
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
        var isSingleOnline = hasSingleFile && selected[0].IsOnline;
        OpenFileButton.IsEnabled = isSingleOnline && !_isOperating;
        RevealFileButton.IsEnabled = isSingleOnline && !_isOperating;
        ReidentifyTypeButton.IsEnabled = isSingleOnline && !_isOperating;
        CopyPathButton.IsEnabled = hasSingleFile && !_isOperating;
        CopyFileButton.IsEnabled = selected.Count > 0 && !_isOperating;
        CutFileButton.IsEnabled = selected.Count > 0 && !_isOperating;
        PasteToFileButton.IsEnabled = _fileOperations.CanPasteFromClipboard() && !_isOperating;
        MoveToFileButton.IsEnabled = selected.Count > 0 && !_isOperating;
        RenameFileButton.IsEnabled = isSingleOnline && !_isOperating;
        DeleteFileButton.IsEnabled = selected.Count > 0 && !_isOperating;
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

    private void ViewModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        var isGraph = ViewModeBox.SelectedIndex == 1;
        SortBarGrid.Visibility = isGraph ? Visibility.Collapsed : Visibility.Visible;
        FileActionsPanel.Visibility = isGraph ? Visibility.Collapsed : Visibility.Visible;
        FilesList.Visibility = isGraph ? Visibility.Collapsed : Visibility.Visible;
        GraphHostContainer.Visibility = isGraph ? Visibility.Visible : Visibility.Collapsed;

        if (isGraph)
        {
            _ = RefreshGraphAsync();
            if (_webPageReady && GraphWebView.CoreWebView2 is not null)
            {
                GraphWebView.CoreWebView2.PostWebMessageAsJson(GraphMessageSerializer.SerializeFitViewport());
                var selected = FilesList.SelectedItems.OfType<IndexedFile>().ToList();
                var selectedFile = selected.Count == 1 ? selected[0] : null;
                var nodeId = selectedFile is not null ? $"file:{selectedFile.Id}" : null;
                GraphWebView.CoreWebView2.PostWebMessageAsJson(GraphMessageSerializer.SerializeSelectNode(nodeId));
            }
        }
    }

    private void FitViewportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webPageReady && GraphWebView.CoreWebView2 is not null)
        {
            GraphWebView.CoreWebView2.PostWebMessageAsJson(GraphMessageSerializer.SerializeFitViewport());
        }
    }

    private void BroadTagsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshGraphAsync();
    }

    private async Task InitializeGraphWebViewAsync()
    {
        if (_webViewInitialized)
        {
            return;
        }

        try
        {
            UpdateGraphState(GraphViewState.Loading());

            await GraphWebView.EnsureCoreWebView2Async();
            var assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "graph");
            GraphWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                GraphSecurityPolicy.VirtualHostName,
                assetsPath,
                CoreWebView2HostResourceAccessKind.Allow);

            GraphWebView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!GraphSecurityPolicy.IsAllowedUri(args.Uri))
                {
                    args.Cancel = true;
                }
            };

            GraphWebView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
            };

            GraphWebView.CoreWebView2.DownloadStarting += (_, args) =>
            {
                args.Cancel = true;
            };

            GraphWebView.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                DispatcherQueue.TryEnqueue(() => HandleWebMessage(args.WebMessageAsJson));
            };

            _webViewInitialized = true;
            GraphWebView.CoreWebView2.Navigate(GraphSecurityPolicy.EntryUrl);
        }
        catch (Exception exception)
        {
            UpdateGraphState(GraphViewState.Error(exception.Message));
        }
    }

    private async Task RefreshGraphAsync()
    {
        if (ViewModeBox.SelectedIndex != 1)
        {
            return;
        }

        if (!_webViewInitialized)
        {
            await InitializeGraphWebViewAsync();
        }

        var files = _currentFiles;
        var includeBroad = BroadTagsCheckBox.IsChecked == true;

        if (files.Count == 0)
        {
            UpdateGraphState(GraphViewState.Empty());
            return;
        }

        if (files.Count > GraphSnapshotService.MaxFileNodes)
        {
            UpdateGraphState(GraphViewState.LimitExceeded(files.Count));
            return;
        }

        var graphGen = _graphInteractionCoordinator.BeginGraphRefresh();
        var cancellation = new CancellationTokenSource();
        var previous = _graphRefreshCancellation;
        _graphRefreshCancellation = cancellation;
        previous?.Cancel();

        try
        {
            UpdateGraphState(GraphViewState.Loading());

            var snapshot = await _graphSnapshotService.CreateAsync(files, includeBroad);
            cancellation.Token.ThrowIfCancellationRequested();

            if (!_graphInteractionCoordinator.CommitSnapshot(graphGen, snapshot) || !ReferenceEquals(_graphRefreshCancellation, cancellation))
            {
                return;
            }

            var state = GraphViewState.FromSnapshot(snapshot);
            if (state.Mode != GraphViewDisplayMode.Ready)
            {
                UpdateGraphState(state);
                return;
            }

            UpdateGraphState(state);
            PostSnapshotToWeb(snapshot);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_graphInteractionCoordinator.CanCommitSnapshot(graphGen) && ReferenceEquals(_graphRefreshCancellation, cancellation))
            {
                UpdateGraphState(GraphViewState.Error(exception.Message));
            }
        }
        finally
        {
            if (ReferenceEquals(_graphRefreshCancellation, cancellation))
            {
                _graphRefreshCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void PostSnapshotToWeb(GraphSnapshot snapshot)
    {
        if (!_webPageReady || GraphWebView.CoreWebView2 is null)
        {
            _pendingSnapshot = snapshot;
            return;
        }

        var json = GraphMessageSerializer.SerializeRenderSnapshot(snapshot);
        GraphWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void HandleWebMessage(string json)
    {
        try
        {
            var message = GraphMessageSerializer.Deserialize(json);
            switch (message.Type)
            {
                case GraphMessageTypes.Ready:
                    _webPageReady = true;
                    if (_pendingSnapshot is not null)
                    {
                        PostSnapshotToWeb(_pendingSnapshot);
                        _pendingSnapshot = null;
                    }
                    break;

                case GraphMessageTypes.FirstFrameRendered:
                    var metrics = GraphMessageSerializer.ParseFirstFrameMetrics(message);
                    GraphLoadingRing.IsActive = false;
                    GraphLoadingRing.Visibility = Visibility.Collapsed;
                    GraphInfoText.Text = $"节点 {metrics.NodeCount}，边 {metrics.EdgeCount}（耗时 {metrics.RenderDurationMs:F0} ms）";
                    if (_webPageReady && GraphWebView.CoreWebView2 is not null)
                    {
                        var selected = FilesList.SelectedItems.OfType<IndexedFile>().ToList();
                        var selectedFile = selected.Count == 1 ? selected[0] : null;
                        var nodeId = selectedFile is not null ? $"file:{selectedFile.Id}" : null;
                        GraphWebView.CoreWebView2.PostWebMessageAsJson(GraphMessageSerializer.SerializeSelectNode(nodeId));
                    }
                    break;

                case GraphMessageTypes.NodeSelected:
                    var selectAction = GraphMessageSerializer.ParseNodeAction(message);
                    var selection = _graphInteractionCoordinator.EvaluateSelection(selectAction);
                    if (selection.Kind == GraphSelectionKind.File && selection.File is not null)
                    {
                        _isSyncingSelectionFromGraph = true;
                        try
                        {
                            FilesList.SelectedItem = selection.File;
                            FilesList.ScrollIntoView(selection.File);
                        }
                        finally
                        {
                            _isSyncingSelectionFromGraph = false;
                        }
                        _ = LoadFileDetailsAsync(selection.File);
                    }
                    else if (selection.Kind == GraphSelectionKind.Tag)
                    {
                        _detailCancellation?.Cancel();
                        var tagModel = FileDetailsPresenter.CreateForTag(
                            selection.TagName ?? selectAction.Label ?? "标签",
                            selection.TagTypeSummary ?? "标签");
                        UpdateDetailsView(tagModel);
                        FileActionStatusText.Text = "";
                    }
                    break;

                case GraphMessageTypes.NodeActivated:
                    var activateAction = GraphMessageSerializer.ParseNodeAction(message);
                    var activation = _graphInteractionCoordinator.EvaluateActivation(activateAction);
                    switch (activation.Status)
                    {
                        case GraphActivationStatus.Success:
                            RunFileAction(activation.File!, _shell.Open, "已交给系统默认应用打开。");
                            break;
                        case GraphActivationStatus.RejectedOffline:
                            FileActionStatusText.Text = activation.ErrorMessage ?? "文件处于离线状态，无法打开。";
                            break;
                        case GraphActivationStatus.RejectedNotFile:
                        case GraphActivationStatus.RejectedFileNotFound:
                            if (!string.IsNullOrWhiteSpace(activation.ErrorMessage))
                            {
                                FileActionStatusText.Text = activation.ErrorMessage;
                            }
                            break;
                    }
                    break;

                case GraphMessageTypes.Error:
                    var error = GraphMessageSerializer.ParseErrorMessage(message);
                    UpdateGraphState(GraphViewState.Error(error));
                    break;
            }
        }
        catch (Exception exception)
        {
            UpdateGraphState(GraphViewState.Error(exception.Message));
        }
    }

    private void UpdateGraphState(GraphViewState state)
    {
        switch (state.Mode)
        {
            case GraphViewDisplayMode.Loading:
                GraphLoadingRing.IsActive = true;
                GraphLoadingRing.Visibility = Visibility.Visible;
                GraphMessagePanel.Visibility = Visibility.Visible;
                GraphStateText.Text = state.Message ?? "正在加载图谱…";
                GraphWebView.Visibility = Visibility.Collapsed;
                GraphInfoText.Text = string.Empty;
                break;

            case GraphViewDisplayMode.Empty:
            case GraphViewDisplayMode.LimitExceeded:
                GraphLoadingRing.IsActive = false;
                GraphLoadingRing.Visibility = Visibility.Collapsed;
                GraphMessagePanel.Visibility = Visibility.Visible;
                GraphStateText.Text = state.Message ?? string.Empty;
                GraphWebView.Visibility = Visibility.Collapsed;
                GraphInfoText.Text = string.Empty;
                break;

            case GraphViewDisplayMode.Ready:
                GraphLoadingRing.IsActive = false;
                GraphLoadingRing.Visibility = Visibility.Collapsed;
                GraphMessagePanel.Visibility = Visibility.Collapsed;
                GraphWebView.Visibility = Visibility.Visible;
                break;

            case GraphViewDisplayMode.Error:
                GraphLoadingRing.IsActive = false;
                GraphLoadingRing.Visibility = Visibility.Collapsed;
                GraphMessagePanel.Visibility = Visibility.Visible;
                GraphStateText.Text = state.Message ?? "图谱加载失败";
                GraphWebView.Visibility = Visibility.Collapsed;
                GraphInfoText.Text = string.Empty;
                break;
        }
    }
}
