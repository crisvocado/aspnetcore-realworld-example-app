using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Conduit.Infrastructure.Logging;

public static class LogcoreProbe
{
    public static async Task RunAsync()
    {
        string? capturedJson = null;

        var originalOut = Console.Out;
        Console.SetOut(TextWriter.Null);

        try
        {
            using var loggerFactory = LoggerFactory.Create(_ => { });
            loggerFactory.AddSerilogLogging();

            LogcoreSink.Instance?.Transport.SetCaptureCallback(json => capturedJson = json);

            try
            {
                throw new InvalidOperationException(
                    "Logcore probe test error for structured logging verification"
                );
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Logcore probe test error for structured logging verification");
            }

            if (LogcoreSink.Instance != null)
            {
                await LogcoreSink.Instance.FlushAsync();
            }

            await Log.CloseAndFlushAsync();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        if (!string.IsNullOrEmpty(capturedJson))
        {
            Console.WriteLine(capturedJson);
            return;
        }

        Console.Error.WriteLine("[Logcore Probe] Failed: No payload was captured.");
        Environment.ExitCode = 1;
    }
}
