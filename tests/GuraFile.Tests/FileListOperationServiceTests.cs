using System.Runtime.Versioning;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class FileListOperationServiceTests
{
    private sealed class InMemoryClipboard : IFileClipboardService
    {
        private FileClipboardContent? _content;

        public bool HasFiles() => _content != null && _content.Files.Count > 0;

        public FileClipboardContent? GetContent() => _content;

        public void SetContent(IReadOnlyList<string> filePaths, FileClipboardEffect effect)
        {
            if (filePaths == null || filePaths.Count == 0)
            {
                _content = null;
                return;
            }

            _content = new FileClipboardContent(filePaths.Select(Path.GetFullPath).ToList(), effect);
        }

        public void Clear()
        {
            _content = null;
        }
    }

    [TestMethod]
    public void ValidateNewFileName_ValidNames_PassValidation()
    {
        var validNames = new[]
        {
            "document.txt",
            "Report 2026.docx",
            "新文档.md",
            "a-b_c.d",
            "my file (1).tar.gz",
            "日记 2026-09-02.txt"
        };

        foreach (var name in validNames)
        {
            var validated = FileListOperationService.ValidateNewFileName(name);
            Assert.AreEqual(name.Trim(), validated);
        }
    }

    [TestMethod]
    public void ValidateNewFileName_InvalidNames_ThrowsArgumentException()
    {
        var invalidNames = new[]
        {
            "",
            "   ",
            "\t\n",
            "file/name.txt",
            "file\\name.txt",
            "file:name.txt",
            "file*name.txt",
            "file?name.txt",
            "file\"name.txt",
            "file<name.txt",
            "file>name.txt",
            "file|name.txt",
            ".",
            "..",
            " . ",
            " .. "
        };

        foreach (var name in invalidNames)
        {
            Assert.ThrowsExactly<ArgumentException>(() => FileListOperationService.ValidateNewFileName(name),
                $"Should have thrown for name: '{name}'");
        }
    }

    [TestMethod]
    public void CopyToClipboard_SetsClipboardFilesWithCopyEffect()
    {
        using var env = TestEnvironment.Create();
        var file1 = env.CreateFile("file1.txt", "content 1");
        var file2 = env.CreateFile("file2.txt", "content 2");

        var clipboard = new InMemoryClipboard();
        var committer = new FileOperationIndexCommitter(env.Scanner);
        var service = new FileListOperationService(committer, env.Scanner, clipboard);

        service.CopyToClipboard([file1, file2]);

        Assert.IsTrue(service.CanPasteFromClipboard());
        var content = service.GetClipboardContent();
        Assert.IsNotNull(content);
        Assert.AreEqual(FileClipboardEffect.Copy, content.Effect);
        Assert.HasCount(2, content.Files);
    }

    [TestMethod]
    public void CutToClipboard_SetsClipboardFilesWithMoveEffect()
    {
        using var env = TestEnvironment.Create();
        var file1 = env.CreateFile("file1.txt", "content 1");

        var clipboard = new InMemoryClipboard();
        var committer = new FileOperationIndexCommitter(env.Scanner);
        var service = new FileListOperationService(committer, env.Scanner, clipboard);

        service.CutToClipboard([file1]);

        Assert.IsTrue(service.CanPasteFromClipboard());
        var content = service.GetClipboardContent();
        Assert.IsNotNull(content);
        Assert.AreEqual(FileClipboardEffect.Move, content.Effect);
        Assert.HasCount(1, content.Files);
    }

    [TestMethod]
    public async Task RenameAsync_ValidName_RenamesFileAndPreservesUserTags()
    {
        using var env = TestEnvironment.Create();
        var file1 = env.CreateFile("old_name.txt", "rename content");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("RenameTag");
        var queryService = new FileQueryService(env.DatabasePath);
        var initFiles = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(tag.Id, [initFiles[0].Id]);

        var clipboard = new InMemoryClipboard();
        var committer = new FileOperationIndexCommitter(env.Scanner);
        var service = new FileListOperationService(committer, env.Scanner, clipboard);

        var result = await service.RenameAsync(file1, "new_name.md");

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.ActualTargetPath);
        Assert.IsTrue(File.Exists(result.ActualTargetPath));
        Assert.IsFalse(File.Exists(file1));

        var updatedFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, updatedFiles);
        Assert.AreEqual("new_name.md", updatedFiles[0].Name);

        var tags = tagService.ListTagsForFile(updatedFiles[0].Id);
        Assert.AreEqual("RenameTag", tags.Single().Name);

        var autoTags = tagService.ListAutomaticTagsForFile(updatedFiles[0].Id);
        CollectionAssert.AreEquivalent(
            new[] { "类型/文档", "格式/Markdown" },
            autoTags.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public async Task RenameAsync_InvalidName_ThrowsBeforeCallingExecutor()
    {
        using var env = TestEnvironment.Create();
        var file1 = env.CreateFile("valid.txt", "valid content");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var clipboard = new InMemoryClipboard();
        var committer = new FileOperationIndexCommitter(env.Scanner);
        var service = new FileListOperationService(committer, env.Scanner, clipboard);

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
        {
            await service.RenameAsync(file1, "invalid/name.txt");
        });

        Assert.IsTrue(File.Exists(file1));
    }

    [TestMethod]
    public async Task MoveToAsync_ValidDestination_MovesFilesAndPreservesTags()
    {
        using var env = TestEnvironment.Create();
        var file1 = env.CreateFile("move_me.txt", "move me content");
        var targetSub = env.CreateDirectory("SubDir");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("MoveTag");
        var queryService = new FileQueryService(env.DatabasePath);
        var initFiles = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(tag.Id, [initFiles[0].Id]);

        var clipboard = new InMemoryClipboard();
        var committer = new FileOperationIndexCommitter(env.Scanner);
        var service = new FileListOperationService(committer, env.Scanner, clipboard);

        var result = await service.MoveToAsync([file1], targetSub);

        Assert.AreEqual(1, result.SucceededCount);
        var expectedPath = Path.Combine(targetSub, "move_me.txt");
        Assert.IsTrue(File.Exists(expectedPath));
        Assert.IsFalse(File.Exists(file1));

        var updatedFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, updatedFiles);
        Assert.AreEqual(expectedPath, updatedFiles[0].Path);

        var tags = tagService.ListTagsForFile(updatedFiles[0].Id);
        Assert.AreEqual("MoveTag", tags.Single().Name);
    }

    [TestMethod]
    public async Task MoveToAsync_DestinationOutsideRoots_ThrowsArgumentException()
    {
        using var env = TestEnvironment.Create();
        var file1 = env.CreateFile("source.txt", "data");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var outsideDir = Path.Combine(Path.GetTempPath(), $"GuraFile_Outside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        try
        {
            var clipboard = new InMemoryClipboard();
            var committer = new FileOperationIndexCommitter(env.Scanner);
            var service = new FileListOperationService(committer, env.Scanner, clipboard);

            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            {
                await service.MoveToAsync([file1], outsideDir);
            });
        }
        finally
        {
            if (Directory.Exists(outsideDir))
            {
                Directory.Delete(outsideDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PasteFromClipboardAsync_WithCopyEffect_CopiesFilesAndInheritsTags()
    {
        using var env = TestEnvironment.Create();
        var file1 = env.CreateFile("copy_src.txt", "copy src text");
        var targetSub = env.CreateDirectory("TargetCopyDir");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("CopyTag");
        var queryService = new FileQueryService(env.DatabasePath);
        var initFiles = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(tag.Id, [initFiles[0].Id]);

        var clipboard = new InMemoryClipboard();
        var committer = new FileOperationIndexCommitter(env.Scanner);
        var service = new FileListOperationService(committer, env.Scanner, clipboard);

        service.CopyToClipboard([file1]);
        Assert.IsTrue(service.CanPasteFromClipboard());

        var result = await service.PasteFromClipboardAsync(targetSub);

        Assert.AreEqual(1, result.SucceededCount);
        var expectedTargetPath = Path.Combine(targetSub, "copy_src.txt");
        Assert.IsTrue(File.Exists(file1));
        Assert.IsTrue(File.Exists(expectedTargetPath));

        var allFiles = await queryService.QueryAsync(new());
        Assert.HasCount(2, allFiles);

        var targetDb = allFiles.Single(f => f.Path == expectedTargetPath);
        var targetTags = tagService.ListTagsForFile(targetDb.Id);
        Assert.AreEqual("CopyTag", targetTags.Single().Name);

        var autoTags = tagService.ListAutomaticTagsForFile(targetDb.Id);
        CollectionAssert.AreEquivalent(
            new[] { "类型/文档", "格式/TXT" },
            autoTags.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public async Task PasteFromClipboardAsync_WithMoveEffect_MovesFilesAndPreservesTags()
    {
        using var env = TestEnvironment.Create();
        var file1 = env.CreateFile("cut_src.txt", "cut src text");
        var targetSub = env.CreateDirectory("TargetCutDir");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("CutTag");
        var queryService = new FileQueryService(env.DatabasePath);
        var initFiles = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(tag.Id, [initFiles[0].Id]);

        var clipboard = new InMemoryClipboard();
        var committer = new FileOperationIndexCommitter(env.Scanner);
        var service = new FileListOperationService(committer, env.Scanner, clipboard);

        service.CutToClipboard([file1]);
        Assert.IsTrue(service.CanPasteFromClipboard());

        var result = await service.PasteFromClipboardAsync(targetSub);

        Assert.AreEqual(1, result.SucceededCount);
        var expectedTargetPath = Path.Combine(targetSub, "cut_src.txt");
        Assert.IsFalse(File.Exists(file1));
        Assert.IsTrue(File.Exists(expectedTargetPath));

        var allFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, allFiles);
        Assert.AreEqual(expectedTargetPath, allFiles[0].Path);

        var targetTags = tagService.ListTagsForFile(allFiles[0].Id);
        Assert.AreEqual("CutTag", targetTags.Single().Name);
    }

    [TestMethod]
    public async Task PasteFromClipboardAsync_EmptyClipboard_ThrowsInvalidOperationException()
    {
        using var env = TestEnvironment.Create();
        var root = env.Scanner.AddRoot(env.RootPath);

        var clipboard = new InMemoryClipboard();
        var committer = new FileOperationIndexCommitter(env.Scanner);
        var service = new FileListOperationService(committer, env.Scanner, clipboard);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await service.PasteFromClipboardAsync(env.RootPath);
        });
    }

    [TestMethod]
    public async Task PasteFromClipboardAsync_DestinationOutsideRoots_ThrowsArgumentException()
    {
        using var env = TestEnvironment.Create();
        var file1 = env.CreateFile("source.txt", "data");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var outsideDir = Path.Combine(Path.GetTempPath(), $"GuraFile_Outside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        try
        {
            var clipboard = new InMemoryClipboard();
            var committer = new FileOperationIndexCommitter(env.Scanner);
            var service = new FileListOperationService(committer, env.Scanner, clipboard);
            service.CopyToClipboard([file1]);

            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            {
                await service.PasteFromClipboardAsync(outsideDir);
            });
        }
        finally
        {
            if (Directory.Exists(outsideDir))
            {
                Directory.Delete(outsideDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ProgressAndCancellation_WorksCorrectly()
    {
        using var env = TestEnvironment.Create();
        var file1 = env.CreateFile("file1.txt", "content 1");
        var file2 = env.CreateFile("file2.txt", "content 2");
        var targetSub = env.CreateDirectory("ProgressSub");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var clipboard = new InMemoryClipboard();
        var committer = new FileOperationIndexCommitter(env.Scanner);
        var service = new FileListOperationService(committer, env.Scanner, clipboard);

        var progressReports = new List<FileOperationProgress>();
        var result = await service.MoveToAsync(
            [file1, file2],
            targetSub,
            FileCollisionPolicy.AutoRename,
            progress: p => progressReports.Add(p));

        Assert.AreEqual(2, result.SucceededCount);
        Assert.IsNotEmpty(progressReports);
    }

    [TestMethod]
    public void FormatBatchSummary_FormatsCorrectly()
    {
        var completedItem = new FileOperationCommitItemResult("src.txt", "dst.txt", FileOperationItemStatus.Completed);
        var failedItem = new FileOperationCommitItemResult("fail.txt", null, FileOperationItemStatus.Failed, "文件被占用");
        var skippedItem = new FileOperationCommitItemResult("skip.txt", "target.txt", FileOperationItemStatus.Skipped);
        var canceledItem = new FileOperationCommitItemResult("cancel.txt", null, FileOperationItemStatus.Canceled, "已取消", IsCanceled: true);

        var successBatch = new FileOperationCommitBatchResult([completedItem, completedItem]);
        var summarySuccess = FileListOperationService.FormatBatchSummary(successBatch, "复制");
        Assert.AreEqual("复制完成：成功 2 个，跳过 0 个，失败 0 个。", summarySuccess);

        var partialBatch = new FileOperationCommitBatchResult([completedItem, failedItem, skippedItem]);
        var summaryPartial = FileListOperationService.FormatBatchSummary(partialBatch, "移动");
        Assert.AreEqual("移动完成：成功 1 个，跳过 1 个，失败 1 个。", summaryPartial);

        var canceledBatch = new FileOperationCommitBatchResult([completedItem, canceledItem], IsCanceled: true);
        var summaryCanceled = FileListOperationService.FormatBatchSummary(canceledBatch, "粘贴");
        Assert.AreEqual("粘贴已取消：成功 1 个，跳过 0 个，取消 1 个，失败 0 个。", summaryCanceled);
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
            var root = Path.Combine(AppContext.BaseDirectory, $"GuraFile_OpServiceTests_{Guid.NewGuid():N}");
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
