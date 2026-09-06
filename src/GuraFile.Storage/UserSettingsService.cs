using System.Text.Json;

namespace GuraFile.Storage;

public sealed class UserSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _settingsPath;
    private readonly DiagnosticLogger _logger;
    private readonly object _lock = new();

    public UserSettingsService(string? settingsPath = null, DiagnosticLogger? logger = null)
    {
        _settingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? AppPaths.DefaultSettingsPath
            : Path.GetFullPath(settingsPath);
        _logger = logger ?? DiagnosticLogger.Default;
    }

    public string SettingsPath => _settingsPath;

    public UserSettings CurrentSettings { get; private set; } = new();

    public UserSettings Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    CurrentSettings = new UserSettings();
                    return CurrentSettings;
                }

                var json = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);
                if (loaded == null)
                {
                    _logger.LogWarning(
                        DiagnosticCategory.App,
                        "SettingsDeserializeNull",
                        message: "Settings file deserialized to null; falling back to defaults.",
                        properties: new Dictionary<string, object?> { ["settingsPath"] = _settingsPath });
                    CurrentSettings = new UserSettings();
                }
                else
                {
                    // Re-assign to ensure setter normalization and boundary clamping
                    loaded.RollingBackupRetainCount = loaded.RollingBackupRetainCount;
                    loaded.AppTheme = loaded.AppTheme;
                    loaded.MinLogLevel = loaded.MinLogLevel;
                    CurrentSettings = loaded;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    DiagnosticCategory.App,
                    "SettingsLoadFailed",
                    message: $"Failed to load settings from {_settingsPath}: {ex.Message}",
                    exception: ex,
                    properties: new Dictionary<string, object?> { ["settingsPath"] = _settingsPath });
                CurrentSettings = new UserSettings();
            }

            return CurrentSettings;
        }
    }

    public bool Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonSerializer.Serialize(settings, JsonOptions);
                var tempPath = _settingsPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _settingsPath, overwrite: true);

                CurrentSettings = settings.Clone();
                _logger.LogInfo(
                    DiagnosticCategory.App,
                    "SettingsSaved",
                    message: "User settings saved successfully.",
                    properties: new Dictionary<string, object?>
                    {
                        ["rollingBackupRetainCount"] = settings.RollingBackupRetainCount,
                        ["diagnosticExportAnonymizePaths"] = settings.DiagnosticExportAnonymizePaths,
                        ["appTheme"] = settings.AppTheme,
                        ["minLogLevel"] = settings.MinLogLevel
                    });
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    DiagnosticCategory.App,
                    "SettingsSaveFailed",
                    message: $"Failed to save settings to {_settingsPath}: {ex.Message}",
                    exception: ex,
                    properties: new Dictionary<string, object?> { ["settingsPath"] = _settingsPath });
                return false;
            }
        }
    }

    public UserSettings ResetToDefault()
    {
        lock (_lock)
        {
            var defaults = new UserSettings();
            Save(defaults);
            CurrentSettings = defaults;
            return CurrentSettings;
        }
    }
}
