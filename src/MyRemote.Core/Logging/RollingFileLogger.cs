using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MyRemote.Core.Logging;

/// <summary>
/// Thread-safe structured rolling file logger with automatic secret redaction.
/// </summary>
public sealed partial class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly LogLevel _minLevel;
    private readonly ConcurrentQueue<string> _recentLines = new();
    private readonly object _writeLock = new();
    private bool _disposed;

    public event Action<string>? LogWritten;

    public RollingFileLoggerProvider(string logDirectory, LogLevel minLevel = LogLevel.Information)
    {
        _logDirectory = logDirectory;
        _minLevel = minLevel;
        Directory.CreateDirectory(_logDirectory);
    }

    public IReadOnlyList<string> GetRecentLines() => _recentLines.ToArray();

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(categoryName, this);

    internal bool IsEnabled(LogLevel logLevel) => !_disposed && logLevel >= _minLevel;

    internal void WriteLine(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        if (_disposed) return;

        var redactedMessage = RedactSecrets(message);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        var line = $"[{timestamp}] [{level}] [{category}] {redactedMessage}";
        if (exception is not null)
        {
            line += $"{Environment.NewLine}  Exception: {exception.GetType().Name}: {RedactSecrets(exception.Message)}";
        }

        _recentLines.Enqueue(line);
        while (_recentLines.Count > 500)
        {
            _recentLines.TryDequeue(out _);
        }

        var filePath = Path.Combine(_logDirectory, $"myremote-{DateTime.UtcNow:yyyy-MM-dd}.log");
        lock (_writeLock)
        {
            File.AppendAllText(filePath, line + Environment.NewLine);
        }

        LogWritten?.Invoke(line);
    }

    public static string RedactSecrets(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var result = TokenRegex().Replace(input, "$1=[REDACTED]");
        return PasswordRegex().Replace(result, "$1=[REDACTED]");
    }

    public void Dispose() => _disposed = true;

    [GeneratedRegex(@"(token|sessiontoken|secret|apikey)\s*[=:]\s*[^\s,;]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(password|passwd|pwd)\s*[=:]\s*[^\s,;]+", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordRegex();

    private sealed class RollingFileLogger(string category, RollingFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            provider.WriteLine(logLevel, category, eventId, msg, exception);
        }
    }
}
