namespace GuraFile.Storage;

public static class AppPaths
{
    public static string AppDataDirectory => DefaultUserDataDirectory;

    public static string DefaultUserDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GuraFile");

    public static string DefaultSettingsPath =>
        Path.Combine(DefaultUserDataDirectory, "settings.json");

    public static string DefaultDatabasePath =>
        Path.Combine(DefaultUserDataDirectory, "index.db");

    public static string DefaultTagBackupDirectory =>
        Path.Combine(DefaultUserDataDirectory, "backups", "tags");

    public static string DefaultLogsDirectory =>
        Path.Combine(DefaultUserDataDirectory, "logs");
}
