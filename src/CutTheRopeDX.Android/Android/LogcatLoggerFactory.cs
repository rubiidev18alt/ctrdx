using System;

using Microsoft.Extensions.Logging;

namespace CutTheRopeDX.Android
{
    /// <summary>Small Microsoft.Extensions.Logging sink that writes to Android's logcat.</summary>
    internal sealed class LogcatLoggerFactory : ILoggerFactory
    {
        private const string Tag = "CutTheRopeDX";

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName)
        {
            return new LogcatLogger(categoryName);
        }

        /// <inheritdoc />
        public void AddProvider(ILoggerProvider provider)
        {
            // The factory directly owns its one provider, as browser and headless hosts do.
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }

        private sealed class LogcatLogger(string category) : ILogger
        {
            /// <inheritdoc />
            public IDisposable BeginScope<TState>(TState state)
            {
                return NullScope.Instance;
            }

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel)
            {
                return logLevel is >= LogLevel.Information and < LogLevel.None;
            }

            /// <inheritdoc />
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception exception,
                Func<TState, Exception, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                string message = formatter(state, exception);
                if (exception is not null)
                {
                    message += Environment.NewLine + exception;
                }

                string text = $"{category}: {message}";
                _ = logLevel switch
                {
                    LogLevel.Trace or LogLevel.Debug => global::Android.Util.Log.Debug(Tag, text),
                    LogLevel.Information => global::Android.Util.Log.Info(Tag, text),
                    LogLevel.Warning => global::Android.Util.Log.Warn(Tag, text),
                    _ => global::Android.Util.Log.Error(Tag, text),
                };
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
