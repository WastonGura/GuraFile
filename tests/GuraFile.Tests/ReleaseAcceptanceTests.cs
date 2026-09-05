using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using GuraFile.Storage;

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

        temp.Dispose();
        Assert.IsFalse(RecycleBinTestHelper.ExistsInRecycleBin(renamedFileName, Path.GetDirectoryName(renamedPath)), "Recycled file must be cleaned up from Recycle Bin after test disposal.");
    }

    [TestMethod]
    public async Task GraphPreview_ThreeHundredFilesSnapshotAndJsonSerialization_UnderOneSecond_AndEnforcesLimit()
    {
        using var temp = TempDirectory.Create();
        var dbPath = Path.Combine(temp.Path, "perf_graph.db");
        using (var conn = SqliteDatabase.Open(dbPath))
        using (var tx = conn.BeginTransaction())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\Root', 'C:\\Root');";
                cmd.ExecuteNonQuery();
            }

            for (var i = 1; i <= 300; i++)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind)
                    VALUES ($id, 1, 'vol', $fid, $path, $path, $name, '.txt', 100, '2026-09-01T00:00:00Z', 'stable');
                    """;
                cmd.Parameters.AddWithValue("$id", i);
                cmd.Parameters.AddWithValue("$fid", $"f-{i}");
                cmd.Parameters.AddWithValue("$path", $@"C:\Root\file_{i:D4}.txt");
                cmd.Parameters.AddWithValue("$name", $"file_{i:D4}.txt");
                cmd.ExecuteNonQuery();
            }

            for (var t = 1; t <= 10; t++)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO tags (id, name, normalized_name, source) VALUES ($id, $name, $norm, 'user');";
                cmd.Parameters.AddWithValue("$id", t);
                cmd.Parameters.AddWithValue("$name", $"Tag_{t}");
                cmd.Parameters.AddWithValue("$norm", $"TAG_{t}");
                cmd.ExecuteNonQuery();
            }

            for (var i = 1; i <= 300; i++)
            {
                var tagId = ((i - 1) % 10) + 1;
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO file_tags (file_id, tag_id, source) VALUES ($fid, $tid, 'user');";
                cmd.Parameters.AddWithValue("$fid", i);
                cmd.Parameters.AddWithValue("$tid", tagId);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        var queryService = new FileQueryService(dbPath);
        var files = await queryService.QueryAsync(new());
        Assert.HasCount(300, files);

        var snapshotService = new GraphSnapshotService(dbPath);

        // Measure snapshot generation and serialization duration
        var sw = Stopwatch.StartNew();
        var snapshot = await snapshotService.CreateAsync(files);
        var json = GraphMessageSerializer.SerializeRenderSnapshot(snapshot);
        sw.Stop();

        Assert.AreEqual(GraphSnapshotStatus.Ready, snapshot.Status);
        Assert.AreEqual(300, snapshot.FileCount);
        Assert.HasCount(300, snapshot.FileNodes);
        Assert.HasCount(10, snapshot.TagNodes);
        Assert.HasCount(300, snapshot.Edges);
        Assert.IsNotNull(json);

        // This measures snapshot generation and JSON serialization only. Real rendering is covered by GraphFirstFrame.ps1.
        Console.WriteLine($"[Graph Acceptance Benchmark] 300 files snapshot + JSON serialization: {sw.ElapsedMilliseconds} ms (target: < 1000 ms)");
        Assert.IsLessThan(1000, sw.ElapsedMilliseconds, $"300 file snapshot generation exceeded 1000ms: {sw.ElapsedMilliseconds}ms");

        // Limit verification: 301 files returns FileLimitExceeded without truncation
        var extraFile = new IndexedFile(301, "file_0301.txt", @"C:\Root\file_0301.txt", ".txt", 100, DateTimeOffset.UtcNow, true, null);
        var files301 = files.Concat([extraFile]).ToArray();
        var exceededSnapshot = await snapshotService.CreateAsync(files301);

        Assert.AreEqual(GraphSnapshotStatus.FileLimitExceeded, exceededSnapshot.Status);
        Assert.AreEqual(301, exceededSnapshot.FileCount);
        Assert.IsEmpty(exceededSnapshot.FileNodes);
        Assert.IsEmpty(exceededSnapshot.TagNodes);
        Assert.IsEmpty(exceededSnapshot.Edges);

        var viewState = GraphViewState.FromSnapshot(exceededSnapshot);
        Assert.AreEqual(GraphViewDisplayMode.LimitExceeded, viewState.Mode);
        StringAssert.Contains(viewState.Message, "300");
    }

    [TestMethod]
    public async Task GraphPreview_RapidFilterSwitching_EliminatesStaleWrites_AndKeepsListAndGraphSynchronized()
    {
        using var temp = TempDirectory.Create();
        var dbPath = Path.Combine(temp.Path, "rapid_filter.db");
        using (var conn = SqliteDatabase.Open(dbPath))
        using (var tx = conn.BeginTransaction())
        {
            using var cmdRoot = conn.CreateCommand();
            cmdRoot.Transaction = tx;
            cmdRoot.CommandText = "INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\Root', 'C:\\Root');";
            cmdRoot.ExecuteNonQuery();

            for (var i = 1; i <= 10; i++)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind)
                    VALUES ($id, 1, 'vol', $fid, $path, $path, $name, '.txt', 100, '2026-09-01T00:00:00Z', 'stable');
                    """;
                cmd.Parameters.AddWithValue("$id", i);
                cmd.Parameters.AddWithValue("$fid", $"f-{i}");
                var category = i <= 3 ? "Alpha" : (i <= 6 ? "Beta" : "Gamma");
                cmd.Parameters.AddWithValue("$path", $@"C:\Root\{category}_{i}.txt");
                cmd.Parameters.AddWithValue("$name", $"{category}_{i}.txt");
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        var queryService = new FileQueryService(dbPath);
        var snapshotService = new GraphSnapshotService(dbPath);
        var coordinator = new GraphInteractionCoordinator();

        // 1. User rapidly triggers 3 searches: "Alpha", "Beta", "Gamma"
        var gen1 = coordinator.BeginQuery();
        var gen2 = coordinator.BeginQuery();
        var gen3 = coordinator.BeginQuery();

        Assert.IsTrue(gen3 > gen2 && gen2 > gen1);

        // 2. Query 1 finishes out of order (stale)
        var alphaFiles = await queryService.QueryAsync(new(Search: "Alpha"));
        Assert.IsFalse(coordinator.CanCommitQuery(gen1), "Query generation 1 must not be eligible to commit.");
        Assert.IsFalse(coordinator.CommitQuery(gen1, alphaFiles), "Stale query 1 commit must be rejected.");

        // 3. Query 2 finishes out of order (stale)
        var betaFiles = await queryService.QueryAsync(new(Search: "Beta"));
        Assert.IsFalse(coordinator.CanCommitQuery(gen2));
        Assert.IsFalse(coordinator.CommitQuery(gen2, betaFiles));

        // 4. Query 3 finishes (latest)
        var gammaFiles = await queryService.QueryAsync(new(Search: "Gamma"));
        Assert.IsTrue(coordinator.CanCommitQuery(gen3));
        Assert.IsTrue(coordinator.CommitQuery(gen3, gammaFiles));

        // Verify coordinator's CurrentFiles strictly contains Gamma files (4 files: 7, 8, 9, 10)
        Assert.HasCount(4, coordinator.CurrentFiles);
        CollectionAssert.AreEquivalent(
            gammaFiles.Select(f => f.Id).ToArray(),
            coordinator.CurrentFiles.Select(f => f.Id).ToArray());

        // 5. Generate graph snapshot for Gamma files
        var sGen = coordinator.BeginGraphRefresh();
        var gammaSnapshot = await snapshotService.CreateAsync(coordinator.CurrentFiles);
        Assert.IsTrue(coordinator.CommitSnapshot(sGen, gammaSnapshot));

        // List and Graph snapshot collections are 100% aligned
        CollectionAssert.AreEquivalent(
            coordinator.CurrentFiles.Select(f => $"file:{f.Id}").ToArray(),
            coordinator.CurrentSnapshot!.FileNodes.Select(n => n.Id).ToArray());

        // 6. Stale selection events arriving from earlier queries are strictly rejected
        var staleAlphaSelection = coordinator.EvaluateSelection(
            new GraphNodeActionPayload("file:1", "file", 1, null, "Alpha_1.txt"));
        Assert.AreEqual(GraphSelectionKind.Unknown, staleAlphaSelection.Kind);

        var staleBatchFromGen1 = coordinator.EvaluateBatchSelection([1, 2, 3], expectedGeneration: gen1);
        Assert.IsEmpty(staleBatchFromGen1);

        // Valid selection for current Gamma file succeeds
        var validGammaSelection = coordinator.EvaluateSelection(
            new GraphNodeActionPayload($"file:{gammaFiles[0].Id}", "file", gammaFiles[0].Id, null, gammaFiles[0].Name));
        Assert.AreEqual(GraphSelectionKind.File, validGammaSelection.Kind);
        Assert.AreEqual(gammaFiles[0].Id, validGammaSelection.File!.Id);
    }

    [TestMethod]
    public async Task GraphPreview_OneThousandFilesSharingSameTag_NeverProducesFileToFileEdges()
    {
        using var temp = TempDirectory.Create();
        var dbPath = Path.Combine(temp.Path, "bipartite_1000.db");
        using (var conn = SqliteDatabase.Open(dbPath))
        using (var tx = conn.BeginTransaction())
        {
            using var cmdRoot = conn.CreateCommand();
            cmdRoot.Transaction = tx;
            cmdRoot.CommandText = "INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\Root', 'C:\\Root');";
            cmdRoot.ExecuteNonQuery();

            // Insert single shared tag
            using var cmdTag = conn.CreateCommand();
            cmdTag.Transaction = tx;
            cmdTag.CommandText = "INSERT INTO tags (id, name, normalized_name, source) VALUES (42, 'UniversalShared', 'UNIVERSALSHARED', 'user');";
            cmdTag.ExecuteNonQuery();

            // Insert 1000 files all linked to tag 42
            for (var i = 1; i <= 1000; i++)
            {
                using var cmdFile = conn.CreateCommand();
                cmdFile.Transaction = tx;
                cmdFile.CommandText = """
                    INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind)
                    VALUES ($id, 1, 'vol', $fid, $path, $path, $name, '.txt', 100, '2026-09-01T00:00:00Z', 'stable');
                    """;
                cmdFile.Parameters.AddWithValue("$id", i);
                cmdFile.Parameters.AddWithValue("$fid", $"file-{i}");
                cmdFile.Parameters.AddWithValue("$path", $@"C:\Root\f_{i:D4}.txt");
                cmdFile.Parameters.AddWithValue("$name", $"f_{i:D4}.txt");
                cmdFile.ExecuteNonQuery();

                using var cmdRel = conn.CreateCommand();
                cmdRel.Transaction = tx;
                cmdRel.CommandText = "INSERT INTO file_tags (file_id, tag_id, source) VALUES ($fid, 42, 'user');";
                cmdRel.Parameters.AddWithValue("$fid", i);
                cmdRel.ExecuteNonQuery();
            }

            tx.Commit();
        }

        var tagService = new TagService(dbPath);
        var allFileIds = Enumerable.Range(1, 1000).Select(i => (long)i).ToArray();
        var relations = tagService.ListTagRelationsForFiles(allFileIds, CancellationToken.None);

        // Exactly 1000 relations (one per file-tag link), NOT pairwise file-file (which would be 499,500)
        Assert.HasCount(1000, relations);
        Assert.IsTrue(relations.All(r => r.TagId == 42));

        var snapshotService = new GraphSnapshotService(dbPath);
        var queryService = new FileQueryService(dbPath);
        var allFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1000, allFiles);

        // 1000 files exceeds 300 limit: protects graph by returning FileLimitExceeded with 0 edges
        var exceededSnapshot = await snapshotService.CreateAsync(allFiles);
        Assert.AreEqual(GraphSnapshotStatus.FileLimitExceeded, exceededSnapshot.Status);
        Assert.IsEmpty(exceededSnapshot.Edges);

        // Subset of 300 files: snapshot creates strictly file->tag edges and ZERO file->file edges
        var subsetFiles = allFiles.Take(300).ToArray();
        var subsetSnapshot = await snapshotService.CreateAsync(subsetFiles);
        Assert.AreEqual(GraphSnapshotStatus.Ready, subsetSnapshot.Status);
        Assert.HasCount(300, subsetSnapshot.Edges);
        Assert.IsTrue(subsetSnapshot.Edges.All(e => e.SourceId.StartsWith("file:", StringComparison.Ordinal) && e.TargetId == "tag:42"));
        Assert.IsFalse(subsetSnapshot.Edges.Any(e => e.SourceId.StartsWith("file:") && e.TargetId.StartsWith("file:")));
        Assert.IsFalse(subsetSnapshot.Edges.Any(e => e.SourceId.StartsWith("tag:") && e.TargetId.StartsWith("tag:")));
    }

    [TestMethod]
    public async Task GraphPreview_HostileStrings_PreservedSafelyAsPlainTextWithoutExecution()
    {
        using var temp = TempDirectory.Create();
        var dbPath = Path.Combine(temp.Path, "hostile_graph.db");

        const string hostileFileName = "</script><script>alert('xss-file')</script>\r\n\"escaped'\\<b>bold</b>";
        const string hostileTagName = "\"><img src=x onerror=alert(1)>' OR '1'='1\n<svg onload=alert(2)>";

        using (var conn = SqliteDatabase.Open(dbPath))
        using (var tx = conn.BeginTransaction())
        {
            using var cmdRoot = conn.CreateCommand();
            cmdRoot.Transaction = tx;
            cmdRoot.CommandText = "INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\Root', 'C:\\Root');";
            cmdRoot.ExecuteNonQuery();

            using var cmdFile = conn.CreateCommand();
            cmdFile.Transaction = tx;
            cmdFile.CommandText = """
                INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind)
                VALUES (1, 1, 'vol', 'f-1', 'C:\\Root\\hostile.txt', 'C:\\Root\\hostile.txt', $name, '.txt', 100, '2026-09-01T00:00:00Z', 'stable');
                """;
            cmdFile.Parameters.AddWithValue("$name", hostileFileName);
            cmdFile.ExecuteNonQuery();

            using var cmdTag = conn.CreateCommand();
            cmdTag.Transaction = tx;
            cmdTag.CommandText = "INSERT INTO tags (id, name, normalized_name, source) VALUES (1, $name, 'HOSTILE', 'user');";
            cmdTag.Parameters.AddWithValue("$name", hostileTagName);
            cmdTag.ExecuteNonQuery();

            using var cmdRel = conn.CreateCommand();
            cmdRel.Transaction = tx;
            cmdRel.CommandText = "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 1, 'user');";
            cmdRel.ExecuteNonQuery();

            tx.Commit();
        }

        var queryService = new FileQueryService(dbPath);
        var files = await queryService.QueryAsync(new());
        Assert.HasCount(1, files);

        var snapshotService = new GraphSnapshotService(dbPath);
        var snapshot = await snapshotService.CreateAsync(files);

        Assert.AreEqual(GraphSnapshotStatus.Ready, snapshot.Status);
        Assert.AreEqual(hostileFileName, snapshot.FileNodes.Single().Label);
        Assert.AreEqual(hostileTagName, snapshot.TagNodes.Single().Label);

        // Serialize snapshot to JSON
        var json = GraphMessageSerializer.SerializeRenderSnapshot(snapshot);
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            var fileLabel = root.GetProperty("payload").GetProperty("files")[0].GetProperty("label").GetString();
            var tagLabel = root.GetProperty("payload").GetProperty("tags")[0].GetProperty("label").GetString();

            Assert.AreEqual(hostileFileName, fileLabel);
            Assert.AreEqual(hostileTagName, tagLabel);
        }

        // Inbound message handling with hostile payload
        var inboundJson = $$"""
            {
                "type": "nodeSelected",
                "version": "1.0",
                "payload": {
                    "nodeId": "file:1",
                    "kind": "file",
                    "fileId": 1,
                    "tagId": null,
                    "label": {{JsonSerializer.Serialize(hostileFileName)}}
                }
            }
            """;
        var inboundMsg = GraphMessageSerializer.Deserialize(inboundJson);
        var nodeAction = GraphMessageSerializer.ParseNodeAction(inboundMsg);
        Assert.AreEqual(hostileFileName, nodeAction.Label);

        var coordinator = new GraphInteractionCoordinator();
        var qGen = coordinator.BeginQuery();
        coordinator.CommitQuery(qGen, files);
        var selected = coordinator.EvaluateSelection(nodeAction);
        Assert.AreEqual(GraphSelectionKind.File, selected.Kind);
        Assert.AreEqual(hostileFileName, selected.File!.Name);
    }

    [TestMethod]
    public void GraphPreview_OfflineEnvironment_LocalResourcesStrictCspAndInterceptionRulesVerified()
    {
        var root = RepositoryRoot();
        var graphDir = Path.Combine(root, "src", "GuraFile", "Assets", "graph");
        Assert.IsTrue(Directory.Exists(graphDir), $"Graph directory missing: {graphDir}");

        var cytoscapeJs = Path.Combine(graphDir, "cytoscape.min.js");
        var indexHtml = Path.Combine(graphDir, "index.html");
        var graphCss = Path.Combine(graphDir, "graph.css");
        var graphJs = Path.Combine(graphDir, "graph.js");

        foreach (var file in new[] { cytoscapeJs, indexHtml, graphCss, graphJs })
        {
            Assert.IsTrue(File.Exists(file), $"Required asset missing: {file}");
            Assert.IsGreaterThan(0, new FileInfo(file).Length, $"Asset is empty: {file}");
        }

        // Verify index.html contains strict CSP
        var htmlContent = File.ReadAllText(indexHtml);
        const string expectedCsp = "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'none'; frame-src 'none'; object-src 'none';";
        StringAssert.Contains(htmlContent, expectedCsp);

        // Verify none of the assets contain remote network references (http/https)
        foreach (var file in new[] { indexHtml, graphCss, graphJs })
        {
            var content = File.ReadAllText(file);
            var match = Regex.Match(content, @"(?:src|href|url)\s*[:=]\s*[""']?https?://", RegexOptions.IgnoreCase);
            Assert.IsFalse(match.Success, $"File {Path.GetFileName(file)} contains remote URL reference: {match.Value}");
        }

        // Verify GraphSecurityPolicy rules
        string hostName = GraphSecurityPolicy.VirtualHostName;
        string entryUrl = GraphSecurityPolicy.EntryUrl;
        Assert.AreEqual("graph.gurafile.local", hostName);
        Assert.AreEqual("https://graph.gurafile.local/index.html", entryUrl);

        // Allowed local virtual host endpoints
        Assert.IsTrue(GraphSecurityPolicy.IsAllowedUri("https://graph.gurafile.local/index.html"));
        Assert.IsTrue(GraphSecurityPolicy.IsAllowedUri("https://graph.gurafile.local/Assets/graph/graph.js"));

        // Strictly blocked remote, schemes, subdomains, file paths
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("https://www.google.com"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("http://malicious.org/script.js"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("https://graph.gurafile.local.evil.com"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("https://sub.graph.gurafile.local"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("file:///C:/Windows/System32/cmd.exe"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("javascript:alert(1)"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("data:text/html,<b>xss</b>"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("about:blank"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri((string?)null));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri(""));
    }

    [TestMethod]
    public async Task GraphPreview_BatchTagging_SingleTransactionRollbackOnFailure_AndSynchronizesBothViewsOnSuccess()
    {
        using var temp = TempDirectory.Create();
        var dbPath = Path.Combine(temp.Path, "batch_tag_e2e.db");
        using (var conn = SqliteDatabase.Open(dbPath))
        using (var tx = conn.BeginTransaction())
        {
            using var cmdRoot = conn.CreateCommand();
            cmdRoot.Transaction = tx;
            cmdRoot.CommandText = "INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\Root', 'C:\\Root');";
            cmdRoot.ExecuteNonQuery();

            for (var i = 1; i <= 3; i++)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind)
                    VALUES ($id, 1, 'vol', $fid, $path, $path, $name, '.txt', 100, '2026-09-01T00:00:00Z', 'stable');
                    """;
                cmd.Parameters.AddWithValue("$id", i);
                cmd.Parameters.AddWithValue("$fid", $"f-{i}");
                cmd.Parameters.AddWithValue("$path", $@"C:\Root\file_{i}.txt");
                cmd.Parameters.AddWithValue("$name", $"file_{i}.txt");
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        var tagService = new TagService(dbPath);
        var queryService = new FileQueryService(dbPath);
        var snapshotService = new GraphSnapshotService(dbPath);
        var coordinator = new GraphInteractionCoordinator();

        var initialFiles = await queryService.QueryAsync(new());
        var qGen = coordinator.BeginQuery();
        coordinator.CommitQuery(qGen, initialFiles);

        // 1. Success path: user creates user tag and tags files 1 & 2
        var userTag = tagService.CreateTag("Release_Verified");
        tagService.AddTagToFiles(userTag.Id, [1L, 2L]);

        // Verify common user tags for selection
        var commonTags = tagService.ListCommonUserTagsForFiles([1L, 2L]);
        Assert.HasCount(1, commonTags);
        Assert.AreEqual("Release_Verified", commonTags[0].Name);

        // Update graph snapshot and sync views
        var sGen = coordinator.BeginGraphRefresh();
        var snapshot = await snapshotService.CreateAsync(coordinator.CurrentFiles);
        coordinator.CommitSnapshot(sGen, snapshot);

        Assert.AreEqual(GraphSnapshotStatus.Ready, coordinator.CurrentSnapshot!.Status);
        Assert.HasCount(1, coordinator.CurrentSnapshot.TagNodes);
        Assert.AreEqual("tag:" + userTag.Id, coordinator.CurrentSnapshot.TagNodes[0].Id);
        Assert.HasCount(2, coordinator.CurrentSnapshot.Edges);

        // 2. Failure path: single transaction all-or-nothing rollback
        var failTag = tagService.CreateTag("Will_Fail");
        // Attempting to batch add with invalid file ID 999
        Assert.Throws<ArgumentException>(() => tagService.AddTagToFiles(failTag.Id, [1L, 999L]));

        // Verify file 1 did NOT get failTag (complete rollback)
        var file1Tags = tagService.ListTagsForFile(1L);
        Assert.IsFalse(file1Tags.Any(t => t.Name == "Will_Fail"), "Transaction rollback failed: file 1 was partially tagged.");

        // Attempting to batch tag with automatic tag (strictly rejected)
        using (var conn = SqliteDatabase.Open(dbPath))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO tags (id, name, normalized_name, source) VALUES (99, '类型/文档', 'TYPE_DOC', 'automatic');";
            cmd.ExecuteNonQuery();
        }

        var autoTagEx = Assert.Throws<ArgumentException>(() => tagService.AddTagToFiles(99, [1L, 2L]));
        StringAssert.Contains(autoTagEx.Message, "用户标签");

        // Verify state is clean and undisturbed
        var file1TagsAfter = tagService.ListTagsForFile(1L);
        Assert.HasCount(1, file1TagsAfter);
        Assert.AreEqual("Release_Verified", file1TagsAfter[0].Name);
    }

    private static string RepositoryRoot() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

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
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (scanner.ListRoots().Single().Status == status)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Managed root did not become {status} within five seconds.");
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
            if (!Directory.Exists(Path))
            {
                return;
            }

            try
            {
                RecycleBinTestHelper.CleanupRecycleBinItemsForDirectory(Path);
            }
            catch
            {
            }

            if (Directory.Exists(Path))
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
}
