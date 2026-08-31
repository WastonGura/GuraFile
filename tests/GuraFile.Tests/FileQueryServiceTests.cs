using System.Diagnostics;
using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

[TestClass]
public sealed class FileQueryServiceTests
{
    [TestMethod]
    public async Task FiltersFilesByName()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();

        var result = await new FileQueryService(database.Path).QueryAsync(new(Search: "alpha"));

        Assert.HasCount(1, result);
        Assert.AreEqual("Alpha.txt", result[0].Name);
    }

    [TestMethod]
    public async Task FiltersFilesByPath()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();

        var result = await new FileQueryService(database.Path).QueryAsync(new(Search: "archive"));

        Assert.HasCount(1, result);
        Assert.AreEqual("Beta.md", result[0].Name);
    }

    [TestMethod]
    public async Task FiltersFilesByExtension()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();

        var result = await new FileQueryService(database.Path).QueryAsync(new(Search: ".pdf"));

        Assert.HasCount(1, result);
        Assert.AreEqual("Gamma.pdf", result[0].Name);
    }

    [TestMethod]
    public async Task EmptyFiltersReturnAllMetadata()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();

        var result = await new FileQueryService(database.Path).QueryAsync(new(Search: ""));

        Assert.HasCount(3, result);
        var alpha = result.Single(file => file.Id == 1);
        Assert.AreEqual(@"C:\Root\Projects\Alpha.txt", alpha.Path);
        Assert.AreEqual(".txt", alpha.Extension);
        Assert.AreEqual(30L, alpha.Size);
        Assert.AreEqual(DateTimeOffset.Parse("2026-08-30T00:00:00Z"), alpha.Modified);
        Assert.IsTrue(alpha.IsOnline);
        Assert.IsNull(alpha.Diagnostic);
    }

    [TestMethod]
    public async Task SortsEveryWhitelistedColumnInBothDirections()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();
        var service = new FileQueryService(database.Path);
        var ascendingIds = new Dictionary<FileSortColumn, long[]>
        {
            [FileSortColumn.Name] = [1, 2, 3],
            [FileSortColumn.Path] = [1, 3, 2],
            [FileSortColumn.Extension] = [2, 3, 1],
            [FileSortColumn.Size] = [2, 3, 1],
            [FileSortColumn.Modified] = [3, 1, 2]
        };

        foreach (var (column, expected) in ascendingIds)
        {
            var ascending = await service.QueryAsync(new(SortBy: column));
            var descending = await service.QueryAsync(new(SortBy: column, Descending: true));

            CollectionAssert.AreEqual(expected, ascending.Select(file => file.Id).ToArray(), $"Ascending {column}");
            CollectionAssert.AreEqual(expected.Reverse().ToArray(), descending.Select(file => file.Id).ToArray(), $"Descending {column}");
        }
    }

    [TestMethod]
    public async Task RejectsUnknownSortColumn()
    {
        using var database = TestDatabase.Create();

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            new FileQueryService(database.Path).QueryAsync(new(SortBy: (FileSortColumn)999)));
    }

    [TestMethod]
    public async Task ReturnsOfflineStateAndDiagnostic()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();

        var result = await new FileQueryService(database.Path).QueryAsync(new(Search: "gamma"));

        Assert.HasCount(1, result);
        Assert.IsFalse(result[0].IsOnline);
        Assert.AreEqual("drive offline", result[0].Diagnostic);
    }

    [TestMethod]
    public async Task PreCanceledQueryIsCanceled()
    {
        using var database = TestDatabase.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await new FileQueryService(database.Path).QueryAsync(new(), cancellation.Token);
            Assert.Fail("The pre-canceled query completed.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    [TestMethod]
    public async Task TenThousandRowsAreReturnedAndTimed()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles(10_000);
        var timer = Stopwatch.StartNew();

        var result = await new FileQueryService(database.Path).QueryAsync(new());

        timer.Stop();
        Assert.HasCount(10_000, result);
        Console.WriteLine($"10,000-row query completed in {timer.Elapsed.TotalMilliseconds:F1} ms.");
    }

    private sealed class TestDatabase : IDisposable
    {
        private TestDatabase(string path)
        {
            Path = path;
            using var connection = SqliteDatabase.Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\Root', 'C:\\Root');";
            command.ExecuteNonQuery();
        }

        public string Path { get; }

        public static TestDatabase Create() =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GuraFile.Tests.{Guid.NewGuid():N}.db"));

        public void SeedFiles()
        {
            using var connection = SqliteDatabase.Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO files (
                    id, root_id, volume_id, file_id, path, normalized_path,
                    name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online)
                VALUES
                    (1, 1, 'volume', 'alpha', 'C:\Root\Projects\Alpha.txt', 'C:\Root\Projects\Alpha.txt', 'Alpha.txt', '.txt', 30, '2026-08-30T00:00:00Z', 'stable', NULL, 1),
                    (2, 1, 'volume', 'beta', 'D:\Archive\Beta.md', 'D:\Archive\Beta.md', 'Beta.md', '.md', 10, '2026-08-31T00:00:00Z', 'path', 'identity fallback', 1),
                    (3, 1, 'volume', 'gamma', 'C:\Root\Reports\Gamma.pdf', 'C:\Root\Reports\Gamma.pdf', 'Gamma.pdf', '.pdf', 20, '2026-08-29T00:00:00Z', 'stable', 'drive offline', 0);
                """;
            command.ExecuteNonQuery();
        }

        public void SeedFiles(int count)
        {
            using var connection = SqliteDatabase.Open(Path);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO files (
                    root_id, volume_id, file_id, path, normalized_path,
                    name, extension, size, modified_utc, identity_kind)
                VALUES (1, 'volume', $fileId, $path, $path, $name, '.dat', $size, '2026-08-31T00:00:00Z', 'stable');
                """;
            var fileId = command.Parameters.Add("$fileId", SqliteType.Text);
            var path = command.Parameters.Add("$path", SqliteType.Text);
            var name = command.Parameters.Add("$name", SqliteType.Text);
            var size = command.Parameters.Add("$size", SqliteType.Integer);
            for (var index = 0; index < count; index++)
            {
                var fileName = $"{index:D5}.dat";
                fileId.Value = index.ToString();
                path.Value = $@"C:\Root\{fileName}";
                name.Value = fileName;
                size.Value = index;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public void Dispose()
        {
            foreach (var path in new[] { Path, $"{Path}-shm", $"{Path}-wal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
