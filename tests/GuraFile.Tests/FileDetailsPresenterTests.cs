using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class FileDetailsPresenterTests
{
    [TestMethod]
    public void Create_EmptySelection_ReturnsUnselectedState()
    {
        var model = FileDetailsPresenter.Create([], [], []);

        Assert.AreEqual("未选择文件", model.Title);
        Assert.IsFalse(model.IsSingleFileSelected);
        Assert.IsFalse(model.IsMultipleFilesSelected);
        Assert.IsFalse(model.CanOpen);
        Assert.IsFalse(model.CanReveal);
        Assert.IsFalse(model.CanReidentify);
        Assert.IsFalse(model.CanCopyPath);
    }

    [TestMethod]
    public void Create_SingleOnlineFile_WithStableIdentity_ReturnsCompleteDetails()
    {
        var file = new IndexedFile(
            Id: 1,
            Name: "Doc.txt",
            Path: @"C:\Data\Doc.txt",
            Extension: ".txt",
            Size: 1024,
            Modified: DateTimeOffset.UtcNow,
            IsOnline: true,
            Diagnostic: null,
            IdentityKind: "stable");

        var userTags = new[] { new UserTag(1, "Important") };
        var autoTags = new[] { new AutomaticTag(2, "TextDocument") };

        var model = FileDetailsPresenter.Create([file], userTags, autoTags);

        Assert.IsTrue(model.IsSingleFileSelected);
        Assert.IsFalse(model.IsMultipleFilesSelected);
        Assert.AreEqual("Doc.txt", model.Name);
        Assert.AreEqual(@"C:\Data\Doc.txt", model.Path);
        Assert.AreEqual(".txt", model.Extension);
        Assert.AreEqual("1,024 字节", model.SizeText);
        Assert.AreEqual("在线", model.StatusText);
        Assert.AreEqual("稳定身份已绑定", model.IdentityStateText);
        Assert.AreEqual("Important", model.UserTagsText);
        Assert.AreEqual("TextDocument", model.AutomaticTagsText);
        Assert.IsTrue(model.CanOpen);
        Assert.IsTrue(model.CanReveal);
        Assert.IsTrue(model.CanReidentify);
        Assert.IsTrue(model.CanCopyPath);
    }

    [TestMethod]
    public void Create_SingleOfflineFile_DisablesOpenAndReidentify()
    {
        var file = new IndexedFile(
            Id: 2,
            Name: "Missing.pdf",
            Path: @"D:\Data\Missing.pdf",
            Extension: ".pdf",
            Size: 2048,
            Modified: DateTimeOffset.UtcNow,
            IsOnline: false,
            Diagnostic: "文件不可访问",
            IdentityKind: "path");

        var model = FileDetailsPresenter.Create([file], [], []);

        Assert.IsTrue(model.IsSingleFileSelected);
        Assert.AreEqual("离线", model.StatusText);
        Assert.AreEqual("⚠️ 身份跟踪有限：当前介质不支持底层稳定文件 ID。同路径原地修改可保留标签；跨目录移动或重命名时可能需要重新关联标签。", model.IdentityStateText);
        Assert.AreEqual("文件不可访问", model.Diagnostic);
        Assert.IsFalse(model.CanOpen);
        Assert.IsFalse(model.CanReveal);
        Assert.IsFalse(model.CanReidentify);
        Assert.IsTrue(model.CanCopyPath);
    }

    [TestMethod]
    public void Create_FileInOfflineRoot_SetsRootOfflineNoticeAndDisablesActions()
    {
        var file = new IndexedFile(
            Id: 3,
            Name: "Keep.txt",
            Path: @"E:\Drive\Keep.txt",
            Extension: ".txt",
            Size: 100,
            Modified: DateTimeOffset.UtcNow,
            IsOnline: true,
            Diagnostic: null,
            IdentityKind: "stable");

        var model = FileDetailsPresenter.Create([file], [], [], isRootOffline: true);

        Assert.IsTrue(model.IsSingleFileSelected);
        Assert.IsTrue(model.IsRootOffline);
        Assert.AreEqual("管理根目录当前离线，文件与标签已妥善保留，等待介质重新连接", model.RootOfflineNotice);
        Assert.IsFalse(model.CanOpen);
        Assert.IsFalse(model.CanReveal);
        Assert.IsFalse(model.CanReidentify);
    }

    [TestMethod]
    public void ManagedRoot_DisplayName_ReflectsDegradationStates()
    {
        var stableCap = new StorageCapability(StorageMediumKind.Fixed, "NTFS", SupportsStableFileId: true, IsReparsePoint: false, "本地固定盘 (NTFS) - 支持稳定身份跟踪");
        var limitedCap = new StorageCapability(StorageMediumKind.Network, "SMB", SupportsStableFileId: false, IsReparsePoint: false, "网络共享 (SMB) - 身份跟踪受限（路径降级）");

        var onlineStableRoot = new ManagedRoot(1, @"C:\Repo", ManagedRootStatus.Online, Capability: stableCap);
        Assert.AreEqual(@"C:\Repo  [在线 · NTFS]", onlineStableRoot.DisplayName);

        var onlineLimitedRoot = new ManagedRoot(2, @"\\server\share", ManagedRootStatus.Online, Capability: limitedCap);
        Assert.AreEqual(@"\\server\share  [在线 · 身份跟踪有限]", onlineLimitedRoot.DisplayName);

        var offlineRoot = new ManagedRoot(3, @"E:\Removable", ManagedRootStatus.Offline, Capability: limitedCap);
        Assert.AreEqual(@"E:\Removable  [离线]", offlineRoot.DisplayName);

        var recoveringRoot = new ManagedRoot(4, @"E:\Removable", ManagedRootStatus.Recovering, Capability: limitedCap);
        Assert.AreEqual(@"E:\Removable  [正在恢复]", recoveringRoot.DisplayName);
    }

    [TestMethod]
    public void Create_MultipleFiles_ShowsCountSummaryAndDisablesSingleActions()
    {
        var file1 = new IndexedFile(1, "1.txt", @"C:\Data\1.txt", ".txt", 10, DateTimeOffset.UtcNow, true, null);
        var file2 = new IndexedFile(2, "2.txt", @"C:\Data\2.txt", ".txt", 20, DateTimeOffset.UtcNow, true, null);

        var model = FileDetailsPresenter.Create([file1, file2], [], []);

        Assert.IsFalse(model.IsSingleFileSelected);
        Assert.IsTrue(model.IsMultipleFilesSelected);
        Assert.AreEqual(2, model.SelectedCount);
        Assert.AreEqual("已选择 2 个文件", model.Title);
        Assert.IsNull(model.Name);
        Assert.IsNull(model.Path);
        Assert.IsFalse(model.CanOpen);
        Assert.IsFalse(model.CanReveal);
        Assert.IsFalse(model.CanReidentify);
        Assert.IsFalse(model.CanCopyPath);
    }

    [TestMethod]
    public void Create_MultipleFiles_WithCommonUserTags_PresentsCommonTags()
    {
        var file1 = new IndexedFile(1, "1.txt", @"C:\Data\1.txt", ".txt", 10, DateTimeOffset.UtcNow, true, null);
        var file2 = new IndexedFile(2, "2.txt", @"C:\Data\2.txt", ".txt", 20, DateTimeOffset.UtcNow, true, null);

        var commonUserTags = new[] { new UserTag(1, "工作"), new UserTag(2, "重要") };
        var model = FileDetailsPresenter.Create([file1, file2], commonUserTags, []);

        Assert.IsFalse(model.IsSingleFileSelected);
        Assert.IsTrue(model.IsMultipleFilesSelected);
        Assert.AreEqual(2, model.SelectedCount);
        Assert.AreEqual("已选择 2 个文件", model.Title);
        Assert.AreEqual("工作、重要", model.UserTagsText);
    }

    [TestMethod]
    public void CreateForTag_PresentsTagDetailsAndDisablesAllFileActions()
    {
        var model = FileDetailsPresenter.CreateForTag("工作", "用户标签");

        Assert.AreEqual("标签：工作", model.Title);
        Assert.AreEqual("工作", model.Name);
        Assert.IsNull(model.Path);
        Assert.IsNull(model.Extension);
        Assert.AreEqual("标签", model.StatusText);
        Assert.AreEqual("用户标签", model.IdentityStateText);
        Assert.IsTrue(model.IsTagSelected);
        Assert.AreEqual("用户标签", model.TagTypeSummary);
        Assert.IsFalse(model.IsSingleFileSelected);
        Assert.IsFalse(model.IsMultipleFilesSelected);
        Assert.IsFalse(model.CanOpen);
        Assert.IsFalse(model.CanReveal);
        Assert.IsFalse(model.CanReidentify);
        Assert.IsFalse(model.CanCopyPath);
    }
}
