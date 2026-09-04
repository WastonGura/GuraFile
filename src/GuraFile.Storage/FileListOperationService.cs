using System.Runtime.Versioning;

namespace GuraFile.Storage;

[SupportedOSPlatform("windows")]
public sealed class FileListOperationService
{
    private readonly FileOperationIndexCommitter _committer;
    private readonly ManagedRootScanner _scanner;
    private readonly IFileClipboardService _clipboard;
    private readonly Func<IReadOnlyList<ManagedRoot>> _getRoots;
    private readonly DiagnosticLogger _logger;

    public FileListOperationService(
        FileOperationIndexCommitter committer,
        ManagedRootScanner scanner,
        IFileClipboardService? clipboard = null,
        Func<IReadOnlyList<ManagedRoot>>? getRoots = null,
        DiagnosticLogger? logger = null)
    {
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _clipboard = clipboard ?? new FileClipboardService();
        _getRoots = getRoots ?? scanner.ListRoots;
        _logger = logger ?? DiagnosticLogger.Default;
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

    public async Task<FileOperationCommitItemResult> RenameAsync(
        string sourcePath,
        string newName,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var validName = ValidateNewFileName(newName);
        var onlineRoots = GetOnlineRootPaths();
        var correlationId = $"op-rename-{Guid.NewGuid():N}";

        _logger.LogInfo(
            DiagnosticCategory.FileOperation,
            "RenameStarted",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: $"Renaming '{sourcePath}' to '{validName}'");

        var result = await _committer.RenameAsync(
            sourcePath,
            validName,
            onlineRoots,
            collisionPolicy,
            ownerWindow,
            cancellationToken);

        if (result.Succeeded)
        {
            _logger.LogInfo(
                DiagnosticCategory.FileOperation,
                "RenameCompleted",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Success,
                message: $"Renamed to '{result.ActualTargetPath}'");
        }
        else
        {
            _logger.LogError(
                DiagnosticCategory.FileOperation,
                "RenameFailed",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Failed,
                message: result.Error,
                errorCode: "RENAME_FAILED");
        }

        return result;
    }

    public async Task<FileOperationCommitBatchResult> MoveToAsync(
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
        var correlationId = $"op-move-{Guid.NewGuid():N}";

        _logger.LogInfo(
            DiagnosticCategory.FileOperation,
            "MoveBatchStarted",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: $"Moving {sourcePaths.Count} files to '{destinationDirectory}'");

        var result = await _committer.MoveAsync(
            sourcePaths,
            destinationDirectory,
            onlineRoots,
            collisionPolicy,
            ownerWindow,
            progress,
            cancellationToken);

        _logger.LogInfo(
            DiagnosticCategory.FileOperation,
            "MoveBatchCompleted",
            correlationId: correlationId,
            status: result.FailedCount == 0 ? DiagnosticResultStatus.Success : DiagnosticResultStatus.Failed,
            message: $"Move completed: {result.SucceededCount} succeeded, {result.FailedCount} failed, {result.SkippedCount} skipped.");

        return result;
    }

    public async Task<FileOperationCommitBatchResult> DeleteToRecycleBinAsync(
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

        var correlationId = $"op-delete-{Guid.NewGuid():N}";
        _logger.LogInfo(
            DiagnosticCategory.FileOperation,
            "DeleteBatchStarted",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: $"Deleting {sourcePaths.Count} files to Recycle Bin");

        var result = await _committer.DeleteToRecycleBinAsync(
            sourcePaths,
            onlineRoots,
            ownerWindow,
            progress,
            cancellationToken);

        _logger.LogInfo(
            DiagnosticCategory.FileOperation,
            "DeleteBatchCompleted",
            correlationId: correlationId,
            status: result.FailedCount == 0 ? DiagnosticResultStatus.Success : DiagnosticResultStatus.Failed,
            message: $"Delete completed: {result.SucceededCount} succeeded, {result.FailedCount} failed.");

        return result;
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
        var correlationId = $"op-paste-{Guid.NewGuid():N}";

        _logger.LogInfo(
            DiagnosticCategory.FileOperation,
            "PasteStarted",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: $"Pasting {content.Files.Count} files ({content.Effect}) to '{destinationDirectory}'");

        FileOperationCommitBatchResult result;
        if (content.Effect == FileClipboardEffect.Move)
        {
            result = await _committer.MoveAsync(
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
        }
        else
        {
            result = await _committer.CopyAsync(
                content.Files,
                destinationDirectory,
                onlineRoots,
                collisionPolicy,
                ownerWindow,
                progress,
                cancellationToken);
        }

        _logger.LogInfo(
            DiagnosticCategory.FileOperation,
            "PasteCompleted",
            correlationId: correlationId,
            status: result.FailedCount == 0 ? DiagnosticResultStatus.Success : DiagnosticResultStatus.Failed,
            message: $"Paste completed: {result.SucceededCount} succeeded, {result.FailedCount} failed.");

        return result;
    }

    public async Task<FileOperationCommitBatchResult> ExecuteDropAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        bool isInternalDrag,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("请提供拖放的文件。", nameof(sourcePaths));
        }

        ValidateDestination(destinationDirectory);
        var normDest = SafeFileOperationExecutor.Normalize(destinationDirectory);

        foreach (var path in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (Directory.Exists(path))
            {
                throw new ArgumentException($"不支持拖入文件夹“{path}”，仅支持拖入文件。", nameof(sourcePaths));
            }
        }

        var onlineRoots = GetOnlineRootPaths();

        if (isInternalDrag)
        {
            var validSources = new List<string>();
            foreach (var path in sourcePaths)
            {
                var normSource = SafeFileOperationExecutor.Normalize(path);
                var sourceParent = Path.GetDirectoryName(normSource);
                if (sourceParent != null && string.Equals(SafeFileOperationExecutor.Normalize(sourceParent), normDest, StringComparison.OrdinalIgnoreCase))
                {
                    // Same directory
                    continue;
                }
                validSources.Add(path);
            }

            if (validSources.Count == 0)
            {
                throw new InvalidOperationException("所选文件已在目标文件夹中。");
            }

            return await _committer.MoveAsync(
                validSources,
                destinationDirectory,
                onlineRoots,
                collisionPolicy,
                ownerWindow,
                progress,
                cancellationToken);
        }
        else
        {
            return await _committer.CopyAsync(
                sourcePaths,
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
