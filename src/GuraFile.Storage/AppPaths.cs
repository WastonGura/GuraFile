namespace GuraFile.Storage;

public static class AppPaths
{
    private static string? _customUserDataDirectory;

    public static void SetCustomUserDataDirectory(string? directory)
    {
        _customUserDataDirectory = string.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory);
    }

    public static string AppDataDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_customUserDataDirectory))
            {
                return _customUserDataDirectory;
            }

            var env = Environment.GetEnvironmentVariable("GURAFILE_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return Path.GetFullPath(env);
            }

            var cmdDir = TryGetDataDirFromCommandLine();
            if (!string.IsNullOrWhiteSpace(cmdDir))
            {
                return Path.GetFullPath(cmdDir);
            }

            return DefaultUserDataDirectory;
        }
    }

    public static string DefaultUserDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GuraFile");

    public static string DefaultSettingsPath =>
        Path.Combine(AppDataDirectory, "settings.json");

    public static string DefaultDatabasePath =>
        Path.Combine(AppDataDirectory, "index.db");

    public static string DefaultTagBackupDirectory =>
        Path.Combine(AppDataDirectory, "backups", "tags");

    public static string DefaultLogsDirectory =>
        Path.Combine(AppDataDirectory, "logs");

    private static string? TryGetDataDirFromCommandLine()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--data-dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        return args[i + 1];
                    }
                }
                else if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    var val = arg.Substring("--data-dir=".Length);
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        return val;
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }
}
