using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GuraFile.Storage;

public enum FileCollisionPolicy
{
    AutoRename,
    Skip,
    Overwrite
}

public enum FileOperationItemStatus
{
    Completed,
    Skipped,
    Failed,
    Canceled
}

public sealed record FileOperationItemResult(
    string SourcePath,
    string? ActualTargetPath,
    FileOperationItemStatus Status,
    string? Error = null,
    bool IsCanceled = false)
{
    public bool Succeeded => Status == FileOperationItemStatus.Completed;
}

public sealed record FileOperationProgress(
    int TotalItems,
    int CompletedItems,
    string? CurrentSourcePath = null);

public sealed record FileOperationBatchResult(
    IReadOnlyList<FileOperationItemResult> Items,
    bool IsCanceled = false)
{
    public int TotalCount => Items.Count;
    public int SucceededCount => Items.Count(i => i.Status == FileOperationItemStatus.Completed);
    public int FailedCount => Items.Count(i => i.Status == FileOperationItemStatus.Failed);
    public int SkippedCount => Items.Count(i => i.Status == FileOperationItemStatus.Skipped);
    public int CanceledCount => Items.Count(i => i.Status == FileOperationItemStatus.Canceled);
}

[SupportedOSPlatform("windows")]
public sealed class SafeFileOperationExecutor
{
    private static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IidFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    internal const uint DeleteOperationFlags = (uint)(
        FileOperationFlags.FOF_SILENT |
        FileOperationFlags.FOF_NOCONFIRMMKDIR |
        FileOperationFlags.FOF_NOERRORUI |
        FileOperationFlags.FOF_NOCONFIRMATION |
        FileOperationFlags.FOF_ALLOWUNDO |
        FileOperationFlags.FOFX_RECYCLEONDELETE);

    public Task<FileOperationBatchResult> CopyAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        IReadOnlyCollection<string> onlineRootPaths,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteBatchAsync(
            sourcePaths,
            destinationDirectory,
            onlineRootPaths,
            isMove: false,
            collisionPolicy,
            ownerWindow,
            progress,
            cancellationToken);
    }

    public Task<FileOperationBatchResult> CopyAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        IReadOnlyCollection<ManagedRoot> onlineRoots,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onlineRoots);
        return CopyAsync(
            sourcePaths,
            destinationDirectory,
            onlineRoots.Where(r => r.Status == ManagedRootStatus.Online).Select(r => r.Path).ToArray(),
            collisionPolicy,
            ownerWindow,
            progress,
            cancellationToken);
    }

    public Task<FileOperationBatchResult> MoveAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        IReadOnlyCollection<string> onlineRootPaths,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteBatchAsync(
            sourcePaths,
            destinationDirectory,
            onlineRootPaths,
            isMove: true,
            collisionPolicy,
            ownerWindow,
            progress,
            cancellationToken);
    }

    public Task<FileOperationBatchResult> MoveAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        IReadOnlyCollection<ManagedRoot> onlineRoots,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onlineRoots);
        return MoveAsync(
            sourcePaths,
            destinationDirectory,
            onlineRoots.Where(r => r.Status == ManagedRootStatus.Online).Select(r => r.Path).ToArray(),
            collisionPolicy,
            ownerWindow,
            progress,
            cancellationToken);
    }

    public Task<FileOperationItemResult> RenameAsync(
        string sourcePath,
        string newName,
        IReadOnlyCollection<string> onlineRootPaths,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ArgumentNullException.ThrowIfNull(onlineRootPaths);

        var trimmedName = newName.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName) ||
            trimmedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            trimmedName.Contains('/') ||
            trimmedName.Contains('\\') ||
            trimmedName == "." ||
            trimmedName == "..")
        {
            throw new ArgumentException($"新文件名“{newName}”包含非法字符或路径分隔符。", nameof(newName));
        }

        var normalizedSource = Normalize(sourcePath);
        var parentDir = Path.GetDirectoryName(normalizedSource);
        if (string.IsNullOrEmpty(parentDir))
        {
            throw new ArgumentException($"无法确定文件“{sourcePath}”的父目录。", nameof(sourcePath));
        }

        ValidateDestinationDirectory(parentDir, onlineRootPaths);

        if (string.Equals(Path.GetFileName(normalizedSource), trimmedName, StringComparison.Ordinal))
        {
            return Task.FromResult(new FileOperationItemResult(
                normalizedSource,
                normalizedSource,
                FileOperationItemStatus.Completed));
        }

        return RunOnStaThreadAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new FileOperationItemResult(
                    normalizedSource,
                    null,
                    FileOperationItemStatus.Canceled,
                    "操作已被取消。",
                    IsCanceled: true);
            }

            if (!File.Exists(normalizedSource) && !Directory.Exists(normalizedSource))
            {
                return new FileOperationItemResult(
                    normalizedSource,
                    null,
                    FileOperationItemStatus.Failed,
                    $"源文件不存在或无法访问：{normalizedSource}");
            }

            var targetPath = Path.Combine(parentDir, trimmedName);
            if (collisionPolicy == FileCollisionPolicy.Skip &&
                (File.Exists(targetPath) || Directory.Exists(targetPath)) &&
                !string.Equals(normalizedSource, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return new FileOperationItemResult(
                    normalizedSource,
                    targetPath,
                    FileOperationItemStatus.Skipped);
            }

            using var sink = new FileOperationProgressSink([normalizedSource], parentDir, 1, null, cancellationToken);
            var hrCreate = CreateFileOperation(out var fileOp);
            if (hrCreate != 0 || fileOp is null)
            {
                return new FileOperationItemResult(
                    normalizedSource,
                    null,
                    FileOperationItemStatus.Failed,
                    FormatHResultError(hrCreate, Path.GetFileName(normalizedSource)));
            }

            uint cookie = 0;
            try
            {
                var flags = (uint)(FileOperationFlags.FOF_SILENT |
                                   FileOperationFlags.FOF_NOCONFIRMMKDIR |
                                   FileOperationFlags.FOF_NOERRORUI |
                                   FileOperationFlags.FOF_NOCONFIRMATION);

                if (collisionPolicy == FileCollisionPolicy.AutoRename)
                {
                    flags |= (uint)FileOperationFlags.FOF_RENAMEONCOLLISION;
                }

                fileOp.SetOperationFlags(flags);
                if (ownerWindow != IntPtr.Zero)
                {
                    fileOp.SetOwnerWindow(ownerWindow);
                }

                var hrSource = CreateShellItem(normalizedSource, out var sourceItem);
                if (hrSource != 0 || sourceItem == IntPtr.Zero)
                {
                    return new FileOperationItemResult(
                        normalizedSource,
                        null,
                        FileOperationItemStatus.Failed,
                        FormatHResultError(hrSource, Path.GetFileName(normalizedSource)));
                }

                try
                {
                    fileOp.RenameItem(sourceItem, trimmedName, IntPtr.Zero);
                    fileOp.Advise(sink.ComPointer, out cookie);
                    try
                    {
                        fileOp.PerformOperations();
                    }
                    catch (COMException ex)
                    {
                        if (sink.TryGetResult(normalizedSource, out var reported) && reported is not null)
                        {
                            return reported;
                        }

                        return new FileOperationItemResult(
                            normalizedSource,
                            null,
                            FileOperationItemStatus.Failed,
                            FormatHResultError(ex.HResult, Path.GetFileName(normalizedSource)));
                    }

                    if (sink.TryGetResult(normalizedSource, out var result) && result is not null)
                    {
                        return result;
                    }

                    fileOp.GetAnyOperationsAborted(out var aborted);
                    if (aborted || cancellationToken.IsCancellationRequested)
                    {
                        return new FileOperationItemResult(
                            normalizedSource,
                            null,
                            FileOperationItemStatus.Canceled,
                            "操作已被取消。",
                            IsCanceled: true);
                    }

                    return new FileOperationItemResult(
                        normalizedSource,
                        targetPath,
                        FileOperationItemStatus.Completed);
                }
                finally
                {
                    if (sourceItem != IntPtr.Zero)
                    {
                        Marshal.Release(sourceItem);
                    }
                }
            }
            finally
            {
                if (cookie != 0)
                {
                    try { fileOp.Unadvise(cookie); } catch { }
                }
            }
        }, cancellationToken);
    }

    public Task<FileOperationItemResult> RenameAsync(
        string sourcePath,
        string newName,
        IReadOnlyCollection<ManagedRoot> onlineRoots,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onlineRoots);
        return RenameAsync(
            sourcePath,
            newName,
            onlineRoots.Where(r => r.Status == ManagedRootStatus.Online).Select(r => r.Path).ToArray(),
            collisionPolicy,
            ownerWindow,
            cancellationToken);
    }

    public Task<FileOperationBatchResult> DeleteToRecycleBinAsync(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyCollection<string> onlineRootPaths,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteDeleteBatchAsync(
            sourcePaths,
            onlineRootPaths,
            ownerWindow,
            progress,
            cancellationToken);
    }

    public Task<FileOperationBatchResult> DeleteToRecycleBinAsync(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyCollection<ManagedRoot> onlineRoots,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onlineRoots);
        return DeleteToRecycleBinAsync(
            sourcePaths,
            onlineRoots.Where(r => r.Status == ManagedRootStatus.Online).Select(r => r.Path).ToArray(),
            ownerWindow,
            progress,
            cancellationToken);
    }

    private Task<FileOperationBatchResult> ExecuteDeleteBatchAsync(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyCollection<string> onlineRootPaths,
        IntPtr ownerWindow,
        Action<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentNullException.ThrowIfNull(onlineRootPaths);

        if (sourcePaths.Count == 0)
        {
            return Task.FromResult(new FileOperationBatchResult([], false));
        }

        return RunOnStaThreadAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var canceledItems = sourcePaths
                    .Select(s => new FileOperationItemResult(
                        string.IsNullOrWhiteSpace(s) ? s : Normalize(s),
                        null,
                        FileOperationItemStatus.Canceled,
                        "操作已被取消。",
                        IsCanceled: true))
                    .ToArray();
                return new FileOperationBatchResult(canceledItems, true);
            }

            var preparedItems = new List<(string Source, FileOperationItemResult? PreResult)>(sourcePaths.Count);
            var completedCount = 0;
            var totalCount = sourcePaths.Count;

            foreach (var rawSource in sourcePaths)
            {
                if (string.IsNullOrWhiteSpace(rawSource))
                {
                    var fail = new FileOperationItemResult(rawSource ?? "", null, FileOperationItemStatus.Failed, "源路径不能为空。");
                    preparedItems.Add(("", fail));
                    completedCount++;
                    progress?.Invoke(new FileOperationProgress(totalCount, completedCount, rawSource));
                    continue;
                }

                string normalizedSource;
                try
                {
                    normalizedSource = Normalize(rawSource);
                }
                catch (Exception ex)
                {
                    var fail = new FileOperationItemResult(rawSource, null, FileOperationItemStatus.Failed, $"路径格式错误：{ex.Message}");
                    preparedItems.Add((rawSource, fail));
                    completedCount++;
                    progress?.Invoke(new FileOperationProgress(totalCount, completedCount, rawSource));
                    continue;
                }

                string? matchingRoot = null;
                foreach (var root in onlineRootPaths)
                {
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    var normalizedRoot = Normalize(root);
                    if (string.Equals(normalizedSource, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                        IsAncestor(normalizedRoot, normalizedSource))
                    {
                        matchingRoot = normalizedRoot;
                        break;
                    }
                }

                if (matchingRoot is null)
                {
                    var fail = new FileOperationItemResult(normalizedSource, null, FileOperationItemStatus.Failed, $"源路径“{rawSource}”不在任何在线管理根目录范围内。");
                    preparedItems.Add((normalizedSource, fail));
                    completedCount++;
                    progress?.Invoke(new FileOperationProgress(totalCount, completedCount, rawSource));
                    continue;
                }

                try
                {
                    CheckReparsePoints(matchingRoot, normalizedSource);
                }
                catch (Exception ex)
                {
                    var fail = new FileOperationItemResult(normalizedSource, null, FileOperationItemStatus.Failed, ex.Message);
                    preparedItems.Add((normalizedSource, fail));
                    completedCount++;
                    progress?.Invoke(new FileOperationProgress(totalCount, completedCount, rawSource));
                    continue;
                }

                var sourceExists = File.Exists(normalizedSource) || Directory.Exists(normalizedSource);
                if (!sourceExists)
                {
                    var fail = new FileOperationItemResult(normalizedSource, null, FileOperationItemStatus.Failed, $"源文件不存在或无法访问：{normalizedSource}");
                    preparedItems.Add((normalizedSource, fail));
                    completedCount++;
                    progress?.Invoke(new FileOperationProgress(totalCount, completedCount, normalizedSource));
                    continue;
                }

                preparedItems.Add((normalizedSource, null));
            }

            var itemsToQueue = preparedItems.Where(i => i.PreResult is null).ToList();
            if (itemsToQueue.Count == 0)
            {
                var finalResults = preparedItems.Select(i => i.PreResult!).ToList();
                return new FileOperationBatchResult(finalResults, false);
            }

            using var sink = new FileOperationProgressSink(itemsToQueue.Select(i => i.Source).ToList(), string.Empty, totalCount, progress, cancellationToken);
            var hrCreate = CreateFileOperation(out var fileOp);
            if (hrCreate != 0 || fileOp is null)
            {
                var createErr = FormatHResultError(hrCreate, "删除");
                var failList = preparedItems.Select(item =>
                    item.PreResult ?? new FileOperationItemResult(item.Source, null, FileOperationItemStatus.Failed, createErr)).ToList();
                return new FileOperationBatchResult(failList, false);
            }

            uint cookie = 0;
            try
            {
                var flags = DeleteOperationFlags;

                fileOp.SetOperationFlags(flags);
                if (ownerWindow != IntPtr.Zero)
                {
                    fileOp.SetOwnerWindow(ownerWindow);
                }

                var allocatedShellItems = new List<IntPtr>();
                try
                {
                    foreach (var (source, _) in itemsToQueue)
                    {
                        var hrSource = CreateShellItem(source, out var sourceItem);
                        if (hrSource != 0 || sourceItem == IntPtr.Zero)
                        {
                            var fail = new FileOperationItemResult(source, null, FileOperationItemStatus.Failed, FormatHResultError(hrSource, Path.GetFileName(source)));
                            sink.RecordDirectResult(source, fail);
                            continue;
                        }

                        allocatedShellItems.Add(sourceItem);
                        fileOp.DeleteItem(sourceItem, IntPtr.Zero);
                    }

                    fileOp.Advise(sink.ComPointer, out cookie);

                    try
                    {
                        fileOp.PerformOperations();
                    }
                    catch (COMException)
                    {
                    }

                    fileOp.GetAnyOperationsAborted(out var wasAborted);
                    wasAborted = wasAborted || cancellationToken.IsCancellationRequested;

                    var results = new List<FileOperationItemResult>(sourcePaths.Count);
                    foreach (var item in preparedItems)
                    {
                        if (item.PreResult is not null)
                        {
                            results.Add(item.PreResult);
                        }
                        else if (sink.TryGetResult(item.Source, out var reported) && reported is not null)
                        {
                            results.Add(reported);
                        }
                        else if (wasAborted)
                        {
                            results.Add(new FileOperationItemResult(
                                item.Source,
                                null,
                                FileOperationItemStatus.Canceled,
                                "操作已被取消。",
                                IsCanceled: true));
                        }
                        else
                        {
                            results.Add(new FileOperationItemResult(
                                item.Source,
                                null,
                                FileOperationItemStatus.Failed,
                                "操作未执行。"));
                        }
                    }

                    return new FileOperationBatchResult(results, wasAborted);
                }
                finally
                {
                    foreach (var ptr in allocatedShellItems)
                    {
                        if (ptr != IntPtr.Zero)
                        {
                            Marshal.Release(ptr);
                        }
                    }
                }
            }
            finally
            {
                if (cookie != 0)
                {
                    try { fileOp.Unadvise(cookie); } catch { }
                }
            }
        }, cancellationToken);
    }

    private Task<FileOperationBatchResult> ExecuteBatchAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        IReadOnlyCollection<string> onlineRootPaths,
        bool isMove,
        FileCollisionPolicy collisionPolicy,
        IntPtr ownerWindow,
        Action<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(onlineRootPaths);

        if (sourcePaths.Count == 0)
        {
            return Task.FromResult(new FileOperationBatchResult([], false));
        }

        var normalizedDest = ValidateDestinationDirectory(destinationDirectory, onlineRootPaths);

        return RunOnStaThreadAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var canceledItems = sourcePaths
                    .Select(s => new FileOperationItemResult(
                        string.IsNullOrWhiteSpace(s) ? s : Normalize(s),
                        null,
                        FileOperationItemStatus.Canceled,
                        "操作已被取消。",
                        IsCanceled: true))
                    .ToArray();
                return new FileOperationBatchResult(canceledItems, true);
            }

            var preparedItems = new List<(string Source, string? DestPath, FileOperationItemResult? PreResult)>(sourcePaths.Count);
            var completedCount = 0;
            var totalCount = sourcePaths.Count;

            foreach (var rawSource in sourcePaths)
            {
                if (string.IsNullOrWhiteSpace(rawSource))
                {
                    var fail = new FileOperationItemResult(rawSource ?? "", null, FileOperationItemStatus.Failed, "源路径不能为空。");
                    preparedItems.Add(("", null, fail));
                    completedCount++;
                    progress?.Invoke(new FileOperationProgress(totalCount, completedCount, rawSource));
                    continue;
                }

                string normalizedSource;
                try
                {
                    normalizedSource = Normalize(rawSource);
                }
                catch (Exception ex)
                {
                    var fail = new FileOperationItemResult(rawSource, null, FileOperationItemStatus.Failed, $"路径格式错误：{ex.Message}");
                    preparedItems.Add((rawSource, null, fail));
                    completedCount++;
                    progress?.Invoke(new FileOperationProgress(totalCount, completedCount, rawSource));
                    continue;
                }

                var sourceExists = File.Exists(normalizedSource) || Directory.Exists(normalizedSource);
                if (!sourceExists)
                {
                    var fail = new FileOperationItemResult(normalizedSource, null, FileOperationItemStatus.Failed, $"源文件不存在或无法访问：{normalizedSource}");
                    preparedItems.Add((normalizedSource, null, fail));
                    completedCount++;
                    progress?.Invoke(new FileOperationProgress(totalCount, completedCount, normalizedSource));
                    continue;
                }

                var isDir = (File.GetAttributes(normalizedSource) & FileAttributes.Directory) != 0;
                if (isDir && (string.Equals(normalizedSource, normalizedDest, StringComparison.OrdinalIgnoreCase) || IsAncestor(normalizedSource, normalizedDest)))
                {
                    var fail = new FileOperationItemResult(normalizedSource, null, FileOperationItemStatus.Failed, $"不能将目录“{Path.GetFileName(normalizedSource)}”复制或移动到其自身或其子目录中。");
                    preparedItems.Add((normalizedSource, null, fail));
                    completedCount++;
                    progress?.Invoke(new FileOperationProgress(totalCount, completedCount, normalizedSource));
                    continue;
                }

                var expectedTargetPath = Path.Combine(normalizedDest, Path.GetFileName(normalizedSource));
                var isSamePath = string.Equals(normalizedSource, expectedTargetPath, StringComparison.OrdinalIgnoreCase);

                if (isMove && isSamePath)
                {
                    var fail = new FileOperationItemResult(normalizedSource, expectedTargetPath, FileOperationItemStatus.Failed, $"源文件与目标路径相同：{normalizedSource}");
                    preparedItems.Add((normalizedSource, expectedTargetPath, fail));
                    completedCount++;
                    progress?.Invoke(new FileOperationProgress(totalCount, completedCount, normalizedSource));
                    continue;
                }

                if (!isMove && isSamePath && collisionPolicy == FileCollisionPolicy.Overwrite)
                {
                    var fail = new FileOperationItemResult(normalizedSource, expectedTargetPath, FileOperationItemStatus.Failed, $"源文件与目标文件相同，无法覆盖自身：{normalizedSource}");
                    preparedItems.Add((normalizedSource, expectedTargetPath, fail));
                    completedCount++;
                    progress?.Invoke(new FileOperationProgress(totalCount, completedCount, normalizedSource));
                    continue;
                }

                var targetExists = File.Exists(expectedTargetPath) || Directory.Exists(expectedTargetPath);
                if (collisionPolicy == FileCollisionPolicy.Skip && targetExists)
                {
                    var skip = new FileOperationItemResult(normalizedSource, expectedTargetPath, FileOperationItemStatus.Skipped);
                    preparedItems.Add((normalizedSource, expectedTargetPath, skip));
                    completedCount++;
                    progress?.Invoke(new FileOperationProgress(totalCount, completedCount, normalizedSource));
                    continue;
                }

                preparedItems.Add((normalizedSource, expectedTargetPath, null));
            }

            var itemsToQueue = preparedItems.Where(i => i.PreResult is null).ToList();
            if (itemsToQueue.Count == 0)
            {
                var finalResults = preparedItems.Select(i => i.PreResult!).ToList();
                return new FileOperationBatchResult(finalResults, false);
            }

            using var sink = new FileOperationProgressSink(itemsToQueue.Select(i => i.Source).ToList(), normalizedDest, totalCount, progress, cancellationToken);
            var hrCreate = CreateFileOperation(out var fileOp);
            if (hrCreate != 0 || fileOp is null)
            {
                var createErr = FormatHResultError(hrCreate, Path.GetFileName(normalizedDest));
                var failList = preparedItems.Select(item =>
                    item.PreResult ?? new FileOperationItemResult(item.Source, null, FileOperationItemStatus.Failed, createErr)).ToList();
                return new FileOperationBatchResult(failList, false);
            }

            uint cookie = 0;

            try
            {
                var flags = (uint)(FileOperationFlags.FOF_SILENT |
                                   FileOperationFlags.FOF_NOCONFIRMMKDIR |
                                   FileOperationFlags.FOF_NOERRORUI |
                                   FileOperationFlags.FOF_NOCONFIRMATION);

                if (collisionPolicy == FileCollisionPolicy.AutoRename)
                {
                    flags |= (uint)FileOperationFlags.FOF_RENAMEONCOLLISION;
                }

                fileOp.SetOperationFlags(flags);
                if (ownerWindow != IntPtr.Zero)
                {
                    fileOp.SetOwnerWindow(ownerWindow);
                }

                var hrDest = CreateShellItem(normalizedDest, out var destFolderItem);
                if (hrDest != 0 || destFolderItem == IntPtr.Zero)
                {
                    var destErr = FormatHResultError(hrDest, Path.GetFileName(normalizedDest));
                    var failList = preparedItems.Select(item =>
                        item.PreResult ?? new FileOperationItemResult(item.Source, null, FileOperationItemStatus.Failed, destErr)).ToList();
                    return new FileOperationBatchResult(failList, false);
                }

                var allocatedShellItems = new List<IntPtr> { destFolderItem };
                try
                {
                    foreach (var (source, _, _) in itemsToQueue)
                    {
                        var hrSource = CreateShellItem(source, out var sourceItem);
                        if (hrSource != 0 || sourceItem == IntPtr.Zero)
                        {
                            var fail = new FileOperationItemResult(source, null, FileOperationItemStatus.Failed, FormatHResultError(hrSource, Path.GetFileName(source)));
                            sink.RecordDirectResult(source, fail);
                            continue;
                        }

                        allocatedShellItems.Add(sourceItem);
                        if (isMove)
                        {
                            fileOp.MoveItem(sourceItem, destFolderItem, null, IntPtr.Zero);
                        }
                        else
                        {
                            fileOp.CopyItem(sourceItem, destFolderItem, null, IntPtr.Zero);
                        }
                    }

                    fileOp.Advise(sink.ComPointer, out cookie);

                    try
                    {
                        fileOp.PerformOperations();
                    }
                    catch (COMException)
                    {
                    }

                    fileOp.GetAnyOperationsAborted(out var wasAborted);
                    wasAborted = wasAborted || cancellationToken.IsCancellationRequested;

                    var results = new List<FileOperationItemResult>(sourcePaths.Count);
                    foreach (var item in preparedItems)
                    {
                        if (item.PreResult is not null)
                        {
                            results.Add(item.PreResult);
                        }
                        else if (sink.TryGetResult(item.Source, out var reported) && reported is not null)
                        {
                            results.Add(reported);
                        }
                        else if (wasAborted)
                        {
                            results.Add(new FileOperationItemResult(
                                item.Source,
                                null,
                                FileOperationItemStatus.Canceled,
                                "操作已被取消。",
                                IsCanceled: true));
                        }
                        else
                        {
                            results.Add(new FileOperationItemResult(
                                item.Source,
                                null,
                                FileOperationItemStatus.Failed,
                                "操作未执行。"));
                        }
                    }

                    return new FileOperationBatchResult(results, wasAborted);
                }
                finally
                {
                    foreach (var ptr in allocatedShellItems)
                    {
                        if (ptr != IntPtr.Zero)
                        {
                            Marshal.Release(ptr);
                        }
                    }
                }
            }
            finally
            {
                if (cookie != 0)
                {
                    try { fileOp.Unadvise(cookie); } catch { }
                }
            }
        }, cancellationToken);
    }

    public static string ValidateDestinationDirectory(string destinationDirectory, IReadOnlyCollection<string> onlineRootPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(onlineRootPaths);

        var normalizedDest = Normalize(destinationDirectory);

        string? matchingRoot = null;
        foreach (var root in onlineRootPaths)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var normalizedRoot = Normalize(root);
            if (string.Equals(normalizedDest, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                IsAncestor(normalizedRoot, normalizedDest))
            {
                matchingRoot = normalizedRoot;
                break;
            }
        }

        if (matchingRoot is null)
        {
            throw new ArgumentException($"目标路径“{destinationDirectory}”不在任何在线管理根目录范围内。", nameof(destinationDirectory));
        }

        CheckReparsePoints(matchingRoot, normalizedDest);

        return normalizedDest;
    }

    private static void CheckReparsePoints(string rootPath, string targetPath)
    {
        if (Directory.Exists(rootPath))
        {
            var rootAttr = File.GetAttributes(rootPath);
            if ((rootAttr & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"管理根目录“{rootPath}”包含重解析点，已被安全策略拒绝。");
            }
        }

        var current = targetPath;
        var segments = new Stack<string>();
        while (!string.IsNullOrEmpty(current) && !string.Equals(current, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            segments.Push(current);
            current = Path.GetDirectoryName(current);
        }

        while (segments.Count > 0)
        {
            var segment = segments.Pop();
            if (Directory.Exists(segment) || File.Exists(segment))
            {
                var attr = File.GetAttributes(segment);
                if ((attr & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException($"目标路径“{targetPath}”包含重解析点或符号链接，已被安全策略拒绝。");
                }
            }
        }
    }

    internal static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static bool IsAncestor(string parent, string child) =>
        child.Length > parent.Length
        && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
        && (Path.EndsInDirectorySeparator(parent) || IsDirectorySeparator(child[parent.Length]));

    private static bool IsDirectorySeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

    private static Task<T> RunOnStaThreadAsync<T>(Func<T> func, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var oleInit = OleInitialize(IntPtr.Zero);
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                var result = func();
                tcs.TrySetResult(result);
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                if (oleInit == 0 || oleInit == 1)
                {
                    OleUninitialize();
                }
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    internal static string FormatHResultError(int hr, string fileName)
    {
        var uintHr = (uint)hr;
        return uintHr switch
        {
            0x80070005 => $"无法访问“{fileName}”：权限被拒绝。",
            0x80070020 => $"无法操作“{fileName}”：文件正被其他程序占用。",
            0x80070021 => $"无法操作“{fileName}”：文件已被锁定。",
            0x80070002 => $"无法操作“{fileName}”：文件不存在或无法访问。",
            0x80070003 => $"无法操作“{fileName}”：路径不存在。",
            0x800700B7 or 0x80070050 => $"无法操作“{fileName}”：目标已存在同名项目。",
            0x80070070 => $"无法操作“{fileName}”：磁盘空间不足。",
            0x80070013 => $"无法操作“{fileName}”：目标介质受写保护。",
            0x800704C7 or 0x80004004 or 0x80270000 => $"操作已被取消。",
            0x80270008 => $"无法操作“{fileName}”：源文件与目标文件相同。",
            0x80270001 => $"无法操作“{fileName}”：目标目录位于源目录的子目录中。",
            _ => $"操作“{fileName}”失败：{Marshal.GetExceptionForHR(hr)?.Message ?? $"错误代码 0x{uintHr:X8}"}"
        };
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern void OleUninitialize();

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        in Guid riid,
        out IntPtr ppv);

    private static int CreateFileOperation(out IFileOperation? fileOp)
    {
        var hr = CoCreateInstance(in ClsidFileOperation, IntPtr.Zero, 1 | 4, in IidFileOperation, out var ptr);
        if (hr == 0 && ptr != IntPtr.Zero)
        {
            fileOp = (IFileOperation)Marshal.GetObjectForIUnknown(ptr);
            Marshal.Release(ptr);
            return 0;
        }

        fileOp = null;
        return hr;
    }

    private static int CreateShellItem(string path, out IntPtr shellItem)
    {
        return SHCreateItemFromParsingName(path, IntPtr.Zero, in IidShellItem, out shellItem);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class FileOperationProgressSink : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IFileOperationProgressSinkVtbl
    {
        public IntPtr QueryInterface;
        public IntPtr AddRef;
        public IntPtr Release;
        public IntPtr StartOperations;
        public IntPtr FinishOperations;
        public IntPtr PreRenameItem;
        public IntPtr PostRenameItem;
        public IntPtr PreMoveItem;
        public IntPtr PostMoveItem;
        public IntPtr PreCopyItem;
        public IntPtr PostCopyItem;
        public IntPtr PreDeleteItem;
        public IntPtr PostDeleteItem;
        public IntPtr PreNewItem;
        public IntPtr PostNewItem;
        public IntPtr UpdateProgress;
        public IntPtr ResetTimer;
        public IntPtr PauseTimer;
        public IntPtr ResumeTimer;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr thisPtr, IntPtr riidPtr, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint AddRefDelegate(IntPtr thisPtr);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr thisPtr);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StartOperationsDelegate(IntPtr thisPtr);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FinishOperationsDelegate(IntPtr thisPtr, int hrResult);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PreRenameItemDelegate(IntPtr thisPtr, uint dwFlags, IntPtr psiItem, IntPtr pszNewName);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PostRenameItemDelegate(IntPtr thisPtr, uint dwFlags, IntPtr psiItem, IntPtr pszNewName, int hrRename, IntPtr psiNewlyCreated);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PreMoveItemDelegate(IntPtr thisPtr, uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PostMoveItemDelegate(IntPtr thisPtr, uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName, int hrMove, IntPtr psiNewlyCreated);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PreCopyItemDelegate(IntPtr thisPtr, uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PostCopyItemDelegate(IntPtr thisPtr, uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName, int hrCopy, IntPtr psiNewlyCreated);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PreDeleteItemDelegate(IntPtr thisPtr, uint dwFlags, IntPtr psiItem);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PostDeleteItemDelegate(IntPtr thisPtr, uint dwFlags, IntPtr psiItem, int hrDelete, IntPtr psiNewlyCreated);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PreNewItemDelegate(IntPtr thisPtr, uint dwFlags, IntPtr psiDestinationFolder, IntPtr pszNewName);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PostNewItemDelegate(IntPtr thisPtr, uint dwFlags, IntPtr psiDestinationFolder, IntPtr pszNewName, IntPtr pszTemplateName, uint dwFileAttributes, int hrNewItem, IntPtr psiNewlyCreated);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UpdateProgressDelegate(IntPtr thisPtr, uint iWorkTotal, uint iWorkSoFar);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResetTimerDelegate(IntPtr thisPtr);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PauseTimerDelegate(IntPtr thisPtr);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResumeTimerDelegate(IntPtr thisPtr);

    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidProgressSink = new("04b0f1a7-9490-44bc-96e1-4296a31252e2");

    private readonly IntPtr _instancePtr;
    private readonly IntPtr _vtblPtr;
    private int _refCount = 1;

    private readonly QueryInterfaceDelegate _queryInterface;
    private readonly AddRefDelegate _addRef;
    private readonly ReleaseDelegate _release;
    private readonly StartOperationsDelegate _startOperations;
    private readonly FinishOperationsDelegate _finishOperations;
    private readonly PreRenameItemDelegate _preRenameItem;
    private readonly PostRenameItemDelegate _postRenameItem;
    private readonly PreMoveItemDelegate _preMoveItem;
    private readonly PostMoveItemDelegate _postMoveItem;
    private readonly PreCopyItemDelegate _preCopyItem;
    private readonly PostCopyItemDelegate _postCopyItem;
    private readonly PreDeleteItemDelegate _preDeleteItem;
    private readonly PostDeleteItemDelegate _postDeleteItem;
    private readonly PreNewItemDelegate _preNewItem;
    private readonly PostNewItemDelegate _postNewItem;
    private readonly UpdateProgressDelegate _updateProgress;
    private readonly ResetTimerDelegate _resetTimer;
    private readonly PauseTimerDelegate _pauseTimer;
    private readonly ResumeTimerDelegate _resumeTimer;

    private readonly Dictionary<string, FileOperationItemResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<FileOperationProgress>? _progress;
    private readonly CancellationToken _cancellationToken;
    private readonly int _totalItems;
    private readonly IReadOnlyList<string> _queuedSources;
    private readonly string _destDirectory;
    private int _completedItems;
    private int _currentIndex;

    public FileOperationProgressSink(
        IReadOnlyList<string> queuedSources,
        string destDirectory,
        int totalItems,
        Action<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        _queuedSources = queuedSources;
        _destDirectory = destDirectory;
        _totalItems = totalItems;
        _progress = progress;
        _cancellationToken = cancellationToken;

        _queryInterface = QueryInterfaceImpl;
        _addRef = AddRefImpl;
        _release = ReleaseImpl;
        _startOperations = StartOperationsImpl;
        _finishOperations = FinishOperationsImpl;
        _preRenameItem = PreRenameItemImpl;
        _postRenameItem = PostRenameItemImpl;
        _preMoveItem = PreMoveItemImpl;
        _postMoveItem = PostMoveItemImpl;
        _preCopyItem = PreCopyItemImpl;
        _postCopyItem = PostCopyItemImpl;
        _preDeleteItem = PreDeleteItemImpl;
        _postDeleteItem = PostDeleteItemImpl;
        _preNewItem = PreNewItemImpl;
        _postNewItem = PostNewItemImpl;
        _updateProgress = UpdateProgressImpl;
        _resetTimer = ResetTimerImpl;
        _pauseTimer = PauseTimerImpl;
        _resumeTimer = ResumeTimerImpl;

        var vtbl = new IFileOperationProgressSinkVtbl
        {
            QueryInterface = Marshal.GetFunctionPointerForDelegate(_queryInterface),
            AddRef = Marshal.GetFunctionPointerForDelegate(_addRef),
            Release = Marshal.GetFunctionPointerForDelegate(_release),
            StartOperations = Marshal.GetFunctionPointerForDelegate(_startOperations),
            FinishOperations = Marshal.GetFunctionPointerForDelegate(_finishOperations),
            PreRenameItem = Marshal.GetFunctionPointerForDelegate(_preRenameItem),
            PostRenameItem = Marshal.GetFunctionPointerForDelegate(_postRenameItem),
            PreMoveItem = Marshal.GetFunctionPointerForDelegate(_preMoveItem),
            PostMoveItem = Marshal.GetFunctionPointerForDelegate(_postMoveItem),
            PreCopyItem = Marshal.GetFunctionPointerForDelegate(_preCopyItem),
            PostCopyItem = Marshal.GetFunctionPointerForDelegate(_postCopyItem),
            PreDeleteItem = Marshal.GetFunctionPointerForDelegate(_preDeleteItem),
            PostDeleteItem = Marshal.GetFunctionPointerForDelegate(_postDeleteItem),
            PreNewItem = Marshal.GetFunctionPointerForDelegate(_preNewItem),
            PostNewItem = Marshal.GetFunctionPointerForDelegate(_postNewItem),
            UpdateProgress = Marshal.GetFunctionPointerForDelegate(_updateProgress),
            ResetTimer = Marshal.GetFunctionPointerForDelegate(_resetTimer),
            PauseTimer = Marshal.GetFunctionPointerForDelegate(_pauseTimer),
            ResumeTimer = Marshal.GetFunctionPointerForDelegate(_resumeTimer)
        };

        _vtblPtr = Marshal.AllocHGlobal(Marshal.SizeOf<IFileOperationProgressSinkVtbl>());
        Marshal.StructureToPtr(vtbl, _vtblPtr, false);

        _instancePtr = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(_instancePtr, _vtblPtr);
    }

    public IntPtr ComPointer => _instancePtr;

    public void Dispose()
    {
        if (_instancePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_instancePtr);
        }
        if (_vtblPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_vtblPtr);
        }
        GC.SuppressFinalize(this);
    }

    private int QueryInterfaceImpl(IntPtr thisPtr, IntPtr riidPtr, out IntPtr ppv)
    {
        if (riidPtr == IntPtr.Zero)
        {
            ppv = IntPtr.Zero;
            return unchecked((int)0x80004003);
        }

        var riid = Marshal.PtrToStructure<Guid>(riidPtr);
        if (riid == IidIUnknown || riid == IidProgressSink)
        {
            ppv = thisPtr;
            AddRefImpl(thisPtr);
            return 0;
        }

        ppv = IntPtr.Zero;
        return unchecked((int)0x80004002);
    }

    private uint AddRefImpl(IntPtr thisPtr) => (uint)Interlocked.Increment(ref _refCount);

    private uint ReleaseImpl(IntPtr thisPtr) => (uint)Interlocked.Decrement(ref _refCount);

    public void RecordDirectResult(string sourcePath, FileOperationItemResult result)
    {
        _results[sourcePath] = result;
        Interlocked.Increment(ref _completedItems);
        _progress?.Invoke(new FileOperationProgress(_totalItems, _completedItems, sourcePath));
    }

    public bool TryGetResult(string sourcePath, out FileOperationItemResult? result)
    {
        return _results.TryGetValue(sourcePath, out result);
    }

    private static string? GetPathFromIntPtr(IntPtr psi)
    {
        if (psi == IntPtr.Zero) return null;
        try
        {
            var item = (IShellItem)Marshal.GetObjectForIUnknown(psi);
            if (item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path) == 0 && !string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
            if (item.GetDisplayName(SIGDN.SIGDN_DESKTOPABSOLUTEPARSING, out var parsePath) == 0 && !string.IsNullOrWhiteSpace(parsePath))
            {
                return parsePath;
            }
        }
        catch
        {
        }
        return null;
    }

    private int CheckCancellation() =>
        _cancellationToken.IsCancellationRequested ? unchecked((int)0x800704C7) : 0;

    private int StartOperationsImpl(IntPtr thisPtr) => CheckCancellation();
    private int FinishOperationsImpl(IntPtr thisPtr, int hrResult) => 0;

    private int PreRenameItemImpl(IntPtr thisPtr, uint dwFlags, IntPtr psiItem, IntPtr pszNewName) => CheckCancellation();

    private int PostRenameItemImpl(IntPtr thisPtr, uint dwFlags, IntPtr psiItem, IntPtr pszNewName, int hrRename, IntPtr psiNewlyCreated)
    {
        var newName = Marshal.PtrToStringUni(pszNewName);
        var src = (_currentIndex < _queuedSources.Count) ? _queuedSources[_currentIndex] : GetPathFromIntPtr(psiItem);
        _currentIndex++;
        string? target = GetPathFromIntPtr(psiNewlyCreated);
        if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(newName))
        {
            var dir = Path.GetDirectoryName(src);
            if (!string.IsNullOrEmpty(dir))
            {
                target = Path.Combine(dir, newName);
            }
        }

        RecordResult(src, target, hrRename);
        return CheckCancellation();
    }

    private int PreMoveItemImpl(IntPtr thisPtr, uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName) => CheckCancellation();

    private int PostMoveItemImpl(IntPtr thisPtr, uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName, int hrMove, IntPtr psiNewlyCreated)
    {
        var newName = Marshal.PtrToStringUni(pszNewName);
        var src = (_currentIndex < _queuedSources.Count) ? _queuedSources[_currentIndex] : GetPathFromIntPtr(psiItem);
        _currentIndex++;
        string? target = GetPathFromIntPtr(psiNewlyCreated);
        if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(src))
        {
            var fileName = !string.IsNullOrEmpty(newName) ? newName : Path.GetFileName(src);
            target = Path.Combine(_destDirectory, fileName);
        }

        RecordResult(src, target, hrMove);
        return CheckCancellation();
    }

    private int PreCopyItemImpl(IntPtr thisPtr, uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName) => CheckCancellation();

    private int PostCopyItemImpl(IntPtr thisPtr, uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName, int hrCopy, IntPtr psiNewlyCreated)
    {
        var newName = Marshal.PtrToStringUni(pszNewName);
        var src = (_currentIndex < _queuedSources.Count) ? _queuedSources[_currentIndex] : GetPathFromIntPtr(psiItem);
        _currentIndex++;
        string? target = GetPathFromIntPtr(psiNewlyCreated);
        if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(src))
        {
            var fileName = !string.IsNullOrEmpty(newName) ? newName : Path.GetFileName(src);
            target = Path.Combine(_destDirectory, fileName);
        }

        RecordResult(src, target, hrCopy);
        return CheckCancellation();
    }

    private int PreDeleteItemImpl(IntPtr thisPtr, uint dwFlags, IntPtr psiItem) => CheckCancellation();

    private int PostDeleteItemImpl(IntPtr thisPtr, uint dwFlags, IntPtr psiItem, int hrDelete, IntPtr psiNewlyCreated)
    {
        var src = (_currentIndex < _queuedSources.Count) ? _queuedSources[_currentIndex] : GetPathFromIntPtr(psiItem);
        _currentIndex++;
        RecordResult(src, null, hrDelete);
        return CheckCancellation();
    }

    private int PreNewItemImpl(IntPtr thisPtr, uint dwFlags, IntPtr psiDestinationFolder, IntPtr pszNewName) => CheckCancellation();
    private int PostNewItemImpl(IntPtr thisPtr, uint dwFlags, IntPtr psiDestinationFolder, IntPtr pszNewName, IntPtr pszTemplateName, uint dwFileAttributes, int hrNewItem, IntPtr psiNewlyCreated) => 0;
    private int UpdateProgressImpl(IntPtr thisPtr, uint iWorkTotal, uint iWorkSoFar) => CheckCancellation();
    private int ResetTimerImpl(IntPtr thisPtr) => 0;
    private int PauseTimerImpl(IntPtr thisPtr) => 0;
    private int ResumeTimerImpl(IntPtr thisPtr) => 0;

    private void RecordResult(string? sourcePath, string? actualTargetPath, int hr)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return;
        }

        FileOperationItemResult itemResult;
        if (hr >= 0)
        {
            itemResult = new FileOperationItemResult(
                sourcePath,
                actualTargetPath,
                FileOperationItemStatus.Completed);
        }
        else if (hr == unchecked((int)0x800704C7) || hr == unchecked((int)0x80004004) || hr == unchecked((int)0x80270000))
        {
            itemResult = new FileOperationItemResult(
                sourcePath,
                actualTargetPath,
                FileOperationItemStatus.Canceled,
                "操作已被取消。",
                IsCanceled: true);
        }
        else
        {
            itemResult = new FileOperationItemResult(
                sourcePath,
                actualTargetPath,
                FileOperationItemStatus.Failed,
                SafeFileOperationExecutor.FormatHResultError(hr, Path.GetFileName(sourcePath)));
        }

        _results[sourcePath] = itemResult;
        Interlocked.Increment(ref _completedItems);
        _progress?.Invoke(new FileOperationProgress(_totalItems, _completedItems, sourcePath));
    }
}

[ComImport]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    void Advise(IntPtr pfops, out uint pdwCookie);
    void Unadvise(uint dwCookie);

    void SetOperationFlags(uint dwOperationFlags);
    void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
    void SetProgressDialog(IntPtr popd);
    void SetProperties(IntPtr pproparray);
    void SetOwnerWindow(IntPtr hwnd);

    void ApplyPropertiesToItem(IntPtr psiItem);
    void ApplyPropertiesToItems(IntPtr punkItems);

    void RenameItem(
        IntPtr psiItem,
        [MarshalAs(UnmanagedType.LPWStr)] string pszNewName,
        IntPtr pfopsItem);

    void RenameItems(
        IntPtr pUnkItems,
        [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);

    void MoveItem(
        IntPtr psiItem,
        IntPtr psiDestinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName,
        IntPtr pfopsItem);

    void MoveItems(
        IntPtr punkItems,
        IntPtr psiDestinationFolder);

    void CopyItem(
        IntPtr psiItem,
        IntPtr psiDestinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName,
        IntPtr pfopsItem);

    void CopyItems(
        IntPtr punkItems,
        IntPtr psiDestinationFolder);

    void DeleteItem(
        IntPtr psiItem,
        IntPtr pfopsItem);

    void DeleteItems(
        IntPtr punkItems);

    void NewItem(
        IntPtr psiFolder,
        uint dwFileAttributes,
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName,
        IntPtr pfopsItem);

    void PerformOperations();

    void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
}

[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig]
    int BindToHandler(
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IntPtr ppv);

    [PreserveSig]
    int GetParent(out IShellItem ppsi);

    [PreserveSig]
    int GetDisplayName(
        SIGDN sigdnName,
        [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

    [PreserveSig]
    int GetAttributes(
        uint sfgaoMask,
        out uint psfgaoAttribs);

    [PreserveSig]
    int Compare(
        IShellItem psi,
        uint hint,
        out int piOrder);
}

internal enum SIGDN : uint
{
    SIGDN_NORMALDISPLAY = 0x00000000,
    SIGDN_PARENTRELATIVEPARSING = 0x80018001,
    SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000,
    SIGDN_PARENTRELATIVEEDITING = 0x80031001,
    SIGDN_DESKTOPABSOLUTEEDITING = 0x8004c000,
    SIGDN_FILESYSPATH = 0x80058000,
    SIGDN_URL = 0x80068000,
    SIGDN_PARENTRELATIVEFORADDRESSBAR = 0x8007c001,
    SIGDN_PARENTRELATIVE = 0x80080001,
    SIGDN_PARENTRELATIVEFORUI = 0x80094001
}

[Flags]
internal enum FileOperationFlags : uint
{
    FOF_MULTIDESTFILES = 0x0001,
    FOF_CONFIRMMOUSE = 0x0002,
    FOF_SILENT = 0x0004,
    FOF_RENAMEONCOLLISION = 0x0008,
    FOF_NOCONFIRMATION = 0x0010,
    FOF_WANTMAPPINGHANDLE = 0x0020,
    FOF_ALLOWUNDO = 0x0040,
    FOF_FILESONLY = 0x0080,
    FOF_SIMPLEPROGRESS = 0x0100,
    FOF_NOCONFIRMMKDIR = 0x0200,
    FOF_NOERRORUI = 0x0400,
    FOF_NOCOPYSECURITYATTRIBS = 0x0800,
    FOF_NORECURSION = 0x1000,
    FOF_NO_CONNECTED_ELEMENTS = 0x2000,
    FOF_WANTNUKEWARNING = 0x4000,
    FOFX_RECYCLEONDELETE = 0x00080000,
}
