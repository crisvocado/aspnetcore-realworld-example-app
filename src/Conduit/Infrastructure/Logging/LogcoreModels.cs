using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Conduit.Infrastructure.Logging;

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

    [JsonPropertyName("source_project")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceProject { get; set; }

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
    public List<LogcoreStackFrame> Stack { get; set; } = [];
}

public sealed class LogcoreStackFrame
{
    [JsonPropertyName("function")]
    public string Function { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; set; }

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }

    [JsonPropertyName("inApp")]
    public bool InApp { get; set; }
}
