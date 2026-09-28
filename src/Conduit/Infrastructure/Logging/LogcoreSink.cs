using System;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Conduit.Infrastructure.Logging;

public sealed class LogcoreSink : ILogEventSink, IDisposable
{
    private readonly LogcoreOptions _options;
    private readonly LogcoreTransport _transport;
    private readonly LogEventLevel _minimumLevel;
    private bool _disposed;

    public LogcoreSink(LogcoreOptions? options = null, LogcoreTransport? transport = null)
    {
        _options = options ?? LogcoreOptions.FromEnvironment();
        _minimumLevel = ParseMinSeverity(_options.MinSeverity);
        _transport = transport ?? new LogcoreTransport(_options);

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed || !_options.Enabled)
        {
            return;
        }

        // Forward ERROR and above only
        if (logEvent.Level < _minimumLevel)
        {
            return;
        }

        try
        {
            var entry = LogcoreFormatter.Format(logEvent, _options);
            _transport.Enqueue(entry);
        }
        catch (Exception ex)
        {
            // Transport / Sink must NEVER crash the application
            Console.Error.WriteLine($"[Logcore] Failed to emit log event: {ex.Message}");
        }
    }

    private static LogEventLevel ParseMinSeverity(string minSeverity)
    {
        return minSeverity.ToUpperInvariant() switch
        {
            "FATAL" or "CRITICAL" => LogEventLevel.Fatal,
            _ => LogEventLevel.Error,
        };
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            try
            {
                var parser = new MessageTemplateParser();
                var template = parser.Parse("Unhandled process crash: {Message}");
                var logEvent = new LogEvent(
                    DateTimeOffset.UtcNow,
                    LogEventLevel.Fatal,
                    ex,
                    template,
                    [new LogEventProperty("Message", new ScalarValue(ex.Message))]
                );
                Emit(logEvent);
            }
            catch
            {
                // Best-effort
            }
        }
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;

        _transport.Dispose();
    }
}
