using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class UserSettingsServiceTests
{
    private string _tempDir = null!;
    private string _settingsPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GuraFile_SettingsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [TestMethod]
    public void AppPaths_DefinesAppDataDirectoryAndDefaultSettingsPath()
    {
        var appData = AppPaths.AppDataDirectory;
        Assert.IsFalse(string.IsNullOrWhiteSpace(appData));
        Assert.AreEqual(AppPaths.DefaultUserDataDirectory, appData);

        var settingsPath = AppPaths.DefaultSettingsPath;
        Assert.IsFalse(string.IsNullOrWhiteSpace(settingsPath));
        Assert.AreEqual(Path.Combine(appData, "settings.json"), settingsPath);
    }

    [TestMethod]
    public void UserSettings_HasSafeExpectedDefaults()
    {
        var settings = new UserSettings();

        Assert.AreEqual(7, settings.RollingBackupRetainCount);
        Assert.IsTrue(settings.DiagnosticExportAnonymizePaths);
        Assert.AreEqual("Default", settings.AppTheme);
        Assert.AreEqual("Info", settings.MinLogLevel);
        Assert.AreEqual(DiagnosticLogLevel.Info, settings.ToDiagnosticLogLevel());
    }

    [TestMethod]
    public void UserSettings_ClampsRollingBackupRetainCountToValidRange()
    {
        var settings = new UserSettings();

        settings.RollingBackupRetainCount = 0;
        Assert.AreEqual(1, settings.RollingBackupRetainCount, "Should clamp below 1 to 1");

        settings.RollingBackupRetainCount = -10;
        Assert.AreEqual(1, settings.RollingBackupRetainCount);

        settings.RollingBackupRetainCount = 100;
        Assert.AreEqual(30, settings.RollingBackupRetainCount, "Should clamp above 30 to 30");

        settings.RollingBackupRetainCount = 14;
        Assert.AreEqual(14, settings.RollingBackupRetainCount);
    }

    [TestMethod]
    public void UserSettings_NormalizesThemeAndLogLevel()
    {
        var settings = new UserSettings();

        settings.AppTheme = "dark";
        Assert.AreEqual("Dark", settings.AppTheme);

        settings.AppTheme = "LIGHT";
        Assert.AreEqual("Light", settings.AppTheme);

        settings.AppTheme = "InvalidTheme";
        Assert.AreEqual("Default", settings.AppTheme);

        settings.MinLogLevel = "debug";
        Assert.AreEqual("Debug", settings.MinLogLevel);
        Assert.AreEqual(DiagnosticLogLevel.Debug, settings.ToDiagnosticLogLevel());

        settings.MinLogLevel = "warn";
        Assert.AreEqual("Warning", settings.MinLogLevel);
        Assert.AreEqual(DiagnosticLogLevel.Warn, settings.ToDiagnosticLogLevel());

        settings.MinLogLevel = "warning";
        Assert.AreEqual("Warning", settings.MinLogLevel);

        settings.MinLogLevel = "error";
        Assert.AreEqual("Error", settings.MinLogLevel);
        Assert.AreEqual(DiagnosticLogLevel.Error, settings.ToDiagnosticLogLevel());

        settings.MinLogLevel = "Verbose";
        Assert.AreEqual("Info", settings.MinLogLevel);
        Assert.AreEqual(DiagnosticLogLevel.Info, settings.ToDiagnosticLogLevel());
    }

    [TestMethod]
    public void Load_WhenFileDoesNotExist_ReturnsDefaultSettingsWithoutCreatingFile()
    {
        var service = new UserSettingsService(_settingsPath);
        Assert.IsFalse(File.Exists(_settingsPath));

        var settings = service.Load();

        Assert.IsNotNull(settings);
        Assert.AreEqual(7, settings.RollingBackupRetainCount);
        Assert.IsTrue(settings.DiagnosticExportAnonymizePaths);
        Assert.AreEqual("Default", settings.AppTheme);
        Assert.AreEqual("Info", settings.MinLogLevel);
    }

    [TestMethod]
    public void Save_AndLoad_PreservesAllConfiguredValues()
    {
        var service = new UserSettingsService(_settingsPath);
        var original = new UserSettings
        {
            RollingBackupRetainCount = 15,
            DiagnosticExportAnonymizePaths = false,
            AppTheme = "Dark",
            MinLogLevel = "Debug"
        };

        var saved = service.Save(original);
        Assert.IsTrue(saved, "Save must succeed");
        Assert.IsTrue(File.Exists(_settingsPath));

        var loaded = service.Load();
        Assert.AreEqual(15, loaded.RollingBackupRetainCount);
        Assert.IsFalse(loaded.DiagnosticExportAnonymizePaths);
        Assert.AreEqual("Dark", loaded.AppTheme);
        Assert.AreEqual("Debug", loaded.MinLogLevel);
    }

    [TestMethod]
    public void Load_WhenJsonCorrupted_FallsBackToSafeDefaultsAndDoesNotThrow()
    {
        File.WriteAllText(_settingsPath, "{ this is corrupted json [[[ :::");

        var service = new UserSettingsService(_settingsPath);
        var settings = service.Load();

        Assert.IsNotNull(settings);
        Assert.AreEqual(7, settings.RollingBackupRetainCount);
        Assert.IsTrue(settings.DiagnosticExportAnonymizePaths);
        Assert.AreEqual("Default", settings.AppTheme);
        Assert.AreEqual("Info", settings.MinLogLevel);
    }

    [TestMethod]
    public void ResetToDefault_ResetsAndPersistsDefaults()
    {
        var service = new UserSettingsService(_settingsPath);
        service.Save(new UserSettings
        {
            RollingBackupRetainCount = 20,
            DiagnosticExportAnonymizePaths = false,
            AppTheme = "Light",
            MinLogLevel = "Error"
        });

        var reset = service.ResetToDefault();
        Assert.AreEqual(7, reset.RollingBackupRetainCount);
        Assert.IsTrue(reset.DiagnosticExportAnonymizePaths);
        Assert.AreEqual("Default", reset.AppTheme);
        Assert.AreEqual("Info", reset.MinLogLevel);

        var reloaded = service.Load();
        Assert.AreEqual(7, reloaded.RollingBackupRetainCount);
        Assert.IsTrue(reloaded.DiagnosticExportAnonymizePaths);
        Assert.AreEqual("Default", reloaded.AppTheme);
        Assert.AreEqual("Info", reloaded.MinLogLevel);
    }

    [TestMethod]
    public void Save_WhenWriteFailsDueToReadOnly_ReturnsFalseWithoutThrowing()
    {
        var service = new UserSettingsService(_settingsPath);
        service.Save(new UserSettings());

        // Make file read-only
        var fileInfo = new FileInfo(_settingsPath)
        {
            IsReadOnly = true
        };

        try
        {
            var success = service.Save(new UserSettings { RollingBackupRetainCount = 25 });
            Assert.IsFalse(success, "Save should return false on write failure instead of throwing");
        }
        finally
        {
            fileInfo.IsReadOnly = false;
        }
    }
}
