using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace Conduit.Infrastructure.Logging;

public sealed partial class LogcoreSink : ILogEventSink, IDisposable, IAsyncDisposable
{
    private static LogcoreSink? _instance;
    public static LogcoreSink? Instance => _instance;

    private readonly LogcoreOptions _options;
    private readonly LogcoreTransport _transport;
    private readonly Channel<LogcoreEntry> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _workerTask;
    private readonly LogEventLevel _minLevel;

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ValidLabelKeyRegex();

    public LogcoreSink(LogcoreOptions? options = null, LogcoreTransport? transport = null)
    {
        _options = options ?? new LogcoreOptions();
        _transport = transport ?? new LogcoreTransport(_options);
        _minLevel = ParseMinLevel(_options.MinSeverity);

        var channelOptions = new BoundedChannelOptions(_options.MaxQueueSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        };
        _channel = Channel.CreateBounded<LogcoreEntry>(channelOptions);
        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(ProcessQueueAsync);
        _instance = this;
    }

    public LogcoreTransport Transport => _transport;

    public void Emit(LogEvent logEvent)
    {
        if (logEvent == null || logEvent.Level < _minLevel)
        {
            return;
        }

        try
        {
            var entry = CreateEntry(logEvent);
            _channel.Writer.TryWrite(entry);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Logcore] Emit error: {ex.Message}");
        }
    }

    private LogcoreEntry CreateEntry(LogEvent logEvent)
    {
        var timestamp = logEvent.Timestamp.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture
        );
        var severity = MapSeverity(logEvent.Level);
        var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = logEvent.Exception?.Message ?? "Log event";
        }
        if (message.Length > 65536)
        {
            message = message[..65536];
        }

        var (labels, context) = ExtractProperties(logEvent);
        var contextJson = context != null ? JsonSerializer.Serialize(context) : null;
        var insertId = ComputeInsertId(timestamp, _options.Service, severity, message, contextJson);

        var trace = ExtractTrace();
        var error = ExtractError(logEvent.Exception);

        return new LogcoreEntry
        {
            Timestamp = timestamp,
            Severity = severity,
            Message = message,
            Service = _options.Service,
            Env = _options.Env,
            InsertId = insertId,
            Trace = trace,
            Error = error,
            Labels = labels,
            Context = context,
        };
    }

    private static (
        Dictionary<string, string>? labels,
        Dictionary<string, object?>? context
    ) ExtractProperties(LogEvent logEvent)
    {
        if (logEvent.Properties.Count == 0)
        {
            return (null, null);
        }

        Dictionary<string, string>? labels = null;
        Dictionary<string, object?>? context = null;

        foreach (var (key, value) in logEvent.Properties)
        {
            if (
                key.Equals("TraceId", StringComparison.OrdinalIgnoreCase)
                || key.Equals("SpanId", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            if (
                value is ScalarValue scalar
                && ValidLabelKeyRegex().IsMatch(key)
                && (labels == null || labels.Count < 32)
            )
            {
                var valStr = scalar.Value?.ToString() ?? string.Empty;
                if (valStr.Length > 1024)
                {
                    valStr = valStr[..1024];
                }

                labels ??= new Dictionary<string, string>();
                labels[key] = valStr;
            }
            else
            {
                context ??= new Dictionary<string, object?>();
                context[key] = value.ToString();
            }
        }

        return (labels, context);
    }

    private static LogcoreTrace? ExtractTrace()
    {
        var activity = Activity.Current;
        if (activity == null || activity.TraceId == default)
        {
            return null;
        }

        var traceId = activity.TraceId.ToHexString();
        if (string.IsNullOrEmpty(traceId) || traceId.Length != 32)
        {
            return null;
        }

        var spanId = activity.SpanId != default ? activity.SpanId.ToHexString() : null;
        var sampled = (activity.ActivityTraceFlags & ActivityTraceFlags.Recorded) != 0;

        return new LogcoreTrace
        {
            TraceId = traceId,
            SpanId = spanId,
            Sampled = sampled,
        };
    }

    private static LogcoreError? ExtractError(Exception? exception)
    {
        if (exception == null)
        {
            return null;
        }

        var type = exception.GetType().FullName ?? exception.GetType().Name;
        if (type.Length > 256)
        {
            type = type[..256];
        }

        var message = exception.Message;
        if (message.Length > 65536)
        {
            message = message[..65536];
        }

        var stack = ExtractStackFrames(exception);

        return new LogcoreError
        {
            Type = type,
            Message = message,
            Stack = stack.Count > 0 ? stack : null,
        };
    }

    private static List<LogcoreStackFrame> ExtractStackFrames(Exception exception)
    {
        var frames = new List<LogcoreStackFrame>();
        var current = exception;
        var chain = new List<Exception>();

        while (current != null && chain.Count < 10)
        {
            chain.Add(current);
            current = current.InnerException;
        }

        // Innermost / root exception frames first
        chain.Reverse();

        foreach (var ex in chain)
        {
            var st = new StackTrace(ex, true);
            var stFrames = st.GetFrames();
            if (stFrames == null)
            {
                continue;
            }

            foreach (var frame in stFrames)
            {
                if (frames.Count >= 50)
                {
                    break;
                }

                var method = frame.GetMethod();
                var declaringType = method?.DeclaringType?.FullName;
                var methodName = method?.Name ?? "<unknown>";
                var function = declaringType != null ? $"{declaringType}.{methodName}" : methodName;
                if (function.Length > 256)
                {
                    function = function[..256];
                }

                var file = frame.GetFileName();
                if (file != null && file.Length > 1024)
                {
                    file = file[..1024];
                }

                int? line = frame.GetFileLineNumber();
                if (line <= 0)
                {
                    line = null;
                }

                int? col = frame.GetFileColumnNumber();
                if (col <= 0)
                {
                    col = null;
                }

                var inApp =
                    declaringType != null
                    && (
                        declaringType.StartsWith("Conduit", StringComparison.OrdinalIgnoreCase)
                        || (
                            file != null
                            && !file.Contains("/.nuget/")
                            && !file.Contains("/Microsoft.NETCore.App/")
                        )
                    );

                frames.Add(
                    new LogcoreStackFrame
                    {
                        Function = function,
                        File = file,
                        Line = line,
                        Column = col,
                        InApp = inApp,
                    }
                );
            }

            if (frames.Count >= 50)
            {
                break;
            }
        }

        return frames;
    }

    public static string ComputeInsertId(
        string timestamp,
        string service,
        string severity,
        string message,
        string? contextJson
    )
    {
        var raw = $"{timestamp}|{service}|{severity}|{message}|{contextJson ?? string.Empty}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hashBytes).ToLowerInvariant()[..32];
    }

    private static string MapSeverity(LogEventLevel level)
    {
        return level switch
        {
            LogEventLevel.Verbose => "DEBUG",
            LogEventLevel.Debug => "DEBUG",
            LogEventLevel.Information => "INFO",
            LogEventLevel.Warning => "WARNING",
            LogEventLevel.Error => "ERROR",
            LogEventLevel.Fatal => "CRITICAL",
            _ => "INFO",
        };
    }

    private static LogEventLevel ParseMinLevel(string? minSeverity)
    {
        if (string.IsNullOrWhiteSpace(minSeverity))
        {
            return LogEventLevel.Error;
        }

        return minSeverity.Trim().ToUpperInvariant() switch
        {
            "VERBOSE" or "DEBUG" => LogEventLevel.Debug,
            "INFO" or "INFORMATION" or "NOTICE" => LogEventLevel.Information,
            "WARN" or "WARNING" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            "CRITICAL" or "FATAL" or "ALERT" or "EMERGENCY" => LogEventLevel.Fatal,
            _ => LogEventLevel.Error,
        };
    }

    private async Task ProcessQueueAsync()
    {
        var batch = new List<LogcoreEntry>();
        var flushInterval = TimeSpan.FromSeconds(_options.FlushIntervalSeconds);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                while (batch.Count < _options.BatchSize && _channel.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count >= _options.BatchSize)
                {
                    await FlushBatchAsync(batch, _cts.Token);
                    continue;
                }

                using var timerCts = new CancellationTokenSource(flushInterval);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _cts.Token,
                    timerCts.Token
                );

                try
                {
                    if (await _channel.Reader.WaitToReadAsync(linkedCts.Token))
                    {
                        while (
                            batch.Count < _options.BatchSize
                            && _channel.Reader.TryRead(out var item)
                        )
                        {
                            batch.Add(item);
                        }

                        if (batch.Count >= _options.BatchSize)
                        {
                            await FlushBatchAsync(batch, _cts.Token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (batch.Count > 0)
                    {
                        await FlushBatchAsync(batch, _cts.Token);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[Logcore] Background worker error: {ex.Message}");
            }
        }

        // Drain any remaining items after cancellation
        while (_channel.Reader.TryRead(out var item))
        {
            batch.Add(item);
            if (batch.Count >= 200)
            {
                await FlushBatchAsync(batch, CancellationToken.None);
            }
        }

        if (batch.Count > 0)
        {
            await FlushBatchAsync(batch, CancellationToken.None);
        }
    }

    private async Task FlushBatchAsync(
        List<LogcoreEntry> batch,
        CancellationToken cancellationToken
    )
    {
        if (batch.Count == 0)
        {
            return;
        }

        var toSend = (IReadOnlyList<LogcoreEntry>)[.. batch];
        batch.Clear();
        await _transport.SendBatchAsync(toSend, cancellationToken);
    }

    public async Task FlushAsync()
    {
        var remaining = new List<LogcoreEntry>();
        while (_channel.Reader.TryRead(out var item))
        {
            remaining.Add(item);
            if (remaining.Count >= 200)
            {
                await _transport.SendBatchAsync([.. remaining], CancellationToken.None);
                remaining.Clear();
            }
        }

        if (remaining.Count > 0)
        {
            await _transport.SendBatchAsync([.. remaining], CancellationToken.None);
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            await _workerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // best-effort shutdown
        }

        await FlushAsync();
        _cts.Dispose();
        _transport.Dispose();

        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }
}

public static class LogcoreExtensions
{
    public static LoggerConfiguration Logcore(
        this LoggerSinkConfiguration sinkConfiguration,
        LogcoreOptions? options = null,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Error
    )
    {
        var sink = new LogcoreSink(options);
        return sinkConfiguration.Sink(sink, restrictedToMinimumLevel);
    }
}
