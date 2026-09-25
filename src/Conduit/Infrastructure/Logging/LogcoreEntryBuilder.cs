using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog.Events;

namespace Conduit.Infrastructure.Logging;

public static class LogcoreEntryBuilder
{
    private const int MaxMessageLength = 65536;
    private const int MaxLabelsCount = 32;
    private const int MaxLabelValueLength = 1024;
    private const int MaxStackFrames = 50;

    public static LogcoreEntry BuildEntry(LogEvent logEvent, LogcoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(options);

        var timestamp = logEvent.Timestamp.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture
        );
        var severity = FormatSeverity(logEvent.Level);
        var message = FormatMessage(logEvent);
        var service = options.Service;
        var env = options.Environment;

        var (labels, context) = ExtractLabelsAndContext(logEvent);
        var trace = ExtractTrace(logEvent);
        var error = ExtractError(logEvent.Exception);
        var insertId = ComputeInsertId(timestamp, service, severity, message, context);

        return new LogcoreEntry
        {
            Timestamp = timestamp,
            Severity = severity,
            Message = message,
            Service = service,
            Env = env,
            SourceProject = null,
            InsertId = insertId,
            Trace = trace,
            Error = error,
            Labels = labels,
            Context = context,
        };
    }

    public static string ComputeInsertId(
        string timestamp,
        string service,
        string severity,
        string message,
        Dictionary<string, object?>? context
    )
    {
        var canonicalContext =
            context != null && context.Count > 0
                ? JsonSerializer.Serialize(new SortedDictionary<string, object?>(context))
                : "{}";

        var raw = $"{timestamp}|{service}|{severity}|{message}|{canonicalContext}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash)[..32];
    }

    private static string FormatSeverity(LogEventLevel level)
    {
        return level switch
        {
            LogEventLevel.Fatal => "CRITICAL",
            LogEventLevel.Error => "ERROR",
            LogEventLevel.Warning => "WARNING",
            LogEventLevel.Information => "INFO",
            _ => "DEBUG",
        };
    }

    private static string FormatMessage(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage();
        if (string.IsNullOrWhiteSpace(message))
        {
            message = logEvent.Exception?.Message ?? "Error";
        }

        if (message.Length > MaxMessageLength)
        {
            message = message[..MaxMessageLength];
        }

        return message;
    }

    private static (
        Dictionary<string, string>? Labels,
        Dictionary<string, object?>? Context
    ) ExtractLabelsAndContext(LogEvent logEvent)
    {
        if (logEvent.Properties.Count == 0)
        {
            return (null, null);
        }

        var labels = new Dictionary<string, string>();
        var context = new Dictionary<string, object?>();

        foreach (var (key, propValue) in logEvent.Properties)
        {
            var value = ConvertPropertyValue(propValue);
            context[key] = value;

            if (labels.Count < MaxLabelsCount && value != null)
            {
                var strValue = value.ToString() ?? string.Empty;
                if (strValue.Length > MaxLabelValueLength)
                {
                    strValue = strValue[..MaxLabelValueLength];
                }
                labels[key] = strValue;
            }
        }

        return (labels.Count > 0 ? labels : null, context.Count > 0 ? context : null);
    }

    private static object? ConvertPropertyValue(LogEventPropertyValue propertyValue)
    {
        return propertyValue switch
        {
            ScalarValue scalar => scalar.Value,
            SequenceValue sequence => sequence.Elements.Select(ConvertPropertyValue).ToList(),
            StructureValue structure => structure.Properties.ToDictionary(
                p => p.Name,
                p => ConvertPropertyValue(p.Value)
            ),
            DictionaryValue dict => dict.Elements.ToDictionary(
                kvp => kvp.Key.Value?.ToString() ?? string.Empty,
                kvp => ConvertPropertyValue(kvp.Value)
            ),
            _ => propertyValue.ToString(),
        };
    }

    private static LogcoreTrace? ExtractTrace(LogEvent logEvent)
    {
        var currentActivity = Activity.Current;
        if (currentActivity != null && currentActivity.TraceId != default)
        {
            var traceId = currentActivity.TraceId.ToHexString();
            var spanId = currentActivity.SpanId.ToHexString();
            var sampled = (currentActivity.ActivityTraceFlags & ActivityTraceFlags.Recorded) != 0;

            if (
                !string.IsNullOrWhiteSpace(traceId)
                && traceId != "00000000000000000000000000000000"
            )
            {
                return new LogcoreTrace
                {
                    TraceId = traceId,
                    SpanId = spanId,
                    Sampled = sampled,
                };
            }
        }

        if (
            logEvent.Properties.TryGetValue("TraceId", out var traceIdProp)
            && logEvent.Properties.TryGetValue("SpanId", out var spanIdProp)
        )
        {
            var traceId = (traceIdProp as ScalarValue)?.Value?.ToString() ?? string.Empty;
            var spanId = (spanIdProp as ScalarValue)?.Value?.ToString() ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(traceId))
            {
                return new LogcoreTrace
                {
                    TraceId = traceId,
                    SpanId = spanId,
                    Sampled = true,
                };
            }
        }

        return null;
    }

    private static LogcoreError? ExtractError(Exception? exception)
    {
        if (exception == null)
        {
            return null;
        }

        var chain = new List<Exception>();
        var current = exception;
        while (current != null && !chain.Contains(current))
        {
            chain.Add(current);
            current = current.InnerException;
        }
        chain.Reverse(); // Root exception first

        var frames = new List<LogcoreStackFrame>();
        foreach (var ex in chain)
        {
            var stackTrace = new StackTrace(ex, true);
            var stackFrames = stackTrace.GetFrames();
            if (stackFrames == null)
            {
                continue;
            }

            foreach (var frame in stackFrames)
            {
                if (frames.Count >= MaxStackFrames)
                {
                    break;
                }

                var method = frame.GetMethod();
                var funcName =
                    method != null
                        ? (
                            method.DeclaringType != null
                                ? $"{method.DeclaringType.Name}.{method.Name}"
                                : method.Name
                        )
                        : "<unknown>";
                var fileName = frame.GetFileName() ?? "<unknown>";
                var line = frame.GetFileLineNumber();
                var col = frame.GetFileColumnNumber();
                var ns = method?.DeclaringType?.Namespace ?? string.Empty;
                var inApp = ns.StartsWith("Conduit", StringComparison.OrdinalIgnoreCase);

                frames.Add(
                    new LogcoreStackFrame
                    {
                        Function = funcName,
                        File = fileName,
                        Line = line,
                        Column = col,
                        InApp = inApp,
                    }
                );
            }

            if (frames.Count >= MaxStackFrames)
            {
                break;
            }
        }

        return new LogcoreError
        {
            Type = exception.GetType().Name,
            Message = exception.Message,
            Stack = frames,
        };
    }
}
