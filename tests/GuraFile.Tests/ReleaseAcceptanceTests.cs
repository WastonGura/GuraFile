using GuraFile.Storage;

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
        await File.WriteAllTextAsync(originalPath, "GuraFile v0.1.0");
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
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
