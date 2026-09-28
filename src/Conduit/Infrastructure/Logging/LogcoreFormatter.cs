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

public static class LogcoreFormatter
{
    private const int MaxMessageLength = 65536;
    private const int MaxLabelCount = 32;
    private const int MaxLabelLength = 1024;
    private const int MaxStackFrames = 50;

    public static LogcoreEntry Format(LogEvent logEvent, LogcoreOptions options)
    {
        var timestamp = logEvent
            .Timestamp.ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var severity = logEvent.Level switch
        {
            LogEventLevel.Fatal => "CRITICAL",
            _ => "ERROR",
        };

        var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(message) && logEvent.Exception != null)
        {
            message = logEvent.Exception.Message;
        }
        if (string.IsNullOrEmpty(message))
        {
            message = "An error occurred.";
        }
        if (message.Length > MaxMessageLength)
        {
            message = message[..MaxMessageLength];
        }

        var trace = ExtractTrace(logEvent);
        var error = ExtractError(logEvent.Exception);
        var (labels, context) = ExtractProperties(logEvent);

        var canonicalContext =
            context != null && context.Count > 0
                ? JsonSerializer.Serialize(
                    context
                        .OrderBy(k => k.Key, StringComparer.Ordinal)
                        .ToDictionary(k => k.Key, k => k.Value),
                    LogcoreJsonOptions.Default
                )
                : "";

        var insertId = ComputeInsertId(
            timestamp,
            options.Service,
            severity,
            message,
            canonicalContext
        );

        return new LogcoreEntry
        {
            Timestamp = timestamp,
            Severity = severity,
            Message = message,
            Service = options.Service,
            Env = options.Env,
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
        string contextJson
    )
    {
        var raw = $"{timestamp}|{service}|{severity}|{message}|{contextJson}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private static LogcoreTrace? ExtractTrace(LogEvent logEvent)
    {
        var activity = Activity.Current;
        if (activity != null && activity.TraceId != default)
        {
            return new LogcoreTrace
            {
                TraceId = activity.TraceId.ToHexString().ToLowerInvariant(),
                SpanId = activity.SpanId.ToHexString().ToLowerInvariant(),
                Sampled = (activity.ActivityTraceFlags & ActivityTraceFlags.Recorded) != 0,
            };
        }

        if (
            logEvent.Properties.TryGetValue("TraceId", out var traceIdProp)
            && logEvent.Properties.TryGetValue("SpanId", out var spanIdProp)
        )
        {
            var traceId = (traceIdProp as ScalarValue)?.Value?.ToString();
            var spanId = (spanIdProp as ScalarValue)?.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(traceId) && !string.IsNullOrWhiteSpace(spanId))
            {
                return new LogcoreTrace
                {
                    TraceId = traceId.ToLowerInvariant(),
                    SpanId = spanId.ToLowerInvariant(),
                    Sampled = true,
                };
            }
        }

        return null;
    }

    public static LogcoreError? ExtractError(Exception? exception)
    {
        if (exception == null)
        {
            return null;
        }

        var frames = new List<LogcoreStackFrame>();
        var chain = new List<Exception>();
        var current = exception;
        while (current != null && chain.Count < 10)
        {
            chain.Add(current);
            current = current.InnerException;
        }

        // Innermost / root cause first
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var ex = chain[i];
            var st = new StackTrace(ex, true);
            var stackFrames = st.GetFrames();
            if (stackFrames != null)
            {
                foreach (var frame in stackFrames)
                {
                    var method = frame.GetMethod();
                    var function =
                        method != null
                            ? (
                                method.DeclaringType != null
                                    ? $"{method.DeclaringType.FullName}.{method.Name}"
                                    : method.Name
                            )
                            : "<unknown>";
                    var file = frame.GetFileName() ?? "";
                    var line = frame.GetFileLineNumber();
                    var column = frame.GetFileColumnNumber();
                    var isApp =
                        method
                            ?.DeclaringType?.Assembly.GetName()
                            .Name?.StartsWith("Conduit", StringComparison.OrdinalIgnoreCase)
                        ?? false;

                    frames.Add(
                        new LogcoreStackFrame
                        {
                            Function = function,
                            File = file,
                            Line = line,
                            Column = column,
                            InApp = isApp,
                        }
                    );

                    if (frames.Count >= MaxStackFrames)
                    {
                        break;
                    }
                }
            }

            if (frames.Count >= MaxStackFrames)
            {
                break;
            }
        }

        if (frames.Count == 0)
        {
            var st = new StackTrace(1, true);
            var stackFrames = st.GetFrames();
            if (stackFrames != null)
            {
                foreach (var frame in stackFrames)
                {
                    var method = frame.GetMethod();
                    var function =
                        method != null
                            ? (
                                method.DeclaringType != null
                                    ? $"{method.DeclaringType.FullName}.{method.Name}"
                                    : method.Name
                            )
                            : "<unknown>";
                    var file = frame.GetFileName() ?? "";
                    var line = frame.GetFileLineNumber();
                    var column = frame.GetFileColumnNumber();
                    var isApp =
                        method
                            ?.DeclaringType?.Assembly.GetName()
                            .Name?.StartsWith("Conduit", StringComparison.OrdinalIgnoreCase)
                        ?? false;

                    frames.Add(
                        new LogcoreStackFrame
                        {
                            Function = function,
                            File = file,
                            Line = line,
                            Column = column,
                            InApp = isApp,
                        }
                    );

                    if (frames.Count >= MaxStackFrames)
                    {
                        break;
                    }
                }
            }
        }

        return new LogcoreError
        {
            Type = exception.GetType().Name,
            Message = exception.Message,
            Stack = frames,
        };
    }

    private static (
        Dictionary<string, string>? Labels,
        Dictionary<string, object?>? Context
    ) ExtractProperties(LogEvent logEvent)
    {
        if (logEvent.Properties.Count == 0)
        {
            return (null, null);
        }

        var labels = new Dictionary<string, string>();
        var context = new Dictionary<string, object?>();

        foreach (var (key, value) in logEvent.Properties)
        {
            if (key is "TraceId" or "SpanId" or "ParentId")
            {
                continue;
            }

            var simplified = SimplifyPropertyValue(value);
            context[key] = simplified;

            if (labels.Count < MaxLabelCount && value is ScalarValue scalar && scalar.Value != null)
            {
                var strVal = scalar.Value.ToString();
                if (!string.IsNullOrEmpty(strVal))
                {
                    if (strVal.Length > MaxLabelLength)
                    {
                        strVal = strVal[..MaxLabelLength];
                    }
                    labels[key] = strVal;
                }
            }
        }

        return (labels.Count > 0 ? labels : null, context.Count > 0 ? context : null);
    }

    private static object? SimplifyPropertyValue(LogEventPropertyValue propertyValue)
    {
        return propertyValue switch
        {
            ScalarValue scalar => scalar.Value,
            SequenceValue sequence => sequence.Elements.Select(SimplifyPropertyValue).ToList(),
            StructureValue structure => structure.Properties.ToDictionary(
                p => p.Name,
                p => SimplifyPropertyValue(p.Value)
            ),
            DictionaryValue dictionary => dictionary.Elements.ToDictionary(
                kvp => kvp.Key.Value?.ToString() ?? "",
                kvp => SimplifyPropertyValue(kvp.Value)
            ),
            _ => propertyValue.ToString(),
        };
    }
}
