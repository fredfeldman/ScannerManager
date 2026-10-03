using SDS200_ControlApp.Domain;
using System.Text.Json;

namespace SDS200_ControlApp.Serial.Tests;

public class RollingFileLoggerTests
{
    [Fact]
    public void Log_RollsFilesAndRetainsConfiguredHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "scanner.log");

        try
        {
            using (var logger = new RollingFileLogger(path, maxFileBytes: 1, retainedFileCount: 2))
            {
                logger.Log(LogLevel.Info, "first");
                logger.Log(LogLevel.Info, "second");
                logger.Log(LogLevel.Info, "third");
            }

            Assert.True(File.Exists(path));
            Assert.True(File.Exists($"{path}.1"));
            Assert.True(File.Exists($"{path}.2"));
            Assert.False(File.Exists($"{path}.3"));
            Assert.Contains("third", File.ReadAllText(path));
            Assert.Contains("second", File.ReadAllText($"{path}.1"));
            Assert.Contains("first", File.ReadAllText($"{path}.2"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Log_RawTrafficCanBeEnabledIndependently()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "scanner.log");

        try
        {
            using var logger = new RollingFileLogger(path);
            logger.Log(LogLevel.Trace, "raw command", "TX", "MDL");
            logger.Log(LogLevel.Info, "connection opened");
            logger.RawTrafficEnabled = true;
            logger.Log(LogLevel.Trace, "raw response", "RX", "MDL");

            Assert.Equal(2, logger.Entries.Count);
            var log = File.ReadAllText(path);
            Assert.DoesNotContain("raw command", log);
            Assert.Contains("connection opened", log);
            Assert.Contains("raw response", log);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Log_MinimumLevelFiltersCapturedEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "scanner.log");

        try
        {
            using var logger = new RollingFileLogger(path) { MinimumLevel = LogLevel.Warning };
            logger.Log(LogLevel.Info, "filtered info");
            logger.Log(LogLevel.Warning, "kept warning");
            logger.Log(LogLevel.Error, "kept error");

            Assert.Equal(new[] { "kept warning", "kept error" }, logger.Entries.Select(entry => entry.Message));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Log_RawTrafficUsesItsOwnSettingInsteadOfMinimumLevel()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "scanner.log");

        try
        {
            using var logger = new RollingFileLogger(path)
            {
                MinimumLevel = LogLevel.Error,
                RawTrafficEnabled = true
            };
            logger.Log(LogLevel.Trace, "raw response", "RX", "MDL");
            logger.Log(LogLevel.Warning, "filtered warning");

            Assert.Single(logger.Entries);
            Assert.Equal("raw response", logger.Entries[0].Message);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Log_OffDisablesRawTrafficAndRegularEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "scanner.log");

        try
        {
            using var logger = new RollingFileLogger(path) { MinimumLevel = null, RawTrafficEnabled = true };
            logger.Log(LogLevel.Error, "disabled error");
            logger.Log(LogLevel.Trace, "disabled raw", "TX", "MDL");

            Assert.Empty(logger.Entries);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Log_FileLoggingCanBeDisabledWithoutDisablingInMemoryLogging()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "scanner.log");

        try
        {
            using var logger = new RollingFileLogger(path) { FileLoggingEnabled = false };
            var writtenEntries = new List<LogEntry>();
            logger.EntryWritten += writtenEntries.Add;
            logger.Log(LogLevel.Info, "in-memory entry");

            Assert.Single(logger.Entries);
            Assert.Single(writtenEntries);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Log_ContinuesWhenAnEntrySubscriberThrows()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "scanner.log");

        try
        {
            using var logger = new RollingFileLogger(path);
            var receivedEntries = new List<LogEntry>();
            logger.EntryWritten += _ => throw new InvalidOperationException("subscriber failed");
            logger.EntryWritten += receivedEntries.Add;

            logger.Log(LogLevel.Info, "still recorded");

            Assert.Single(logger.Entries);
            Assert.Single(receivedEntries);
            Assert.Contains("still recorded", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Log_PersistsResponseCorrelationMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "scanner.log");

        try
        {
            using (var logger = new RollingFileLogger(path))
            {
                logger.RawTrafficEnabled = true;
                logger.Log(LogLevel.Trace, "VER,Version 1.02.03", "RX", "VER", TimeSpan.FromMilliseconds(18));
            }

            using var document = JsonDocument.Parse(File.ReadAllLines(path)[0]);
            var entry = document.RootElement;
            Assert.Equal("RX", entry.GetProperty("Direction").GetString());
            Assert.Equal("VER", entry.GetProperty("Command").GetString());
            Assert.Equal(TimeSpan.FromMilliseconds(18), TimeSpan.Parse(entry.GetProperty("Elapsed").GetString()!));
            Assert.True(entry.TryGetProperty("Timestamp", out _));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Log_BoundsInMemoryHistoryWhileKeepingFileHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "scanner.log");

        try
        {
            using var logger = new RollingFileLogger(path, maxMemoryEntries: 2);
            logger.Log(LogLevel.Info, "first");
            logger.Log(LogLevel.Info, "second");
            logger.Log(LogLevel.Info, "third");

            Assert.Equal(new[] { "second", "third" }, logger.Entries.Select(entry => entry.Message));
            var persistedLog = File.ReadAllText(path);
            Assert.Contains("first", persistedLog);
            Assert.Contains("third", persistedLog);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}