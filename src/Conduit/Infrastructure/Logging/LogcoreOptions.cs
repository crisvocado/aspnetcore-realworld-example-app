using System;
using System.Text.RegularExpressions;

namespace Conduit.Infrastructure.Logging;

public sealed partial class LogcoreOptions
{
    public string? Url { get; set; } =
        Environment.GetEnvironmentVariable("LOGCORE_URL")
        ?? "https://logcore-dev-352942961463.us-east4.run.app";

    public string? ApiKey { get; set; } = Environment.GetEnvironmentVariable("LOGCORE_KEY");

    public bool Enabled { get; set; } =
        !string.Equals(
            Environment.GetEnvironmentVariable("LOGCORE_ENABLED"),
            "false",
            StringComparison.OrdinalIgnoreCase
        )
        && !string.Equals(
            Environment.GetEnvironmentVariable("LOGCORE_ENABLED"),
            "0",
            StringComparison.OrdinalIgnoreCase
        );

    public string Service { get; set; } =
        NormalizeService(Environment.GetEnvironmentVariable("LOGCORE_SERVICE"));

    public string Env { get; set; } =
        NormalizeEnv(
            Environment.GetEnvironmentVariable("LOGCORE_ENV")
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        );

    public string MinSeverity { get; set; } =
        Environment.GetEnvironmentVariable("LOGCORE_MIN_SEVERITY") ?? "ERROR";

    public int BatchSize { get; set; } = 20;

    public int FlushIntervalSeconds { get; set; } = 5;

    public int MaxQueueSize { get; set; } = 1000;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,62}$")]
    private static partial Regex ServiceNameRegex();

    public static string NormalizeService(string? service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return "conduit";
        }

        var normalized = service.Trim().ToLowerInvariant();
        if (ServiceNameRegex().IsMatch(normalized))
        {
            return normalized;
        }

        return "conduit";
    }

    public static string NormalizeEnv(string? env)
    {
        if (string.IsNullOrWhiteSpace(env))
        {
            return "dev";
        }

        var lower = env.Trim().ToLowerInvariant();
        return lower switch
        {
            "production" or "prod" => "prod",
            "staging" or "stage" => "staging",
            "development" or "dev" => "dev",
            "testing" or "test" => "test",
            "local" => "local",
            _ => "dev",
        };
    }
}
