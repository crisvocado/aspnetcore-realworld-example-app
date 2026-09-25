using System;
using System.Text.RegularExpressions;

namespace Conduit.Infrastructure.Logging;

public sealed partial class LogcoreOptions
{
    private static readonly Regex ValidServiceRegex = GetValidServiceRegex();

    public bool Enabled { get; init; } = true;

    public string Url { get; init; } = "https://logcore-dev-352942961463.us-east4.run.app";

    public string? ApiKey { get; init; }

    public string Service { get; init; } = "conduit";

    public string Environment { get; init; } = "dev";

    public string MinSeverity { get; init; } = "ERROR";

    public static LogcoreOptions FromEnvironment()
    {
        var enabledStr = System.Environment.GetEnvironmentVariable("LOGCORE_ENABLED");
        var enabled =
            string.IsNullOrWhiteSpace(enabledStr) || !bool.TryParse(enabledStr, out var b) || b;

        var url = System.Environment.GetEnvironmentVariable("LOGCORE_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            url = "https://logcore-dev-352942961463.us-east4.run.app";
        }

        var apiKey = System.Environment.GetEnvironmentVariable("LOGCORE_KEY");

        var service = System.Environment.GetEnvironmentVariable("LOGCORE_SERVICE");
        if (string.IsNullOrWhiteSpace(service) || !ValidServiceRegex.IsMatch(service))
        {
            service = "conduit";
        }
        service = service.ToLowerInvariant();

        var env =
            System.Environment.GetEnvironmentVariable("LOGCORE_ENV")
            ?? System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "dev";

        env = NormalizeEnvironment(env);

        var minSeverity =
            System.Environment.GetEnvironmentVariable("LOGCORE_MIN_SEVERITY") ?? "ERROR";
        minSeverity = NormalizeSeverity(minSeverity);

        return new LogcoreOptions
        {
            Enabled = enabled,
            Url = url,
            ApiKey = apiKey,
            Service = service,
            Environment = env,
            MinSeverity = minSeverity,
        };
    }

    public static string NormalizeEnvironment(string? env)
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
            "test" or "testing" => "test",
            "local" => "local",
            _ => "dev",
        };
    }

    public static string NormalizeSeverity(string? severity)
    {
        if (string.IsNullOrWhiteSpace(severity))
        {
            return "ERROR";
        }

        var upper = severity.Trim().ToUpperInvariant();
        return upper switch
        {
            "VERBOSE" or "DEBUG" => "DEBUG",
            "INFO" or "INFORMATION" => "INFO",
            "WARN" or "WARNING" => "WARNING",
            "ERROR" => "ERROR",
            "FATAL" or "CRITICAL" => "CRITICAL",
            _ => "ERROR",
        };
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,62}$")]
    private static partial Regex GetValidServiceRegex();
}
