using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Events;

namespace Conduit.Infrastructure.Logging;

public sealed class LogcoreSink : ILogEventSink, IDisposable, IAsyncDisposable
{
    private readonly LogcoreOptions _options;
    private readonly LogcoreClient _client;
    private readonly Action<LogcoreEntry>? _onEmit;
    private readonly Channel<LogcoreEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _backgroundWorker;
    private readonly EventHandler _processExitHandler;
    private readonly UnhandledExceptionEventHandler _unhandledExceptionHandler;
    private bool _disposed;

    public LogcoreSink(
        LogcoreOptions options,
        LogcoreClient? client = null,
        Action<LogcoreEntry>? onEmit = null
    )
    {
        _options = options;
        _client = client ?? new LogcoreClient(_options.Url, _options.ApiKey);
        _onEmit = onEmit;

        var channelOptions = new BoundedChannelOptions(_options.MaxQueueSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        };
        _channel = Channel.CreateBounded<LogcoreEntry>(channelOptions);
        _backgroundWorker = Task.Run(ProcessQueueAsync);

        _processExitHandler = (_, _) => FlushSync();
        _unhandledExceptionHandler = (_, _) => FlushSync();

        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
        AppDomain.CurrentDomain.UnhandledException += _unhandledExceptionHandler;
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Error)
        {
            return;
        }

        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            var entry = Transform(logEvent);
            _onEmit?.Invoke(entry);
            _channel.Writer.TryWrite(entry);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Logcore] Error emitting log event: {ex.Message}");
        }
    }

    public LogcoreEntry Transform(LogEvent logEvent)
    {
        var timestamp = logEvent
            .Timestamp.ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        var severity = logEvent.Level switch
        {
            LogEventLevel.Fatal => "CRITICAL",
            _ => "ERROR",
        };

        var rawMessage = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        var message = rawMessage.Length > 65536 ? rawMessage.Substring(0, 65536) : rawMessage;

        var labels = ExtractLabels(logEvent);
        var context = ExtractContext(logEvent);

        LogcoreTrace? trace = null;
        var currentActivity = Activity.Current;
        if (currentActivity != null && currentActivity.TraceId != default)
        {
            trace = new LogcoreTrace
            {
                TraceId = currentActivity.TraceId.ToHexString(),
                SpanId = currentActivity.SpanId.ToHexString(),
                Sampled = currentActivity.Recorded,
            };
        }

        LogcoreError? error = null;
        if (logEvent.Exception != null)
        {
            error = ExtractError(logEvent.Exception);
        }

        var insertId = ComputeInsertId(timestamp, _options.Service, severity, message, context);

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
            Labels = labels.Count > 0 ? labels : null,
            Context = context.Count > 0 ? context : null,
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
        var contextJson = string.Empty;
        if (context != null && context.Count > 0)
        {
            var sorted = context
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            contextJson = JsonSerializer.Serialize(sorted);
        }

        var payload = $"{timestamp}|{service}|{severity}|{message}|{contextJson}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hashBytes).ToLowerInvariant().Substring(0, 32);
    }

    public static LogcoreError ExtractError(Exception ex)
    {
        var chain = new List<Exception>();
        var current = ex;
        while (current != null)
        {
            chain.Add(current);
            current = current.InnerException;
        }
        // Root exception first
        chain.Reverse();

        var allFrames = new List<LogcoreStackFrame>();

        foreach (var exc in chain)
        {
            var stackTrace = new StackTrace(exc, true);
            var frames = stackTrace.GetFrames();
            if (frames != null && frames.Length > 0)
            {
                foreach (var frame in frames)
                {
                    var method = frame.GetMethod();
                    var function =
                        method != null
                            ? (
                                method.DeclaringType != null
                                    ? $"{method.DeclaringType.Name}.{method.Name}"
                                    : method.Name
                            )
                            : "<anon>";

                    var file = frame.GetFileName();
                    var line = frame.GetFileLineNumber();
                    var column = frame.GetFileColumnNumber();
                    var inApp = IsInAppFrame(method, file);

                    allFrames.Add(
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
            else if (!string.IsNullOrWhiteSpace(exc.StackTrace))
            {
                var parsed = ParseStackTraceString(exc.StackTrace);
                allFrames.AddRange(parsed);
            }
        }

        var clampedFrames = allFrames.Take(50).ToList();

        return new LogcoreError
        {
            Type = ex.GetType().Name,
            Message = ex.Message,
            Stack = clampedFrames,
        };
    }

    private static bool IsInAppFrame(MethodBase? method, string? file)
    {
        if (method == null)
        {
            return false;
        }

        var typeName = method.DeclaringType?.FullName ?? string.Empty;
        if (
            typeName.StartsWith("System.", StringComparison.Ordinal)
            || typeName.StartsWith("Microsoft.", StringComparison.Ordinal)
            || typeName.StartsWith("Serilog.", StringComparison.Ordinal)
            || typeName.StartsWith("Mediator.", StringComparison.Ordinal)
            || typeName.StartsWith("Bullseye.", StringComparison.Ordinal)
            || typeName.StartsWith("SimpleExec.", StringComparison.Ordinal)
            || typeName.StartsWith("Xunit.", StringComparison.Ordinal)
            || typeName.StartsWith("xunit.", StringComparison.Ordinal)
        )
        {
            return false;
        }

        return true;
    }

    private static List<LogcoreStackFrame> ParseStackTraceString(string stackTraceStr)
    {
        var frames = new List<LogcoreStackFrame>();
        var lines = stackTraceStr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("at ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            line = line.Substring(3).Trim();

            string function;
            string? file = null;
            var lineNum = 0;
            var colNum = 0;

            var inIdx = line.IndexOf(" in ", StringComparison.Ordinal);
            if (inIdx >= 0)
            {
                function = line.Substring(0, inIdx).Trim();
                var filePart = line.Substring(inIdx + 4).Trim();
                var lineIdx = filePart.LastIndexOf(":line ", StringComparison.Ordinal);
                if (lineIdx >= 0)
                {
                    file = filePart.Substring(0, lineIdx).Trim();
                    var lineStr = filePart.Substring(lineIdx + 6).Trim();
                    _ = int.TryParse(lineStr, out lineNum);
                }
                else
                {
                    file = filePart;
                }
            }
            else
            {
                function = line;
            }

            var parenIdx = function.IndexOf('(');
            if (parenIdx > 0)
            {
                function = function.Substring(0, parenIdx);
            }

            var inApp =
                !function.StartsWith("System.", StringComparison.Ordinal)
                && !function.StartsWith("Microsoft.", StringComparison.Ordinal)
                && !function.StartsWith("Serilog.", StringComparison.Ordinal);

            frames.Add(
                new LogcoreStackFrame
                {
                    Function = function,
                    File = file,
                    Line = lineNum,
                    Column = colNum,
                    InApp = inApp,
                }
            );
        }

        return frames;
    }

    private static Dictionary<string, string> ExtractLabels(LogEvent logEvent)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in logEvent.Properties)
        {
            if (labels.Count >= 32)
            {
                break;
            }

            if (property.Key.Equals("SourceContext", StringComparison.OrdinalIgnoreCase))
            {
                var val = property.Value.ToString().Trim('"');
                if (val.Length > 1024)
                {
                    val = val.Substring(0, 1024);
                }
                labels["logger"] = val;
                continue;
            }

            if (property.Value is ScalarValue scalar)
            {
                var val = scalar.Value?.ToString() ?? string.Empty;
                if (val.Length > 1024)
                {
                    val = val.Substring(0, 1024);
                }
                labels[property.Key] = val;
            }
        }

        return labels;
    }

    private static Dictionary<string, object> ExtractContext(LogEvent logEvent)
    {
        var context = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var property in logEvent.Properties)
        {
            if (context.Count >= 32)
            {
                break;
            }

            if (property.Key.Equals("SourceContext", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value is ScalarValue scalar)
            {
                if (scalar.Value != null)
                {
                    context[property.Key] = scalar.Value;
                }
            }
            else
            {
                context[property.Key] = property.Value.ToString();
            }
        }

        return context;
    }

    private async Task ProcessQueueAsync()
    {
        var batch = new List<LogcoreEntry>(20);
        var consecutiveFailures = 0;

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(_options.FlushInterval);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _cts.Token,
                    timeoutCts.Token
                );

                while (batch.Count < _options.BatchSize)
                {
                    if (_channel.Reader.TryRead(out var entry))
                    {
                        batch.Add(entry);
                    }
                    else
                    {
                        try
                        {
                            if (
                                await _channel
                                    .Reader.WaitToReadAsync(linkedCts.Token)
                                    .ConfigureAwait(false)
                            )
                            {
                                if (_channel.Reader.TryRead(out var nextEntry))
                                {
                                    batch.Add(nextEntry);
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }

                if (batch.Count > 0)
                {
                    if (consecutiveFailures >= _options.MaxConsecutiveFailures)
                    {
                        Console.Error.WriteLine(
                            "[Logcore] Circuit breaker active after consecutive failures. Dropping batch."
                        );
                        while (_channel.Reader.TryRead(out _)) { }
                        batch.Clear();
                        await Task.Delay(5000, _cts.Token).ConfigureAwait(false);
                        consecutiveFailures = 0;
                        continue;
                    }

                    var success = await _client
                        .SendBatchAsync(batch, _cts.Token)
                        .ConfigureAwait(false);
                    if (success)
                    {
                        consecutiveFailures = 0;
                    }
                    else
                    {
                        consecutiveFailures++;
                    }
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Logcore] Background processing error: {ex.Message}");
            }
        }

        DrainRemaining();
    }

    private void DrainRemaining()
    {
        var batch = new List<LogcoreEntry>(200);
        while (_channel.Reader.TryRead(out var remaining) && batch.Count < 200)
        {
            batch.Add(remaining);
        }

        if (batch.Count > 0)
        {
            try
            {
                using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                _client.SendBatchAsync(batch, flushCts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                // Best effort during exit
            }
        }
    }

    private void FlushSync()
    {
        DrainRemaining();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        AppDomain.CurrentDomain.UnhandledException -= _unhandledExceptionHandler;

        _channel.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            _backgroundWorker.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Ignore during shutdown
        }

        _cts.Dispose();
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        AppDomain.CurrentDomain.UnhandledException -= _unhandledExceptionHandler;

        _channel.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _backgroundWorker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore during shutdown
        }

        _cts.Dispose();
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
