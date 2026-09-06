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
    private ManagedRootScanner _scanner = null!;
    private FileChangeCoordinator _fileChanges = null!;
    private FileQueryService _fileQuery = null!;
    private TagService _tags = null!;
    private UserTagBackupService _tagBackup = null!;
    private RollingTagBackupService _rollingBackup = null!;
    private readonly ShellFileActions _shell = new();
    private readonly IFileClipboardService _clipboard;
    private FileListOperationService _fileOperations = null!;
    private GraphSnapshotService _graphSnapshotService = null!;
    private SavedFilterViewService _savedFilterViews = null!;
    private bool _isApplyingSavedView;
    private readonly DatabaseHealthService _healthService = new();
    private readonly DatabaseRecoveryService _recoveryService = new();
    private readonly string _databasePath = AppPaths.DefaultDatabasePath;
    private DatabaseHealthResult _currentHealth = new(DatabaseHealthStatus.Healthy);
    private bool _isRecoveringDatabase;
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
        _clipboard = new FileClipboardService();

        Closed += async (_, _) =>
        {
            _scanCancellation?.Cancel();
            _fileQueryCancellation?.Cancel();
            _detailCancellation?.Cancel();
            _fileOpCancellation?.Cancel();
            _graphRefreshCancellation?.Cancel();
            if (_fileChanges != null)
            {
                await _fileChanges.DisposeAsync();
            }
        };

        (Content as FrameworkElement)!.Loaded += (_, _) =>
        {
            if (_currentHealth.Status == DatabaseHealthStatus.Corrupted)
            {
                _ = PromptDatabaseRecoveryDialogAsync();
            }
        };

        InitializeDatabaseAndServices();
    }

    private void InitializeDatabaseAndServices()
    {
        _currentHealth = _healthService.CheckHealth(_databasePath);
        ApplyDatabaseHealthState(_currentHealth);
    }

    private void ApplyDatabaseHealthState(DatabaseHealthResult health)
    {
        switch (health.Status)
        {
            case DatabaseHealthStatus.Healthy:
                DatabaseNoticeBar.IsOpen = false;
                DatabaseNoticeActionButton.Visibility = Visibility.Collapsed;
                ProgressText.Text = "空闲";
                EnableControlsForHealthyDatabase();

                _rollingBackup = new(_databasePath);
                _scanner = new(_databasePath);
                _fileChanges = new(
                    _scanner,
                    result => DispatcherQueue.TryEnqueue(() => _ = ShowRealtimeResultAsync(result)),
                    exception => DispatcherQueue.TryEnqueue(() => ShowRealtimeError(exception)),
                    onRootChanged: () => DispatcherQueue.TryEnqueue(RefreshRoots));
                _fileQuery = new(_databasePath);
                _tags = new(_databasePath, _rollingBackup);
                _tagBackup = new(_databasePath);
                _savedFilterViews = new(_databasePath);
                _graphSnapshotService = new(_databasePath);
                var committer = new FileOperationIndexCommitter(_scanner);
                _fileOperations = new FileListOperationService(committer, _scanner, _clipboard);
                _initialized = true;

                RefreshRoots();
                foreach (var root in _scanner.ListRoots())
                {
                    if (!_fileChanges.CheckAndStartCrashRecovery(root))
                    {
                        _fileChanges.Watch(root);
                    }
                }
                _ = RefreshTagsAsync();
                _ = RefreshAutomaticTagsAsync();
                _ = RefreshSavedFilterViewsAsync();
                _ = RefreshFilesAsync();
                UpdateFileButtonsState();
                _ = StartFileOperationCrashRecoveryAsync(committer);
                break;

            case DatabaseHealthStatus.Locked:
                _initialized = false;
                DisableControlsForUnhealthyDatabase();
                DatabaseNoticeBar.Severity = InfoBarSeverity.Warning;
                DatabaseNoticeBar.Title = "数据库被锁定";
                DatabaseNoticeBar.Message = "数据库正在被其他进程使用，请关闭其他进程后重试。";
                DatabaseNoticeActionButton.Content = "重试连接";
                DatabaseNoticeActionButton.Visibility = Visibility.Visible;
                DatabaseNoticeBar.IsOpen = true;
                ProgressText.Text = "数据库被其他进程锁定，请重试";
                FilesStateText.Text = "数据库正在被其他进程使用，请关闭其他进程后重试。";
                break;

            case DatabaseHealthStatus.UnsupportedFutureSchema:
                _initialized = false;
                DisableControlsForUnhealthyDatabase();
                DatabaseNoticeBar.Severity = InfoBarSeverity.Error;
                DatabaseNoticeBar.Title = "数据库版本不受支持";
                DatabaseNoticeBar.Message = "当前数据库由更新版本的 GuraFile 创建，请升级客户端。";
                DatabaseNoticeActionButton.Visibility = Visibility.Collapsed;
                DatabaseNoticeBar.IsOpen = true;
                ProgressText.Text = "数据库架构版本不受支持，请升级客户端";
                FilesStateText.Text = "当前数据库由更新版本的 GuraFile 创建，请升级客户端。";
                break;

            case DatabaseHealthStatus.Corrupted:
                _initialized = false;
                DisableControlsForUnhealthyDatabase();
                DatabaseNoticeBar.Severity = InfoBarSeverity.Error;
                DatabaseNoticeBar.Title = "数据库已损坏";
                DatabaseNoticeBar.Message = "检测到数据库损坏。可隔离损坏文件，重新扫描管理根目录并自动恢复历史用户标签。";
                DatabaseNoticeActionButton.Content = "恢复数据库...";
                DatabaseNoticeActionButton.Visibility = Visibility.Visible;
                DatabaseNoticeBar.IsOpen = true;
                ProgressText.Text = "检测到数据库损坏，等待恢复";
                FilesStateText.Text = "数据库损坏，需要恢复。";
                break;

            case DatabaseHealthStatus.IoError:
            default:
                _initialized = false;
                DisableControlsForUnhealthyDatabase();
                DatabaseNoticeBar.Severity = InfoBarSeverity.Error;
                DatabaseNoticeBar.Title = "数据库访问失败";
                DatabaseNoticeBar.Message = health.Message ?? "无法访问数据库。";
                DatabaseNoticeActionButton.Content = "重试连接";
                DatabaseNoticeActionButton.Visibility = Visibility.Visible;
                DatabaseNoticeBar.IsOpen = true;
                ProgressText.Text = "数据库访问 I/O 错误";
                FilesStateText.Text = "数据库无法访问。";
                break;
        }
    }

    private void DisableControlsForUnhealthyDatabase()
    {
        AddRootButton.IsEnabled = false;
        RemoveRootButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        CreateTagButton.IsEnabled = false;
        RenameTagButton.IsEnabled = false;
        DeleteTagButton.IsEnabled = false;
        ApplyTagButton.IsEnabled = false;
        RemoveTagButton.IsEnabled = false;
        ExportTagsButton.IsEnabled = false;
        ImportTagsButton.IsEnabled = false;
        BackupNowButton.IsEnabled = false;
        RollingBackupsButton.IsEnabled = false;
        OpenFileButton.IsEnabled = false;
        RevealFileButton.IsEnabled = false;
        CopyPathButton.IsEnabled = false;
        ReidentifyTypeButton.IsEnabled = false;
        CopyFileButton.IsEnabled = false;
        CutFileButton.IsEnabled = false;
        PasteToFileButton.IsEnabled = false;
        MoveToFileButton.IsEnabled = false;
        RenameFileButton.IsEnabled = false;
        DeleteFileButton.IsEnabled = false;
        SaveViewButton.IsEnabled = false;
        UpdateViewButton.IsEnabled = false;
        RenameViewButton.IsEnabled = false;
        DeleteViewButton.IsEnabled = false;
        ExportDiagnosticsButton.IsEnabled = true;
        FileOperationRecoveryNoticeBar.IsOpen = false;
    }

    private void EnableControlsForHealthyDatabase()
    {
        AddRootButton.IsEnabled = true;
        RemoveRootButton.IsEnabled = RootsList.SelectedItem is not null;
        ScanButton.IsEnabled = RootsList.SelectedItem is not null;
        CreateTagButton.IsEnabled = true;
        ExportTagsButton.IsEnabled = true;
        ImportTagsButton.IsEnabled = true;
        BackupNowButton.IsEnabled = true;
        RollingBackupsButton.IsEnabled = true;
        SaveViewButton.IsEnabled = true;
        UpdateViewButton.IsEnabled = SavedFilterViewsList.SelectedItem is not null;
        RenameViewButton.IsEnabled = SavedFilterViewsList.SelectedItem is not null;
        DeleteViewButton.IsEnabled = SavedFilterViewsList.SelectedItem is not null;
        ExportDiagnosticsButton.IsEnabled = true;
    }

    private async void DatabaseNoticeActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentHealth.Status is DatabaseHealthStatus.Locked or DatabaseHealthStatus.IoError)
        {
            InitializeDatabaseAndServices();
        }
        else if (_currentHealth.Status == DatabaseHealthStatus.Corrupted)
        {
            await PromptDatabaseRecoveryDialogAsync();
        }
    }

    private async Task PromptDatabaseRecoveryDialogAsync()
    {
        if (_isRecoveringDatabase)
        {
            return;
        }

        var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot == null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "数据库损坏恢复",
            Content = new TextBlock
            {
                Text = "系统检测到 GuraFile 数据库已损坏。\n\n" +
                       "确认后将执行以下安全无损恢复步骤：\n" +
                       "1. 将损坏的数据库文件安全隔离备份为 .corrupt_*.bak（绝不覆盖或删除原有数据库）；\n" +
                       "2. 初始化全新的空白索引数据库；\n" +
                       "3. 从磁盘重新扫描已配置的管理根目录重建文件索引；\n" +
                       "4. 自动从最近的历史有效备份恢复用户标签与关系。\n\n" +
                       "此操作严禁修改您的磁盘原始文件。是否立即开始恢复？",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "开始安全恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ExecuteDatabaseRecoveryAsync();
    }

    private async Task ExecuteDatabaseRecoveryAsync()
    {
        if (_isRecoveringDatabase)
        {
            return;
        }

        _isRecoveringDatabase = true;
        ProgressText.Text = "正在执行数据库恢复...";
        ScanProgressRing.Visibility = Visibility.Visible;
        var correlationId = $"dbrecovery-{Guid.NewGuid():N}";

        DiagnosticLogger.Default.LogInfo(
            DiagnosticCategory.Database,
            "RecoveryStarted",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: $"Starting database rebuild for {_databasePath}");

        try
        {
            var roots = DatabaseRecoveryService.TryExtractRoots(_databasePath);

            var report = await Task.Run(() => _recoveryService.RebuildIndexAndRestoreTagsAsync(
                _databasePath,
                roots,
                tagBackupDirectory: AppPaths.DefaultTagBackupDirectory,
                statusCallback: msg => DispatcherQueue.TryEnqueue(() => ProgressText.Text = msg)));

            if (!report.Succeeded)
            {
                DiagnosticLogger.Default.LogError(
                    DiagnosticCategory.Database,
                    "RecoveryFailed",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Failed,
                    message: report.ErrorMessage,
                    errorCode: "DB_RECOVERY_FAILED");

                ProgressText.Text = "数据库恢复失败";
                FailureList.ItemsSource = new[] { report.ErrorMessage ?? "未知错误" };

                var failureDialog = new ContentDialog
                {
                    Title = "数据库恢复未完成",
                    Content = new TextBlock
                    {
                        Text = $"恢复过程中发生错误：{report.ErrorMessage}\n\n已隔离的损坏数据库及备份均完好保留。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    CloseButtonText = "确定",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = (Content as FrameworkElement)?.XamlRoot
                };
                await failureDialog.ShowAsync();
                return;
            }

            DiagnosticLogger.Default.LogInfo(
                DiagnosticCategory.Database,
                "RecoveryCompleted",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Success,
                message: $"Recovered {report.IndexedFiles} files, {report.RestoredTags} tags.");

            // Successfully recovered!
            InitializeDatabaseAndServices();

            var summaryText =
                $"已成功隔离损坏数据库并重建索引：\n\n" +
                $"• 扫描根目录: {report.ScannedRoots} 个\n" +
                $"• 发现文件: {report.DiscoveredFiles} 个（已索引 {report.IndexedFiles} 个）\n" +
                $"• 恢复用户标签: {report.RestoredTags} 个\n" +
                $"• 恢复标签关系: {report.RestoredRelations} 条\n" +
                $"• 标签名称冲突: {report.TagConflictsCount} 个\n" +
                $"• 未匹配文件: {report.UnmatchedFiles.Count} 个\n" +
                (report.QuarantineBackupPath != null ? $"• 已隔离损坏库: {Path.GetFileName(report.QuarantineBackupPath)}\n" : "") +
                (report.RestoredBackupPath != null ? $"• 恢复标签来源: {Path.GetFileName(report.RestoredBackupPath)}" : "• 未找到有效历史标签备份");

            var summaryDialog = new ContentDialog
            {
                Title = "数据库恢复完成",
                Content = new TextBlock
                {
                    Text = summaryText,
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = "完成",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (Content as FrameworkElement)?.XamlRoot
            };
            await summaryDialog.ShowAsync();
        }
        catch (Exception ex)
        {
            ProgressText.Text = "数据库恢复失败";
            FailureList.ItemsSource = new[] { ex.Message };
        }
        finally
        {
            ScanProgressRing.Visibility = Visibility.Collapsed;
            _isRecoveringDatabase = false;
        }
    }

    private async Task StartFileOperationCrashRecoveryAsync(FileOperationIndexCommitter committer)
    {
        try
        {
            var recoveryService = new FileOperationCrashRecoveryService(_databasePath, _scanner, committer);
            var report = await recoveryService.RecoverAsync();
            if (report.HasIndeterminateOperations)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    FileOperationRecoveryNoticeBar.IsOpen = true;
                    FileOperationRecoveryNoticeBar.Message = $"存在 {report.IndeterminateIntentsCount} 个需要检查的中断文件操作。";
                    FilesStateText.Text = "存在需要检查的中断文件操作。";
                });
            }
            else if (report.RecoveredIntentsCount > 0)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _ = RefreshFilesAsync();
                });
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Default.LogError(
                DiagnosticCategory.FileOperation,
                "FileOperationRecoveryFailed",
                exception: ex,
                message: $"崩溃恢复对齐失败：{ex.Message}");
        }
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
        if (_initialized && !_isApplyingSavedView)
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
            var fileIds = selected.Select(f => f.Id).ToArray();
            GraphWebView.CoreWebView2.PostWebMessageAsJson(GraphMessageSerializer.SerializeSetSelection(fileIds));
            var nodeId = selected.Count == 1 ? $"file:{selected[0].Id}" : null;
            _ = GraphMessageSerializer.SerializeSelectNode(nodeId);
        }

        if (selected.Count == 0)
        {
            var isSelectedRootOffline = (RootsList.SelectedItem as ManagedRoot)?.Status == ManagedRootStatus.Offline;
            var model = FileDetailsPresenter.Create([], [], [], isRootOffline: isSelectedRootOffline);
            UpdateDetailsView(model);
            return;
        }

        if (selected.Count == 1)
        {
            _ = LoadFileDetailsAsync(selected[0]);
            return;
        }

        _ = LoadMultipleFilesDetailsAsync(selected);
    }

    private async Task LoadMultipleFilesDetailsAsync(IReadOnlyList<IndexedFile> files)
    {
        _detailCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _detailCancellation = cancellation;
        var isRootOffline = files.Any(IsFileRootOffline);
        var initialModel = FileDetailsPresenter.Create(files, [], [], isRootOffline: isRootOffline);
        UpdateDetailsView(initialModel);
        try
        {
            var fileIds = files.Select(f => f.Id).ToArray();
            var commonUserTags = await Task.Run(
                () => _tags.ListCommonUserTagsForFiles(fileIds),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (ReferenceEquals(_detailCancellation, cancellation))
            {
                var loadedModel = FileDetailsPresenter.Create(files, commonUserTags, [], isRootOffline: isRootOffline);
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

    private async Task LoadFileDetailsAsync(IndexedFile file)
    {
        _detailCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _detailCancellation = cancellation;
        var isRootOffline = IsFileRootOffline(file);
        var initialModel = FileDetailsPresenter.Create([file], [], [], isRootOffline: isRootOffline);
        UpdateDetailsView(initialModel);
        try
        {
            var tags = await Task.Run(
                () => (_tags.ListTagsForFile(file.Id), _tags.ListAutomaticTagsForFile(file.Id)),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (ReferenceEquals(_detailCancellation, cancellation))
            {
                var loadedModel = FileDetailsPresenter.Create([file], tags.Item1, tags.Item2, isRootOffline: isRootOffline);
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

        if (model.IsRootOffline && !string.IsNullOrWhiteSpace(model.RootOfflineNotice))
        {
            DetailsRootOfflineNoticeText.Text = model.RootOfflineNotice;
            DetailsRootOfflineNoticeText.Visibility = Visibility.Visible;
        }
        else
        {
            DetailsRootOfflineNoticeText.Visibility = Visibility.Collapsed;
        }

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

            DetailsStatusText.Text = model.IsRootOffline ? "在线状态：离线（管理根目录当前离线）" : $"在线状态：{model.StatusText}";
            DetailsStatusText.Visibility = Visibility.Visible;

            if (model.IdentityStateText.StartsWith("⚠️", StringComparison.Ordinal))
            {
                if (Application.Current.Resources.TryGetValue("SystemFillColorCautionBrush", out var cautionBrush) && cautionBrush is Brush brush)
                {
                    DetailsIdentityText.Foreground = brush;
                }
                DetailsIdentityText.Text = model.IdentityStateText;
            }
            else
            {
                if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var secondaryBrush) && secondaryBrush is Brush brush)
                {
                    DetailsIdentityText.Foreground = brush;
                }
                DetailsIdentityText.Text = $"身份状态：{model.IdentityStateText}";
            }
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
        else if (model.IsMultipleFilesSelected)
        {
            DetailsPathText.Visibility = Visibility.Collapsed;
            DetailsMetaText.Visibility = Visibility.Collapsed;
            DetailsStatusText.Visibility = Visibility.Collapsed;
            DetailsIdentityText.Visibility = Visibility.Collapsed;
            DetailsUserTagsText.Text = $"公共标签：{model.UserTagsText}";
            DetailsUserTagsText.Visibility = Visibility.Visible;
            DetailsAutoTagsText.Visibility = Visibility.Collapsed;
            DetailsDiagnosticText.Visibility = Visibility.Collapsed;
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

    private void RootsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RemoveRootButton.IsEnabled = !_isScanning && !_isOperating && RootsList.SelectedItem is not null;
        ScanButton.IsEnabled = !_isScanning && !_isOperating && RootsList.SelectedItem is not null;

        var selected = FilesList?.SelectedItems?.OfType<IndexedFile>().ToList();
        if (selected == null || selected.Count == 0)
        {
            var isSelectedRootOffline = (RootsList.SelectedItem as ManagedRoot)?.Status == ManagedRootStatus.Offline;
            var model = FileDetailsPresenter.Create([], [], [], isRootOffline: isSelectedRootOffline);
            UpdateDetailsView(model);
        }
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
        BackupNowButton.IsEnabled = !operating && !_transferringTags;
        RollingBackupsButton.IsEnabled = !operating && !_transferringTags;

        UpdateFileButtonsState();
    }

    private async void TagsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingTags || !_initialized || _isApplyingSavedView)
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
        if (_refreshingAutomaticTags || !_initialized || _isApplyingSavedView)
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
        if (_initialized && !_isApplyingSavedView)
        {
            await RefreshFilesAsync();
        }
    }

    private async void TagMatchBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && TagFilterToggle.IsOn && !_isApplyingSavedView)
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
            await RefreshSavedFilterViewsAsync();
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

        var correlationId = Guid.NewGuid().ToString("N");
        DiagnosticLogger.Default.LogInfo(
            DiagnosticCategory.Backup,
            "TagExportStarted",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: "Starting user tag export.",
            properties: new Dictionary<string, object?> { ["targetPath"] = file.Path });

        SetTagTransfer(true);
        try
        {
            var json = await Task.Run(_tagBackup.Export);
            await File.WriteAllTextAsync(file.Path, json);
            TagStatusText.Text = $"已导出用户标签备份：{file.Name}";

            DiagnosticLogger.Default.LogInfo(
                DiagnosticCategory.Backup,
                "TagExportCompleted",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Success,
                message: $"User tags exported to {file.Path}.",
                properties: new Dictionary<string, object?>
                {
                    ["targetPath"] = file.Path,
                    ["bytes"] = json.Length
                });
        }
        catch (Exception exception)
        {
            DiagnosticLogger.Default.LogError(
                DiagnosticCategory.Backup,
                "TagExportFailed",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Failed,
                message: $"Failed to export tags: {exception.Message}",
                errorCode: "ERR_TAG_EXPORT",
                exception: exception);

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

        var correlationId = Guid.NewGuid().ToString("N");
        DiagnosticLogger.Default.LogInfo(
            DiagnosticCategory.Backup,
            "TagImportStarted",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: "Starting user tag import.",
            properties: new Dictionary<string, object?> { ["sourcePath"] = file.Path });

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

            DiagnosticLogger.Default.LogInfo(
                DiagnosticCategory.Backup,
                "TagImportCompleted",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Success,
                message: "User tags imported successfully.",
                properties: new Dictionary<string, object?>
                {
                    ["createdTags"] = result.CreatedTags,
                    ["reusedTags"] = result.ReusedTags,
                    ["restoredRelations"] = result.RestoredRelations,
                    ["conflicts"] = result.Conflicts.Count,
                    ["missingFiles"] = result.MissingFiles.Count
                });
        }
        catch (Exception exception)
        {
            DiagnosticLogger.Default.LogError(
                DiagnosticCategory.Backup,
                "TagImportFailed",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Failed,
                message: $"Failed to import tags: {exception.Message}",
                errorCode: "ERR_TAG_IMPORT",
                exception: exception);

            TagStatusText.Text = $"导入失败：{exception.Message}";
        }
        finally
        {
            SetTagTransfer(false);
        }
    }

    private async void BackupNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transferringTags || _isOperating)
        {
            return;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        DiagnosticLogger.Default.LogInfo(
            DiagnosticCategory.Backup,
            "ManualBackupRequested",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: "User triggered manual rolling backup.");

        SetTagTransfer(true);
        try
        {
            var result = await Task.Run(() => _rollingBackup.TriggerBackup());
            if (result.Status == BackupWriteStatus.Created)
            {
                TagStatusText.Text = $"已生成用户标签备份：{Path.GetFileName(result.BackupPath)}";
                DiagnosticLogger.Default.LogInfo(
                    DiagnosticCategory.Backup,
                    "ManualBackupCompleted",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Success,
                    message: "Manual rolling backup created.",
                    properties: new Dictionary<string, object?> { ["backupPath"] = result.BackupPath });
            }
            else if (result.Status == BackupWriteStatus.Updated)
            {
                TagStatusText.Text = $"已更新今日用户标签备份：{Path.GetFileName(result.BackupPath)}";
                DiagnosticLogger.Default.LogInfo(
                    DiagnosticCategory.Backup,
                    "ManualBackupCompleted",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Success,
                    message: "Manual rolling backup updated.",
                    properties: new Dictionary<string, object?> { ["backupPath"] = result.BackupPath });
            }
            else if (result.Status == BackupWriteStatus.Unchanged)
            {
                TagStatusText.Text = "当前用户标签与今日备份一致，无需重复写入。";
                DiagnosticLogger.Default.LogInfo(
                    DiagnosticCategory.Backup,
                    "ManualBackupCompleted",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Skipped,
                    message: "Manual rolling backup unchanged; existing snapshot matches current state.",
                    properties: new Dictionary<string, object?> { ["backupPath"] = result.BackupPath });
            }
            else
            {
                TagStatusText.Text = $"备份失败：{result.ErrorMessage}";
                DiagnosticLogger.Default.LogError(
                    DiagnosticCategory.Backup,
                    "ManualBackupFailed",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Failed,
                    message: $"Manual rolling backup failed: {result.ErrorMessage}",
                    errorCode: "ERR_MANUAL_BACKUP");
            }
        }
        catch (Exception exception)
        {
            DiagnosticLogger.Default.LogError(
                DiagnosticCategory.Backup,
                "ManualBackupFailed",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Failed,
                message: $"Manual rolling backup exception: {exception.Message}",
                errorCode: "ERR_MANUAL_BACKUP_EX",
                exception: exception);

            TagStatusText.Text = $"备份失败：{exception.Message}";
        }
        finally
        {
            SetTagTransfer(false);
        }
    }

    private async void RollingBackupsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transferringTags || _isOperating)
        {
            return;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        DiagnosticLogger.Default.LogInfo(
            DiagnosticCategory.Backup,
            "RollingBackupDialogOpened",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: "User opened rolling backups dialog.");

        var backups = await Task.Run(() => _rollingBackup.ListBackups());
        if (backups.Count == 0)
        {
            var emptyDialog = new ContentDialog
            {
                Title = "用户标签历史备份",
                Content = $"备份目录暂无备份文件：\n{_rollingBackup.BackupDirectory}",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (Content as FrameworkElement)?.XamlRoot
            };
            await emptyDialog.ShowAsync();
            return;
        }

        var listView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            DisplayMemberPath = nameof(TagBackupInfo.DisplayText),
            ItemsSource = backups,
            MaxHeight = 260
        };
        listView.SelectedIndex = 0;

        var dialogPanel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = $"备份目录：{_rollingBackup.BackupDirectory}",
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "选择要恢复的快照（恢复将合并用户标签与关系，自动标签不受影响）：",
                    TextWrapping = TextWrapping.Wrap
                },
                listView
            }
        };

        var dialog = new ContentDialog
        {
            Title = "用户标签历史备份与恢复",
            Content = dialogPanel,
            PrimaryButtonText = "恢复所选备份",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || listView.SelectedItem is not TagBackupInfo selected)
        {
            return;
        }

        if (!selected.IsValid)
        {
            TagStatusText.Text = $"无法恢复损坏的备份：{selected.ValidationErrorMessage}";
            DiagnosticLogger.Default.LogWarning(
                DiagnosticCategory.Backup,
                "RollingBackupRestoreRejected",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Rejected,
                message: $"Selected backup is invalid: {selected.ValidationErrorMessage}",
                properties: new Dictionary<string, object?> { ["backupPath"] = selected.Path });
            return;
        }

        DiagnosticLogger.Default.LogInfo(
            DiagnosticCategory.Backup,
            "RollingBackupRestoreRequested",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: $"Restoring rolling backup from {selected.Path}.",
            properties: new Dictionary<string, object?> { ["backupPath"] = selected.Path });

        SetTagTransfer(true);
        try
        {
            var result = await Task.Run(() => _rollingBackup.RestoreBackup(selected.Path));
            await RefreshTagsAsync();
            await RefreshFilesAsync();
            TagStatusText.Text =
                $"恢复完成：新建 {result.CreatedTags} 个标签，复用 {result.ReusedTags} 个，恢复 {result.RestoredRelations} 条关系；" +
                $"名称冲突 {result.Conflicts.Count}，未匹配文件 {result.MissingFiles.Count}。";

            DiagnosticLogger.Default.LogInfo(
                DiagnosticCategory.Backup,
                "RollingBackupRestoreCompleted",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Success,
                message: "Rolling backup restored successfully.",
                properties: new Dictionary<string, object?>
                {
                    ["createdTags"] = result.CreatedTags,
                    ["reusedTags"] = result.ReusedTags,
                    ["restoredRelations"] = result.RestoredRelations,
                    ["conflicts"] = result.Conflicts.Count,
                    ["missingFiles"] = result.MissingFiles.Count
                });
        }
        catch (Exception exception)
        {
            DiagnosticLogger.Default.LogError(
                DiagnosticCategory.Backup,
                "RollingBackupRestoreFailed",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Failed,
                message: $"Failed to restore rolling backup: {exception.Message}",
                errorCode: "ERR_RESTORE_FAILED",
                exception: exception);

            TagStatusText.Text = $"恢复失败：{exception.Message}";
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
        BackupNowButton.IsEnabled = !transferring && !_isOperating;
        RollingBackupsButton.IsEnabled = !transferring && !_isOperating;
        ExportDiagnosticsButton.IsEnabled = !transferring && !_isOperating;
    }

    private async void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transferringTags || _isOperating)
        {
            return;
        }

        var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot == null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "导出诊断日志",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "即将导出系统诊断包（ZIP 压缩文件），用于排查应用故障。\n\n" +
                       "【包含项说明】（严格白名单）：\n" +
                       "• 运行环境信息（environment.json）：操作系统、.NET 版本、运行架构等；\n" +
                       "• 配置摘要信息（config_summary.json）：脱敏的管理根目录、数据库架构版本、备份元数据；\n" +
                       "• 本地诊断日志（logs/*.log）：已自动对所有绝对路径执行用户名脱敏；\n\n" +
                       "【安全保证】：\n" +
                       "诊断包严格不包含索引数据库（index.db）、标签备份数据、您的个人文件内容或任何私钥凭据。"
            },
            PrimaryButtonText = "选择保存位置并导出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = $"GuraFile_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}"
        };
        picker.FileTypeChoices.Add("ZIP 压缩文件", [".zip"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        SetTagTransfer(true);
        var correlationId = $"diag-export-{Guid.NewGuid():N}";
        try
        {
            DiagnosticLogger.Default.LogInfo(
                DiagnosticCategory.App,
                "DiagnosticExportStarted",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Started,
                message: $"Exporting diagnostics to {file.Path}");

            var exportService = new DiagnosticExportService(
                databasePath: _databasePath,
                logsDirectory: AppPaths.DefaultLogsDirectory,
                backupDirectory: AppPaths.DefaultTagBackupDirectory,
                getRoots: _initialized ? _scanner.ListRoots : null);

            var result = await Task.Run(() => exportService.Export(file.Path));
            if (result.Succeeded)
            {
                DiagnosticLogger.Default.LogInfo(
                    DiagnosticCategory.App,
                    "DiagnosticExportCompleted",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Success,
                    message: $"Exported {result.LogFilesCount} logs, {result.TotalZipBytes} bytes.");

                TagStatusText.Text = $"已成功导出诊断包：{file.Name}";
            }
            else
            {
                DiagnosticLogger.Default.LogError(
                    DiagnosticCategory.App,
                    "DiagnosticExportFailed",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Failed,
                    message: result.ErrorMessage,
                    errorCode: "DIAG_EXPORT_FAILED");

                TagStatusText.Text = $"导出诊断失败：{result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Default.LogError(
                DiagnosticCategory.App,
                "DiagnosticExportException",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Failed,
                message: ex.Message,
                errorCode: "DIAG_EXPORT_EXCEPTION",
                exception: ex);

            TagStatusText.Text = $"导出诊断异常：{ex.Message}";
        }
        finally
        {
            SetTagTransfer(false);
        }
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
            UpdateFileButtonsState();
            if (add)
            {
                await Task.Run(() => _tags.AddTagToFiles(tag.Id, fileIds));
            }
            else
            {
                await Task.Run(() => _tags.RemoveTagFromFiles(tag.Id, fileIds));
            }

            var previouslySelectedIds = fileIds.ToHashSet();
            await RefreshFilesAsync();

            var remaining = _currentFiles.Where(f => previouslySelectedIds.Contains(f.Id)).ToList();
            _isSyncingSelectionFromGraph = true;
            try
            {
                FilesList.SelectedItems.Clear();
                foreach (var file in remaining)
                {
                    FilesList.SelectedItems.Add(file);
                }
            }
            finally
            {
                _isSyncingSelectionFromGraph = false;
            }

            if (remaining.Count == 0)
            {
                _detailCancellation?.Cancel();
                var isSelectedRootOffline = (RootsList.SelectedItem as ManagedRoot)?.Status == ManagedRootStatus.Offline;
                var emptyModel = FileDetailsPresenter.Create([], [], [], isRootOffline: isSelectedRootOffline);
                UpdateDetailsView(emptyModel);
            }
            else if (remaining.Count == 1)
            {
                _ = LoadFileDetailsAsync(remaining[0]);
            }
            else
            {
                _ = LoadMultipleFilesDetailsAsync(remaining);
            }

            if (ViewModeBox.SelectedIndex == 1 && _webPageReady && GraphWebView.CoreWebView2 is not null)
            {
                var remainingIds = remaining.Select(f => f.Id).ToArray();
                GraphWebView.CoreWebView2.PostWebMessageAsJson(GraphMessageSerializer.SerializeSetSelection(remainingIds));
            }

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
            UpdateFileButtonsState();
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

    private async void SavedFilterViewsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        var view = SavedFilterViewsList.SelectedItem as SavedFilterView;
        UpdateViewButtonsState();

        if (view is null)
        {
            return;
        }

        ViewNameBox.Text = view.Name;

        _isApplyingSavedView = true;
        try
        {
            SearchBox.Text = view.SearchText ?? "";
            _sortColumn = view.SortColumn;
            _sortDescending = view.SortDescending;
            UpdateSortLabels();

            TagFilterToggle.IsOn = view.IsTagFilterEnabled;
            TagMatchBox.SelectedIndex = view.TagMatchMode == TagMatchMode.All ? 1 : 0;

            var viewTagIds = view.TagIds.ToHashSet();
            var userTags = TagsList.ItemsSource as IReadOnlyList<UserTag> ?? [];
            var autoTags = AutomaticTagsList.ItemsSource as IReadOnlyList<AutomaticTag> ?? [];

            TagsList.SelectedItems.Clear();
            foreach (var ut in userTags)
            {
                if (viewTagIds.Contains(ut.Id))
                {
                    TagsList.SelectedItems.Add(ut);
                }
            }

            AutomaticTagsList.SelectedItems.Clear();
            foreach (var at in autoTags)
            {
                if (viewTagIds.Contains(at.Id))
                {
                    AutomaticTagsList.SelectedItems.Add(at);
                }
            }
        }
        finally
        {
            _isApplyingSavedView = false;
        }

        await ApplySavedFilterViewAsync(view);
    }

    private async Task ApplySavedFilterViewAsync(SavedFilterView view)
    {
        var generation = _graphInteractionCoordinator.BeginQuery();
        var cancellation = new CancellationTokenSource();
        var previous = _fileQueryCancellation;
        _fileQueryCancellation = cancellation;
        previous?.Cancel();

        try
        {
            if (!_graphInteractionCoordinator.CanCommitQuery(generation) || !ReferenceEquals(_fileQueryCancellation, cancellation))
            {
                return;
            }

            if (view.HasInvalidTags)
            {
                ViewStatusText.Text = $"视图“{view.Name}”包含已删除标签，筛选条件已失效。已判定为无匹配。请点击“更新”修复或“删除”。";
            }
            else
            {
                ViewStatusText.Text = $"已应用视图“{view.Name}”。";
            }

            FilesLoadingRing.IsActive = true;
            FilesLoadingRing.Visibility = Visibility.Visible;
            FilesStateText.Text = "正在加载文件…";

            var query = _savedFilterViews.ToFileQuery(view);
            var files = await _fileQuery.QueryAsync(query, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            if (!_graphInteractionCoordinator.CommitQuery(generation, files) || !ReferenceEquals(_fileQueryCancellation, cancellation))
            {
                return;
            }

            _currentFiles = files;
            FilesList.ItemsSource = files;
            FilesStateText.Text = files.Count == 0
                ? (view.HasInvalidTags ? "已保存视图条件失效（0 个文件）" : "没有匹配的文件")
                : $"{files.Count:N0} 个文件";

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

    private async void SaveViewButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = ViewNameBox.Text;
            var searchText = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text;
            var tagIds = SelectedFilterTagIds();
            var isTagFilterEnabled = TagFilterToggle.IsOn;
            var matchMode = TagMatchBox.SelectedIndex == 1 ? TagMatchMode.All : TagMatchMode.Any;

            var created = await Task.Run(() => _savedFilterViews.CreateView(
                name: name,
                searchText: searchText,
                sortColumn: _sortColumn,
                sortDescending: _sortDescending,
                tagMatchMode: matchMode,
                isTagFilterEnabled: isTagFilterEnabled,
                tagIds: tagIds));

            await RefreshSavedFilterViewsAsync(created.Id);
            ViewStatusText.Text = $"视图“{created.Name}”已保存。";
        }
        catch (Exception ex)
        {
            ViewStatusText.Text = $"保存视图失败：{ex.Message}";
        }
    }

    private async void UpdateViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (SavedFilterViewsList.SelectedItem is not SavedFilterView selected)
        {
            return;
        }

        try
        {
            var searchText = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text;
            var tagIds = SelectedFilterTagIds();
            var isTagFilterEnabled = TagFilterToggle.IsOn;
            var matchMode = TagMatchBox.SelectedIndex == 1 ? TagMatchMode.All : TagMatchMode.Any;

            var updated = await Task.Run(() => _savedFilterViews.UpdateViewFilter(
                id: selected.Id,
                searchText: searchText,
                sortColumn: _sortColumn,
                sortDescending: _sortDescending,
                tagMatchMode: matchMode,
                isTagFilterEnabled: isTagFilterEnabled,
                tagIds: tagIds));

            await RefreshSavedFilterViewsAsync(updated.Id);
            ViewStatusText.Text = $"视图“{updated.Name}”条件已更新。";
        }
        catch (Exception ex)
        {
            ViewStatusText.Text = $"更新视图失败：{ex.Message}";
        }
    }

    private async void RenameViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (SavedFilterViewsList.SelectedItem is not SavedFilterView selected)
        {
            return;
        }

        try
        {
            var newName = ViewNameBox.Text;
            var renamed = await Task.Run(() => _savedFilterViews.RenameView(selected.Id, newName));
            await RefreshSavedFilterViewsAsync(renamed.Id);
            ViewStatusText.Text = $"视图已重命名为“{renamed.Name}”。";
        }
        catch (Exception ex)
        {
            ViewStatusText.Text = $"重命名视图失败：{ex.Message}";
        }
    }

    private async void DeleteViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (SavedFilterViewsList.SelectedItem is not SavedFilterView selected)
        {
            return;
        }

        try
        {
            await Task.Run(() => _savedFilterViews.DeleteView(selected.Id));
            await RefreshSavedFilterViewsAsync();
            ViewStatusText.Text = $"视图“{selected.Name}”已删除。";
            ViewNameBox.Text = "";
        }
        catch (Exception ex)
        {
            ViewStatusText.Text = $"删除视图失败：{ex.Message}";
        }
    }

    private async Task RefreshSavedFilterViewsAsync(long? selectViewId = null)
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            var views = await Task.Run(_savedFilterViews.ListViews);
            var previouslySelectedId = selectViewId ?? (SavedFilterViewsList.SelectedItem as SavedFilterView)?.Id;
            SavedFilterViewsList.ItemsSource = views;
            SavedFilterViewsList.SelectedItem = views.FirstOrDefault(v => v.Id == previouslySelectedId);
            UpdateViewButtonsState();
        }
        catch (Exception ex)
        {
            ViewStatusText.Text = $"加载已保存视图失败：{ex.Message}";
        }
    }

    private void UpdateViewButtonsState()
    {
        var hasSelection = SavedFilterViewsList.SelectedItem is not null;
        UpdateViewButton.IsEnabled = hasSelection;
        RenameViewButton.IsEnabled = hasSelection;
        DeleteViewButton.IsEnabled = hasSelection;
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
        RemoveRootButton.IsEnabled = !scanning && !_isOperating && RootsList.SelectedItem is not null;
        ScanButton.IsEnabled = !scanning && !_isOperating && RootsList.SelectedItem is not null;
    }

    private bool IsFileRootOffline(IndexedFile? file)
    {
        if (_scanner is null || string.IsNullOrWhiteSpace(file?.Path))
        {
            return false;
        }

        var roots = _scanner.ListRoots();
        foreach (var root in roots)
        {
            if (root.Status == ManagedRootStatus.Offline)
            {
                var rootPath = root.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (file.Path.Equals(rootPath, StringComparison.OrdinalIgnoreCase) ||
                    file.Path.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    file.Path.StartsWith(rootPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void UpdateFileButtonsState()
    {
        var selected = FilesList.SelectedItems.OfType<IndexedFile>().ToList();
        var hasSingleFile = selected.Count == 1;
        var isFileRootOffline = hasSingleFile && IsFileRootOffline(selected[0]);
        var isSingleOnline = hasSingleFile && selected[0].IsOnline && !isFileRootOffline;
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
        ApplyTagButton.IsEnabled = selected.Count > 0 && !_isOperating && !_changingFileTags;
        RemoveTagButton.IsEnabled = selected.Count > 0 && !_isOperating && !_changingFileTags;
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
                var fileIds = selected.Select(f => f.Id).ToArray();
                GraphWebView.CoreWebView2.PostWebMessageAsJson(GraphMessageSerializer.SerializeSetSelection(fileIds));
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

        var correlationId = Guid.NewGuid().ToString("N");
        DiagnosticLogger.Default.LogInfo(
            DiagnosticCategory.GraphHost,
            "WebViewInitializationStarted",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: "Initializing WebView2 control for graph view.");

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
                    DiagnosticLogger.Default.LogWarning(
                        DiagnosticCategory.GraphHost,
                        "NavigationBlocked",
                        status: DiagnosticResultStatus.Blocked,
                        message: $"Blocked untrusted navigation to {args.Uri}.",
                        properties: new Dictionary<string, object?> { ["uri"] = args.Uri });
                }
            };

            GraphWebView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                DiagnosticLogger.Default.LogWarning(
                    DiagnosticCategory.GraphHost,
                    "NewWindowBlocked",
                    status: DiagnosticResultStatus.Blocked,
                    message: $"Blocked untrusted new window request for {args.Uri}.",
                    properties: new Dictionary<string, object?> { ["uri"] = args.Uri });
            };

            GraphWebView.CoreWebView2.DownloadStarting += (_, args) =>
            {
                args.Cancel = true;
                DiagnosticLogger.Default.LogWarning(
                    DiagnosticCategory.GraphHost,
                    "DownloadBlocked",
                    status: DiagnosticResultStatus.Blocked,
                    message: $"Blocked untrusted download request for {args.ResultFilePath}.",
                    properties: new Dictionary<string, object?> { ["resultFilePath"] = args.ResultFilePath });
            };

            GraphWebView.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                DispatcherQueue.TryEnqueue(() => HandleWebMessage(args.WebMessageAsJson));
            };

            _webViewInitialized = true;
            GraphWebView.CoreWebView2.Navigate(GraphSecurityPolicy.EntryUrl);

            DiagnosticLogger.Default.LogInfo(
                DiagnosticCategory.GraphHost,
                "WebViewInitializationCompleted",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Success,
                message: "WebView2 control initialized successfully.");
        }
        catch (Exception exception)
        {
            DiagnosticLogger.Default.LogError(
                DiagnosticCategory.GraphHost,
                "WebViewInitializationFailed",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Failed,
                message: $"WebView2 initialization failed: {exception.Message}",
                errorCode: "ERR_WEBVIEW_INIT",
                exception: exception);

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

        var correlationId = Guid.NewGuid().ToString("N");
        DiagnosticLogger.Default.LogInfo(
            DiagnosticCategory.GraphHost,
            "GraphRefreshStarted",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: $"Starting graph refresh with {files.Count} files (includeBroad={includeBroad}).",
            properties: new Dictionary<string, object?>
            {
                ["fileCount"] = files.Count,
                ["includeBroad"] = includeBroad
            });

        if (files.Count == 0)
        {
            DiagnosticLogger.Default.LogInfo(
                DiagnosticCategory.GraphHost,
                "GraphSnapshotEmpty",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Success,
                message: "No files available for graph visualization.");

            UpdateGraphState(GraphViewState.Empty());
            return;
        }

        if (files.Count > GraphSnapshotService.MaxFileNodes)
        {
            DiagnosticLogger.Default.LogWarning(
                DiagnosticCategory.GraphHost,
                "GraphLimitExceeded",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Blocked,
                message: $"File count ({files.Count}) exceeds maximum allowed node limit ({GraphSnapshotService.MaxFileNodes}).",
                properties: new Dictionary<string, object?>
                {
                    ["fileCount"] = files.Count,
                    ["maxLimit"] = GraphSnapshotService.MaxFileNodes
                });

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

            var totalNodes = snapshot.FileNodes.Count + snapshot.TagNodes.Count;
            DiagnosticLogger.Default.LogInfo(
                DiagnosticCategory.GraphHost,
                "GraphSnapshotRendered",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Success,
                message: $"Graph snapshot rendered with {totalNodes} nodes and {snapshot.Edges.Count} edges.",
                properties: new Dictionary<string, object?>
                {
                    ["nodeCount"] = totalNodes,
                    ["fileNodes"] = snapshot.FileNodes.Count,
                    ["tagNodes"] = snapshot.TagNodes.Count,
                    ["edgeCount"] = snapshot.Edges.Count
                });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLogger.Default.LogError(
                DiagnosticCategory.GraphHost,
                "GraphRefreshFailed",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Failed,
                message: $"Graph refresh failed: {exception.Message}",
                errorCode: "ERR_GRAPH_REFRESH",
                exception: exception);

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
                        var fileIds = selected.Select(f => f.Id).ToArray();
                        GraphWebView.CoreWebView2.PostWebMessageAsJson(GraphMessageSerializer.SerializeSetSelection(fileIds));
                    }
                    break;

                case GraphMessageTypes.SelectionChanged:
                    var selectionChanged = GraphMessageSerializer.ParseSelectionChanged(message);
                    var validFiles = _graphInteractionCoordinator.EvaluateBatchSelection(selectionChanged.FileIds);
                    _isSyncingSelectionFromGraph = true;
                    try
                    {
                        FilesList.SelectedItems.Clear();
                        foreach (var file in validFiles)
                        {
                            FilesList.SelectedItems.Add(file);
                        }
                        if (validFiles.Count == 1)
                        {
                            FilesList.ScrollIntoView(validFiles[0]);
                        }
                    }
                    finally
                    {
                        _isSyncingSelectionFromGraph = false;
                    }
                    UpdateFileButtonsState();
                    if (validFiles.Count == 0)
                    {
                        _detailCancellation?.Cancel();
                        var isSelectedRootOffline = (RootsList.SelectedItem as ManagedRoot)?.Status == ManagedRootStatus.Offline;
                        var emptyModel = FileDetailsPresenter.Create([], [], [], isRootOffline: isSelectedRootOffline);
                        UpdateDetailsView(emptyModel);
                    }
                    else if (validFiles.Count == 1)
                    {
                        _ = LoadFileDetailsAsync(validFiles[0]);
                    }
                    else
                    {
                        _ = LoadMultipleFilesDetailsAsync(validFiles);
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
                            DiagnosticLogger.Default.LogWarning(
                                DiagnosticCategory.GraphHost,
                                "NodeActivationRejected",
                                status: DiagnosticResultStatus.Rejected,
                                message: "File is offline, activation rejected.",
                                properties: new Dictionary<string, object?> { ["nodeId"] = activateAction.NodeId });
                            break;
                        case GraphActivationStatus.RejectedNotFile:
                        case GraphActivationStatus.RejectedFileNotFound:
                            if (!string.IsNullOrWhiteSpace(activation.ErrorMessage))
                            {
                                FileActionStatusText.Text = activation.ErrorMessage;
                            }
                            DiagnosticLogger.Default.LogWarning(
                                DiagnosticCategory.GraphHost,
                                "NodeActivationRejected",
                                status: DiagnosticResultStatus.Rejected,
                                message: activation.ErrorMessage ?? "Node activation rejected.",
                                properties: new Dictionary<string, object?>
                                {
                                    ["nodeId"] = activateAction.NodeId,
                                    ["status"] = activation.Status.ToString()
                                });
                            break;
                    }
                    break;

                case GraphMessageTypes.Error:
                    var error = GraphMessageSerializer.ParseErrorMessage(message);
                    DiagnosticLogger.Default.LogError(
                        DiagnosticCategory.GraphHost,
                        "WebGraphError",
                        status: DiagnosticResultStatus.Failed,
                        message: $"Graph frontend reported error: {error}",
                        errorCode: "ERR_WEB_GRAPH");
                    UpdateGraphState(GraphViewState.Error(error));
                    break;
            }
        }
        catch (Exception exception)
        {
            DiagnosticLogger.Default.LogError(
                DiagnosticCategory.GraphHost,
                "WebMessageHandlingError",
                status: DiagnosticResultStatus.Failed,
                message: $"Failed to process web message from graph: {exception.Message}",
                errorCode: "ERR_WEB_MESSAGE",
                exception: exception);

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
