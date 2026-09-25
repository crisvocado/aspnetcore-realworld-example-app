using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Conduit.Infrastructure.Logging;

public static class LogcoreProbe
{
    public static bool RunIfRequested(WebApplication app, string[] args)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(args);

        if (!args.Contains("--logcore-probe"))
        {
            return false;
        }

        Run(app.Services);
        return true;
    }

    public static void Run(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("LogcoreProbe");
        try
        {
            throw new InvalidOperationException("Logcore probe test error");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Logcore probe test event");
        }

        if (!string.IsNullOrEmpty(LogcoreSink.LastEmittedJson))
        {
            Console.WriteLine(LogcoreSink.LastEmittedJson);
        }

        LogcoreSink.Instance?.FlushAsync().GetAwaiter().GetResult();

        Log.CloseAndFlush();
    }
}
