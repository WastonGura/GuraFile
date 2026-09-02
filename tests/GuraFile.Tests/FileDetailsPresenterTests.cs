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
        Assert.AreEqual("路径降级模式 (未绑定稳定 ID)", model.IdentityStateText);
        Assert.AreEqual("文件不可访问", model.Diagnostic);
        Assert.IsFalse(model.CanOpen);
        Assert.IsFalse(model.CanReveal);
        Assert.IsFalse(model.CanReidentify);
        Assert.IsTrue(model.CanCopyPath);
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
}
