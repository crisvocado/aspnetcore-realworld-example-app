using System;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Events;

namespace Conduit.Infrastructure.Logging;

public sealed class LogcoreSink : ILogEventSink, IDisposable, IAsyncDisposable
{
    private static LogcoreSink? s_instance;
    public static LogcoreSink? Instance => s_instance;

    public static LogcoreEntry? LastEmittedEntry { get; internal set; }
    public static string? LastEmittedJson { get; internal set; }

    private readonly LogcoreOptions _options;
    private readonly LogcoreTransport _transport;
    private readonly LogEventLevel _minLevel;

    public LogcoreSink(LogcoreOptions? options = null, LogcoreTransport? transport = null)
    {
        _options = options ?? LogcoreOptions.FromEnvironment();
        _transport = transport ?? new LogcoreTransport(_options);
        _minLevel = ParseMinLevel(_options.MinSeverity);
        s_instance = this;

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        try
        {
            _transport.FlushPendingAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best effort on exit
        }
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (!_options.Enabled || logEvent.Level < _minLevel)
        {
            return;
        }

        try
        {
            var entry = LogcoreEntryBuilder.BuildEntry(logEvent, _options);
            LastEmittedEntry = entry;
            LastEmittedJson = JsonSerializer.Serialize(entry);
            _transport.Enqueue(entry);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Logcore] Error building log entry: {ex.Message}");
        }
    }

    public async Task FlushAsync()
    {
        await _transport.FlushPendingAsync().ConfigureAwait(false);
    }

    private static LogEventLevel ParseMinLevel(string minSeverity)
    {
        return minSeverity.ToUpperInvariant() switch
        {
            "VERBOSE" or "DEBUG" => LogEventLevel.Debug,
            "INFO" or "INFORMATION" => LogEventLevel.Information,
            "WARN" or "WARNING" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            "FATAL" or "CRITICAL" => LogEventLevel.Fatal,
            _ => LogEventLevel.Error,
        };
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _transport.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
