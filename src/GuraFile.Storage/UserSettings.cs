namespace GuraFile.Storage;

public sealed class UserSettings
{
    public const int DefaultRollingBackupRetainCount = 7;
    public const int MinRollingBackupRetainCount = 1;
    public const int MaxRollingBackupRetainCount = 30;
    public const bool DefaultDiagnosticExportAnonymizePaths = true;
    public const string DefaultAppTheme = "Default";
    public const string DefaultMinLogLevel = "Info";

    private int _rollingBackupRetainCount = DefaultRollingBackupRetainCount;
    private string _appTheme = DefaultAppTheme;
    private string _minLogLevel = DefaultMinLogLevel;

    public int RollingBackupRetainCount
    {
        get => _rollingBackupRetainCount;
        set => _rollingBackupRetainCount = Math.Clamp(value, MinRollingBackupRetainCount, MaxRollingBackupRetainCount);
    }

    public bool DiagnosticExportAnonymizePaths { get; set; } = DefaultDiagnosticExportAnonymizePaths;

    public string AppTheme
    {
        get => _appTheme;
        set => _appTheme = NormalizeAppTheme(value);
    }

    public string MinLogLevel
    {
        get => _minLogLevel;
        set => _minLogLevel = NormalizeMinLogLevel(value);
    }

    public static string NormalizeAppTheme(string? theme)
    {
        if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase))
        {
            return "Light";
        }

        if (string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            return "Dark";
        }

        return DefaultAppTheme;
    }

    public static string NormalizeMinLogLevel(string? level)
    {
        if (string.Equals(level, "Debug", StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }

        if (string.Equals(level, "Warning", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(level, "Warn", StringComparison.OrdinalIgnoreCase))
        {
            return "Warning";
        }

        if (string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase))
        {
            return "Error";
        }

        return DefaultMinLogLevel;
    }

    public DiagnosticLogLevel ToDiagnosticLogLevel()
    {
        return MinLogLevel switch
        {
            "Debug" => DiagnosticLogLevel.Debug,
            "Warning" => DiagnosticLogLevel.Warn,
            "Error" => DiagnosticLogLevel.Error,
            _ => DiagnosticLogLevel.Info
        };
    }

    public UserSettings Clone()
    {
        return new UserSettings
        {
            RollingBackupRetainCount = RollingBackupRetainCount,
            DiagnosticExportAnonymizePaths = DiagnosticExportAnonymizePaths,
            AppTheme = AppTheme,
            MinLogLevel = MinLogLevel
        };
    }
}
