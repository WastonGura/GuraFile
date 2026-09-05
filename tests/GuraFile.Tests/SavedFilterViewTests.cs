using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

[TestClass]
public sealed class SavedFilterViewTests
{
    private string _databasePath = null!;

    [TestInitialize]
    public void Setup()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"GuraFile.SavedFilterViewTests.{Guid.NewGuid():N}.db");
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _databasePath, $"{_databasePath}-shm", $"{_databasePath}-wal" })
        {
            if (File.Exists(file))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    [TestMethod]
    public void CreateView_ValidParameters_CreatesAndPersistsView()
    {
        var tagService = new TagService(_databasePath);
        var tag1 = tagService.CreateTag("文档");
        var tag2 = tagService.CreateTag("重要");

        var viewService = new SavedFilterViewService(_databasePath);
        var created = viewService.CreateView(
            name: "我的工作视图",
            searchText: "report",
            sortColumn: FileSortColumn.Modified,
            sortDescending: true,
            tagMatchMode: TagMatchMode.All,
            isTagFilterEnabled: true,
            tagIds: [tag1.Id, tag2.Id]);

        Assert.IsNotNull(created);
        Assert.IsGreaterThan(0L, created.Id);
        Assert.AreEqual("我的工作视图", created.Name);
        Assert.AreEqual("report", created.SearchText);
        Assert.AreEqual(FileSortColumn.Modified, created.SortColumn);
        Assert.IsTrue(created.SortDescending);
        Assert.AreEqual(TagMatchMode.All, created.TagMatchMode);
        Assert.IsTrue(created.IsTagFilterEnabled);
        Assert.HasCount(2, created.TagIds);
        CollectionAssert.AreEquivalent(new[] { tag1.Id, tag2.Id }, created.TagIds.ToArray());
        Assert.IsFalse(created.HasInvalidTags);
        Assert.AreEqual("我的工作视图", created.DisplayName);

        // Fetch back
        var fetched = viewService.GetViewById(created.Id);
        Assert.IsNotNull(fetched);
        Assert.AreEqual(created.Id, fetched.Id);
        Assert.AreEqual("我的工作视图", fetched.Name);
        Assert.AreEqual("report", fetched.SearchText);
        Assert.AreEqual(FileSortColumn.Modified, fetched.SortColumn);
        Assert.IsTrue(fetched.SortDescending);
        Assert.AreEqual(TagMatchMode.All, fetched.TagMatchMode);
        Assert.IsTrue(fetched.IsTagFilterEnabled);
        CollectionAssert.AreEquivalent(new[] { tag1.Id, tag2.Id }, fetched.TagIds.ToArray());
        Assert.IsFalse(fetched.HasInvalidTags);
    }

    [TestMethod]
    public void CreateView_NameValidation_EnforcesRules()
    {
        var viewService = new SavedFilterViewService(_databasePath);

        // Empty / whitespace
        Assert.ThrowsExactly<ArgumentException>(() => viewService.CreateView("", null, FileSortColumn.Name, false, TagMatchMode.Any, false));
        Assert.ThrowsExactly<ArgumentException>(() => viewService.CreateView("   ", null, FileSortColumn.Name, false, TagMatchMode.Any, false));
        Assert.ThrowsExactly<ArgumentNullException>(() => viewService.CreateView(null!, null, FileSortColumn.Name, false, TagMatchMode.Any, false));

        // > 100 characters
        var longName = new string('A', 101);
        Assert.ThrowsExactly<ArgumentException>(() => viewService.CreateView(longName, null, FileSortColumn.Name, false, TagMatchMode.Any, false));

        // Trims and normalizes Unicode
        var created = viewService.CreateView("  测试视图 1  ", null, FileSortColumn.Name, false, TagMatchMode.Any, false);
        Assert.AreEqual("测试视图 1", created.Name);

        // Unicode compatibility: FormKC compatibility equivalence (e.g. \u00A0 vs ' ')
        var unicodeView = viewService.CreateView("视图\u00A0A", null, FileSortColumn.Name, false, TagMatchMode.Any, false);
        Assert.AreEqual("视图\u00A0A", unicodeView.Name);
        // "视图 A" with regular space has same FormKC normalized name as "视图\u00A0A", so duplicate check triggers!
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            viewService.CreateView("视图 A", null, FileSortColumn.Name, false, TagMatchMode.Any, false));

        // Duplicate name case-insensitive
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            viewService.CreateView("测试视图 1", null, FileSortColumn.Name, false, TagMatchMode.Any, false));
        StringAssert.Contains(ex.Message, "已存在");

        // Duplicate name different casing
        var ex2 = Assert.ThrowsExactly<InvalidOperationException>(() =>
            viewService.CreateView("测试视图 1".ToUpperInvariant(), null, FileSortColumn.Name, false, TagMatchMode.Any, false));
        StringAssert.Contains(ex2.Message, "已存在");
    }

    [TestMethod]
    public void CreateView_NonexistentTag_ThrowsArgumentException()
    {
        var viewService = new SavedFilterViewService(_databasePath);
        Assert.ThrowsExactly<ArgumentException>(() =>
            viewService.CreateView("测试", null, FileSortColumn.Name, false, TagMatchMode.Any, true, [9999L]));
    }

    [TestMethod]
    public void ListViews_ReturnsOrderedViews()
    {
        var viewService = new SavedFilterViewService(_databasePath);
        var v1 = viewService.CreateView("视图 A", null, FileSortColumn.Name, false, TagMatchMode.Any, false);
        var v2 = viewService.CreateView("视图 B", null, FileSortColumn.Name, false, TagMatchMode.Any, false);
        var v3 = viewService.CreateView("视图 C", null, FileSortColumn.Name, false, TagMatchMode.Any, false);

        var list = viewService.ListViews();
        Assert.HasCount(3, list);
        Assert.AreEqual(v1.Id, list[0].Id);
        Assert.AreEqual(v2.Id, list[1].Id);
        Assert.AreEqual(v3.Id, list[2].Id);
    }

    [TestMethod]
    public void RenameView_UpdatesNameAndPreservesCriteria()
    {
        var tagService = new TagService(_databasePath);
        var tag = tagService.CreateTag("标签1");

        var viewService = new SavedFilterViewService(_databasePath);
        var v = viewService.CreateView("原始名称", "abc", FileSortColumn.Size, true, TagMatchMode.All, true, [tag.Id]);

        // Duplicate check
        viewService.CreateView("既有视图", null, FileSortColumn.Name, false, TagMatchMode.Any, false);
        Assert.ThrowsExactly<InvalidOperationException>(() => viewService.RenameView(v.Id, "既有视图"));

        // Nonexistent view
        Assert.ThrowsExactly<ArgumentException>(() => viewService.RenameView(99999L, "新名称"));

        // Valid rename
        var renamed = viewService.RenameView(v.Id, "新名称");
        Assert.AreEqual("新名称", renamed.Name);
        Assert.AreEqual("abc", renamed.SearchText);
        Assert.AreEqual(FileSortColumn.Size, renamed.SortColumn);
        Assert.IsTrue(renamed.SortDescending);
        CollectionAssert.AreEqual(new[] { tag.Id }, renamed.TagIds.ToArray());

        var fetched = viewService.GetViewById(v.Id);
        Assert.IsNotNull(fetched);
        Assert.AreEqual("新名称", fetched.Name);
    }

    [TestMethod]
    public void UpdateViewFilter_UpdatesCriteriaAndReplacesTags()
    {
        var tagService = new TagService(_databasePath);
        var t1 = tagService.CreateTag("T1");
        var t2 = tagService.CreateTag("T2");
        var t3 = tagService.CreateTag("T3");

        var viewService = new SavedFilterViewService(_databasePath);
        var v = viewService.CreateView("视图", "old", FileSortColumn.Name, false, TagMatchMode.Any, false, [t1.Id]);

        var updated = viewService.UpdateViewFilter(
            v.Id,
            searchText: "newSearch",
            sortColumn: FileSortColumn.Path,
            sortDescending: true,
            tagMatchMode: TagMatchMode.All,
            isTagFilterEnabled: true,
            tagIds: [t2.Id, t3.Id]);

        Assert.AreEqual("newSearch", updated.SearchText);
        Assert.AreEqual(FileSortColumn.Path, updated.SortColumn);
        Assert.IsTrue(updated.SortDescending);
        Assert.AreEqual(TagMatchMode.All, updated.TagMatchMode);
        Assert.IsTrue(updated.IsTagFilterEnabled);
        CollectionAssert.AreEquivalent(new[] { t2.Id, t3.Id }, updated.TagIds.ToArray());

        var fetched = viewService.GetViewById(v.Id);
        Assert.IsNotNull(fetched);
        Assert.AreEqual("newSearch", fetched.SearchText);
        CollectionAssert.AreEquivalent(new[] { t2.Id, t3.Id }, fetched.TagIds.ToArray());
    }

    [TestMethod]
    public void DeleteView_DeletesViewAndCascadesTags()
    {
        var tagService = new TagService(_databasePath);
        var t1 = tagService.CreateTag("TagD");

        var viewService = new SavedFilterViewService(_databasePath);
        var v = viewService.CreateView("删除视图", null, FileSortColumn.Name, false, TagMatchMode.Any, true, [t1.Id]);

        Assert.IsTrue(viewService.DeleteView(v.Id));
        Assert.IsNull(viewService.GetViewById(v.Id));
        Assert.IsEmpty(viewService.ListViews());

        // Verify saved_filter_view_tags table has no leftover rows
        using var conn = SqliteDatabase.Open(_databasePath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM saved_filter_view_tags WHERE view_id = $viewId;";
        cmd.Parameters.AddWithValue("$viewId", v.Id);
        Assert.AreEqual(0L, cmd.ExecuteScalar());

        // Deleting already deleted view returns false
        Assert.IsFalse(viewService.DeleteView(v.Id));
    }

    [TestMethod]
    public void ReorderViews_UpdatesSortOrderTransactionally()
    {
        var viewService = new SavedFilterViewService(_databasePath);
        var v1 = viewService.CreateView("V1", null, FileSortColumn.Name, false, TagMatchMode.Any, false);
        var v2 = viewService.CreateView("V2", null, FileSortColumn.Name, false, TagMatchMode.Any, false);
        var v3 = viewService.CreateView("V3", null, FileSortColumn.Name, false, TagMatchMode.Any, false);

        // Reorder to V3, V1, V2
        viewService.ReorderViews([v3.Id, v1.Id, v2.Id]);

        var list = viewService.ListViews();
        Assert.HasCount(3, list);
        Assert.AreEqual(v3.Id, list[0].Id);
        Assert.AreEqual(v1.Id, list[1].Id);
        Assert.AreEqual(v2.Id, list[2].Id);
    }

    [TestMethod]
    public void TagLifecycle_TagRenamed_ViewRemainsValid()
    {
        var tagService = new TagService(_databasePath);
        var tag = tagService.CreateTag("开发");

        var viewService = new SavedFilterViewService(_databasePath);
        var view = viewService.CreateView("开发视图", null, FileSortColumn.Name, false, TagMatchMode.Any, true, [tag.Id]);

        // Rename the tag
        tagService.RenameTag(tag.Id, "软件开发");

        // The view still references the stable tag.Id, so it is still fully valid!
        var fetched = viewService.GetViewById(view.Id);
        Assert.IsNotNull(fetched);
        Assert.IsFalse(fetched.HasInvalidTags);
        Assert.AreEqual("开发视图", fetched.DisplayName);
        Assert.HasCount(1, fetched.TagIds);
        Assert.AreEqual(tag.Id, fetched.TagIds[0]);
    }

    [TestMethod]
    public void TagLifecycle_TagDeleted_ViewMarkedInvalid_AndQueryDoesNotSilentlyExpand()
    {
        // Setup root, files and tags
        using (var conn = SqliteDatabase.Open(_databasePath))
        using (var tx = conn.BeginTransaction())
        {
            DatabaseMigrationFixtures.Execute(conn, "INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\Work', 'c:\\work');", tx);
            DatabaseMigrationFixtures.Execute(conn, "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc) " +
                "VALUES (1, 1, 'vol', 'f1', 'C:\\Work\\A.txt', 'c:\\work\\a.txt', 'A.txt', '.txt', 10, '2026-09-01T00:00:00Z');", tx);
            DatabaseMigrationFixtures.Execute(conn, "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc) " +
                "VALUES (2, 1, 'vol', 'f2', 'C:\\Work\\B.txt', 'c:\\work\\b.txt', 'B.txt', '.txt', 20, '2026-09-01T00:00:00Z');", tx);
            tx.Commit();
        }

        var tagService = new TagService(_databasePath);
        var t1 = tagService.CreateTag("标签A");
        var t2 = tagService.CreateTag("标签B");

        // File 1 has TagA and TagB; File 2 has only TagA
        tagService.AddTagToFiles(t1.Id, [1, 2]);
        tagService.AddTagToFiles(t2.Id, [1]);

        var viewService = new SavedFilterViewService(_databasePath);
        // Saved view requires both TagA and TagB
        var view = viewService.CreateView("组合视图", null, FileSortColumn.Name, false, TagMatchMode.All, true, [t1.Id, t2.Id]);

        var queryService = new FileQueryService(_databasePath);
        var initialQuery = viewService.ToFileQuery(view);
        var initialResults = queryService.QueryAsync(initialQuery).GetAwaiter().GetResult();
        // File 1 has both TagA and TagB, File 2 does not -> exactly 1 file matches
        Assert.HasCount(1, initialResults);
        Assert.AreEqual(1L, initialResults[0].Id);

        // Now delete TagB from tags
        tagService.DeleteTag(t2.Id);

        // Check view status
        var invalidView = viewService.GetViewById(view.Id);
        Assert.IsNotNull(invalidView);
        Assert.IsTrue(invalidView.HasInvalidTags, "View must be identified as HasInvalidTags when associated tag is deleted.");
        Assert.AreEqual("组合视图 [失效]", invalidView.DisplayName);
        Assert.IsNotNull(invalidView.MissingTagIds);
        CollectionAssert.AreEqual(new[] { t2.Id }, invalidView.MissingTagIds.ToArray());

        // Verify ListViews also reports HasInvalidTags
        var list = viewService.ListViews();
        Assert.IsTrue(list.Single(v => v.Id == view.Id).HasInvalidTags);

        // Critical safety check: When applying an invalid view's query,
        // it must NOT silently drop the deleted tag (which would cause File 2 to match TagA alone and expand results to 2 files!).
        // Instead, the query must yield 0 matches (no match)!
        var safeQuery = viewService.ToFileQuery(invalidView);
        var safeResults = queryService.QueryAsync(safeQuery).GetAwaiter().GetResult();
        Assert.IsEmpty(safeResults, "Applying an invalid view must never silently drop missing tags and widen results.");

        // User can repair the view by updating it with current valid tags
        var repaired = viewService.UpdateViewFilter(
            view.Id,
            searchText: null,
            sortColumn: FileSortColumn.Name,
            sortDescending: false,
            tagMatchMode: TagMatchMode.Any,
            isTagFilterEnabled: true,
            tagIds: [t1.Id]);

        Assert.IsFalse(repaired.HasInvalidTags);
        Assert.AreEqual("组合视图", repaired.DisplayName);
        var repairedQuery = viewService.ToFileQuery(repaired);
        var repairedResults = queryService.QueryAsync(repairedQuery).GetAwaiter().GetResult();
        // Now files with TagA match (2 files) because user explicitly updated the criteria
        Assert.HasCount(2, repairedResults);
    }

    [TestMethod]
    public void Concurrency_RapidSwitching_OnlyLatestQueryCommits()
    {
        var coordinator = new GraphInteractionCoordinator();

        // Simulate 3 rapid view switch requests
        var gen1 = coordinator.BeginQuery();
        var cts1 = new CancellationTokenSource();

        var gen2 = coordinator.BeginQuery();
        var cts2 = new CancellationTokenSource();
        cts1.Cancel(); // gen1 is superseded and cancelled

        var gen3 = coordinator.BeginQuery();
        var cts3 = new CancellationTokenSource();
        cts2.Cancel(); // gen2 is superseded and cancelled

        // gen1 attempts to commit -> rejected
        Assert.IsFalse(coordinator.CanCommitQuery(gen1));
        var dummyFiles1 = new List<IndexedFile> { new(1, "1.txt", "C:\\1.txt", ".txt", 10, DateTimeOffset.UtcNow, true, null) };
        Assert.IsFalse(coordinator.CommitQuery(gen1, dummyFiles1));

        // gen2 attempts to commit -> rejected
        Assert.IsFalse(coordinator.CanCommitQuery(gen2));
        var dummyFiles2 = new List<IndexedFile> { new(2, "2.txt", "C:\\2.txt", ".txt", 20, DateTimeOffset.UtcNow, true, null) };
        Assert.IsFalse(coordinator.CommitQuery(gen2, dummyFiles2));

        // gen3 attempts to commit -> accepted!
        Assert.IsTrue(coordinator.CanCommitQuery(gen3));
        var dummyFiles3 = new List<IndexedFile> { new(3, "3.txt", "C:\\3.txt", ".txt", 30, DateTimeOffset.UtcNow, true, null) };
        Assert.IsTrue(coordinator.CommitQuery(gen3, dummyFiles3));

        Assert.HasCount(1, coordinator.CurrentFiles);
        Assert.AreEqual(3L, coordinator.CurrentFiles[0].Id);
    }
}
