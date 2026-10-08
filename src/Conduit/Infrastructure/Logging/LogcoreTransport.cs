using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit.Infrastructure.Logging;

public sealed class LogcoreTransport : IDisposable
{
    private readonly LogcoreOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private int _consecutiveFailures;
    private const int CircuitBreakerThreshold = 10;
    private Action<string>? _captureCallback;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public LogcoreTransport(
        LogcoreOptions options,
        HttpClient? httpClient = null,
        Action<string>? captureCallback = null
    )
    {
        _options = options;
        _captureCallback = captureCallback;

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _ownsHttpClient = true;
        }
    }

    public void SetCaptureCallback(Action<string>? callback)
    {
        _captureCallback = callback;
    }

    public async Task<bool> SendBatchAsync(
        IReadOnlyList<LogcoreEntry> entries,
        CancellationToken cancellationToken = default
    )
    {
        if (entries == null || entries.Count == 0)
        {
            return true;
        }

        var envelope = new LogcoreEnvelope { SchemaVersion = 1, Entries = entries };

        string jsonPayload;
        try
        {
            jsonPayload = JsonSerializer.Serialize(envelope, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Logcore] Serialization error: {ex.Message}");
            return false;
        }

        if (_captureCallback != null)
        {
            try
            {
                _captureCallback(jsonPayload);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Logcore] Capture callback error: {ex.Message}");
                return false;
            }
        }

        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return false;
        }

        if (_consecutiveFailures >= CircuitBreakerThreshold)
        {
            return false;
        }

        var endpoint = _options.Url?.TrimEnd('/') + "/v1/logs";

        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (attempt > 0)
            {
                var delayMs = (int)(Math.Pow(2, attempt - 1) * 250);
                try
                {
                    await Task.Delay(delayMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Add("x-api-key", _options.ApiKey);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _consecutiveFailures = 0;
                    return true;
                }

                var statusCode = (int)response.StatusCode;
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                // Permanent 4xx error (except 429 rate limit)
                if (statusCode >= 400 && statusCode < 500 && statusCode != 429)
                {
                    Console.Error.WriteLine(
                        $"[Logcore] Permanent rejection ({statusCode}): {responseBody}"
                    );
                    _consecutiveFailures = 0;
                    return false;
                }

                // 5xx or 429: retry
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == 3)
                {
                    Console.Error.WriteLine(
                        $"[Logcore] Failed to deliver logs after retries: {ex.Message}"
                    );
                }
            }
        }

        _consecutiveFailures++;
        if (_consecutiveFailures >= CircuitBreakerThreshold)
        {
            Console.Error.WriteLine(
                "[Logcore] Circuit breaker tripped after 10 consecutive delivery failures. Pausing delivery."
            );
        }

        return false;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
