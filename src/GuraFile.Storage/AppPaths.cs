namespace GuraFile.Storage;

public static class AppPaths
{
    public static string DefaultUserDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GuraFile");

    public static string DefaultDatabasePath =>
        Path.Combine(DefaultUserDataDirectory, "index.db");

    public static string DefaultTagBackupDirectory =>
        Path.Combine(DefaultUserDataDirectory, "backups", "tags");
}
