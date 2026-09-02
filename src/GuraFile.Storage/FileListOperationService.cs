using System.Runtime.Versioning;

namespace GuraFile.Storage;

[SupportedOSPlatform("windows")]
public sealed class FileListOperationService
{
    private readonly FileOperationIndexCommitter _committer;
    private readonly ManagedRootScanner _scanner;
    private readonly IFileClipboardService _clipboard;
    private readonly Func<IReadOnlyList<ManagedRoot>> _getRoots;

    public FileListOperationService(
        FileOperationIndexCommitter committer,
        ManagedRootScanner scanner,
        IFileClipboardService? clipboard = null,
        Func<IReadOnlyList<ManagedRoot>>? getRoots = null)
    {
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _clipboard = clipboard ?? new FileClipboardService();
        _getRoots = getRoots ?? scanner.ListRoots;
    }

    public void CopyToClipboard(IReadOnlyList<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("请选择要复制的文件。", nameof(sourcePaths));
        }

        _clipboard.SetContent(sourcePaths, FileClipboardEffect.Copy);
    }

    public void CutToClipboard(IReadOnlyList<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("请选择要剪切的文件。", nameof(sourcePaths));
        }

        _clipboard.SetContent(sourcePaths, FileClipboardEffect.Move);
    }

    public bool CanPasteFromClipboard() => _clipboard.HasFiles();

    public FileClipboardContent? GetClipboardContent() => _clipboard.GetContent();

    public void ClearClipboard() => _clipboard.Clear();

    public string ValidateDestination(string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var onlineRoots = GetOnlineRootPaths();
        return SafeFileOperationExecutor.ValidateDestinationDirectory(destinationDirectory, onlineRoots);
    }

    public static string ValidateNewFileName(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("新文件名不能为空。", nameof(newName));
        }

        var trimmed = newName.Trim();
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            trimmed.Contains('/') ||
            trimmed.Contains('\\'))
        {
            throw new ArgumentException($"新文件名“{newName}”包含非法字符或路径分隔符。", nameof(newName));
        }

        if (trimmed == "." || trimmed == "..")
        {
            throw new ArgumentException($"新文件名不能为“{trimmed}”。", nameof(newName));
        }

        return trimmed;
    }

    public Task<FileOperationCommitItemResult> RenameAsync(
        string sourcePath,
        string newName,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var validName = ValidateNewFileName(newName);
        var onlineRoots = GetOnlineRootPaths();

        return _committer.RenameAsync(
            sourcePath,
            validName,
            onlineRoots,
            collisionPolicy,
            ownerWindow,
            cancellationToken);
    }

    public Task<FileOperationCommitBatchResult> MoveToAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("请选择要移动的文件。", nameof(sourcePaths));
        }

        ValidateDestination(destinationDirectory);
        var onlineRoots = GetOnlineRootPaths();

        return _committer.MoveAsync(
            sourcePaths,
            destinationDirectory,
            onlineRoots,
            collisionPolicy,
            ownerWindow,
            progress,
            cancellationToken);
    }

    public Task<FileOperationCommitBatchResult> DeleteToRecycleBinAsync(
        IReadOnlyList<string> sourcePaths,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("请选择要删除的文件。", nameof(sourcePaths));
        }

        var onlineRoots = GetOnlineRootPaths();
        foreach (var path in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("源路径不能为空。", nameof(sourcePaths));
            }

            var normalized = SafeFileOperationExecutor.Normalize(path);
            var isInManagedRoot = false;
            foreach (var root in onlineRoots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var normalizedRoot = SafeFileOperationExecutor.Normalize(root);
                if (string.Equals(normalized, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                    SafeFileOperationExecutor.IsAncestor(normalizedRoot, normalized))
                {
                    isInManagedRoot = true;
                    break;
                }
            }

            if (!isInManagedRoot)
            {
                throw new ArgumentException($"源路径“{path}”不在任何在线管理根目录范围内。", nameof(sourcePaths));
            }
        }

        return _committer.DeleteToRecycleBinAsync(
            sourcePaths,
            onlineRoots,
            ownerWindow,
            progress,
            cancellationToken);
    }

    public async Task<FileOperationCommitBatchResult> PasteFromClipboardAsync(
        string destinationDirectory,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var content = _clipboard.GetContent();
        if (content == null || content.Files.Count == 0)
        {
            throw new InvalidOperationException("剪贴板中没有有效的文件。");
        }

        ValidateDestination(destinationDirectory);
        var onlineRoots = GetOnlineRootPaths();

        if (content.Effect == FileClipboardEffect.Move)
        {
            var result = await _committer.MoveAsync(
                content.Files,
                destinationDirectory,
                onlineRoots,
                collisionPolicy,
                ownerWindow,
                progress,
                cancellationToken);

            if (result.FailedCount == 0 && !result.IsCanceled)
            {
                _clipboard.Clear();
            }

            return result;
        }
        else
        {
            return await _committer.CopyAsync(
                content.Files,
                destinationDirectory,
                onlineRoots,
                collisionPolicy,
                ownerWindow,
                progress,
                cancellationToken);
        }
    }

    public static string FormatBatchSummary(FileOperationCommitBatchResult result, string operationName)
    {
        if (result.IsCanceled)
        {
            return $"{operationName}已取消：成功 {result.SucceededCount} 个，跳过 {result.SkippedCount} 个，取消 {result.CanceledCount} 个，失败 {result.FailedCount} 个。";
        }

        return $"{operationName}完成：成功 {result.SucceededCount} 个，跳过 {result.SkippedCount} 个，失败 {result.FailedCount} 个。";
    }

    private string[] GetOnlineRootPaths()
    {
        return _getRoots()
            .Where(r => r.Status == ManagedRootStatus.Online)
            .Select(r => r.Path)
            .ToArray();
    }
}
