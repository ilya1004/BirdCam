using System.Text;
using Microsoft.Extensions.Logging;

namespace BirdCam.Infrastructure.Logging;

/// <summary>Простой файловый логгер с разбивкой по дням.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _sync = new();

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_directory, categoryName, _sync);

    public void Dispose()
    {
    }

    private sealed class FileLogger(string directory, string category, object sync) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var builder = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(logLevel).Append("] ")
                .Append(category).Append(": ")
                .Append(message);

            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }

            var path = Path.Combine(directory, $"birdcam-{DateTime.Now:yyyyMMdd}.log");

            try
            {
                lock (sync)
                {
                    File.AppendAllText(path, builder.AppendLine().ToString());
                }
            }
            catch (IOException)
            {
                // логгер не должен ронять приложение
            }
        }
    }
}
