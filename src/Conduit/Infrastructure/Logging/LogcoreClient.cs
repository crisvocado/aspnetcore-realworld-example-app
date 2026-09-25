using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit.Infrastructure.Logging;

public class LogcoreClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private readonly string _endpointUrl;
    private readonly string _apiKey;

    public LogcoreClient(string baseUrl, string apiKey, HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey;
        var cleanBaseUrl = baseUrl.TrimEnd('/');
        _endpointUrl = cleanBaseUrl.EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase)
            ? cleanBaseUrl
            : $"{cleanBaseUrl}/v1/logs";

        if (handler != null)
        {
            _httpClient = new HttpClient(handler);
            _disposeClient = true;
        }
        else
        {
            _httpClient = new HttpClient(
                new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                }
            )
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
            _disposeClient = true;
        }
    }

    public async Task<bool> SendBatchAsync(
        IReadOnlyList<LogcoreEntry> entries,
        CancellationToken cancellationToken = default
    )
    {
        if (entries.Count == 0)
        {
            return true;
        }

        var envelope = new LogcoreEnvelope { SchemaVersion = 1, Entries = [.. entries] };

        var json = JsonSerializer.Serialize(envelope);

        const int maxRetries = 3;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpointUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };

                if (!string.IsNullOrEmpty(_apiKey))
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
                }

                using var response = await _httpClient
                    .SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                var statusCode = (int)response.StatusCode;
                if (statusCode == 429 || statusCode >= 500)
                {
                    if (attempt < maxRetries)
                    {
                        var delayMs = (1 << attempt) * 250;
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var errBody = await response
                        .Content.ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);
                    Console.Error.WriteLine(
                        $"[Logcore] Delivery failed with status {statusCode} after {maxRetries} retries: {errBody}"
                    );
                    return false;
                }

                // Permanent rejection for other 4xx status codes
                var rejectBody = await response
                    .Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                Console.Error.WriteLine(
                    $"[Logcore] Permanent rejection ({statusCode}): {rejectBody}"
                );
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
                when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
            {
                if (attempt < maxRetries && !cancellationToken.IsCancellationRequested)
                {
                    var delayMs = (1 << attempt) * 250;
                    try
                    {
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                    continue;
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    Console.Error.WriteLine(
                        $"[Logcore] Network error after {attempt} retries: {ex.Message}"
                    );
                }
                return false;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Logcore] Unexpected error sending logs: {ex.Message}");
                return false;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
