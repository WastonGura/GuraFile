using System.Xml.Linq;

[assembly: DoNotParallelize]

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
}
