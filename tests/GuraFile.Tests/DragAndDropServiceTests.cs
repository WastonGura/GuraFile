using System.Runtime.Versioning;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class DragAndDropServiceTests
{
    [TestMethod]
    public async Task ExecuteDropAsync_ExternalFiles_CopiesToTargetRoot_AndInheritsAutoTags()
    {
        using var env = TestEnvironment.Create();
        var rootA = env.CreateDirectory("RootA");
        var rootB = env.CreateDirectory("RootB");
        var managedA = env.Scanner.AddRoot(rootA);
        var managedB = env.Scanner.AddRoot(rootB);
        await env.Scanner.ScanAsync(managedA.Id);
        await env.Scanner.ScanAsync(managedB.Id);

        var extDir = Path.Combine(Path.GetTempPath(), $"GuraFile_Ext_{Guid.NewGuid():N}");
        Directory.CreateDirectory(extDir);
        try
        {
            var extFile = Path.Combine(extDir, "external.txt");
            File.WriteAllText(extFile, "external content");

            var committer = new FileOperationIndexCommitter(env.Scanner);
            var service = new FileListOperationService(committer, env.Scanner);

            var result = await service.ExecuteDropAsync(
                [extFile],
                rootA,
                isInternalDrag: false,
                FileCollisionPolicy.AutoRename);

            Assert.AreEqual(1, result.SucceededCount);
            var targetFile = Path.Combine(rootA, "external.txt");
            Assert.IsTrue(File.Exists(targetFile));
            Assert.IsTrue(File.Exists(extFile)); // Source still exists (copy)

            var queryService = new FileQueryService(env.DatabasePath);
            var files = await queryService.QueryAsync(new());
            Assert.IsTrue(files.Any(f => f.Path == targetFile));
        }
        finally
        {
            if (Directory.Exists(extDir))
            {
                Directory.Delete(extDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ExecuteDropAsync_InternalFiles_MovesToTargetRoot_AndPreservesTags()
    {
        using var env = TestEnvironment.Create();
        var rootA = env.CreateDirectory("RootA");
        var rootB = env.CreateDirectory("RootB");
        var fileA = Path.Combine(rootA, "fileA.txt");
        File.WriteAllText(fileA, "hello root A");

        var managedA = env.Scanner.AddRoot(rootA);
        var managedB = env.Scanner.AddRoot(rootB);
        await env.Scanner.ScanAsync(managedA.Id);
        await env.Scanner.ScanAsync(managedB.Id);

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("TagA");
        var queryService = new FileQueryService(env.DatabasePath);
        var files = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(tag.Id, [files.Single(f => f.Path == fileA).Id]);

        var committer = new FileOperationIndexCommitter(env.Scanner);
        var service = new FileListOperationService(committer, env.Scanner);

        var result = await service.ExecuteDropAsync(
            [fileA],
            rootB,
            isInternalDrag: true,
            FileCollisionPolicy.AutoRename);

        Assert.AreEqual(1, result.SucceededCount);
        Assert.IsFalse(File.Exists(fileA)); // Source gone (move)
        var targetFile = Path.Combine(rootB, "fileA.txt");
        Assert.IsTrue(File.Exists(targetFile));

        var updatedFiles = await queryService.QueryAsync(new());
        var movedFile = updatedFiles.Single(f => f.Path == targetFile);
        var fileTags = tagService.ListTagsForFile(movedFile.Id);
        Assert.AreEqual("TagA", fileTags.Single().Name);
    }

    [TestMethod]
    public async Task ExecuteDropAsync_InternalFiles_ToSameDirectory_ThrowsInvalidOperationException()
    {
        using var env = TestEnvironment.Create();
        var rootA = env.CreateDirectory("RootA");
        var fileA = Path.Combine(rootA, "same.txt");
        File.WriteAllText(fileA, "same");
        var managedA = env.Scanner.AddRoot(rootA);
        await env.Scanner.ScanAsync(managedA.Id);

        var committer = new FileOperationIndexCommitter(env.Scanner);
        var service = new FileListOperationService(committer, env.Scanner);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await service.ExecuteDropAsync(
                [fileA],
                rootA,
                isInternalDrag: true,
                FileCollisionPolicy.AutoRename);
        });
    }

    [TestMethod]
    public async Task ExecuteDropAsync_WhenDirectoryIncluded_ThrowsArgumentException()
    {
        using var env = TestEnvironment.Create();
        var rootA = env.CreateDirectory("RootA");
        var subDir = env.CreateDirectory("SubFolder");
        var managedA = env.Scanner.AddRoot(rootA);
        await env.Scanner.ScanAsync(managedA.Id);

        var committer = new FileOperationIndexCommitter(env.Scanner);
        var service = new FileListOperationService(committer, env.Scanner);

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
        {
            await service.ExecuteDropAsync(
                [subDir],
                rootA,
                isInternalDrag: false,
                FileCollisionPolicy.AutoRename);
        });
    }

    private sealed class TestEnvironment : IDisposable
    {
        public string RootPath { get; }
        public string DatabasePath { get; }
        public ManagedRootScanner Scanner { get; }

        private TestEnvironment(string rootPath, string databasePath, ManagedRootScanner scanner)
        {
            RootPath = rootPath;
            DatabasePath = databasePath;
            Scanner = scanner;
        }

        public static TestEnvironment Create()
        {
            var root = Path.Combine(AppContext.BaseDirectory, $"GuraFile_DndTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var dbPath = Path.Combine(root, ".gurafile", "index.db");
            var scanner = new ManagedRootScanner(dbPath);
            return new(root, dbPath, scanner);
        }

        public string CreateFile(string relativeName, string content)
        {
            var filePath = Path.Combine(RootPath, relativeName);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, content);
            return filePath;
        }

        public string CreateDirectory(string relativeName)
        {
            var dirPath = Path.Combine(RootPath, relativeName);
            Directory.CreateDirectory(dirPath);
            return dirPath;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
