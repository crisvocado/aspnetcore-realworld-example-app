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
    private const int MaxBatchSize = 200;
    private const int FlushThreshold = 20;
    private const int MaxRetries = 3;
    private const int CircuitBreakerThreshold = 10;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly LogcoreOptions _options;
    private readonly HttpClient _httpClient;
    private readonly Channel<LogcoreEntry> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _workerTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly string _ingestUrl;
    private int _consecutiveFailures;

    public LogcoreTransport(LogcoreOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _cts = new CancellationTokenSource();

        var channelOptions = new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        };
        _channel = Channel.CreateBounded<LogcoreEntry>(channelOptions);

        var url = _options.Url.TrimEnd('/');
        if (!url.EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase))
        {
            url += "/v1/logs";
        }
        _ingestUrl = url;

        _workerTask = Task.Run(ProcessQueueAsync);
    }

    public void Enqueue(LogcoreEntry entry)
    {
        if (!_options.Enabled)
        {
            return;
        }

        _channel.Writer.TryWrite(entry);
    }

    private async Task ProcessQueueAsync()
    {
        var batch = new List<LogcoreEntry>(FlushThreshold);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                while (batch.Count < FlushThreshold)
                {
                    if (batch.Count == 0)
                    {
                        if (
                            !await _channel.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false)
                        )
                        {
                            break;
                        }
                    }

                    if (_channel.Reader.TryRead(out var entry))
                    {
                        batch.Add(entry);
                        if (batch.Count >= MaxBatchSize)
                        {
                            break;
                        }
                    }
                    else
                    {
                        using var timeoutCts = new CancellationTokenSource(FlushInterval);
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                            _cts.Token,
                            timeoutCts.Token
                        );

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
                        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }

                if (batch.Count > 0)
                {
                    await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
                    try
                    {
                        await SendBatchWithRetryAsync([.. batch], _cts.Token).ConfigureAwait(false);
                        batch.Clear();
                    }
                    finally
                    {
                        _sendLock.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[Logcore] Unexpected error in transport worker: {ex.Message}"
                );
            }
        }

        // Drain remaining entries on shutdown
        await _sendLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            while (_channel.Reader.TryRead(out var remaining))
            {
                batch.Add(remaining);
                if (batch.Count >= MaxBatchSize)
                {
                    await SendBatchWithRetryAsync([.. batch], CancellationToken.None)
                        .ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await SendBatchWithRetryAsync([.. batch], CancellationToken.None)
                    .ConfigureAwait(false);
                batch.Clear();
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendBatchWithRetryAsync(
        List<LogcoreEntry> entries,
        CancellationToken cancellationToken
    )
    {
        if (entries.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            // No API key configured (e.g. unconfigured environment or local probe). Skip network dispatch.
            return;
        }

        if (Interlocked.CompareExchange(ref _consecutiveFailures, 0, 0) >= CircuitBreakerThreshold)
        {
            Console.Error.WriteLine("[Logcore] Circuit breaker tripped. Dropping batch.");
            while (_channel.Reader.TryRead(out _)) { }
            return;
        }

        var envelope = new LogcoreEnvelope { SchemaVersion = 1, Entries = entries };

        var json = JsonSerializer.Serialize(envelope);

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _ingestUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };

                if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                {
                    request.Headers.Add("x-api-key", _options.ApiKey);
                }

                using var response = await _httpClient
                    .SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    return;
                }

                var statusCode = (int)response.StatusCode;
                var responseBody = await response
                    .Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

                // Permanent rejection for 4xx except 429
                if (statusCode is >= 400 and < 500 and not 429)
                {
                    Console.Error.WriteLine(
                        $"[Logcore] Ingest rejected batch ({statusCode}): {responseBody}"
                    );
                    Interlocked.Increment(ref _consecutiveFailures);
                    return;
                }

                // 429 or 5xx -> retry
                if (attempt < MaxRetries)
                {
                    var delayMs = (int)(Math.Pow(2, attempt) * 250);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[Logcore] Ingest failed after {MaxRetries} retries ({statusCode}): {responseBody}"
                    );
                    Interlocked.Increment(ref _consecutiveFailures);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (attempt < MaxRetries)
                {
                    var delayMs = (int)(Math.Pow(2, attempt) * 250);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[Logcore] Network error delivering logs: {ex.Message}"
                    );
                    Interlocked.Increment(ref _consecutiveFailures);
                }
            }
        }
    }

    public async Task FlushPendingAsync(CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = new List<LogcoreEntry>();
            while (_channel.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
                if (batch.Count >= MaxBatchSize)
                {
                    await SendBatchWithRetryAsync([.. batch], cancellationToken)
                        .ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await SendBatchWithRetryAsync([.. batch], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task FlushAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _workerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // Best effort
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
            await _workerTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            // Best effort
        }
        _sendLock.Dispose();
        _cts.Dispose();
        _httpClient.Dispose();
    }
}
