using System;
using System.Text;
using Serilog.Events;

namespace Conduit.Infrastructure.Logcore;

public sealed class LogcoreConfig
{
    public static LogcoreConfig Current { get; } = LoadFromEnvironment();

    public string Url { get; set; } = "https://logcore-dev-352942961463.us-east4.run.app";
    public string IngestUrl { get; set; } =
        "https://logcore-dev-352942961463.us-east4.run.app/v1/logs";
    public string ApiKey { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Service { get; set; } = "conduit";
    public string Env { get; set; } = "dev";
    public LogEventLevel MinSeverity { get; set; } = LogEventLevel.Error;

    public static LogcoreConfig LoadFromEnvironment()
    {
        var config = new LogcoreConfig();

        var url = Environment.GetEnvironmentVariable("LOGCORE_URL");
        if (!string.IsNullOrWhiteSpace(url))
        {
            config.Url = url.Trim();
        }

        var trimmedUrl = config.Url.TrimEnd('/');
        config.IngestUrl = trimmedUrl.EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase)
            ? trimmedUrl
            : $"{trimmedUrl}/v1/logs";

        config.ApiKey = Environment.GetEnvironmentVariable("LOGCORE_KEY")?.Trim() ?? string.Empty;

        var enabledStr = Environment.GetEnvironmentVariable("LOGCORE_ENABLED")?.Trim();
        if (!string.IsNullOrEmpty(enabledStr))
        {
            if (
                string.Equals(enabledStr, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(enabledStr, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(enabledStr, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(enabledStr, "off", StringComparison.OrdinalIgnoreCase)
            )
            {
                config.Enabled = false;
            }
            else
            {
                config.Enabled = true;
            }
        }

        var svc = Environment.GetEnvironmentVariable("LOGCORE_SERVICE")?.Trim();
        config.Service = SanitizeService(svc);

        var env = Environment.GetEnvironmentVariable("LOGCORE_ENV")?.Trim();
        if (string.IsNullOrEmpty(env))
        {
            env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")?.Trim();
        }

        if (string.IsNullOrEmpty(env))
        {
            env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")?.Trim();
        }

        config.Env = NormalizeEnv(env);

        var minSev = Environment.GetEnvironmentVariable("LOGCORE_MIN_SEVERITY")?.Trim();
        config.MinSeverity = ParseMinSeverity(minSev);

        return config;
    }

    private static string SanitizeService(string? svc)
    {
        if (string.IsNullOrWhiteSpace(svc))
        {
            return "conduit";
        }

        var lower = svc.ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var c in lower)
        {
            if (
                (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '.'
                || c == '_'
                || c == '-'
            )
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('-');
            }
        }

        var sanitized = sb.ToString();
        if (
            sanitized.Length == 0
            || !(
                (sanitized[0] >= 'a' && sanitized[0] <= 'z')
                || (sanitized[0] >= '0' && sanitized[0] <= '9')
            )
        )
        {
            sanitized = "s-" + sanitized;
        }

        if (sanitized.Length > 63)
        {
            sanitized = sanitized[..63];
        }

        return sanitized;
    }

    private static string NormalizeEnv(string? env)
    {
        if (string.IsNullOrWhiteSpace(env))
        {
            return "dev";
        }

        var lower = env.ToLowerInvariant().Trim();
        return lower switch
        {
            "prod" or "production" => "prod",
            "staging" => "staging",
            "test" or "testing" => "test",
            "local" => "local",
            _ => "dev",
        };
    }

    private static LogEventLevel ParseMinSeverity(string? minSev)
    {
        if (string.IsNullOrWhiteSpace(minSev))
        {
            return LogEventLevel.Error;
        }

        var lower = minSev.ToLowerInvariant().Trim();
        return lower switch
        {
            "verbose" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "info" or "information" => LogEventLevel.Information,
            "warn" or "warning" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "critical" or "fatal" => LogEventLevel.Fatal,
            _ => LogEventLevel.Error,
        };
    }
}
