using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class SafeFileOperationExecutorTests
{
    [TestMethod]
    public async Task DestinationOutsideManagedRootsThrowsArgumentException()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var outsideDir = Path.Combine(Path.GetTempPath(), $"Outside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        try
        {
            var sourceFile = temp.CreateFile("source.txt", "content");

            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            {
                await executor.CopyAsync([sourceFile], outsideDir, [temp.RootPath]);
            });

            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            {
                await executor.MoveAsync([sourceFile], outsideDir, [temp.RootPath]);
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
    public async Task DestinationWithParentTraversalOutsideRootThrows()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var sourceFile = temp.CreateFile("source.txt", "content");
        var escapeDir = Path.Combine(temp.RootPath, @"..\OutsideDir");

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
        {
            await executor.CopyAsync([sourceFile], escapeDir, [temp.RootPath]);
        });
    }

    [TestMethod]
    public async Task CopySingleFileSuccessfully()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var sourceFile = temp.CreateFile("source.txt", "hello world");
        var targetDir = temp.CreateDirectory("SubFolder");

        var result = await executor.CopyAsync([sourceFile], targetDir, [temp.RootPath]);

        Assert.AreEqual(1, result.TotalCount);
        Assert.AreEqual(1, result.SucceededCount);
        Assert.AreEqual(0, result.FailedCount);
        Assert.IsFalse(result.IsCanceled);

        var item = result.Items[0];
        Assert.AreEqual(FileOperationItemStatus.Completed, item.Status);
        Assert.IsNotNull(item.ActualTargetPath);
        Assert.IsTrue(File.Exists(sourceFile));
        Assert.IsTrue(File.Exists(item.ActualTargetPath));
        Assert.AreEqual("hello world", File.ReadAllText(item.ActualTargetPath));
    }

    [TestMethod]
    public async Task MoveSingleFileSuccessfully()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var sourceFile = temp.CreateFile("source.txt", "move me");
        var targetDir = temp.CreateDirectory("TargetSub");

        var result = await executor.MoveAsync([sourceFile], targetDir, [temp.RootPath]);

        Assert.AreEqual(1, result.TotalCount);
        Assert.AreEqual(1, result.SucceededCount, $"Status: {result.Items[0].Status}, Error: '{result.Items[0].Error}'");
        Assert.AreEqual(0, result.FailedCount);

        var item = result.Items[0];
        Assert.AreEqual(FileOperationItemStatus.Completed, item.Status);
        Assert.IsNotNull(item.ActualTargetPath);
        Assert.IsFalse(File.Exists(sourceFile));
        Assert.IsTrue(File.Exists(item.ActualTargetPath));
        Assert.AreEqual("move me", File.ReadAllText(item.ActualTargetPath));
    }

    [TestMethod]
    public async Task RenameSingleFileSuccessfully()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var sourceFile = temp.CreateFile("oldname.txt", "rename test");

        var item = await executor.RenameAsync(sourceFile, "newname.txt", [temp.RootPath]);

        Assert.AreEqual(FileOperationItemStatus.Completed, item.Status, $"Actual error: '{item.Error}', target: '{item.ActualTargetPath}'");
        Assert.IsNotNull(item.ActualTargetPath);
        Assert.IsFalse(File.Exists(sourceFile));
        Assert.IsTrue(File.Exists(item.ActualTargetPath));
        Assert.AreEqual("newname.txt", Path.GetFileName(item.ActualTargetPath));
        Assert.AreEqual("rename test", File.ReadAllText(item.ActualTargetPath));
    }

    [TestMethod]
    public async Task RenameWithInvalidCharactersThrowsArgumentException()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var sourceFile = temp.CreateFile("valid.txt", "data");

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
        {
            await executor.RenameAsync(sourceFile, "invalid/name.txt", [temp.RootPath]);
        });

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
        {
            await executor.RenameAsync(sourceFile, "invalid\\name.txt", [temp.RootPath]);
        });

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
        {
            await executor.RenameAsync(sourceFile, "   ", [temp.RootPath]);
        });
    }

    [TestMethod]
    public async Task CollisionPolicySkipDoesNotOverwriteExisting()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var sourceFile = temp.CreateFile("item.txt", "new content");
        var targetDir = temp.CreateDirectory("Dest");
        var existingTarget = Path.Combine(targetDir, "item.txt");
        File.WriteAllText(existingTarget, "original content");

        var result = await executor.CopyAsync(
            [sourceFile],
            targetDir,
            [temp.RootPath],
            collisionPolicy: FileCollisionPolicy.Skip);

        Assert.AreEqual(1, result.TotalCount);
        Assert.AreEqual(0, result.SucceededCount);
        Assert.AreEqual(1, result.SkippedCount);
        Assert.AreEqual(FileOperationItemStatus.Skipped, result.Items[0].Status);
        Assert.AreEqual("original content", File.ReadAllText(existingTarget));
    }

    [TestMethod]
    public async Task CollisionPolicyOverwriteReplacesTargetContent()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var sourceFile = temp.CreateFile("item.txt", "new content");
        var targetDir = temp.CreateDirectory("Dest");
        var existingTarget = Path.Combine(targetDir, "item.txt");
        File.WriteAllText(existingTarget, "original content");

        var result = await executor.CopyAsync(
            [sourceFile],
            targetDir,
            [temp.RootPath],
            collisionPolicy: FileCollisionPolicy.Overwrite);

        Assert.AreEqual(1, result.TotalCount);
        Assert.AreEqual(1, result.SucceededCount);
        Assert.AreEqual(0, result.SkippedCount);
        Assert.AreEqual("new content", File.ReadAllText(existingTarget));
    }

    [TestMethod]
    public async Task CollisionPolicyAutoRenameCreatesNewFileAndReturnsActualPath()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var sourceFile = temp.CreateFile("item.txt", "new content");
        var targetDir = temp.CreateDirectory("Dest");
        var existingTarget = Path.Combine(targetDir, "item.txt");
        File.WriteAllText(existingTarget, "original content");

        var result = await executor.CopyAsync(
            [sourceFile],
            targetDir,
            [temp.RootPath],
            collisionPolicy: FileCollisionPolicy.AutoRename);

        Assert.AreEqual(1, result.TotalCount);
        Assert.AreEqual(1, result.SucceededCount);
        var item = result.Items[0];
        Assert.AreEqual(FileOperationItemStatus.Completed, item.Status);
        Assert.IsNotNull(item.ActualTargetPath);
        Assert.AreNotEqual(existingTarget, item.ActualTargetPath);
        Assert.IsTrue(File.Exists(existingTarget));
        Assert.IsTrue(File.Exists(item.ActualTargetPath));
        Assert.AreEqual("original content", File.ReadAllText(existingTarget));
        Assert.AreEqual("new content", File.ReadAllText(item.ActualTargetPath));
    }

    [TestMethod]
    public async Task MixedBatchReportsAccurateItemStatus()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var validFile = temp.CreateFile("valid.txt", "valid content");
        var missingFile = Path.Combine(temp.RootPath, "non_existent.txt");
        var targetDir = temp.CreateDirectory("Dest");

        var result = await executor.CopyAsync([validFile, missingFile], targetDir, [temp.RootPath]);

        Assert.AreEqual(2, result.TotalCount);
        Assert.AreEqual(1, result.SucceededCount);
        Assert.AreEqual(1, result.FailedCount);

        var validResult = result.Items.First(i => i.SourcePath == validFile);
        var missingResult = result.Items.First(i => i.SourcePath == missingFile);

        Assert.AreEqual(FileOperationItemStatus.Completed, validResult.Status);
        Assert.AreEqual(FileOperationItemStatus.Failed, missingResult.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(missingResult.Error));
    }

    [TestMethod]
    public async Task ExternalSourceAllowedWhenDestinationIsInManagedRoot()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var externalDir = Path.Combine(Path.GetTempPath(), $"ExternalSource_{Guid.NewGuid():N}");
        Directory.CreateDirectory(externalDir);
        try
        {
            var externalFile = Path.Combine(externalDir, "external.txt");
            File.WriteAllText(externalFile, "external data");
            var targetDir = temp.CreateDirectory("Dest");

            var result = await executor.CopyAsync([externalFile], targetDir, [temp.RootPath]);

            Assert.AreEqual(1, result.TotalCount);
            Assert.AreEqual(1, result.SucceededCount);
            Assert.AreEqual(FileOperationItemStatus.Completed, result.Items[0].Status);
            Assert.IsTrue(File.Exists(result.Items[0].ActualTargetPath!));
            Assert.AreEqual("external data", File.ReadAllText(result.Items[0].ActualTargetPath!));
        }
        finally
        {
            if (Directory.Exists(externalDir))
            {
                Directory.Delete(externalDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task MoveDirectoryIntoSelfOrChildFailsGracefully()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var parentFolder = temp.CreateDirectory("ParentFolder");
        var subFolder = Path.Combine(parentFolder, "ChildFolder");
        Directory.CreateDirectory(subFolder);

        var result = await executor.MoveAsync([parentFolder], subFolder, [temp.RootPath]);

        Assert.AreEqual(1, result.TotalCount);
        Assert.AreEqual(0, result.SucceededCount);
        Assert.AreEqual(1, result.FailedCount);
        Assert.AreEqual(FileOperationItemStatus.Failed, result.Items[0].Status);
        Assert.IsNotNull(result.Items[0].Error);
        var error = result.Items[0].Error!;
        Assert.IsTrue(error.Contains("包含") || error.Contains("子目录"));
    }

    [TestMethod]
    public async Task CopyDirectoryIntoSelfOrChildFailsGracefully()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var parentFolder = temp.CreateDirectory("ParentFolderCopy");
        var subFolder = Path.Combine(parentFolder, "ChildFolder");
        Directory.CreateDirectory(subFolder);

        var result = await executor.CopyAsync([parentFolder], subFolder, [temp.RootPath]);

        Assert.AreEqual(1, result.TotalCount);
        Assert.AreEqual(0, result.SucceededCount);
        Assert.AreEqual(1, result.FailedCount);
        Assert.AreEqual(FileOperationItemStatus.Failed, result.Items[0].Status);
    }

    [TestMethod]
    public async Task ProgressCallbackReceivesUpdates()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var file1 = temp.CreateFile("f1.txt", "1");
        var file2 = temp.CreateFile("f2.txt", "2");
        var targetDir = temp.CreateDirectory("ProgressDest");

        var progressUpdates = new List<FileOperationProgress>();
        var result = await executor.CopyAsync(
            [file1, file2],
            targetDir,
            [temp.RootPath],
            progress: p => progressUpdates.Add(p));

        Assert.AreEqual(2, result.TotalCount);
        Assert.AreEqual(2, result.SucceededCount);
        Assert.IsGreaterThanOrEqualTo(progressUpdates.Count, 2);
        Assert.AreEqual(2, progressUpdates[^1].TotalItems);
        Assert.AreEqual(2, progressUpdates[^1].CompletedItems);
    }

    [TestMethod]
    public async Task CanceledTokenStopsExecution()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var file1 = temp.CreateFile("cancel1.txt", "data");
        var targetDir = temp.CreateDirectory("CancelDest");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
        {
            await executor.CopyAsync([file1], targetDir, [temp.RootPath], cancellationToken: cts.Token);
        });
    }

    [TestMethod]
    public async Task ReadOnlyFileIsCopiedSuccessfully()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var sourceFile = temp.CreateFile("readonly.txt", "read only data");
        File.SetAttributes(sourceFile, FileAttributes.ReadOnly);
        var targetDir = temp.CreateDirectory("ReadOnlyDest");

        try
        {
            var result = await executor.CopyAsync([sourceFile], targetDir, [temp.RootPath]);

            Assert.AreEqual(1, result.TotalCount);
            Assert.AreEqual(1, result.SucceededCount);
            Assert.AreEqual(FileOperationItemStatus.Completed, result.Items[0].Status);
            Assert.IsTrue(File.Exists(result.Items[0].ActualTargetPath!));
        }
        finally
        {
            File.SetAttributes(sourceFile, FileAttributes.Normal);
        }
    }

    [TestMethod]
    public async Task RenameWithCollisionPolicySkipReturnsSkipped()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var file1 = temp.CreateFile("file1.txt", "data1");
        var file2 = temp.CreateFile("file2.txt", "data2");

        var result = await executor.RenameAsync(
            file1,
            "file2.txt",
            [temp.RootPath],
            collisionPolicy: FileCollisionPolicy.Skip);

        Assert.AreEqual(FileOperationItemStatus.Skipped, result.Status);
        Assert.IsTrue(File.Exists(file1));
        Assert.IsTrue(File.Exists(file2));
        Assert.AreEqual("data1", File.ReadAllText(file1));
        Assert.AreEqual("data2", File.ReadAllText(file2));
    }

    [TestMethod]
    public async Task MoveWithCollisionPolicyOverwriteReplacesTarget()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var sourceFile = temp.CreateFile("move_overwrite.txt", "new moved data");
        var targetDir = temp.CreateDirectory("MoveDest");
        var existingTarget = Path.Combine(targetDir, "move_overwrite.txt");
        File.WriteAllText(existingTarget, "old target data");

        var result = await executor.MoveAsync(
            [sourceFile],
            targetDir,
            [temp.RootPath],
            collisionPolicy: FileCollisionPolicy.Overwrite);

        Assert.AreEqual(1, result.TotalCount);
        Assert.AreEqual(1, result.SucceededCount);
        Assert.IsFalse(File.Exists(sourceFile));
        Assert.IsTrue(File.Exists(existingTarget));
        Assert.AreEqual("new moved data", File.ReadAllText(existingTarget));
    }

    [TestMethod]
    public async Task MoveWithCollisionPolicySkipPreservesTarget()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var sourceFile = temp.CreateFile("move_skip.txt", "source data");
        var targetDir = temp.CreateDirectory("MoveSkipDest");
        var existingTarget = Path.Combine(targetDir, "move_skip.txt");
        File.WriteAllText(existingTarget, "target data");

        var result = await executor.MoveAsync(
            [sourceFile],
            targetDir,
            [temp.RootPath],
            collisionPolicy: FileCollisionPolicy.Skip);

        Assert.AreEqual(1, result.TotalCount);
        Assert.AreEqual(0, result.SucceededCount);
        Assert.AreEqual(1, result.SkippedCount);
        Assert.AreEqual(FileOperationItemStatus.Skipped, result.Items[0].Status);
        Assert.IsTrue(File.Exists(sourceFile));
        Assert.IsTrue(File.Exists(existingTarget));
        Assert.AreEqual("source data", File.ReadAllText(sourceFile));
        Assert.AreEqual("target data", File.ReadAllText(existingTarget));
    }

    [TestMethod]
    public async Task DeleteToRecycleBin_SingleFile_SuccessfullyDeletesToRecycleBin()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var fileName = $"delete_me_{Guid.NewGuid():N}.txt";
        var sourceFile = temp.CreateFile(fileName, "to be recycled");

        var result = await executor.DeleteToRecycleBinAsync([sourceFile], [temp.RootPath]);

        Assert.AreEqual(1, result.TotalCount);
        Assert.AreEqual(1, result.SucceededCount, $"Status: {result.Items[0].Status}, Error: '{result.Items[0].Error}'");
        Assert.AreEqual(0, result.FailedCount);
        Assert.IsFalse(result.IsCanceled);

        var item = result.Items[0];
        Assert.AreEqual(FileOperationItemStatus.Completed, item.Status);
        Assert.IsFalse(File.Exists(sourceFile));
        Assert.IsTrue(RecycleBinTestHelper.ExistsInRecycleBin(fileName, temp.RootPath), "Deleted file was not found in Recycle Bin.");

        temp.Dispose();
        Assert.IsFalse(RecycleBinTestHelper.ExistsInRecycleBin(fileName, temp.RootPath), "Deleted file was not cleaned from Recycle Bin after disposal.");
    }

    [TestMethod]
    public async Task DeleteToRecycleBin_MultipleFiles_SuccessfullyDeletesAll()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var name1 = $"del1_{Guid.NewGuid():N}.txt";
        var name2 = $"del2_{Guid.NewGuid():N}.txt";
        var name3 = $"del3_{Guid.NewGuid():N}.txt";
        var file1 = temp.CreateFile(name1, "content 1");
        var file2 = temp.CreateFile(name2, "content 2");
        var file3 = temp.CreateFile(name3, "content 3");

        var result = await executor.DeleteToRecycleBinAsync([file1, file2, file3], [temp.RootPath]);

        Assert.AreEqual(3, result.TotalCount);
        Assert.AreEqual(3, result.SucceededCount);
        Assert.AreEqual(0, result.FailedCount);

        Assert.IsFalse(File.Exists(file1));
        Assert.IsFalse(File.Exists(file2));
        Assert.IsFalse(File.Exists(file3));

        Assert.IsTrue(RecycleBinTestHelper.ExistsInRecycleBin(name1, temp.RootPath), "File 1 was not found in Recycle Bin.");
        Assert.IsTrue(RecycleBinTestHelper.ExistsInRecycleBin(name2, temp.RootPath), "File 2 was not found in Recycle Bin.");
        Assert.IsTrue(RecycleBinTestHelper.ExistsInRecycleBin(name3, temp.RootPath), "File 3 was not found in Recycle Bin.");

        temp.Dispose();
        Assert.IsFalse(RecycleBinTestHelper.ExistsInRecycleBin(name1, temp.RootPath));
        Assert.IsFalse(RecycleBinTestHelper.ExistsInRecycleBin(name2, temp.RootPath));
        Assert.IsFalse(RecycleBinTestHelper.ExistsInRecycleBin(name3, temp.RootPath));
    }

    [TestMethod]
    public async Task DeleteToRecycleBin_Directory_SuccessfullyDeletesDirectoryAndContents()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var dirName = $"DeleteDir_{Guid.NewGuid():N}";
        var dir = temp.CreateDirectory(dirName);
        var childName = $"child_{Guid.NewGuid():N}.txt";
        var childFile = temp.CreateFile(Path.Combine(dirName, childName), "child data");

        var result = await executor.DeleteToRecycleBinAsync([dir], [temp.RootPath]);

        Assert.AreEqual(1, result.TotalCount);
        Assert.AreEqual(1, result.SucceededCount);
        Assert.IsFalse(Directory.Exists(dir));
        Assert.IsFalse(File.Exists(childFile));
        Assert.IsTrue(RecycleBinTestHelper.ExistsInRecycleBin(dirName, temp.RootPath), "Deleted directory was not found in Recycle Bin.");

        temp.Dispose();
        Assert.IsFalse(RecycleBinTestHelper.ExistsInRecycleBin(dirName, temp.RootPath));
    }

    [TestMethod]
    public async Task DeleteToRecycleBin_NonExistentFile_FailsWithDescriptiveError()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var missingFile = Path.Combine(temp.RootPath, "non_existent.txt");

        var result = await executor.DeleteToRecycleBinAsync([missingFile], [temp.RootPath]);

        Assert.AreEqual(1, result.TotalCount);
        Assert.AreEqual(0, result.SucceededCount);
        Assert.AreEqual(1, result.FailedCount);
        Assert.AreEqual(FileOperationItemStatus.Failed, result.Items[0].Status);
        StringAssert.Contains(result.Items[0].Error, "不存在或无法访问");
    }

    [TestMethod]
    public async Task DeleteToRecycleBin_SourceOutsideManagedRoots_Fails()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var outsideDir = Path.Combine(Path.GetTempPath(), $"GuraFile_Outside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        try
        {
            var outsideFile = Path.Combine(outsideDir, "outside.txt");
            File.WriteAllText(outsideFile, "outside data");

            var result = await executor.DeleteToRecycleBinAsync([outsideFile], [temp.RootPath]);

            Assert.AreEqual(1, result.TotalCount);
            Assert.AreEqual(0, result.SucceededCount);
            Assert.AreEqual(1, result.FailedCount);
            StringAssert.Contains(result.Items[0].Error, "不在任何在线管理根目录范围内");
            Assert.IsTrue(File.Exists(outsideFile));
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
    public async Task DeleteToRecycleBin_ProgressCallback_ReceivesUpdates()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var name1 = $"prog1_{Guid.NewGuid():N}.txt";
        var name2 = $"prog2_{Guid.NewGuid():N}.txt";
        var file1 = temp.CreateFile(name1, "1");
        var file2 = temp.CreateFile(name2, "2");

        var progressUpdates = new List<FileOperationProgress>();
        var result = await executor.DeleteToRecycleBinAsync(
            [file1, file2],
            [temp.RootPath],
            progress: p => progressUpdates.Add(p));

        Assert.AreEqual(2, result.TotalCount);
        Assert.AreEqual(2, result.SucceededCount);
        Assert.IsGreaterThanOrEqualTo(progressUpdates.Count, 2);
    }

    [TestMethod]
    public void DeleteOperationFlags_ExplicitlyIncludesFofxRecycleOnDeleteAndAllowUndo()
    {
        var recycleOnDelete = (uint)Enum.Parse<FileOperationFlags>("FOFX_RECYCLEONDELETE");
        var allowUndo = (uint)Enum.Parse<FileOperationFlags>("FOF_ALLOWUNDO");
        Assert.AreEqual(0x00080000u, recycleOnDelete);
        Assert.AreEqual(0x0040u, allowUndo);

        var flags = SafeFileOperationExecutor.DeleteOperationFlags;
        Assert.AreNotEqual(0u, flags & (uint)FileOperationFlags.FOFX_RECYCLEONDELETE, "DeleteOperationFlags must include FOFX_RECYCLEONDELETE to prevent permanent deletion fallback.");
        Assert.AreNotEqual(0u, flags & (uint)FileOperationFlags.FOF_ALLOWUNDO, "DeleteOperationFlags must include FOF_ALLOWUNDO.");
    }

    [TestMethod]
    public async Task CleanupRecycleBinItem_WhenItemExists_RestoresAndPurgesSuccessfully()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var fileName = $"cleanup_file_{Guid.NewGuid():N}.txt";
        var sourceFile = temp.CreateFile(fileName, "content to clean up");

        var result = await executor.DeleteToRecycleBinAsync([sourceFile], [temp.RootPath]);
        Assert.AreEqual(1, result.SucceededCount);
        Assert.IsTrue(RecycleBinTestHelper.ExistsInRecycleBin(fileName, temp.RootPath), "File must be in Recycle Bin before cleanup.");

        var cleaned = RecycleBinTestHelper.CleanupRecycleBinItem(fileName, temp.RootPath);
        Assert.AreEqual(1, cleaned);
        Assert.IsFalse(RecycleBinTestHelper.ExistsInRecycleBin(fileName, temp.RootPath), "File must be removed from Recycle Bin after cleanup.");
    }

    [TestMethod]
    public async Task CleanupRecycleBinItemsForDirectory_AccuratelyPurgesRecycledItemsForDirectory()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var name1 = $"clean_dir_f1_{Guid.NewGuid():N}.txt";
        var name2 = $"clean_dir_f2_{Guid.NewGuid():N}.txt";
        var file1 = temp.CreateFile(name1, "content 1");
        var file2 = temp.CreateFile(name2, "content 2");

        var result = await executor.DeleteToRecycleBinAsync([file1, file2], [temp.RootPath]);
        Assert.AreEqual(2, result.SucceededCount);
        Assert.IsTrue(RecycleBinTestHelper.ExistsInRecycleBin(name1, temp.RootPath));
        Assert.IsTrue(RecycleBinTestHelper.ExistsInRecycleBin(name2, temp.RootPath));

        var cleaned = RecycleBinTestHelper.CleanupRecycleBinItemsForDirectory(temp.RootPath);
        Assert.AreEqual(2, cleaned, $"Expected 2 cleaned items, got {cleaned}");
        Assert.IsFalse(RecycleBinTestHelper.ExistsInRecycleBin(name1, temp.RootPath));
        Assert.IsFalse(RecycleBinTestHelper.ExistsInRecycleBin(name2, temp.RootPath));
    }

    [TestMethod]
    public async Task DeleteToRecycleBin_WhenFileLockedExclusively_ReportsFailureAndLeavesSourceFileIntact()
    {
        using var temp = TestEnvironment.Create();
        var executor = new SafeFileOperationExecutor();
        var fileName = $"locked_{Guid.NewGuid():N}.txt";
        const string originalContent = "critical locked file content that must remain intact";
        var sourceFile = temp.CreateFile(fileName, originalContent);

        using (var lockStream = File.Open(sourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await executor.DeleteToRecycleBinAsync([sourceFile], [temp.RootPath]);

            Assert.AreEqual(1, result.TotalCount);
            Assert.AreEqual(0, result.SucceededCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(FileOperationItemStatus.Failed, result.Items[0].Status);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Items[0].Error));
        }

        Assert.IsTrue(File.Exists(sourceFile), "Locked file must remain on disk.");
        Assert.AreEqual(originalContent, File.ReadAllText(sourceFile), "File content must not be modified or truncated.");
    }

    private sealed class TestEnvironment : IDisposable
    {
        public string RootPath { get; }

        private TestEnvironment(string rootPath)
        {
            RootPath = rootPath;
        }

        public static TestEnvironment Create()
        {
            var path = Path.Combine(Path.GetTempPath(), $"GuraFile_OpTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new(path);
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
                RecycleBinTestHelper.CleanupRecycleBinItemsForDirectory(RootPath);
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
