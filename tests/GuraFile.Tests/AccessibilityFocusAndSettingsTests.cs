using System.Xml.Linq;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class AccessibilityFocusAndSettingsTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private string GetMainWindowXamlPath() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GuraFile", "MainWindow.xaml"));

    private string GetMainWindowCsPath() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GuraFile", "MainWindow.xaml.cs"));

    [TestMethod]
    public void MainWindow_AllInteractiveControlsHaveAutomationPropertiesName()
    {
        var xamlPath = GetMainWindowXamlPath();
        Assert.IsTrue(File.Exists(xamlPath), $"MainWindow.xaml not found: {xamlPath}");

        var doc = XDocument.Load(xamlPath);
        var interactiveTypes = new HashSet<string>
        {
            "Button", "TextBox", "ListView", "ComboBox", "ToggleSwitch", "CheckBox"
        };

        var elements = doc.Descendants()
            .Where(e => interactiveTypes.Contains(e.Name.LocalName))
            .ToList();

        Assert.IsGreaterThan(20, elements.Count, $"Should find all interactive controls in MainWindow. Found {elements.Count}");

        foreach (var element in elements)
        {
            var nameAttr = element.Attribute(XNs + "Name")?.Value ?? element.Attribute("Name")?.Value;
            var autoName = element.Attribute("AutomationProperties.Name")?.Value;

            // Skip controls inside DataTemplate or nested without a name if any
            if (element.Ancestors().Any(a => a.Name.LocalName == "DataTemplate"))
            {
                continue;
            }

            Assert.IsFalse(
                string.IsNullOrWhiteSpace(autoName),
                $"Control '{element.Name.LocalName}' (x:Name='{nameAttr}') must define AutomationProperties.Name for accessibility.");
        }
    }

    [TestMethod]
    public void MainWindow_InteractiveButtonsAndInputsHaveHelpText()
    {
        var xamlPath = GetMainWindowXamlPath();
        var doc = XDocument.Load(xamlPath);
        var inputTypes = new HashSet<string> { "Button", "TextBox" };

        var elements = doc.Descendants()
            .Where(e => inputTypes.Contains(e.Name.LocalName) && !e.Ancestors().Any(a => a.Name.LocalName == "DataTemplate"))
            .ToList();

        foreach (var element in elements)
        {
            var nameAttr = element.Attribute(XNs + "Name")?.Value;
            if (nameAttr == "DatabaseNoticeActionButton" || nameAttr == "SortNameButton" || nameAttr == "SortPathButton" ||
                nameAttr == "SortExtensionButton" || nameAttr == "SortSizeButton" || nameAttr == "SortModifiedButton")
            {
                // Sort and dynamic action buttons may have concise help or specific tags
            }

            var helpText = element.Attribute("AutomationProperties.HelpText")?.Value;
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(helpText),
                $"Interactive control '{element.Name.LocalName}' (x:Name='{nameAttr}') should define AutomationProperties.HelpText.");
        }
    }

    [TestMethod]
    public void MainWindow_DynamicStatusControlsHaveLiveSetting()
    {
        var xamlPath = GetMainWindowXamlPath();
        var doc = XDocument.Load(xamlPath);

        var statusControlNames = new[]
        {
            "DatabaseNoticeBar",
            "FileOperationRecoveryNoticeBar",
            "ProgressText",
            "FilesStateText",
            "TagStatusText",
            "ViewStatusText",
            "FileActionStatusText"
        };

        foreach (var controlName in statusControlNames)
        {
            var element = doc.Descendants().FirstOrDefault(e => e.Attribute(XNs + "Name")?.Value == controlName);
            Assert.IsNotNull(element, $"Dynamic status control '{controlName}' must exist in MainWindow.xaml");

            var liveSetting = element.Attribute("AutomationProperties.LiveSetting")?.Value;
            Assert.IsTrue(
                liveSetting is "Polite" or "Assertive",
                $"Status control '{controlName}' must define AutomationProperties.LiveSetting as 'Polite' or 'Assertive'. Actual: '{liveSetting}'");
        }
    }

    [TestMethod]
    public void MainWindow_DefinesPredictableTabIndicesAcrossKeyControls()
    {
        var xamlPath = GetMainWindowXamlPath();
        var doc = XDocument.Load(xamlPath);

        var keyControls = new[]
        {
            "RootsList", "AddRootButton", "TagNameBox", "TagsList", "ViewNameBox", "SavedFilterViewsList",
            "SearchBox", "ViewModeBox", "SettingsButton", "FilesList", "OpenFileButton"
        };

        foreach (var name in keyControls)
        {
            var element = doc.Descendants().FirstOrDefault(e => e.Attribute(XNs + "Name")?.Value == name);
            Assert.IsNotNull(element, $"Key control '{name}' must exist in MainWindow.xaml");

            var tabIndex = element.Attribute("TabIndex")?.Value;
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(tabIndex),
                $"Key control '{name}' must have an explicit TabIndex for predictable keyboard navigation.");
        }
    }

    [TestMethod]
    public void MainWindow_ExposesSettingsButtonWithAccessibility()
    {
        var xamlPath = GetMainWindowXamlPath();
        var doc = XDocument.Load(xamlPath);

        var settingsButton = doc.Descendants().FirstOrDefault(e => e.Attribute(XNs + "Name")?.Value == "SettingsButton");
        Assert.IsNotNull(settingsButton, "SettingsButton must exist in MainWindow.xaml");
        Assert.AreEqual("设置", settingsButton.Attribute("AutomationProperties.Name")?.Value);
        Assert.IsFalse(string.IsNullOrWhiteSpace(settingsButton.Attribute("AutomationProperties.HelpText")?.Value));
        Assert.AreEqual("SettingsButton_Click", settingsButton.Attribute("Click")?.Value);
    }

    [TestMethod]
    public void MainWindow_CodeBehind_ImplementsFocusTrapPreventionWhenSwitchingViews()
    {
        var csPath = GetMainWindowCsPath();
        var code = File.ReadAllText(csPath);

        StringAssert.Contains(code, "FilesList.IsTabStop", "Code-behind must manage FilesList.IsTabStop when toggling views");
        StringAssert.Contains(code, "GraphWebView.IsTabStop", "Code-behind must manage GraphWebView.IsTabStop when toggling views");
    }

    [TestMethod]
    public void MainWindow_CodeBehind_RestoresFocusToSettingsButtonOnDialogClose()
    {
        var csPath = GetMainWindowCsPath();
        var code = File.ReadAllText(csPath);

        StringAssert.Contains(code, "SettingsButton.Focus(FocusState.Programmatic)",
            "Settings dialog handler must restore focus precisely to SettingsButton upon closure");
    }

    [TestMethod]
    public void MainWindow_CodeBehind_SettingsDialogContainsOnlyActiveConfigsAndNoPlaceholders()
    {
        var csPath = GetMainWindowCsPath();
        var code = File.ReadAllText(csPath);

        StringAssert.Contains(code, "UserSettingsService", "MainWindow must use UserSettingsService");
        StringAssert.Contains(code, "RollingBackupRetainCount", "Settings dialog must configure RollingBackupRetainCount");
        StringAssert.Contains(code, "DiagnosticExportAnonymizePaths", "Settings dialog must configure DiagnosticExportAnonymizePaths");
        StringAssert.Contains(code, "AppTheme", "Settings dialog must configure AppTheme");
        StringAssert.Contains(code, "MinLogLevel", "Settings dialog must configure MinLogLevel");

        // Verify no placeholder terms in settings implementation
        var settingsStart = code.IndexOf("private async void SettingsButton_Click", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, settingsStart, "SettingsButton_Click must exist");
        var settingsEnd = code.IndexOf("private ", settingsStart + 20, StringComparison.Ordinal);
        var settingsMethod = settingsEnd > settingsStart ? code[settingsStart..settingsEnd] : code[settingsStart..];

        Assert.IsFalse(settingsMethod.Contains("IsEnabled = false", StringComparison.OrdinalIgnoreCase),
            "Settings dialog must not contain disabled placeholder controls");
        Assert.IsFalse(settingsMethod.Contains("未实现", StringComparison.OrdinalIgnoreCase),
            "Settings dialog must not contain unimplemented placeholder notes");
        Assert.IsFalse(settingsMethod.Contains("敬请期待", StringComparison.OrdinalIgnoreCase),
            "Settings dialog must not contain coming soon placeholders");
    }

    [TestMethod]
    public void MainWindow_CodeBehind_ImmediateThemeSwitchingSupported()
    {
        var csPath = GetMainWindowCsPath();
        var code = File.ReadAllText(csPath);

        StringAssert.Contains(code, "RequestedTheme", "MainWindow must update FrameworkElement.RequestedTheme dynamically");
    }
}
