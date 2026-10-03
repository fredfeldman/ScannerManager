using System.Text;
using System.Text.Json;

namespace SDS200_ControlApp.Domain;

public enum LogLevel
{
    Error,
    Warning,
    Info,
    Debug,
    Trace
}

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message,
    string? Direction = null,
    string? Command = null,
    TimeSpan? Elapsed = null);

public interface IScannerLogger
{
    event Action<LogEntry>? EntryWritten;

    void Log(
        LogLevel level,
        string message,
        string? direction = null,
        string? command = null,
        TimeSpan? elapsed = null);
}

public sealed class InMemoryLogger : IScannerLogger
{
    public event Action<LogEntry>? EntryWritten;

    public void Log(
        LogLevel level,
        string message,
        string? direction = null,
        string? command = null,
        TimeSpan? elapsed = null)
    {
        EntryWritten?.Invoke(new LogEntry(DateTimeOffset.Now, level, message, direction, command, elapsed));
    }
}

public sealed class RollingFileLogger : IScannerLogger, IDisposable
{
    private readonly object _sync = new();
    private readonly string _logFilePath;
    private readonly long _maxFileBytes;
    private readonly int _retainedFileCount;
    private readonly int _maxMemoryEntries;
    private readonly List<LogEntry> _entries = [];
    private bool _disposed;

    public RollingFileLogger(
        string logFilePath,
        long maxFileBytes = 5 * 1024 * 1024,
        int retainedFileCount = 3,
        int maxMemoryEntries = 5000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedFileCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMemoryEntries);
        _logFilePath = logFilePath;
        _maxFileBytes = maxFileBytes;
        _retainedFileCount = retainedFileCount;
        _maxMemoryEntries = maxMemoryEntries;
    }

    public event Action<LogEntry>? EntryWritten;

    public bool RawTrafficEnabled { get; set; }

    public LogLevel? MinimumLevel { get; set; } = LogLevel.Trace;

    public bool FileLoggingEnabled { get; set; } = true;

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Log(
        LogLevel level,
        string message,
        string? direction = null,
        string? command = null,
        TimeSpan? elapsed = null)
    {
        if (MinimumLevel is not LogLevel minimumLevel)
        {
            return;
        }

        var isRawTraffic = level == LogLevel.Trace && direction is not null;
        if (isRawTraffic)
        {
            if (!RawTrafficEnabled)
            {
                return;
            }
        }
        else if (level > minimumLevel)
        {
            return;
        }

        var entry = new LogEntry(DateTimeOffset.Now, level, message, direction, command, elapsed);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _entries.Add(entry);
            if (_entries.Count > _maxMemoryEntries)
            {
                _entries.RemoveAt(0);
            }

            try
            {
                if (FileLoggingEnabled)
                {
                    AppendEntry(entry);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
        }

        var handlers = EntryWritten;
        if (handlers is not null)
        {
            foreach (Action<LogEntry> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(entry);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private void AppendEntry(LogEntry entry)
    {
        var json = JsonSerializer.Serialize(entry) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(json);
        var directory = Path.GetDirectoryName(_logFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(_logFilePath) && new FileInfo(_logFilePath).Length + bytes.Length > _maxFileBytes)
        {
            RollFiles();
        }

        using var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
    }

    private void RollFiles()
    {
        if (_retainedFileCount == 0)
        {
            File.Delete(_logFilePath);
            return;
        }

        var oldestPath = $"{_logFilePath}.{_retainedFileCount}";
        if (File.Exists(oldestPath))
        {
            File.Delete(oldestPath);
        }

        for (var index = _retainedFileCount - 1; index >= 1; index--)
        {
            var source = $"{_logFilePath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{_logFilePath}.{index + 1}");
            }
        }

        File.Move(_logFilePath, $"{_logFilePath}.1");
    }
}
