using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GuraFile.Storage;

public enum DiagnosticLogLevel
{
    Info,
    Warn,
    Error
}

public enum DiagnosticCategory
{
    Scanner,
    Watcher,
    FileOperation,
    Database,
    Backup,
    GraphHost,
    App
}

public enum DiagnosticResultStatus
{
    None,
    Started,
    Success,
    Failed,
    Skipped
}

public sealed record DiagnosticLogEntry(
    [property: JsonPropertyName("timestamp")] DateTime TimestampUtc,
    [property: JsonPropertyName("level"), JsonConverter(typeof(JsonStringEnumConverter))] DiagnosticLogLevel Level,
    [property: JsonPropertyName("category"), JsonConverter(typeof(JsonStringEnumConverter))] DiagnosticCategory Category,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("correlationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CorrelationId = null,
    [property: JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter)), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DiagnosticResultStatus Status = DiagnosticResultStatus.None,
    [property: JsonPropertyName("message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null,
    [property: JsonPropertyName("errorCode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode = null,
    [property: JsonPropertyName("exception"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Exception = null,
    [property: JsonPropertyName("properties"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, object?>? Properties = null);

public class DiagnosticLogger
{
    public const long DefaultMaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB
    public const int DefaultMaxFileCount = 10;
    public const int DefaultRetentionDays = 14;
    public const int DefaultFloodThresholdPerSecond = 10;

    private static readonly Regex UserProfileRegex = new(
        @"(?i)([a-zA-Z]:[\\/]Users[\\/])[^\\/\r\n]+",
        RegexOptions.Compiled);

    private static readonly Regex SecretTokenRegex = new(
        @"(?i)\b(bearer\s+)[a-zA-Z0-9_\-\.]+",
        RegexOptions.Compiled);

    private static readonly Regex PasswordSecretRegex = new(
        @"(?i)\b(password|secret|token|apikey)\s*[:=]\s*([^\s,;]+)",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static DiagnosticLogger? s_default;
    private static readonly object s_defaultLock = new();

    public static DiagnosticLogger Default
    {
        get
        {
            if (s_default is null)
            {
                lock (s_defaultLock)
                {
                    s_default ??= new DiagnosticLogger(AppPaths.DefaultLogsDirectory);
                }
            }
            return s_default;
        }
        set
        {
            lock (s_defaultLock)
            {
                s_default = value;
            }
        }
    }

    private readonly object _syncLock = new();
    private readonly string _logsDirectory;
    private readonly long _maxFileSizeBytes;
    private readonly int _maxFileCount;
    private readonly int _retentionDays;
    private readonly bool _enableFloodProtection;
    private readonly int _floodThresholdPerSecond;
    private readonly Func<DateTime> _clock;

    // Flood guard state: key -> (currentSecond, eventCount, suppressedCount)
    private readonly Dictionary<string, (long Second, int Count, int Suppressed)> _floodTracker = [];

    public DiagnosticLogger(
        string logsDirectory,
        long maxFileSizeBytes = DefaultMaxFileSizeBytes,
        int maxFileCount = DefaultMaxFileCount,
        int retentionDays = DefaultRetentionDays,
        bool enableFloodProtection = false,
        int floodThresholdPerSecond = DefaultFloodThresholdPerSecond,
        Func<DateTime>? clock = null)
    {
        _logsDirectory = logsDirectory;
        _maxFileSizeBytes = maxFileSizeBytes > 0 ? maxFileSizeBytes : DefaultMaxFileSizeBytes;
        _maxFileCount = maxFileCount > 0 ? maxFileCount : DefaultMaxFileCount;
        _retentionDays = retentionDays > 0 ? retentionDays : DefaultRetentionDays;
        _enableFloodProtection = enableFloodProtection;
        _floodThresholdPerSecond = floodThresholdPerSecond > 0 ? floodThresholdPerSecond : DefaultFloodThresholdPerSecond;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public string LogsDirectory => _logsDirectory;

    public void LogInfo(
        DiagnosticCategory category,
        string eventName,
        string? correlationId = null,
        DiagnosticResultStatus status = DiagnosticResultStatus.None,
        string? message = null,
        string? errorCode = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        Log(DiagnosticLogLevel.Info, category, eventName, correlationId, status, message, errorCode, exception: null, properties);
    }

    public void LogWarning(
        DiagnosticCategory category,
        string eventName,
        string? correlationId = null,
        DiagnosticResultStatus status = DiagnosticResultStatus.None,
        string? message = null,
        string? errorCode = null,
        Exception? exception = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        Log(DiagnosticLogLevel.Warn, category, eventName, correlationId, status, message, errorCode, exception, properties);
    }

    public void LogError(
        DiagnosticCategory category,
        string eventName,
        string? correlationId = null,
        DiagnosticResultStatus status = DiagnosticResultStatus.None,
        string? message = null,
        string? errorCode = null,
        Exception? exception = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        Log(DiagnosticLogLevel.Error, category, eventName, correlationId, status, message, errorCode, exception, properties);
    }

    public void Log(
        DiagnosticLogLevel level,
        DiagnosticCategory category,
        string eventName,
        string? correlationId = null,
        DiagnosticResultStatus status = DiagnosticResultStatus.None,
        string? message = null,
        string? errorCode = null,
        Exception? exception = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        try
        {
            var utcNow = _clock();

            if (_enableFloodProtection && level != DiagnosticLogLevel.Error)
            {
                if (CheckAndApplyFloodGuard(category, eventName, utcNow, out var suppressedCount))
                {
                    return; // Dropped by anti-flood
                }

                if (suppressedCount > 0)
                {
                    // Emit flood summary log entry
                    WriteEntryCore(new DiagnosticLogEntry(
                        TimestampUtc: utcNow,
                        Level: DiagnosticLogLevel.Warn,
                        Category: DiagnosticCategory.App,
                        Event: "FloodSuppressed",
                        CorrelationId: correlationId,
                        Status: DiagnosticResultStatus.Success,
                        Message: $"Flood protection suppressed {suppressedCount} repetitive events for [{category}] {eventName}."));
                }
            }

            var sanitizedMessage = message != null ? SanitizeText(message) : null;
            var sanitizedErrorCode = errorCode != null ? SanitizeText(errorCode) : null;
            var sanitizedException = exception != null ? SanitizeText(exception.ToString()) : null;

            IReadOnlyDictionary<string, object?>? sanitizedProps = null;
            if (properties != null && properties.Count > 0)
            {
                var dict = new Dictionary<string, object?>(properties.Count);
                foreach (var (k, v) in properties)
                {
                    dict[k] = v is string s ? SanitizeText(s) : v;
                }
                sanitizedProps = dict;
            }

            var entry = new DiagnosticLogEntry(
                TimestampUtc: utcNow,
                Level: level,
                Category: category,
                Event: eventName,
                CorrelationId: correlationId,
                Status: status,
                Message: sanitizedMessage,
                ErrorCode: sanitizedErrorCode,
                Exception: sanitizedException,
                Properties: sanitizedProps);

            WriteEntryCore(entry);
        }
        catch
        {
            // Fault isolation: strictly swallow all exceptions to prevent breaking core business logic
        }
    }

    private bool CheckAndApplyFloodGuard(
        DiagnosticCategory category,
        string eventName,
        DateTime utcNow,
        out int previouslySuppressed)
    {
        previouslySuppressed = 0;
        var key = $"{category}:{eventName}";
        var currentSecond = (long)(utcNow - DateTime.UnixEpoch).TotalSeconds;

        lock (_floodTracker)
        {
            if (_floodTracker.TryGetValue(key, out var state))
            {
                if (state.Second == currentSecond)
                {
                    if (state.Count >= _floodThresholdPerSecond)
                    {
                        // Throttled: increment suppressed count
                        _floodTracker[key] = (state.Second, state.Count, state.Suppressed + 1);
                        return true;
                    }

                    _floodTracker[key] = (state.Second, state.Count + 1, state.Suppressed);
                    return false;
                }

                // New second window
                previouslySuppressed = state.Suppressed;
                _floodTracker[key] = (currentSecond, 1, 0);
                return false;
            }

            _floodTracker[key] = (currentSecond, 1, 0);
            return false;
        }
    }

    private void WriteEntryCore(DiagnosticLogEntry entry)
    {
        lock (_syncLock)
        {
            try
            {
                if (!Directory.Exists(_logsDirectory))
                {
                    Directory.CreateDirectory(_logsDirectory);
                }

                var targetFile = ResolveTargetLogFile(entry.TimestampUtc);
                var jsonLine = JsonSerializer.Serialize(entry, JsonOptions);

                using (var stream = new FileStream(targetFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    writer.WriteLine(jsonLine);
                }

                CleanUpRetention(entry.TimestampUtc);
            }
            catch
            {
                // Fault isolation: strictly swallow all exceptions (disk full, permissions, locked file)
            }
        }
    }

    private string ResolveTargetLogFile(DateTime utcNow)
    {
        var datePrefix = $"gurafile_{utcNow:yyyy-MM-dd}";
        var primaryFile = Path.Combine(_logsDirectory, $"{datePrefix}.log");

        if (!File.Exists(primaryFile))
        {
            return primaryFile;
        }

        var primaryInfo = new FileInfo(primaryFile);
        if (primaryInfo.Length < _maxFileSizeBytes)
        {
            return primaryFile;
        }

        // Rolled files for the day: gurafile_yyyy-MM-dd_1.log, _2.log...
        int index = 1;
        while (true)
        {
            var rolledFile = Path.Combine(_logsDirectory, $"{datePrefix}_{index}.log");
            if (!File.Exists(rolledFile))
            {
                return rolledFile;
            }

            var rolledInfo = new FileInfo(rolledFile);
            if (rolledInfo.Length < _maxFileSizeBytes)
            {
                return rolledFile;
            }

            index++;
        }
    }

    private void CleanUpRetention(DateTime utcNow)
    {
        try
        {
            if (!Directory.Exists(_logsDirectory))
            {
                return;
            }

            var files = Directory.GetFiles(_logsDirectory, "gurafile_*.log")
                .Select(f => new FileInfo(f))
                .ToList();

            var cutoffDate = utcNow.Date.AddDays(-_retentionDays);

            // 1. Purge by retention days
            foreach (var file in files.ToList())
            {
                if (file.LastWriteTimeUtc < cutoffDate)
                {
                    try
                    {
                        file.Delete();
                        files.Remove(file);
                    }
                    catch
                    {
                        // Ignore locked file
                    }
                }
            }

            // 2. Purge by maximum file count
            if (files.Count > _maxFileCount)
            {
                var sorted = files.OrderBy(f => f.LastWriteTimeUtc).ToList();
                var toRemove = sorted.Take(sorted.Count - _maxFileCount);
                foreach (var file in toRemove)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch
                    {
                        // Ignore locked file
                    }
                }
            }
        }
        catch
        {
            // Ignore retention cleanup errors
        }
    }

    public static string SanitizePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        return SanitizeText(path);
    }

    public static string SanitizeText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;

        // Replace C:\Users\<name> or C:/Users/<name> with C:\Users\<user>
        result = UserProfileRegex.Replace(result, "${1}<user>");

        // Replace current user profile explicitly if present
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile) && result.Contains(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            var userDir = Path.GetDirectoryName(userProfile) ?? @"C:\Users";
            var sanitizedProfile = Path.Combine(userDir, "<user>");
            result = result.Replace(userProfile, sanitizedProfile, StringComparison.OrdinalIgnoreCase);
        }

        // Replace Environment.UserName if still present in path segments
        var userName = Environment.UserName;
        if (!string.IsNullOrEmpty(userName) && result.Contains(userName, StringComparison.OrdinalIgnoreCase))
        {
            // Replace \username\ or /username/
            result = Regex.Replace(result, @"([\\/])" + Regex.Escape(userName) + @"([\\/])", "$1<user>$2", RegexOptions.IgnoreCase);
            // Replace \username at end of path
            result = Regex.Replace(result, @"([\\/])" + Regex.Escape(userName) + @"$", "$1<user>", RegexOptions.IgnoreCase);
        }

        // Sanitize bearer tokens
        result = SecretTokenRegex.Replace(result, "${1}***");

        // Sanitize passwords and secrets
        result = PasswordSecretRegex.Replace(result, "$1=***");

        return result;
    }
}
