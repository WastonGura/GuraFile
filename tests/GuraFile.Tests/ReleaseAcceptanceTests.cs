using System.Runtime.Versioning;
using GuraFile.Storage;
using System.Reflection;

namespace GuraFile.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class ReleaseAcceptanceTests
{
    [TestMethod]
    public async Task ScanTagRestartSearchOpenExportRestoreAndRenameKeepsUserData()
    {
        using var temp = TempDirectory.Create();
        var rootPath = Path.Combine(temp.Path, "管理 根目录");
        Directory.CreateDirectory(rootPath);
        var originalPath = Path.Combine(rootPath, "资料 文件.txt");
        await File.WriteAllTextAsync(originalPath, "GuraFile v0.2.0");
        var sourceDatabase = Path.Combine(temp.Path, "source.db");
        var scanner = new ManagedRootScanner(sourceDatabase);
        var root = scanner.AddRoot(rootPath);

        var firstScan = await scanner.ScanAsync(root.Id);
        var indexed = (await new FileQueryService(sourceDatabase).QueryAsync(new(Search: "资料"))).Single();
        var tag = new TagService(sourceDatabase).CreateTag("Release");
        new TagService(sourceDatabase).AddTagToFiles(tag.Id, [indexed.Id]);

        var reopened = (await new FileQueryService(sourceDatabase).QueryAsync(new(Search: "文件"))).Single();
        string? openedPath = null;
        new ShellFileActions(path => openedPath = path, _ => { }).Open(reopened.Path);
        var backup = new UserTagBackupService(sourceDatabase).Export();

        var targetDatabase = Path.Combine(temp.Path, "target.db");
        var targetScanner = new ManagedRootScanner(targetDatabase);
        var targetRoot = targetScanner.AddRoot(rootPath);
        await targetScanner.ScanAsync(targetRoot.Id);
        var imported = new UserTagBackupService(targetDatabase).Import(backup);
        var restored = (await new FileQueryService(targetDatabase).QueryAsync(new(Search: "资料"))).Single();

        var renamedPath = Path.Combine(rootPath, "已重命名.txt");
        File.Move(originalPath, renamedPath);
        var secondScan = await scanner.ScanAsync(root.Id);
        var renamed = (await new FileQueryService(sourceDatabase).QueryAsync(new(Search: "已重命名"))).Single();

        Assert.AreEqual(1, firstScan.CommittedFiles);
        Assert.AreEqual(Path.GetFullPath(originalPath), openedPath);
        Assert.AreEqual(1, imported.RestoredRelations);
        Assert.AreEqual("Release", new TagService(targetDatabase).ListTagsForFile(restored.Id).Single().Name);
        Assert.AreEqual(indexed.Id, renamed.Id, "Same-volume rename did not preserve the indexed node.");
        Assert.AreEqual("Release", new TagService(sourceDatabase).ListTagsForFile(renamed.Id).Single().Name);
        Assert.AreEqual(0, secondScan.MissingFiles);
    }

    [TestMethod]
    public async Task RealtimeChangesReclassifyFilesWithoutChangingUserTags()
    {
        using var temp = TempDirectory.Create();
        var rootPath = Path.Combine(temp.Path, "实时根目录");
        Directory.CreateDirectory(rootPath);
        var originalPath = Path.Combine(rootPath, "笔记.txt");
        await File.WriteAllTextAsync(originalPath, "notes");
        var databasePath = Path.Combine(temp.Path, "realtime.db");
        var scanner = new ManagedRootScanner(databasePath);
        var root = scanner.AddRoot(rootPath);
        await scanner.ScanAsync(root.Id);
        var query = new FileQueryService(databasePath);
        var tags = new TagService(databasePath);
        var indexed = (await query.QueryAsync(new())).Single();
        var userTag = tags.CreateTag("发布验收");
        tags.AddTagToFiles(userTag.Id, [indexed.Id]);
        await using var coordinator = new FileChangeCoordinator(
            scanner,
            debounce: TimeSpan.FromMilliseconds(50),
            retryInterval: TimeSpan.FromHours(1));
        Assert.IsTrue(coordinator.Watch(root));

        var markdownPath = Path.Combine(rootPath, "笔记.md");
        File.Move(originalPath, markdownPath);
        var markdown = await WaitForFileWithinTwoSecondsAsync(
            query,
            file => file.Id == indexed.Id && file.Path == markdownPath && file.IsOnline);
        Assert.AreEqual("发布验收", tags.ListTagsForFile(markdown.Id).Single().Name);
        CollectionAssert.Contains(
            tags.ListAutomaticTagsForFile(markdown.Id).Select(tag => tag.Name).ToArray(),
            "格式/Markdown");

        var directory = Directory.CreateDirectory(Path.Combine(rootPath, "外部移动")).FullName;
        var movedPath = Path.Combine(directory, "笔记.md");
        File.Move(markdownPath, movedPath);
        var moved = await WaitForFileWithinTwoSecondsAsync(
            query,
            file => file.Id == indexed.Id && file.Path == movedPath && file.IsOnline);
        await File.AppendAllTextAsync(movedPath, "-changed");
        await WaitForFileWithinTwoSecondsAsync(query, file => file.Id == moved.Id && file.Size > moved.Size);
        Assert.AreEqual("发布验收", tags.ListTagsForFile(moved.Id).Single().Name);

        var conflictPath = Path.Combine(rootPath, "冲突.txt");
        await File.WriteAllBytesAsync(conflictPath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var conflict = await WaitForFileWithinTwoSecondsAsync(query, file => file.Path == conflictPath && file.IsOnline);
        CollectionAssert.Contains(
            tags.ListAutomaticTagsForFile(conflict.Id).Select(tag => tag.Name).ToArray(),
            "状态/类型冲突");

        File.Delete(conflictPath);
        await WaitForFileWithinTwoSecondsAsync(query, file => file.Id == conflict.Id && !file.IsOnline);
    }

    [TestMethod]
    public async Task StartupOverflowAndOfflineRecoveryPreserveIndexedNodesAndTags()
    {
        using var temp = TempDirectory.Create();
        var rootPath = Path.Combine(temp.Path, "恢复根目录");
        Directory.CreateDirectory(rootPath);
        var originalPath = Path.Combine(rootPath, "保留.txt");
        await File.WriteAllTextAsync(originalPath, "before");
        var databasePath = Path.Combine(temp.Path, "recovery.db");
        var scanner = new ManagedRootScanner(databasePath);
        var root = scanner.AddRoot(rootPath);
        await scanner.ScanAsync(root.Id);
        var query = new FileQueryService(databasePath);
        var tags = new TagService(databasePath);
        var original = (await query.QueryAsync(new())).Single();
        var userTag = tags.CreateTag("始终保留");
        tags.AddTagToFiles(userTag.Id, [original.Id]);

        var renamedPath = Path.Combine(rootPath, "保留.md");
        File.Move(originalPath, renamedPath);
        var missedPath = Path.Combine(rootPath, "关闭期间.txt");
        await File.WriteAllTextAsync(missedPath, "missed");
        var reopenedScanner = new ManagedRootScanner(databasePath);
        var reopenedRoot = reopenedScanner.ListRoots().Single();
        var updates = new SemaphoreSlim(0);
        var reportedError = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new FileChangeCoordinator(
            reopenedScanner,
            _ => updates.Release(),
            exception => reportedError.TrySetResult(exception),
            debounce: TimeSpan.FromMilliseconds(50),
            retryInterval: TimeSpan.FromMilliseconds(50));

        coordinator.Start(reopenedRoot);
        Assert.IsTrue(await updates.WaitAsync(TimeSpan.FromSeconds(2)));
        var renamed = (await query.QueryAsync(new())).Single(file => file.Id == original.Id);
        Assert.AreEqual(renamedPath, renamed.Path);
        Assert.AreEqual("始终保留", tags.ListTagsForFile(renamed.Id).Single().Name);
        CollectionAssert.Contains(
            tags.ListAutomaticTagsForFile(renamed.Id).Select(tag => tag.Name).ToArray(),
            "格式/Markdown");

        var watcher = CurrentWatcher(coordinator, reopenedRoot.Id);
        watcher.EnableRaisingEvents = false;
        var droppedPath = Path.Combine(rootPath, "丢失事件.txt");
        await File.WriteAllTextAsync(droppedPath, "dropped");
        File.Delete(missedPath);
        var overflow = new InternalBufferOverflowException("simulated dropped events");
        RaiseError(watcher, overflow);
        Assert.AreSame(overflow, await reportedError.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(await updates.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue((await query.QueryAsync(new())).Single(file => file.Path == droppedPath).IsOnline);
        Assert.IsFalse((await query.QueryAsync(new())).Single(file => file.Path == missedPath).IsOnline);

        var unavailablePath = rootPath + "-offline";
        Directory.Move(rootPath, unavailablePath);
        coordinator.RequestRecovery(reopenedRoot, new IOException("simulated offline root"));
        await WaitForRootStatusAsync(reopenedScanner, ManagedRootStatus.Offline);
        Assert.IsTrue((await query.QueryAsync(new())).Single(file => file.Id == original.Id).IsOnline);
        Assert.AreEqual("始终保留", tags.ListTagsForFile(original.Id).Single().Name);
        await File.AppendAllTextAsync(Path.Combine(unavailablePath, "保留.md"), "-offline-change");
        Directory.Move(unavailablePath, rootPath);
        await WaitForRootStatusAsync(reopenedScanner, ManagedRootStatus.Online);

        var recovered = (await query.QueryAsync(new())).Single(file => file.Id == original.Id);
        Assert.IsGreaterThan(renamed.Size, recovered.Size);
        Assert.AreEqual("始终保留", tags.ListTagsForFile(recovered.Id).Single().Name);
    }

    [TestMethod]
    public async Task OccupiedFileDoesNotBlockRealtimeIndexingOfOtherFiles()
    {
        using var temp = TempDirectory.Create();
        var rootPath = Path.Combine(temp.Path, "占用根目录");
        Directory.CreateDirectory(rootPath);
        var databasePath = Path.Combine(temp.Path, "occupied.db");
        var scanner = new ManagedRootScanner(databasePath);
        var root = scanner.AddRoot(rootPath);
        var query = new FileQueryService(databasePath);
        await using var coordinator = new FileChangeCoordinator(
            scanner,
            debounce: TimeSpan.FromMilliseconds(50),
            retryInterval: TimeSpan.FromHours(1));
        Assert.IsTrue(coordinator.Watch(root));
        var occupiedPath = Path.Combine(rootPath, "占用.txt");
        var siblingPath = Path.Combine(rootPath, "正常.md");

        using (var occupied = new FileStream(
            occupiedPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            occupied.Write("occupied"u8);
            occupied.Flush(flushToDisk: true);
            await File.WriteAllTextAsync(siblingPath, "available");
            await WaitForFileWithinTwoSecondsAsync(query, file => file.Path == siblingPath && file.IsOnline);
        }

        await File.AppendAllTextAsync(occupiedPath, "-released");
        await WaitForFileWithinTwoSecondsAsync(query, file => file.Path == occupiedPath && file.IsOnline);
    }

    [TestMethod]
    public async Task FullBusinessChain_CreateScanTagCopyMoveRenameRecycleRestart_PreservesConsistencyAndUserDataAsync()
    {
        using var temp = TempDirectory.Create();
        var rootPath = Path.Combine(temp.Path, "业务链根目录");
        Directory.CreateDirectory(rootPath);
        var databasePath = Path.Combine(temp.Path, "e2e_acceptance.db");

        // 1. Create file on disk
        var originalFileName = $"acceptance_{Guid.NewGuid():N}.txt";
        var originalPath = Path.Combine(rootPath, originalFileName);
        await File.WriteAllTextAsync(originalPath, "GuraFile v0.3 Full Chain Acceptance Data");

        // 2. Add root and Scan
        var scanner = new ManagedRootScanner(databasePath);
        var root = scanner.AddRoot(rootPath);
        var scanResult = await scanner.ScanAsync(root.Id);
        Assert.AreEqual(1, scanResult.CommittedFiles);

        var queryService = new FileQueryService(databasePath);
        var initialFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, initialFiles);
        var initialFile = initialFiles[0];
        Assert.AreEqual(originalPath, initialFile.Path);
        Assert.IsTrue(initialFile.IsOnline);

        // 3. User applies user tag
        var tagService = new TagService(databasePath);
        var userTag = tagService.CreateTag("v0.3 Acceptance Tag");
        tagService.AddTagToFiles(userTag.Id, [initialFile.Id]);
        var tagsAfterTagging = tagService.ListTagsForFile(initialFile.Id);
        Assert.AreEqual("v0.3 Acceptance Tag", tagsAfterTagging.Single().Name);

        var initialAutoTags = tagService.ListAutomaticTagsForFile(initialFile.Id);
        CollectionAssert.Contains(initialAutoTags.Select(t => t.Name).ToArray(), "类型/文档");
        CollectionAssert.Contains(initialAutoTags.Select(t => t.Name).ToArray(), "格式/TXT");

        // 4. Copy operation (inherits user tags and computes automatic tags)
        var copyDir = Path.Combine(rootPath, "Copied");
        Directory.CreateDirectory(copyDir);
        var committer = new FileOperationIndexCommitter(scanner);
        var copyResult = await committer.CopyAsync([originalPath], copyDir, [root.Path]);
        Assert.AreEqual(1, copyResult.SucceededCount);
        var copiedPath = copyResult.Items[0].ActualTargetPath!;
        Assert.IsTrue(File.Exists(originalPath));
        Assert.IsTrue(File.Exists(copiedPath));

        var filesAfterCopy = await queryService.QueryAsync(new());
        Assert.HasCount(2, filesAfterCopy.Where(f => f.IsOnline));
        var copiedDb = filesAfterCopy.Single(f => f.Path == copiedPath);
        Assert.AreEqual("v0.3 Acceptance Tag", tagService.ListTagsForFile(copiedDb.Id).Single().Name);

        // 5. Move operation (preserves user tags and stable identity)
        var moveDir = Path.Combine(rootPath, "Moved");
        Directory.CreateDirectory(moveDir);
        var moveResult = await committer.MoveAsync([copiedPath], moveDir, [root.Path]);
        Assert.AreEqual(1, moveResult.SucceededCount);
        var movedPath = moveResult.Items[0].ActualTargetPath!;
        Assert.IsFalse(File.Exists(copiedPath));
        Assert.IsTrue(File.Exists(movedPath));

        var filesAfterMove = await queryService.QueryAsync(new());
        var movedDb = filesAfterMove.Single(f => f.Path == movedPath);
        Assert.AreEqual(copiedDb.Id, movedDb.Id);
        Assert.AreEqual("v0.3 Acceptance Tag", tagService.ListTagsForFile(movedDb.Id).Single().Name);

        // 6. Rename operation (preserves user tags and updates automatic tags for .md extension)
        var renamedFileName = $"renamed_{Guid.NewGuid():N}.md";
        var renameResult = await committer.RenameAsync(movedPath, renamedFileName, [root.Path]);
        Assert.AreEqual(FileOperationItemStatus.Completed, renameResult.Status);
        var renamedPath = renameResult.ActualTargetPath!;
        Assert.IsFalse(File.Exists(movedPath));
        Assert.IsTrue(File.Exists(renamedPath));

        var filesAfterRename = await queryService.QueryAsync(new());
        var renamedDb = filesAfterRename.Single(f => f.Path == renamedPath);
        Assert.AreEqual(copiedDb.Id, renamedDb.Id);
        Assert.AreEqual("v0.3 Acceptance Tag", tagService.ListTagsForFile(renamedDb.Id).Single().Name);
        var autoTagsAfterRename = tagService.ListAutomaticTagsForFile(renamedDb.Id);
        CollectionAssert.Contains(autoTagsAfterRename.Select(t => t.Name).ToArray(), "格式/Markdown");

        // 7. Delete to Recycle Bin (marks offline and preserves user tags)
        var deleteResult = await committer.DeleteToRecycleBinAsync([renamedPath], [root.Path]);
        Assert.AreEqual(1, deleteResult.SucceededCount);
        Assert.IsFalse(File.Exists(renamedPath));
        Assert.IsTrue(RecycleBinTestHelper.ExistsInRecycleBin(renamedFileName, Path.GetDirectoryName(renamedPath)), "Deleted file was not found in Recycle Bin.");

        using (var connection = SqliteDatabase.Open(databasePath))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT is_online FROM files WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", renamedDb.Id);
            var isOnline = (long)cmd.ExecuteScalar()!;
            Assert.AreEqual(0, isOnline, "Deleted file must be marked offline in database.");
        }

        var offlineTags = tagService.ListTagsForFile(renamedDb.Id);
        Assert.AreEqual("v0.3 Acceptance Tag", offlineTags.Single().Name, "Offline deleted file must preserve user tags.");

        // 8. Restart application and rescan reconciliation
        var restartedScanner = new ManagedRootScanner(databasePath);
        var roots = restartedScanner.ListRoots();
        Assert.HasCount(1, roots);
        Assert.AreEqual(ManagedRootStatus.Online, roots[0].Status);

        var restartScanResult = await restartedScanner.ScanAsync(roots[0].Id);
        Assert.AreEqual(0, restartScanResult.AddedFiles);
        Assert.AreEqual(0, restartScanResult.MissingFiles);

        var filesAfterRestart = await queryService.QueryAsync(new());
        var onlineAfterRestart = filesAfterRestart.Where(f => f.IsOnline).ToList();
        var offlineAfterRestart = filesAfterRestart.Where(f => !f.IsOnline).ToList();

        Assert.HasCount(1, onlineAfterRestart);
        Assert.AreEqual(originalPath, onlineAfterRestart[0].Path);
        Assert.AreEqual("v0.3 Acceptance Tag", tagService.ListTagsForFile(onlineAfterRestart[0].Id).Single().Name);

        Assert.HasCount(1, offlineAfterRestart);
        Assert.AreEqual(renamedDb.Id, offlineAfterRestart[0].Id);
        Assert.AreEqual("v0.3 Acceptance Tag", tagService.ListTagsForFile(offlineAfterRestart[0].Id).Single().Name);
    }

    private static async Task<IndexedFile> WaitForFileWithinTwoSecondsAsync(
        FileQueryService query,
        Func<IndexedFile, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var file = (await query.QueryAsync(new())).FirstOrDefault(predicate);
            if (file is not null)
            {
                return file;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Real-time file state did not converge within two seconds.");
        throw new InvalidOperationException();
    }

    private static FileSystemWatcher CurrentWatcher(FileChangeCoordinator coordinator, long rootId)
    {
        var field = typeof(FileChangeCoordinator).GetField("_watchers", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(FileChangeCoordinator).FullName, "_watchers");
        var watchers = (Dictionary<long, FileSystemWatcher>)field.GetValue(coordinator)!;
        return watchers[rootId];
    }

    private static void RaiseError(FileSystemWatcher watcher, Exception exception)
    {
        var method = typeof(FileSystemWatcher).GetMethod("OnError", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(FileSystemWatcher).FullName, "OnError");
        method.Invoke(watcher, [new ErrorEventArgs(exception)]);
    }

    private static async Task WaitForRootStatusAsync(ManagedRootScanner scanner, ManagedRootStatus status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (scanner.ListRoots().Single().Status == status)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Managed root did not become {status} within two seconds.");
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"GuraFile.Release.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new(path);
        }

        public void Dispose()
        {
            try
            {
                RecycleBinTestHelper.CleanupRecycleBinItemsForDirectory(Path);
            }
            catch
            {
            }

            foreach (var offlinePath in Directory.GetDirectories(Path, "*-offline"))
            {
                var restoredPath = offlinePath[..^"-offline".Length];
                if (!Directory.Exists(restoredPath))
                {
                    Directory.Move(offlinePath, restoredPath);
                }
            }

            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
