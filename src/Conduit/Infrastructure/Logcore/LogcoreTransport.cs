using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Conduit.Infrastructure.Logcore;

public sealed class LogcoreTransport : IDisposable
{
    private static readonly Lazy<LogcoreTransport> LazyInstance = new(() =>
        new LogcoreTransport(LogcoreConfig.Current)
    );
    public static LogcoreTransport Instance => LazyInstance.Value;

    private readonly LogcoreConfig _config;
    private readonly Channel<LogcoreEntry> _channel;
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private int _consecutiveFailures;
    private bool _disposed;

    public LogcoreTransport(LogcoreConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var channelOptions = new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        };
        _channel = Channel.CreateBounded<LogcoreEntry>(channelOptions);

        _workerTask = Task.Run(ProcessQueueAsync);

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    public void Enqueue(LogcoreEntry entry)
    {
        if (!_config.Enabled || _disposed)
        {
            return;
        }

        // Clamp message length
        if (entry.Message.Length > 65536)
        {
            entry.Message = entry.Message[..65536];
        }

        // Clamp labels
        if (entry.Labels != null)
        {
            if (entry.Labels.Count > 32)
            {
                entry.Labels = entry.Labels.Take(32).ToDictionary(k => k.Key, v => v.Value);
            }

            foreach (var key in entry.Labels.Keys.ToList())
            {
                if (entry.Labels[key]?.Length > 1024)
                {
                    entry.Labels[key] = entry.Labels[key][..1024];
                }
            }
        }

        _channel.Writer.TryWrite(entry);
    }

    private async Task ProcessQueueAsync()
    {
        var reader = _channel.Reader;
        var batch = new List<LogcoreEntry>(20);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (await reader.WaitToReadAsync(_cts.Token))
                {
                    var timer = Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);

                    while (batch.Count < 20 && reader.TryRead(out var item))
                    {
                        batch.Add(item);
                    }

                    while (batch.Count < 20 && !timer.IsCompleted)
                    {
                        var readTask = reader.WaitToReadAsync(_cts.Token).AsTask();
                        var completed = await Task.WhenAny(readTask, timer);
                        if (completed == readTask && await readTask)
                        {
                            while (batch.Count < 20 && reader.TryRead(out var item))
                            {
                                batch.Add(item);
                            }
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (batch.Count > 0)
                    {
                        await SendBatchWithRetryAsync(batch, _cts.Token);
                        batch.Clear();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Logcore] Worker loop error: {ex.Message}");
            }
        }
    }

    public async Task SendBatchWithRetryAsync(List<LogcoreEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return;
        }

        if (_consecutiveFailures >= 10)
        {
            DrainChannel();
            return;
        }

        for (var i = 0; i < entries.Count; i += 200)
        {
            var chunk = entries.Skip(i).Take(200).ToList();
            var success = await SendChunkWithRetryAsync(chunk, ct);
            if (success)
            {
                _consecutiveFailures = 0;
            }
            else
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= 10)
                {
                    Console.Error.WriteLine(
                        "[Logcore] Circuit breaker tripped after 10 consecutive failed flushes. Dropping buffer."
                    );
                    DrainChannel();
                    break;
                }
            }
        }
    }

    private async Task<bool> SendChunkWithRetryAsync(List<LogcoreEntry> chunk, CancellationToken ct)
    {
        var envelope = new LogcoreEnvelope { SchemaVersion = 1, Entries = chunk };

        var json = JsonSerializer.Serialize(envelope);

        for (var attempt = 0; attempt <= 3; attempt++)
        {
            if (attempt > 0)
            {
                var delayMs = (int)(Math.Pow(2, attempt - 1) * 250);
                try
                {
                    await Task.Delay(delayMs, ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _config.IngestUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };

                if (!string.IsNullOrEmpty(_config.ApiKey))
                {
                    request.Headers.Add("x-api-key", _config.ApiKey);
                }

                using var response = await _httpClient.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                var statusCode = (int)response.StatusCode;

                // 4xx other than 429 -> permanent rejection
                if (statusCode >= 400 && statusCode < 500 && statusCode != 429)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    Console.Error.WriteLine(
                        $"[Logcore] Permanent rejection ({response.StatusCode}): {errorBody}"
                    );
                    return false;
                }

                // 429 or 5xx -> retry
            }
            catch (HttpRequestException ex)
            {
                if (attempt == 3)
                {
                    Console.Error.WriteLine(
                        $"[Logcore] Flush failed after 3 retries: {ex.Message}"
                    );
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Logcore] Unexpected error during send: {ex.Message}");
                return false;
            }
        }

        return false;
    }

    private void DrainChannel()
    {
        while (_channel.Reader.TryRead(out _)) { }
    }

    public void Flush(TimeSpan? timeout = null)
    {
        if (_disposed)
        {
            return;
        }

        var limit = timeout ?? TimeSpan.FromSeconds(5);
        try
        {
            var remaining = new List<LogcoreEntry>();
            while (_channel.Reader.TryRead(out var item))
            {
                remaining.Add(item);
            }

            if (remaining.Count > 0)
            {
                using var cts = new CancellationTokenSource(limit);
                SendBatchWithRetryAsync(remaining, cts.Token).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Logcore] Flush error on exit: {ex.Message}");
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            if (e.ExceptionObject is Exception ex)
            {
                var entry = LogcoreSink.CreateEntryFromException(ex, "Unhandled exception");
                if (entry != null)
                {
                    Enqueue(entry);
                }
            }

            Flush(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Best effort
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            Flush(TimeSpan.FromSeconds(5));
            _workerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Ignore during shutdown
        }
        finally
        {
            _cts.Dispose();
            _httpClient.Dispose();
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        }
    }
}
