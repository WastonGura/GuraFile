using System.Xml.Linq;

namespace GuraFile.Tests;

[TestClass]
public sealed class MainWindowSmokeTests
{
    [TestMethod]
    public void MainWindowHasGuraFileTitle()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GuraFile", "MainWindow.xaml"));

        Assert.IsTrue(File.Exists(path), $"Missing main window: {path}");

        var window = XDocument.Load(path).Root;
        Assert.IsNotNull(window);
        Assert.AreEqual("Window", window.Name.LocalName);
        Assert.AreEqual("GuraFile", window.Attribute("Title")?.Value);
    }

    [TestMethod]
    public void MainWindowHasManagedRootScanningControls()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GuraFile", "MainWindow.xaml"));
        var document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var columns = document.Descendants().First(element => element.Name.LocalName == "Grid.ColumnDefinitions");
        Assert.HasCount(3, columns.Elements());

        var names = document.Descendants()
            .Select(element => element.Attribute(x + "Name")?.Value)
            .Where(name => name is not null)
            .ToHashSet();
        CollectionAssert.IsSubsetOf(
            new[] { "RootsList", "AddRootButton", "RemoveRootButton", "ScanButton", "CancelButton", "ProgressText", "FailureList" },
            names.ToArray());
        var source = File.ReadAllText(Path.ChangeExtension(path, ".xaml.cs"));
        StringAssert.Contains(source, "FileChangeCoordinator");
        StringAssert.Contains(source, "await Task.Run(() => _scanner.AddRoot(folder.Path))");
        StringAssert.Contains(source, "await Task.Run(() => _scanner.RemoveRoot(root.Id))");
    }

    [TestMethod]
    public void FileListUsesBoundedNativeVirtualizationAndExtendedSelection()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GuraFile", "MainWindow.xaml"));
        var document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var filesList = document.Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "FilesList");

        Assert.AreEqual("Extended", filesList.Attribute("SelectionMode")?.Value);
        Assert.IsTrue(filesList.Descendants().Any(element => element.Name.LocalName == "ItemsStackPanel"));
        Assert.IsFalse(document.Descendants().Any(element => element.Name.LocalName == "ScrollViewer"));
        Assert.AreEqual("3", filesList.Attribute("Grid.Row")?.Value);

        var names = document.Descendants()
            .Select(element => element.Attribute(x + "Name")?.Value)
            .Where(name => name is not null)
            .ToHashSet();
        CollectionAssert.IsSubsetOf(
            new[] { "SearchBox", "FilesStateText", "DetailsText", "SortNameButton", "SortPathButton", "SortExtensionButton", "SortSizeButton", "SortModifiedButton" },
            names.ToArray());
    }

    [TestMethod]
    public void MainWindowExposesTagManagementAndFilteringControls()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GuraFile", "MainWindow.xaml"));
        var document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var names = document.Descendants()
            .Select(element => element.Attribute(x + "Name")?.Value)
            .Where(name => name is not null)
            .ToHashSet();

        CollectionAssert.IsSubsetOf(
            new[] { "TagsList", "AutomaticTagsList", "TagNameBox", "CreateTagButton", "RenameTagButton", "DeleteTagButton", "ApplyTagButton", "RemoveTagButton", "TagFilterToggle", "TagMatchBox", "ExportTagsButton", "ImportTagsButton", "TagStatusText", "ReidentifyTypeButton" },
            names.ToArray());
        var tagsList = document.Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "TagsList");
        Assert.AreEqual("Extended", tagsList.Attribute("SelectionMode")?.Value);
        var automaticTags = document.Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "AutomaticTagsList");
        Assert.AreEqual("Extended", automaticTags.Attribute("SelectionMode")?.Value);
        Assert.AreEqual("自动标签（只读）", automaticTags.Attribute("Header")?.Value);
    }

    [TestMethod]
    public void MainWindowExposesStructuredShellActions()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GuraFile", "MainWindow.xaml"));
        var document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var names = document.Descendants()
            .Select(element => element.Attribute(x + "Name")?.Value)
            .Where(name => name is not null)
            .ToHashSet();

        CollectionAssert.IsSubsetOf(
            new[] { "OpenFileButton", "RevealFileButton", "FileActionStatusText" },
            names.ToArray());
        var filesList = document.Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "FilesList");
        Assert.AreEqual("FilesList_DoubleTapped", filesList.Attribute("DoubleTapped")?.Value);

        var source = File.ReadAllText(Path.ChangeExtension(path, ".xaml.cs"));
        var handlerStart = source.IndexOf("private void FilesList_DoubleTapped", StringComparison.Ordinal);
        var handlerEnd = source.IndexOf("private void OpenFileButton_Click", handlerStart, StringComparison.Ordinal);
        var handler = source[handlerStart..handlerEnd];
        StringAssert.Contains(handler, "FindListViewItem(e.OriginalSource as DependencyObject)?.Content is not IndexedFile file");
        StringAssert.Contains(handler, "return;");
        StringAssert.Contains(handler, "RunFileAction(file, _shell.Open");
        Assert.AreEqual(1, handler.Split("RunFileAction", StringSplitOptions.None).Length - 1);
        Assert.IsFalse(handler.Contains("RunSelectedFileAction", StringComparison.Ordinal));
        StringAssert.Contains(source, "VisualTreeHelper.GetParent(source)");
    }

    [TestMethod]
    public void AddRootHandlerReportsFailuresAtTheUiBoundary()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GuraFile", "MainWindow.xaml.cs"));
        var source = File.ReadAllText(path);
        var start = source.IndexOf("private async void AddRootButton_Click", StringComparison.Ordinal);
        var end = source.IndexOf("private async void RemoveRootButton_Click", start, StringComparison.Ordinal);
        var handler = source[start..end];

        StringAssert.Contains(handler, "catch (Exception exception)");
        StringAssert.Contains(handler, "ProgressText.Text = \"添加根目录失败\"");
        StringAssert.Contains(handler, "FailureList.ItemsSource = new[] { exception.Message }");
    }
}
