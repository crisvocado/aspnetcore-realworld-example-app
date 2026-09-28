using System;
using System.Text.RegularExpressions;

namespace Conduit.Infrastructure.Logging;

public sealed partial class LogcoreOptions
{
    private const string DefaultEndpoint = "https://logcore-dev-352942961463.us-east4.run.app";
    private const string DefaultService = "conduit";
    private const string DefaultEnv = "dev";
    private const string DefaultMinSeverity = "ERROR";

    public string Url { get; set; } = DefaultEndpoint;
    public string? Key { get; set; }
    public bool Enabled { get; set; } = true;
    public string Service { get; set; } = DefaultService;
    public string Env { get; set; } = DefaultEnv;
    public string MinSeverity { get; set; } = DefaultMinSeverity;

    public int BatchSize { get; set; } = 20;
    public TimeSpan BatchInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxBatchSize { get; set; } = 200;
    public int BufferCapacity { get; set; } = 1000;
    public int MaxRetries { get; set; } = 3;
    public int CircuitBreakerThreshold { get; set; } = 10;

    public static LogcoreOptions FromEnvironment()
    {
        var options = new LogcoreOptions();

        var url = Environment.GetEnvironmentVariable("LOGCORE_URL");
        if (!string.IsNullOrWhiteSpace(url))
        {
            options.Url = url.Trim();
        }

        var key = Environment.GetEnvironmentVariable("LOGCORE_KEY");
        if (!string.IsNullOrWhiteSpace(key))
        {
            options.Key = key.Trim();
        }

        var enabledStr = Environment.GetEnvironmentVariable("LOGCORE_ENABLED");
        if (!string.IsNullOrWhiteSpace(enabledStr))
        {
            if (bool.TryParse(enabledStr, out var parsedBool))
            {
                options.Enabled = parsedBool;
            }
            else if (enabledStr == "0")
            {
                options.Enabled = false;
            }
            else if (enabledStr == "1")
            {
                options.Enabled = true;
            }
        }
        else
        {
            options.Enabled = true;
        }

        var service = Environment.GetEnvironmentVariable("LOGCORE_SERVICE");
        if (!string.IsNullOrWhiteSpace(service))
        {
            options.Service = SanitizeService(service.Trim());
        }

        var env =
            Environment.GetEnvironmentVariable("LOGCORE_ENV")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(env))
        {
            options.Env = NormalizeEnv(env.Trim());
        }

        var minSeverity = Environment.GetEnvironmentVariable("LOGCORE_MIN_SEVERITY");
        if (!string.IsNullOrWhiteSpace(minSeverity))
        {
            options.MinSeverity = minSeverity.Trim().ToUpperInvariant();
        }

        return options;
    }

    public static string NormalizeEnv(string env)
    {
        var lower = env.ToLowerInvariant();
        return lower switch
        {
            "production" or "prod" => "prod",
            "staging" or "stage" => "staging",
            "development" or "dev" => "dev",
            "test" or "testing" => "test",
            "local" => "local",
            _ => "dev",
        };
    }

    public static string SanitizeService(string service)
    {
        var lower = service.ToLowerInvariant();
        var sanitized = ServiceRegex().Replace(lower, "");
        if (sanitized.Length == 0)
        {
            return DefaultService;
        }
        if (!char.IsLetterOrDigit(sanitized[0]))
        {
            sanitized = "s" + sanitized;
        }
        if (sanitized.Length > 63)
        {
            sanitized = sanitized[..63];
        }
        return sanitized;
    }

    [GeneratedRegex(@"[^a-z0-9._-]")]
    private static partial Regex ServiceRegex();
}
