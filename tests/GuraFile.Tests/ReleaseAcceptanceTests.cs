using GuraFile.Storage;
using System.Reflection;

namespace GuraFile.Tests;

[TestClass]
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
