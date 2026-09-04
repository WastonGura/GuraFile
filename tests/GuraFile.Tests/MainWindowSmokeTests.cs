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
        StringAssert.Contains(source, "_fileChanges.Start(root)");
        var roots = document.Descendants().Single(element => element.Attribute(x + "Name")?.Value == "RootsList");
        Assert.AreEqual("DisplayName", roots.Attribute("DisplayMemberPath")?.Value);
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
            new[] { "TagsList", "AutomaticTagsList", "TagNameBox", "CreateTagButton", "RenameTagButton", "DeleteTagButton", "ApplyTagButton", "RemoveTagButton", "TagFilterToggle", "TagMatchBox", "ExportTagsButton", "ImportTagsButton", "BackupNowButton", "RollingBackupsButton", "TagStatusText", "ReidentifyTypeButton" },
            names.ToArray());
        var tagsList = document.Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "TagsList");
        Assert.AreEqual("Extended", tagsList.Attribute("SelectionMode")?.Value);
        var automaticTags = document.Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "AutomaticTagsList");
        Assert.AreEqual("Extended", automaticTags.Attribute("SelectionMode")?.Value);
        Assert.AreEqual("自动标签（只读）", automaticTags.Attribute("Header")?.Value);
        var source = File.ReadAllText(Path.ChangeExtension(path, ".xaml.cs"));
        StringAssert.Contains(source, "_rollingBackup");
        StringAssert.Contains(source, "BackupNowButton_Click");
        StringAssert.Contains(source, "RollingBackupsButton_Click");
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
    public void MainWindowExposesFileListOperationsControls()
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
            new[]
            {
                "CopyFileButton",
                "CutFileButton",
                "PasteToFileButton",
                "MoveToFileButton",
                "RenameFileButton",
                "CollisionPolicyBox",
                "CancelFileOperationButton",
                "FileOperationProgressRing"
            },
            names.ToArray());

        var source = File.ReadAllText(Path.ChangeExtension(path, ".xaml.cs"));
        StringAssert.Contains(source, "_fileOperations.CopyToClipboard");
        StringAssert.Contains(source, "_fileOperations.CutToClipboard");
        StringAssert.Contains(source, "_fileOperations.PasteFromClipboardAsync");
        StringAssert.Contains(source, "_fileOperations.MoveToAsync");
        StringAssert.Contains(source, "_fileOperations.RenameAsync");
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

    [TestMethod]
    public void MainWindowExposesGraphHostControlsAndSafeHandlers()
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
            new[]
            {
                "ViewModeBox",
                "GraphHostContainer",
                "GraphWebView",
                "FitViewportButton",
                "BroadTagsCheckBox",
                "GraphLoadingRing",
                "GraphMessagePanel",
                "GraphStateText",
                "GraphInfoText"
            },
            names.ToArray());

        var source = File.ReadAllText(Path.ChangeExtension(path, ".xaml.cs"));
        StringAssert.Contains(source, "SetVirtualHostNameToFolderMapping");
        StringAssert.Contains(source, "GraphSecurityPolicy.VirtualHostName");
        StringAssert.Contains(source, "GraphSecurityPolicy.IsAllowedUri");
        StringAssert.Contains(source, "PostWebMessageAsJson");
        StringAssert.Contains(source, "NewWindowRequested");
        StringAssert.Contains(source, "DownloadStarting");
        StringAssert.Contains(source, "NavigationStarting");
        StringAssert.Contains(source, "GraphMessageTypes.NodeSelected");
        StringAssert.Contains(source, "GraphMessageTypes.NodeActivated");
        StringAssert.Contains(source, "GraphMessageSerializer.SerializeSelectNode");
        StringAssert.Contains(source, "_graphInteractionCoordinator.EvaluateSelection");
        StringAssert.Contains(source, "_graphInteractionCoordinator.EvaluateActivation");
        StringAssert.Contains(source, "RunFileAction(activation.File!, _shell.Open");
    }

    [TestMethod]
    public void MainWindowExposesGraphBoxSelectionAndBatchTaggingHandlers()
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
            new[] { "GraphWebView", "FilesList", "ApplyTagButton", "RemoveTagButton" },
            names.ToArray());

        var source = File.ReadAllText(Path.ChangeExtension(path, ".xaml.cs"));
        StringAssert.Contains(source, "GraphMessageTypes.SelectionChanged");
        StringAssert.Contains(source, "_graphInteractionCoordinator.EvaluateBatchSelection");
        StringAssert.Contains(source, "GraphMessageSerializer.SerializeSetSelection");
        StringAssert.Contains(source, "GraphMessageSerializer.ParseSelectionChanged");
        StringAssert.Contains(source, "_isSyncingSelectionFromGraph");
        StringAssert.Contains(source, "_tags.AddTagToFiles");
        StringAssert.Contains(source, "_tags.RemoveTagFromFiles");
        StringAssert.Contains(source, "_tags.ListCommonUserTagsForFiles");
    }

    [TestMethod]
    public void MainWindowExposesDiagnosticExportControls()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GuraFile", "MainWindow.xaml"));
        var document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var names = document.Descendants()
            .Select(element => element.Attribute(x + "Name")?.Value)
            .Where(name => name is not null)
            .ToHashSet();

        Assert.Contains("ExportDiagnosticsButton", names);

        var source = File.ReadAllText(Path.ChangeExtension(path, ".xaml.cs"));
        StringAssert.Contains(source, "ExportDiagnosticsButton_Click");
        StringAssert.Contains(source, "DiagnosticExportService");
        StringAssert.Contains(source, "DiagnosticLogger.Default");
    }

    [TestMethod]
    public void MainWindowLogsGraphHostAndBackupDiagnostics()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GuraFile", "MainWindow.xaml.cs"));
        var source = File.ReadAllText(path);

        StringAssert.Contains(source, "DiagnosticCategory.GraphHost");
        StringAssert.Contains(source, "WebViewInitializationStarted");
        StringAssert.Contains(source, "NavigationBlocked");
        StringAssert.Contains(source, "NewWindowBlocked");
        StringAssert.Contains(source, "DownloadBlocked");
        StringAssert.Contains(source, "GraphRefreshStarted");
        StringAssert.Contains(source, "GraphSnapshotEmpty");
        StringAssert.Contains(source, "GraphLimitExceeded");
        StringAssert.Contains(source, "GraphSnapshotRendered");
        StringAssert.Contains(source, "NodeActivationRejected");
        StringAssert.Contains(source, "WebGraphError");
        StringAssert.Contains(source, "WebMessageHandlingError");

        StringAssert.Contains(source, "DiagnosticCategory.Backup");
        StringAssert.Contains(source, "TagExportStarted");
        StringAssert.Contains(source, "TagExportCompleted");
        StringAssert.Contains(source, "TagImportStarted");
        StringAssert.Contains(source, "TagImportCompleted");
        StringAssert.Contains(source, "ManualBackupRequested");
        StringAssert.Contains(source, "RollingBackupDialogOpened");
        StringAssert.Contains(source, "RollingBackupRestoreRequested");
    }
}
