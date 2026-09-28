using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Conduit.Infrastructure.Logging;

public sealed class LogcoreTransport : IAsyncDisposable, IDisposable
{
    private readonly LogcoreOptions _options;
    private readonly Channel<LogcoreEntry> _channel;
    private readonly HttpClient _httpClient;
    private readonly string _endpointUrl;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private int _consecutiveFailures;
    private bool _disposed;

    public LogcoreTransport(LogcoreOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _endpointUrl = FormatEndpointUrl(_options.Url);

        var channelOptions = new BoundedChannelOptions(_options.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        };
        _channel = Channel.CreateBounded<LogcoreEntry>(channelOptions);

        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _processingTask = Task.Run(ProcessQueueAsync);
    }

    public void Enqueue(LogcoreEntry entry)
    {
        if (_disposed || !_options.Enabled)
        {
            return;
        }

        // Circuit breaker check: if 10 consecutive failures, drop entries
        if (Volatile.Read(ref _consecutiveFailures) >= _options.CircuitBreakerThreshold)
        {
            return;
        }

        _channel.Writer.TryWrite(entry);
    }

    private async Task ProcessQueueAsync()
    {
        var batch = new List<LogcoreEntry>(_options.BatchSize);
        var token = _cts.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var reader = _channel.Reader;
                if (!await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    break;
                }

                var batchDeadline = DateTime.UtcNow.Add(_options.BatchInterval);

                while (batch.Count < _options.MaxBatchSize)
                {
                    if (reader.TryRead(out var entry))
                    {
                        batch.Add(entry);
                        if (batch.Count >= _options.BatchSize)
                        {
                            break;
                        }
                    }
                    else
                    {
                        var remaining = batchDeadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero)
                        {
                            break;
                        }

                        using var timeoutCts = new CancellationTokenSource(remaining);
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                            token,
                            timeoutCts.Token
                        );

                        try
                        {
                            if (await reader.WaitToReadAsync(linkedCts.Token).ConfigureAwait(false))
                            {
                                continue;
                            }
                            break;
                        }
                        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }

                if (batch.Count > 0)
                {
                    await SendBatchWithRetryAsync(batch, token).ConfigureAwait(false);
                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Logcore] Unexpected error in processing loop: {ex.Message}");
        }

        // Drain any remaining items on shutdown
        while (_channel.Reader.TryRead(out var entry))
        {
            batch.Add(entry);
            if (batch.Count >= _options.MaxBatchSize)
            {
                await SendBatchWithRetryAsync(batch, CancellationToken.None).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await SendBatchWithRetryAsync(batch, CancellationToken.None).ConfigureAwait(false);
            batch.Clear();
        }
    }

    public async Task SendBatchWithRetryAsync(
        List<LogcoreEntry> batch,
        CancellationToken cancellationToken
    )
    {
        if (batch.Count == 0 || string.IsNullOrWhiteSpace(_options.Key))
        {
            return;
        }

        if (Volatile.Read(ref _consecutiveFailures) >= _options.CircuitBreakerThreshold)
        {
            return;
        }

        var payload = new LogcorePayload { SchemaVersion = 1, Entries = batch };

        var json = JsonSerializer.Serialize(payload, LogcoreJsonOptions.Default);

        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpointUrl);
                request.Headers.Add("x-api-key", _options.Key);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await _httpClient
                    .SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    return;
                }

                var statusCode = (int)response.StatusCode;
                if (statusCode == 429 || statusCode >= 500)
                {
                    // Retryable
                    if (attempt < _options.MaxRetries)
                    {
                        var delayMs = (int)(250 * Math.Pow(2, attempt));
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    Interlocked.Increment(ref _consecutiveFailures);
                    Console.Error.WriteLine(
                        $"[Logcore] Delivery failed with status {statusCode} after {attempt + 1} attempts"
                    );
                    return;
                }

                // Permanent error (e.g. 400, 401, 403, 422) - do NOT retry
                var errorBody = await response
                    .Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                Console.Error.WriteLine(
                    $"[Logcore] Permanent delivery failure ({statusCode}): {errorBody}"
                );
                Interlocked.Increment(ref _consecutiveFailures);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (attempt < _options.MaxRetries)
                {
                    var delayMs = (int)(250 * Math.Pow(2, attempt));
                    try
                    {
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    continue;
                }

                Interlocked.Increment(ref _consecutiveFailures);
                Console.Error.WriteLine(
                    $"[Logcore] Network delivery failure after {attempt + 1} attempts: {ex.Message}"
                );
                return;
            }
        }
    }

    private static string FormatEndpointUrl(string url)
    {
        var trimmed = url.TrimEnd('/');
        return trimmed.EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}/v1/logs";
    }

    public async ValueTask DisposeAsync()
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
            await _processingTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore timeout on shutdown
        }

        _cts.Dispose();
        _httpClient.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
