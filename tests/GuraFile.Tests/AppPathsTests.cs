using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class AppPathsTests
{
    [TestCleanup]
    public void Cleanup()
    {
        AppPaths.SetCustomUserDataDirectory(null);
        Environment.SetEnvironmentVariable("GURAFILE_DATA_DIR", null);
    }

    [TestMethod]
    public void AppDataDirectory_DefaultsToDefaultUserDataDirectory()
    {
        AppPaths.SetCustomUserDataDirectory(null);
        Environment.SetEnvironmentVariable("GURAFILE_DATA_DIR", null);

        Assert.AreEqual(AppPaths.DefaultUserDataDirectory, AppPaths.AppDataDirectory);
        Assert.AreEqual(Path.Combine(AppPaths.DefaultUserDataDirectory, "settings.json"), AppPaths.DefaultSettingsPath);
        Assert.AreEqual(Path.Combine(AppPaths.DefaultUserDataDirectory, "index.db"), AppPaths.DefaultDatabasePath);
        Assert.AreEqual(Path.Combine(AppPaths.DefaultUserDataDirectory, "backups", "tags"), AppPaths.DefaultTagBackupDirectory);
        Assert.AreEqual(Path.Combine(AppPaths.DefaultUserDataDirectory, "logs"), AppPaths.DefaultLogsDirectory);
    }

    [TestMethod]
    public void EnvironmentVariable_OverridesDefaultUserDataDirectory()
    {
        AppPaths.SetCustomUserDataDirectory(null);
        var tempDir = Path.Combine(Path.GetTempPath(), $"GuraFile_EnvTest_{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("GURAFILE_DATA_DIR", tempDir);

        Assert.AreEqual(Path.GetFullPath(tempDir), AppPaths.AppDataDirectory);
        Assert.AreEqual(Path.Combine(Path.GetFullPath(tempDir), "settings.json"), AppPaths.DefaultSettingsPath);
        Assert.AreEqual(Path.Combine(Path.GetFullPath(tempDir), "index.db"), AppPaths.DefaultDatabasePath);
        Assert.AreEqual(Path.Combine(Path.GetFullPath(tempDir), "backups", "tags"), AppPaths.DefaultTagBackupDirectory);
        Assert.AreEqual(Path.Combine(Path.GetFullPath(tempDir), "logs"), AppPaths.DefaultLogsDirectory);
    }

    [TestMethod]
    public void SetCustomUserDataDirectory_HasHighestPrecedence()
    {
        var envDir = Path.Combine(Path.GetTempPath(), $"GuraFile_Env_{Guid.NewGuid():N}");
        var customDir = Path.Combine(Path.GetTempPath(), $"GuraFile_Custom_{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("GURAFILE_DATA_DIR", envDir);

        AppPaths.SetCustomUserDataDirectory(customDir);

        Assert.AreEqual(Path.GetFullPath(customDir), AppPaths.AppDataDirectory);
        Assert.AreEqual(Path.Combine(Path.GetFullPath(customDir), "settings.json"), AppPaths.DefaultSettingsPath);
        Assert.AreEqual(Path.Combine(Path.GetFullPath(customDir), "index.db"), AppPaths.DefaultDatabasePath);
        Assert.AreEqual(Path.Combine(Path.GetFullPath(customDir), "backups", "tags"), AppPaths.DefaultTagBackupDirectory);
        Assert.AreEqual(Path.Combine(Path.GetFullPath(customDir), "logs"), AppPaths.DefaultLogsDirectory);

        // Reset custom directory -> falls back to environment variable
        AppPaths.SetCustomUserDataDirectory(null);
        Assert.AreEqual(Path.GetFullPath(envDir), AppPaths.AppDataDirectory);
    }

    [TestMethod]
    public void SetCustomUserDataDirectory_WhitespaceOrEmpty_ResetsToFallback()
    {
        AppPaths.SetCustomUserDataDirectory("   ");
        Assert.AreEqual(AppPaths.DefaultUserDataDirectory, AppPaths.AppDataDirectory);
    }
}
