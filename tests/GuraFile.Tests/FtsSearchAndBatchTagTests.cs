using System.Diagnostics;
using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

[TestClass]
public sealed class FtsSearchAndBatchTagTests
{
    private sealed class TempTestDatabase : IDisposable
    {
        public string Path { get; }

        public TempTestDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GuraFile.FtsTest.{Guid.NewGuid():N}.db");
            using var connection = SqliteDatabase.Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO roots (id, path, normalized_path, status) VALUES (1, 'C:\\TestRoot', 'C:\\TestRoot', 'online');";
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            foreach (var file in new[] { Path, $"{Path}-shm", $"{Path}-wal" })
            {
                if (File.Exists(file))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
    }

    [TestMethod]
    public void FtsQuerySanitizer_SpecialCharactersAndKeywords_AreSafeAndDoNotThrow()
    {
        using var db = new TempTestDatabase();
        using var connection = SqliteDatabase.Open(db.Path);

        // Inputs that would cause syntax errors in raw unquoted FTS5
        string[] maliciousOrDangerousInputs =
        [
            "*",
            ":",
            "^",
            "+",
            "-",
            "(",
            ")",
            "{",
            "}",
            "\"",
            "\"\"\"",
            "AND",
            "OR",
            "NOT",
            "NEAR",
            "AND OR NOT NEAR",
            "foo AND bar",
            "tag:important",
            "name:report*",
            "^root",
            "(a OR b) AND NOT (c NEAR/5 d)",
            "C:\\Program Files (x86)\\Microsoft\\",
            "'; DROP TABLE files; --",
            "\"unclosed quote",
            "*** ::: ^^^ +++",
            "   ",
            ""
        ];

        foreach (var input in maliciousOrDangerousInputs)
        {
            var built = FtsQueryBuilder.Build(input);

            if (string.IsNullOrWhiteSpace(input) || input.All(c => !char.IsLetterOrDigit(c)))
            {
                Assert.IsNull(built, $"Input '{input}' with no alphanumeric characters should return null.");
            }
            else
            {
                Assert.IsNotNull(built, $"Input '{input}' should produce a safe query.");

                // Verify the query never causes a syntax error when executed by SQLite FTS5
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM files_fts WHERE files_fts MATCH $query;";
                cmd.Parameters.AddWithValue("$query", built);
                var result = cmd.ExecuteScalar();
                Assert.IsNotNull(result);
            }
        }
    }

    [TestMethod]
    public void FtsQuerySanitizer_UnicodeAndChinese_ExtractsTokensCorrectly()
    {
        var chineseQuery = FtsQueryBuilder.Build("财务 报表");
        Assert.AreEqual("\"财务\"* \"报表\"*", chineseQuery);

        var mixedQuery = FtsQueryBuilder.Build("2026年财务报告_Q1.xlsx");
        Assert.AreEqual("\"2026年财务报告\"* \"Q1\"* \"xlsx\"*", mixedQuery);

        var spacedMixedQuery = FtsQueryBuilder.Build("2026 年财务报告_Q1.xlsx");
        Assert.AreEqual("\"2026\"* \"年财务报告\"* \"Q1\"* \"xlsx\"*", spacedMixedQuery);

        var pathQuery = FtsQueryBuilder.Build(@"C:\Users\Admin\Projects\Alpha.txt");
        Assert.AreEqual("\"C\"* \"Users\"* \"Admin\"* \"Projects\"* \"Alpha\"* \"txt\"*", pathQuery);
    }

    [TestMethod]
    public async Task FtsConsistency_Insert_SyncsWithFtsIndex()
    {
        using var db = new TempTestDatabase();
        var queryService = new FileQueryService(db.Path);

        using (var connection = SqliteDatabase.Open(db.Path))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc)
                VALUES (1, 1, 'vol-1', 'fid-1', 'C:\TestRoot\ProjectAlpha.cs', 'c:\testroot\projectalpha.cs', 'ProjectAlpha.cs', '.cs', 1024, '2026-09-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }

        var results = await queryService.QueryAsync(new FileQuery(Search: "ProjectAlpha"));
        Assert.HasCount(1, results);
        Assert.AreEqual("ProjectAlpha.cs", results[0].Name);
    }

    [TestMethod]
    public async Task FtsConsistency_Update_Rename_And_Move_SyncsWithFtsIndex()
    {
        using var db = new TempTestDatabase();
        var queryService = new FileQueryService(db.Path);

        using (var connection = SqliteDatabase.Open(db.Path))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc)
                VALUES (1, 1, 'vol-1', 'fid-1', 'C:\TestRoot\Draft.docx', 'c:\testroot\draft.docx', 'Draft.docx', '.docx', 2048, '2026-09-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }

        // Search initial name
        var initial = await queryService.QueryAsync(new FileQuery(Search: "Draft"));
        Assert.HasCount(1, initial);

        // 1. Rename: Draft.docx -> FinalReport.docx
        using (var connection = SqliteDatabase.Open(db.Path))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                UPDATE files
                SET name = 'FinalReport.docx',
                    normalized_path = 'c:\testroot\finalreport.docx',
                    path = 'C:\TestRoot\FinalReport.docx'
                WHERE id = 1;
                """;
            cmd.ExecuteNonQuery();
        }

        var oldSearch = await queryService.QueryAsync(new FileQuery(Search: "Draft"));
        Assert.IsEmpty(oldSearch);

        var newSearch = await queryService.QueryAsync(new FileQuery(Search: "FinalReport"));
        Assert.HasCount(1, newSearch);
        Assert.AreEqual("FinalReport.docx", newSearch[0].Name);

        // 2. Move: C:\TestRoot\FinalReport.docx -> C:\TestRoot\Archive\FinalReport.docx
        using (var connection = SqliteDatabase.Open(db.Path))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                UPDATE files
                SET path = 'C:\TestRoot\Archive\FinalReport.docx',
                    normalized_path = 'c:\testroot\archive\finalreport.docx'
                WHERE id = 1;
                """;
            cmd.ExecuteNonQuery();
        }

        var archiveSearch = await queryService.QueryAsync(new FileQuery(Search: "Archive"));
        Assert.HasCount(1, archiveSearch);
        Assert.AreEqual(@"C:\TestRoot\Archive\FinalReport.docx", archiveSearch[0].Path);
    }

    [TestMethod]
    public async Task FtsConsistency_Delete_SyncsWithFtsIndex()
    {
        using var db = new TempTestDatabase();
        var queryService = new FileQueryService(db.Path);

        using (var connection = SqliteDatabase.Open(db.Path))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc)
                VALUES (1, 1, 'vol-1', 'fid-1', 'C:\TestRoot\ToDelete.txt', 'c:\testroot\todelete.txt', 'ToDelete.txt', '.txt', 500, '2026-09-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }

        var beforeDelete = await queryService.QueryAsync(new FileQuery(Search: "ToDelete"));
        Assert.HasCount(1, beforeDelete);

        using (var connection = SqliteDatabase.Open(db.Path))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM files WHERE id = 1;";
            cmd.ExecuteNonQuery();
        }

        var afterDelete = await queryService.QueryAsync(new FileQuery(Search: "ToDelete"));
        Assert.IsEmpty(afterDelete);
    }

    [TestMethod]
    public async Task FtsConsistency_ReconcileAndOfflineState_IntegrityPreserved()
    {
        using var db = new TempTestDatabase();
        var queryService = new FileQueryService(db.Path);

        using (var connection = SqliteDatabase.Open(db.Path))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, is_online, identity_diagnostic)
                VALUES (1, 1, 'vol-1', 'fid-1', 'C:\TestRoot\OfflineDoc.txt', 'c:\testroot\offlinedoc.txt', 'OfflineDoc.txt', '.txt', 100, '2026-09-01T00:00:00Z', 1, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        // Reconcile/offline marks is_online = 0
        using (var connection = SqliteDatabase.Open(db.Path))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE files SET is_online = 0, identity_diagnostic = 'volume unreachable' WHERE id = 1;";
            cmd.ExecuteNonQuery();
        }

        var results = await queryService.QueryAsync(new FileQuery(Search: "OfflineDoc"));
        Assert.HasCount(1, results);
        Assert.IsFalse(results[0].IsOnline);
        Assert.AreEqual("volume unreachable", results[0].Diagnostic);
    }

    [TestMethod]
    public async Task FtsRebuild_RestoresEntireIndex_FromFilesTable()
    {
        using var db = new TempTestDatabase();
        var queryService = new FileQueryService(db.Path);

        using (var connection = SqliteDatabase.Open(db.Path))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc)
                VALUES
                    (1, 1, 'vol-1', 'f-1', 'C:\TestRoot\DocA.txt', 'c:\testroot\doca.txt', 'DocA.txt', '.txt', 100, '2026-09-01T00:00:00Z'),
                    (2, 1, 'vol-1', 'f-2', 'C:\TestRoot\DocB.txt', 'c:\testroot\docb.txt', 'DocB.txt', '.txt', 200, '2026-09-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }

        // Manually delete FTS entries directly to simulate desync/corruption
        using (var connection = SqliteDatabase.Open(db.Path))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO files_fts(files_fts, rowid, name, path) VALUES('delete', 1, 'DocA.txt', 'C:\TestRoot\DocA.txt');
                INSERT INTO files_fts(files_fts, rowid, name, path) VALUES('delete', 2, 'DocB.txt', 'C:\TestRoot\DocB.txt');
                """;
            cmd.ExecuteNonQuery();
        }

        // Search should now find nothing
        var empty = await queryService.QueryAsync(new FileQuery(Search: "DocA"));
        Assert.IsEmpty(empty);

        // Rebuild search index
        await queryService.RebuildSearchIndexAsync();

        // Search should now succeed for both files
        var foundA = await queryService.QueryAsync(new FileQuery(Search: "DocA"));
        Assert.HasCount(1, foundA);
        Assert.AreEqual("DocA.txt", foundA[0].Name);

        var foundB = await queryService.QueryAsync(new FileQuery(Search: "DocB"));
        Assert.HasCount(1, foundB);
        Assert.AreEqual("DocB.txt", foundB[0].Name);
    }

    [TestMethod]
    public void BatchTagging_1000Files_Atomicity_RollsBackOnMissingFile()
    {
        using var db = new TempTestDatabase();
        var tagService = new TagService(db.Path);
        var tag = tagService.CreateTag("ProjectTest");

        // Seed 999 existing files
        using (var connection = SqliteDatabase.Open(db.Path))
        using (var transaction = connection.BeginTransaction())
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText =
                """
                INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc)
                VALUES ($id, 1, 'vol-1', $fid, $path, $norm, $name, '.txt', 100, '2026-09-01T00:00:00Z');
                """;
            var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
            var pFid = cmd.Parameters.Add("$fid", SqliteType.Text);
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pNorm = cmd.Parameters.Add("$norm", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);

            for (var i = 1; i <= 999; i++)
            {
                pId.Value = i;
                pFid.Value = $"f-{i}";
                pPath.Value = $@"C:\TestRoot\File_{i}.txt";
                pNorm.Value = $@"c:\testroot\file_{i}.txt";
                pName.Value = $"File_{i}.txt";
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        // Pass 1,000 files including non-existent ID 99999
        var targetFileIds = Enumerable.Range(1, 999).Select(i => (long)i).Append(99999L).ToArray();

        var ex = Assert.ThrowsExactly<ArgumentException>(() =>
            tagService.AddTagToFiles(tag.Id, targetFileIds));
        StringAssert.Contains(ex.Message, "99999");

        // Verify single-transaction rollback: 0 relations created
        using (var connection = SqliteDatabase.Open(db.Path))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM file_tags;";
            Assert.AreEqual(0L, (long)cmd.ExecuteScalar()!);
        }
    }

    [TestMethod]
    public void BatchTagging_1000Files_Performance_WithinThreshold()
    {
        using var db = new TempTestDatabase();
        var tagService = new TagService(db.Path);
        var tag = tagService.CreateTag("BatchPerf");

        // Seed 1,000 files
        using (var connection = SqliteDatabase.Open(db.Path))
        using (var transaction = connection.BeginTransaction())
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText =
                """
                INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc)
                VALUES ($id, 1, 'vol-1', $fid, $path, $norm, $name, '.txt', 100, '2026-09-01T00:00:00Z');
                """;
            var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
            var pFid = cmd.Parameters.Add("$fid", SqliteType.Text);
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pNorm = cmd.Parameters.Add("$norm", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);

            for (var i = 1; i <= 1000; i++)
            {
                pId.Value = i;
                pFid.Value = $"f-{i}";
                pPath.Value = $@"C:\TestRoot\File_{i}.txt";
                pNorm.Value = $@"c:\testroot\file_{i}.txt";
                pName.Value = $"File_{i}.txt";
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        var targetFileIds = Enumerable.Range(1, 1000).Select(i => (long)i).ToArray();

        // 3 consecutive batch adds (< 2s required, target < 200ms)
        for (var run = 1; run <= 3; run++)
        {
            var sw = Stopwatch.StartNew();
            tagService.AddTagToFiles(tag.Id, targetFileIds);
            sw.Stop();
            Console.WriteLine($"[Batch Add Run {run}] 1,000 files tagged in {sw.Elapsed.TotalMilliseconds:F2} ms");
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(2.0), $"Batch add run {run} took {sw.Elapsed.TotalMilliseconds} ms, exceeding 2s budget.");
        }

        // Verify count
        using (var connection = SqliteDatabase.Open(db.Path))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM file_tags WHERE tag_id = $tagId;";
            cmd.Parameters.AddWithValue("$tagId", tag.Id);
            Assert.AreEqual(1000L, (long)cmd.ExecuteScalar()!);
        }

        // 3 consecutive batch removes (< 2s required)
        for (var run = 1; run <= 3; run++)
        {
            var sw = Stopwatch.StartNew();
            tagService.RemoveTagFromFiles(tag.Id, targetFileIds);
            sw.Stop();
            Console.WriteLine($"[Batch Remove Run {run}] 1,000 files untagged in {sw.Elapsed.TotalMilliseconds:F2} ms");
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(2.0), $"Batch remove run {run} took {sw.Elapsed.TotalMilliseconds} ms, exceeding 2s budget.");
        }

        // Verify count
        using (var connection = SqliteDatabase.Open(db.Path))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM file_tags WHERE tag_id = $tagId;";
            cmd.Parameters.AddWithValue("$tagId", tag.Id);
            Assert.AreEqual(0L, (long)cmd.ExecuteScalar()!);
        }
    }

    [TestMethod]
    public async Task Scale_100kFiles_Search_And_TagFilter_Performance_Within200ms()
    {
        using var db = new TempTestDatabase();
        var seedTimer = Stopwatch.StartNew();

        using (var connection = SqliteDatabase.Open(db.Path))
        using (var transaction = connection.BeginTransaction())
        {
            using (var tagCmd = connection.CreateCommand())
            {
                tagCmd.Transaction = transaction;
                tagCmd.CommandText =
                    """
                    INSERT INTO tags (id, name, normalized_name, source) VALUES
                    (1, 'Priority', 'PRIORITY', 'user'),
                    (2, 'Project', 'PROJECT', 'user');
                    """;
                tagCmd.ExecuteNonQuery();
            }

            using (var fileCmd = connection.CreateCommand())
            {
                fileCmd.Transaction = transaction;
                fileCmd.CommandText =
                    """
                    INSERT INTO files (
                        id, root_id, volume_id, file_id, path, normalized_path,
                        name, extension, size, modified_utc, identity_kind, is_online, scan_token)
                    VALUES ($id, 1, 'vol-scale', $fileId, $path, $normPath, $name, $ext, $size, $modUtc, 'stable', 1, 'tok');
                    """;

                var pId = fileCmd.Parameters.Add("$id", SqliteType.Integer);
                var pFileId = fileCmd.Parameters.Add("$fileId", SqliteType.Text);
                var pPath = fileCmd.Parameters.Add("$path", SqliteType.Text);
                var pNormPath = fileCmd.Parameters.Add("$normPath", SqliteType.Text);
                var pName = fileCmd.Parameters.Add("$name", SqliteType.Text);
                var pExt = fileCmd.Parameters.Add("$ext", SqliteType.Text);
                var pSize = fileCmd.Parameters.Add("$size", SqliteType.Integer);
                var pModUtc = fileCmd.Parameters.Add("$modUtc", SqliteType.Text);

                var timestamp = DateTimeOffset.UtcNow.ToString("O");
                for (var i = 1; i <= 100_000; i++)
                {
                    var dir = i / 1000;
                    var ext = (i % 4) switch
                    {
                        0 => ".txt",
                        1 => ".cs",
                        2 => ".png",
                        _ => ".pdf"
                    };
                    var name = $"Document_{i:D6}{ext}";
                    var path = $@"C:\TestRoot\Dir_{dir:D3}\{name}";

                    pId.Value = i;
                    pFileId.Value = $"fid-{i:D6}";
                    pPath.Value = path;
                    pNormPath.Value = path;
                    pName.Value = name;
                    pExt.Value = ext;
                    pSize.Value = 1000 + (i % 50000);
                    pModUtc.Value = timestamp;

                    fileCmd.ExecuteNonQuery();
                }
            }

            // Tag 10,000 files with tag 1 (Priority) and 5,000 files with tag 2 (Project)
            using (var relCmd = connection.CreateCommand())
            {
                relCmd.Transaction = transaction;
                relCmd.CommandText = "INSERT INTO file_tags (file_id, tag_id, source) VALUES ($fileId, $tagId, 'user');";
                var pFileId = relCmd.Parameters.Add("$fileId", SqliteType.Integer);
                var pTagId = relCmd.Parameters.Add("$tagId", SqliteType.Integer);

                for (var i = 1; i <= 10_000; i++)
                {
                    pFileId.Value = i;
                    pTagId.Value = 1;
                    relCmd.ExecuteNonQuery();

                    if (i <= 5_000)
                    {
                        pTagId.Value = 2;
                        relCmd.ExecuteNonQuery();
                    }
                }
            }

            transaction.Commit();
        }

        seedTimer.Stop();
        Console.WriteLine($"[Scale 100k] 100,000 files + tags seeded in {seedTimer.Elapsed.TotalMilliseconds:F1} ms");

        var queryService = new FileQueryService(db.Path);

        // 1. Three consecutive common filename searches (< 200ms)
        for (var run = 1; run <= 3; run++)
        {
            var sw = Stopwatch.StartNew();
            var results = await queryService.QueryAsync(new FileQuery(Search: "Document_00005"));
            sw.Stop();
            Console.WriteLine($"[Scale Search Run {run}] 'Document_00005' returned {results.Count} rows in {sw.Elapsed.TotalMilliseconds:F2} ms");
            Assert.HasCount(10, results);
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromMilliseconds(200), $"Search run {run} took {sw.Elapsed.TotalMilliseconds} ms, exceeding 200ms budget.");
        }

        // 2. Three consecutive search + Tag Any filter (< 200ms)
        for (var run = 1; run <= 3; run++)
        {
            var sw = Stopwatch.StartNew();
            var results = await queryService.QueryAsync(new FileQuery(
                Search: "Document_00005",
                TagIds: [1, 2],
                TagMatch: TagMatchMode.Any));
            sw.Stop();
            Console.WriteLine($"[Scale Search + Tag Any Run {run}] returned {results.Count} rows in {sw.Elapsed.TotalMilliseconds:F2} ms");
            Assert.HasCount(10, results);
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromMilliseconds(200), $"Search + Tag Any run {run} took {sw.Elapsed.TotalMilliseconds} ms, exceeding 200ms budget.");
        }

        // 3. Three consecutive search + Tag All filter (< 200ms)
        for (var run = 1; run <= 3; run++)
        {
            var sw = Stopwatch.StartNew();
            var results = await queryService.QueryAsync(new FileQuery(
                Search: "Document_00005",
                TagIds: [1, 2],
                TagMatch: TagMatchMode.All));
            sw.Stop();
            Console.WriteLine($"[Scale Search + Tag All Run {run}] returned {results.Count} rows in {sw.Elapsed.TotalMilliseconds:F2} ms");
            Assert.HasCount(10, results);
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromMilliseconds(200), $"Search + Tag All run {run} took {sw.Elapsed.TotalMilliseconds} ms, exceeding 200ms budget.");
        }

        // 4. Three consecutive search + sort by Size descending (< 200ms)
        for (var run = 1; run <= 3; run++)
        {
            var sw = Stopwatch.StartNew();
            var results = await queryService.QueryAsync(new FileQuery(
                Search: "Document_00005",
                SortBy: FileSortColumn.Size,
                Descending: true));
            sw.Stop();
            Console.WriteLine($"[Scale Search + Sort Run {run}] returned {results.Count} rows in {sw.Elapsed.TotalMilliseconds:F2} ms");
            Assert.HasCount(10, results);
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromMilliseconds(200), $"Search + Sort run {run} took {sw.Elapsed.TotalMilliseconds} ms, exceeding 200ms budget.");
        }
    }
}
