using System;
using System.Linq;
using System.Text.Json;
using Serilog;

namespace Conduit.Infrastructure.Logcore;

public static class LogcoreProbe
{
    public static bool HandleCommandLineArgs(string[] args)
    {
        if (
            args.Contains("--logcore-probe", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--probe", StringComparer.OrdinalIgnoreCase)
        )
        {
            Run();
            return true;
        }

        return false;
    }

    public static void Run()
    {
        void Handler(LogcoreEntry entry)
        {
            var json = JsonSerializer.Serialize(entry);
            Console.WriteLine(json);
        }

        LogcoreSink.Emitted += Handler;
        try
        {
            try
            {
                throw new InvalidOperationException("Logcore probe test exception");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Logcore probe test error message");
            }

            LogcoreTransport.Instance.Flush(TimeSpan.FromSeconds(5));
        }
        finally
        {
            LogcoreSink.Emitted -= Handler;
        }
    }
}
