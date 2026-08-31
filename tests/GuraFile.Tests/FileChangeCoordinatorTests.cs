using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class FileChangeCoordinatorTests
{
    [TestMethod]
    public async Task TargetedReconciliationPreservesIdentityAndTagsAcrossMoves()
    {
        using var temp = TempDirectory.Create();
        var oldDirectory = Directory.CreateDirectory(Path.Combine(temp.RootPath, "old")).FullName;
        var originalPath = Path.Combine(oldDirectory, "notes.txt");
        await File.WriteAllTextAsync(originalPath, "notes");
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var root = scanner.AddRoot(temp.RootPath);
        await scanner.ScanAsync(root.Id);
        var original = (await new FileQueryService(temp.DatabasePath).QueryAsync(new())).Single();
        var tags = new TagService(temp.DatabasePath);
        var userTag = tags.CreateTag("Keep");
        tags.AddTagToFiles(userTag.Id, [original.Id]);
        var renamedPath = Path.Combine(oldDirectory, "notes.md");
        File.Move(originalPath, renamedPath);

        await scanner.ReconcilePathsAsync(root.Id, [originalPath, renamedPath]);

        var renamed = (await new FileQueryService(temp.DatabasePath).QueryAsync(new())).Single(file => file.IsOnline);
        Assert.AreEqual(original.Id, renamed.Id);
        Assert.AreEqual(renamedPath, renamed.Path);
        Assert.AreEqual("Keep", tags.ListTagsForFile(renamed.Id).Single().Name);
        CollectionAssert.Contains(
            tags.ListAutomaticTagsForFile(renamed.Id).Select(tag => tag.Name).ToArray(),
            "格式/Markdown");

        var newDirectory = Path.Combine(temp.RootPath, "new");
        Directory.Move(oldDirectory, newDirectory);
        var movedPath = Path.Combine(newDirectory, "notes.md");

        await scanner.ReconcilePathsAsync(root.Id, [oldDirectory, newDirectory]);

        var moved = (await new FileQueryService(temp.DatabasePath).QueryAsync(new())).Single(file => file.IsOnline);
        Assert.AreEqual(original.Id, moved.Id);
        Assert.AreEqual(movedPath, moved.Path);
        Assert.AreEqual("Keep", tags.ListTagsForFile(moved.Id).Single().Name);

        File.Delete(movedPath);
        await scanner.ReconcilePathsAsync(root.Id, [movedPath]);

        Assert.IsFalse((await new FileQueryService(temp.DatabasePath).QueryAsync(new())).Single(file => file.Id == original.Id).IsOnline);
        Assert.AreEqual("Keep", tags.ListTagsForFile(original.Id).Single().Name);
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => scanner.ReconcilePathsAsync(root.Id, [temp.Path]));
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => scanner.ReconcilePathsAsync(root.Id, [temp.DatabasePath]));
    }

    [TestMethod]
    public async Task TargetedReconciliationHandlesFileDirectoryReplacement()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.RootPath, "swap");
        await File.WriteAllTextAsync(path, "file");
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var root = scanner.AddRoot(temp.RootPath);
        await scanner.ScanAsync(root.Id);
        var original = (await new FileQueryService(temp.DatabasePath).QueryAsync(new())).Single();
        File.Delete(path);
        Directory.CreateDirectory(path);
        var childPath = Path.Combine(path, "child.txt");
        await File.WriteAllTextAsync(childPath, "child");

        await scanner.ReconcilePathsAsync(root.Id, [path]);

        var files = await new FileQueryService(temp.DatabasePath).QueryAsync(new());
        Assert.IsFalse(files.Single(file => file.Id == original.Id).IsOnline);
        Assert.IsTrue(files.Single(file => file.Path == childPath).IsOnline);
        File.Delete(childPath);
        Directory.Delete(path);
        await File.WriteAllTextAsync(path, "replacement");

        await scanner.ReconcilePathsAsync(root.Id, [path]);

        files = await new FileQueryService(temp.DatabasePath).QueryAsync(new());
        Assert.IsTrue(files.Single(file => file.Path == path && file.IsOnline).IsOnline);
        Assert.IsFalse(files.Single(file => file.Path == childPath).IsOnline);
    }

    [TestMethod]
    public async Task PartialTargetedEnumerationDoesNotMarkUnvisitedFilesMissing()
    {
        using var temp = TempDirectory.Create();
        var blocked = Directory.CreateDirectory(Path.Combine(temp.RootPath, "blocked")).FullName;
        var filePath = Path.Combine(blocked, "keep.txt");
        await File.WriteAllTextAsync(filePath, "keep");
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var root = scanner.AddRoot(temp.RootPath);
        await scanner.ScanAsync(root.Id);
        var failing = new ManagedRootScanner(
            temp.DatabasePath,
            FileIdentityReader.Read,
            path => string.Equals(path, blocked, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("blocked")
                : Directory.GetFileSystemEntries(path));

        var result = await failing.ReconcilePathsAsync(root.Id, [blocked]);

        Assert.HasCount(1, result.Failures);
        Assert.IsTrue((await new FileQueryService(temp.DatabasePath).QueryAsync(new())).Single(file => file.Path == filePath).IsOnline);
    }

    [TestMethod]
    public async Task TargetedEnumerationRechecksReparsePointBeforeTraversal()
    {
        using var temp = TempDirectory.Create();
        var directory = Directory.CreateDirectory(Path.Combine(temp.RootPath, "changing")).FullName;
        var filePath = Path.Combine(directory, "old.txt");
        await File.WriteAllTextAsync(filePath, "old");
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var root = scanner.AddRoot(temp.RootPath);
        await scanner.ScanAsync(root.Id);
        var checks = 0;
        var guarded = new ManagedRootScanner(
            temp.DatabasePath,
            FileIdentityReader.Read,
            _ => throw new AssertFailedException("Reparse directory was enumerated."),
            path => string.Equals(path, directory, StringComparison.OrdinalIgnoreCase) && ++checks > 1
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        await guarded.ReconcilePathsAsync(root.Id, [directory]);

        Assert.IsFalse((await new FileQueryService(temp.DatabasePath).QueryAsync(new())).Single(file => file.Path == filePath).IsOnline);
    }

    [TestMethod]
    public async Task FullScanRechecksReparsePointBeforeTraversal()
    {
        using var temp = TempDirectory.Create();
        var directory = Directory.CreateDirectory(Path.Combine(temp.RootPath, "changing-full")).FullName;
        var filePath = Path.Combine(directory, "old.txt");
        await File.WriteAllTextAsync(filePath, "old");
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var root = scanner.AddRoot(temp.RootPath);
        await scanner.ScanAsync(root.Id);
        var checks = 0;
        var guarded = new ManagedRootScanner(
            temp.DatabasePath,
            FileIdentityReader.Read,
            path => string.Equals(path, directory, StringComparison.OrdinalIgnoreCase)
                ? throw new AssertFailedException("Reparse directory was enumerated.")
                : Directory.GetFileSystemEntries(path),
            path => string.Equals(path, directory, StringComparison.OrdinalIgnoreCase) && ++checks > 1
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        await guarded.ScanAsync(root.Id);

        Assert.IsFalse((await new FileQueryService(temp.DatabasePath).QueryAsync(new())).Single(file => file.Path == filePath).IsOnline);
    }

    [TestMethod]
    public async Task WatcherReflectsFileChangesWithinTwoSecondsAndStopsAfterDispose()
    {
        using var temp = TempDirectory.Create();
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var root = scanner.AddRoot(temp.RootPath);
        var query = new FileQueryService(temp.DatabasePath);
        var coordinator = new FileChangeCoordinator(scanner, debounce: TimeSpan.FromMilliseconds(75));
        Assert.IsTrue(coordinator.Watch(root));
        var originalPath = Path.Combine(temp.RootPath, "live.txt");
        await File.WriteAllTextAsync(originalPath, "one");
        var original = await WaitForFileAsync(query, file => file.Path == originalPath && file.IsOnline);
        var tags = new TagService(temp.DatabasePath);
        var userTag = tags.CreateTag("Live");
        tags.AddTagToFiles(userTag.Id, [original.Id]);
        var renamedPath = Path.Combine(temp.RootPath, "live.md");
        File.Move(originalPath, renamedPath);
        var renamed = await WaitForFileAsync(query, file => file.Id == original.Id && file.Path == renamedPath && file.IsOnline);
        Assert.AreEqual("Live", tags.ListTagsForFile(renamed.Id).Single().Name);
        CollectionAssert.Contains(
            tags.ListAutomaticTagsForFile(renamed.Id).Select(tag => tag.Name).ToArray(),
            "格式/Markdown");
        await File.AppendAllTextAsync(renamedPath, "-changed");
        await WaitForFileAsync(query, file => file.Id == original.Id && file.Size > renamed.Size);
        File.Delete(renamedPath);
        await WaitForFileAsync(query, file => file.Id == original.Id && !file.IsOnline);

        await coordinator.DisposeAsync();
        var ignoredPath = Path.Combine(temp.RootPath, "after-dispose.txt");
        await File.WriteAllTextAsync(ignoredPath, "ignored");
        await Task.Delay(300);

        Assert.IsFalse((await query.QueryAsync(new())).Any(file => file.Path == ignoredPath));
    }

    [TestMethod]
    public async Task WatcherPreservesIdentityAcrossManagedRoots()
    {
        using var temp = TempDirectory.Create();
        var secondRootPath = Directory.CreateDirectory(Path.Combine(temp.Path, "second-root")).FullName;
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var firstRoot = scanner.AddRoot(temp.RootPath);
        var secondRoot = scanner.AddRoot(secondRootPath);
        var originalPath = Path.Combine(temp.RootPath, "move.txt");
        await File.WriteAllTextAsync(originalPath, "move");
        await scanner.ScanAsync(firstRoot.Id);
        var query = new FileQueryService(temp.DatabasePath);
        var original = (await query.QueryAsync(new())).Single(file => file.IsOnline);
        var tags = new TagService(temp.DatabasePath);
        var tag = tags.CreateTag("Across roots");
        tags.AddTagToFiles(tag.Id, [original.Id]);
        await using var coordinator = new FileChangeCoordinator(scanner, debounce: TimeSpan.FromMilliseconds(75));
        coordinator.Watch(firstRoot);
        coordinator.Watch(secondRoot);
        var movedPath = Path.Combine(secondRootPath, "move.txt");

        File.Move(originalPath, movedPath);

        var moved = await WaitForFileAsync(query, file => file.Id == original.Id && file.Path == movedPath && file.IsOnline);
        Assert.AreEqual("Across roots", tags.ListTagsForFile(moved.Id).Single().Name);
    }

    [TestMethod]
    public async Task DatabaseFilesInsideRootDoNotTriggerReconciliationLoop()
    {
        using var temp = TempDirectory.Create(databaseInsideRoot: true);
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var root = scanner.AddRoot(temp.RootPath);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var coordinator = new FileChangeCoordinator(
            scanner,
            _ =>
            {
                Interlocked.Increment(ref calls);
                changed.TrySetResult();
            },
            debounce: TimeSpan.FromMilliseconds(50));
        coordinator.Watch(root);

        await File.WriteAllTextAsync(Path.Combine(temp.RootPath, "visible.txt"), "visible");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(400);

        Assert.AreEqual(1, calls);
        Assert.IsFalse((await new FileQueryService(temp.DatabasePath).QueryAsync(new()))
            .Any(file => Path.GetFileName(file.Path).StartsWith("index.db", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task DuplicateNotificationsAreCoalesced()
    {
        var completed = new TaskCompletionSource<IReadOnlyCollection<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var coordinator = new FileChangeCoordinator(
            (rootId, paths, _) =>
            {
                Interlocked.Increment(ref calls);
                completed.TrySetResult(paths);
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(30));

        for (var index = 0; index < 10; index++)
        {
            Assert.IsTrue(coordinator.Notify(7, @"C:\root\same.txt"));
        }

        var paths = await completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(100);

        Assert.AreEqual(1, calls);
        CollectionAssert.AreEqual(new[] { @"C:\root\same.txt" }, paths.ToArray());
    }

    [TestMethod]
    public async Task BatchesAreProcessedSerially()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var active = 0;
        var maxActive = 0;
        await using var coordinator = new FileChangeCoordinator(
            async (_, _, _) =>
            {
                var current = Interlocked.Increment(ref active);
                maxActive = Math.Max(maxActive, current);
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }

                Interlocked.Decrement(ref active);
                if (call == 2)
                {
                    secondCompleted.TrySetResult();
                }
            },
            TimeSpan.FromMilliseconds(30));

        coordinator.Notify(1, @"C:\root\first.txt");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        coordinator.Notify(2, @"C:\root\second.txt");
        await Task.Delay(100);
        Assert.AreEqual(1, calls);
        releaseFirst.TrySetResult();
        await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, maxActive);
    }

    [TestMethod]
    public async Task UnwatchDropsQueuedChanges()
    {
        var calls = 0;
        await using var coordinator = new FileChangeCoordinator(
            (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(50));
        coordinator.Notify(9, @"C:\root\queued.txt");

        coordinator.Unwatch(9);
        await Task.Delay(150);

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task FailedWatchRecoveryKeepsRootDisabled()
    {
        using var temp = TempDirectory.Create();
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var root = scanner.AddRoot(temp.RootPath);
        var calls = 0;
        await using var coordinator = new FileChangeCoordinator(
            scanner,
            _ => Interlocked.Increment(ref calls),
            debounce: TimeSpan.FromMilliseconds(30));
        coordinator.Watch(root);
        coordinator.Unwatch(root.Id);
        coordinator.Notify(root.Id, Path.Combine(root.Path, "queued.txt"));
        Directory.Delete(root.Path);

        Assert.IsFalse(coordinator.Watch(root));
        await Task.Delay(100);

        Assert.AreEqual(0, calls);
    }

    private static async Task<IndexedFile> WaitForFileAsync(
        FileQueryService query,
        Func<IndexedFile, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        do
        {
            var file = (await query.QueryAsync(new())).FirstOrDefault(predicate);
            if (file is not null)
            {
                return file;
            }

            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Fail("File change was not indexed within two seconds.");
        throw new InvalidOperationException();
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path, bool databaseInsideRoot)
        {
            Path = path;
            RootPath = System.IO.Path.Combine(path, "root");
            Directory.CreateDirectory(RootPath);
            DatabasePath = System.IO.Path.Combine(databaseInsideRoot ? RootPath : path, "index.db");
        }

        public string Path { get; }
        public string RootPath { get; }
        public string DatabasePath { get; }

        public static TempDirectory Create(bool databaseInsideRoot = false)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"GuraFile.Watcher.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new(path, databaseInsideRoot);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
