using System;
using System.Collections.Generic;
using System.Text.Json;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;

namespace Conduit.Infrastructure.Logging;

public static class LogcoreProbe
{
    public static void Run(LogcoreOptions? options = null)
    {
        options ??= LogcoreOptions.FromEnvironment();

        try
        {
            throw new InvalidOperationException("Logcore probe test error");
        }
        catch (Exception ex)
        {
            var parser = new MessageTemplateParser();
            var template = parser.Parse("Logcore probe test error");
            var logEvent = new LogEvent(
                DateTimeOffset.UtcNow,
                LogEventLevel.Error,
                ex,
                template,
                []
            );

            var entry = LogcoreFormatter.Format(logEvent, options);
            var json = JsonSerializer.Serialize(entry, LogcoreJsonOptions.Default);

            Console.WriteLine(json);

            Log.Error(ex, "Logcore probe test error");
        }
    }
}
