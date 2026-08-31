using GuraFile.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GuraFile;

public sealed partial class MainWindow : Window
{
    private readonly ManagedRootScanner _scanner;
    private readonly FileQueryService _fileQuery;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _fileQueryCancellation;
    private FileSortColumn _sortColumn = FileSortColumn.Name;
    private bool _sortDescending;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GuraFile",
            "index.db");
        _scanner = new(databasePath);
        _fileQuery = new(databasePath);
        _initialized = true;
        Closed += (_, _) =>
        {
            _scanCancellation?.Cancel();
            _fileQueryCancellation?.Cancel();
        };
        RefreshRoots();
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

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = FilesList.SelectedItems.OfType<IndexedFile>().ToList();
        DetailsText.Text = selected.Count switch
        {
            0 => "未选择文件",
            1 => Describe(selected[0]),
            _ => $"已选择 {selected.Count} 个文件"
        };
    }

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
            var files = await _fileQuery.QueryAsync(
                new(SearchBox.Text, _sortColumn, _sortDescending),
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

    private static string Describe(IndexedFile file)
    {
        var status = file.IsOnline ? "在线" : "离线";
        var diagnostic = string.IsNullOrWhiteSpace(file.Diagnostic) ? "" : $"\n诊断：{file.Diagnostic}";
        return $"{file.Name}\n{file.Path}\n扩展名：{file.Extension}\n大小：{file.Size:N0} 字节\n修改时间：{file.Modified.LocalDateTime:g}\n状态：{status}{diagnostic}";
    }
}
