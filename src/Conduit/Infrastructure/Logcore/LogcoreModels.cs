using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Conduit.Infrastructure.Logcore;

public sealed class LogcoreEnvelope
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("entries")]
    public List<LogcoreEntry> Entries { get; set; } = [];
}

public sealed class LogcoreEntry
{
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("service")]
    public string Service { get; set; } = string.Empty;

    [JsonPropertyName("env")]
    public string Env { get; set; } = string.Empty;

    [JsonPropertyName("insert_id")]
    public string InsertId { get; set; } = string.Empty;

    [JsonPropertyName("trace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LogcoreTrace? Trace { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LogcoreError? Error { get; set; }

    [JsonPropertyName("labels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Labels { get; set; }

    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Context { get; set; }
}

public sealed class LogcoreTrace
{
    [JsonPropertyName("traceId")]
    public string TraceId { get; set; } = string.Empty;

    [JsonPropertyName("spanId")]
    public string SpanId { get; set; } = string.Empty;

    [JsonPropertyName("sampled")]
    public bool Sampled { get; set; }
}

public sealed class LogcoreError
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("stack")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LogcoreStackFrame>? Stack { get; set; }
}

public sealed class LogcoreStackFrame
{
    [JsonPropertyName("function")]
    public string Function { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }

    [JsonPropertyName("inApp")]
    public bool InApp { get; set; }
}

public static class LogcoreExtractors
{
    public static LogcoreTrace? ExtractTrace()
    {
        var activity = Activity.Current;
        if (activity != null)
        {
            var traceId = activity.TraceId.ToHexString();
            var spanId = activity.SpanId.ToHexString();
            var sampled = activity.Recorded;

            if (!string.IsNullOrEmpty(traceId) && traceId != "00000000000000000000000000000000")
            {
                return new LogcoreTrace
                {
                    TraceId = traceId,
                    SpanId = spanId,
                    Sampled = sampled,
                };
            }
        }

        return null;
    }

    public static LogcoreError? ExtractError(Exception? ex)
    {
        if (ex == null)
        {
            return null;
        }

        var root = ex.GetBaseException() ?? ex;
        var frames = new List<LogcoreStackFrame>();

        void AddFramesFromException(Exception exception)
        {
            var st = new StackTrace(exception, true);
            var stackFrames = st.GetFrames();
            if (stackFrames == null)
            {
                return;
            }

            foreach (var frame in stackFrames)
            {
                if (frames.Count >= 50)
                {
                    break;
                }

                var method = frame.GetMethod();
                var declaringType = method?.DeclaringType;
                var typeName = declaringType?.FullName;
                var methodName = method?.Name ?? "<unknown>";
                var function = !string.IsNullOrEmpty(typeName)
                    ? $"{typeName}.{methodName}"
                    : methodName;
                var file = frame.GetFileName() ?? string.Empty;
                var line = frame.GetFileLineNumber();
                var column = frame.GetFileColumnNumber();

                var assemblyName = declaringType?.Assembly.GetName().Name ?? string.Empty;
                var inApp = IsInApp(assemblyName, file);

                frames.Add(
                    new LogcoreStackFrame
                    {
                        Function = function,
                        File = file,
                        Line = line,
                        Column = column,
                        InApp = inApp,
                    }
                );
            }
        }

        AddFramesFromException(root);

        if (!ReferenceEquals(ex, root) && frames.Count < 50)
        {
            AddFramesFromException(ex);
        }

        return new LogcoreError
        {
            Type = ex.GetType().Name,
            Message = ex.Message,
            Stack = frames.Count > 0 ? frames : null,
        };
    }

    private static bool IsInApp(string assemblyName, string fileName)
    {
        if (string.IsNullOrEmpty(assemblyName))
        {
            return false;
        }

        if (assemblyName.StartsWith("Conduit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (
            assemblyName.StartsWith("System", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("Serilog", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("Mediator", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("FluentValidation", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("Riok", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        return !string.IsNullOrEmpty(fileName);
    }
}
