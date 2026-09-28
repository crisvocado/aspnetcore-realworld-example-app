using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace Conduit.Infrastructure.Logcore;

public sealed class LogcoreSink(LogcoreConfig? config = null, LogcoreTransport? transport = null)
    : ILogEventSink,
        IDisposable
{
    private readonly LogcoreConfig _config = config ?? LogcoreConfig.Current;
    private readonly LogcoreTransport _transport = transport ?? LogcoreTransport.Instance;

    public static event Action<LogcoreEntry>? Emitted;

    public void Emit(LogEvent logEvent)
    {
        if (!_config.Enabled)
        {
            return;
        }

        if (logEvent.Level < _config.MinSeverity)
        {
            return;
        }

        var entry = CreateEntry(logEvent, _config);
        Emitted?.Invoke(entry);
        _transport.Enqueue(entry);
    }

    public static LogcoreEntry CreateEntry(LogEvent logEvent, LogcoreConfig config)
    {
        var timestamp = logEvent.Timestamp.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture
        );
        var severity = MapSeverity(logEvent.Level);
        var message = logEvent.RenderMessage();
        if (string.IsNullOrWhiteSpace(message) && logEvent.Exception != null)
        {
            message = logEvent.Exception.Message;
        }

        Dictionary<string, object>? context = null;
        if (logEvent.Properties.Count > 0)
        {
            context = new Dictionary<string, object>();
            foreach (var prop in logEvent.Properties)
            {
                context[prop.Key] = ConvertPropertyValue(prop.Value);
            }
        }

        var insertId = ComputeInsertId(timestamp, config.Service, severity, message, context);
        var trace = LogcoreExtractors.ExtractTrace();
        var error = LogcoreExtractors.ExtractError(logEvent.Exception);

        return new LogcoreEntry
        {
            Timestamp = timestamp,
            Severity = severity,
            Message = message,
            Service = config.Service,
            Env = config.Env,
            InsertId = insertId,
            Trace = trace,
            Error = error,
            Context = context,
        };
    }

    public static LogcoreEntry CreateEntryFromException(Exception ex, string fallbackMessage)
    {
        var config = LogcoreConfig.Current;
        var timestamp = DateTimeOffset.UtcNow.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture
        );
        var severity = "ERROR";
        var message = !string.IsNullOrWhiteSpace(ex.Message) ? ex.Message : fallbackMessage;
        var insertId = ComputeInsertId(timestamp, config.Service, severity, message, null);
        var trace = LogcoreExtractors.ExtractTrace();
        var error = LogcoreExtractors.ExtractError(ex);

        return new LogcoreEntry
        {
            Timestamp = timestamp,
            Severity = severity,
            Message = message,
            Service = config.Service,
            Env = config.Env,
            InsertId = insertId,
            Trace = trace,
            Error = error,
        };
    }

    private static string MapSeverity(LogEventLevel level) =>
        level switch
        {
            LogEventLevel.Verbose => "DEBUG",
            LogEventLevel.Debug => "DEBUG",
            LogEventLevel.Information => "INFO",
            LogEventLevel.Warning => "WARNING",
            LogEventLevel.Error => "ERROR",
            LogEventLevel.Fatal => "CRITICAL",
            _ => "ERROR",
        };

    private static object ConvertPropertyValue(LogEventPropertyValue value)
    {
        return value switch
        {
            ScalarValue sv => sv.Value ?? string.Empty,
            DictionaryValue dv => dv.Elements.ToDictionary(
                k => k.Key.Value?.ToString() ?? string.Empty,
                v => ConvertPropertyValue(v.Value)
            ),
            SequenceValue sv => sv.Elements.Select(ConvertPropertyValue).ToList(),
            StructureValue sv => sv.Properties.ToDictionary(
                p => p.Name,
                p => ConvertPropertyValue(p.Value)
            ),
            _ => value.ToString(),
        };
    }

    public static string ComputeInsertId(
        string timestamp,
        string service,
        string severity,
        string message,
        Dictionary<string, object>? context
    )
    {
        var canonicalContextJson = "{}";
        if (context != null && context.Count > 0)
        {
            var sorted = new SortedDictionary<string, object>(context, StringComparer.Ordinal);
            canonicalContextJson = JsonSerializer.Serialize(sorted);
        }

        var payload = $"{timestamp}|{service}|{severity}|{message}|{canonicalContextJson}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash)[..32];
    }

    public void Dispose()
    {
        _transport.Flush(TimeSpan.FromSeconds(5));
    }
}
