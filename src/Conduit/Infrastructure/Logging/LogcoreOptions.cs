using System;
using System.Text.RegularExpressions;

namespace Conduit.Infrastructure.Logging;

public sealed partial class LogcoreOptions
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,62}$")]
    private static partial Regex ServiceNameRegex();

    public string Url { get; set; } = "https://logcore-dev-352942961463.us-east4.run.app";

    public string ApiKey { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string Service { get; set; } = "conduit";

    public string Env { get; set; } = "dev";

    public string MinSeverity { get; set; } = "ERROR";

    public int BatchSize { get; set; } = 20;

    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int MaxQueueSize { get; set; } = 1000;

    public int MaxRetries { get; set; } = 3;

    public int MaxConsecutiveFailures { get; set; } = 10;

    public static LogcoreOptions FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("LOGCORE_URL");
        var key = Environment.GetEnvironmentVariable("LOGCORE_KEY");
        var enabledStr = Environment.GetEnvironmentVariable("LOGCORE_ENABLED");
        var service = Environment.GetEnvironmentVariable("LOGCORE_SERVICE");
        var env =
            Environment.GetEnvironmentVariable("LOGCORE_ENV")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var minSev = Environment.GetEnvironmentVariable("LOGCORE_MIN_SEVERITY");

        var enabled = true;
        if (!string.IsNullOrWhiteSpace(enabledStr))
        {
            if (bool.TryParse(enabledStr, out var parsedBool))
            {
                enabled = parsedBool;
            }
            else if (enabledStr.Trim() == "0")
            {
                enabled = false;
            }
        }

        return new LogcoreOptions
        {
            Url = string.IsNullOrWhiteSpace(url)
                ? "https://logcore-dev-352942961463.us-east4.run.app"
                : url.Trim(),
            ApiKey = key?.Trim() ?? string.Empty,
            Enabled = enabled,
            Service = NormalizeService(service),
            Env = NormalizeEnv(env),
            MinSeverity = string.IsNullOrWhiteSpace(minSev)
                ? "ERROR"
                : minSev.Trim().ToUpperInvariant(),
        };
    }

    public static string NormalizeService(string? rawService)
    {
        if (string.IsNullOrWhiteSpace(rawService))
        {
            return "conduit";
        }

        var trimmed = rawService.Trim().ToLowerInvariant();
        if (ServiceNameRegex().IsMatch(trimmed))
        {
            return trimmed;
        }

        return "conduit";
    }

    public static string NormalizeEnv(string? rawEnv)
    {
        if (string.IsNullOrWhiteSpace(rawEnv))
        {
            return "dev";
        }

        var lower = rawEnv.Trim().ToLowerInvariant();
        return lower switch
        {
            "prod" or "production" => "prod",
            "staging" or "stage" => "staging",
            "dev" or "development" => "dev",
            "test" or "testing" => "test",
            "local" => "local",
            _ => "dev",
        };
    }
}
