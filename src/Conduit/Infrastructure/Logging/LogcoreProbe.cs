using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Conduit.Infrastructure.Logging;

public static class LogcoreProbe
{
    private sealed class NoOpHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    public static void Run()
    {
        var options = LogcoreOptions.FromEnvironment();
        LogcoreEntry? captured = null;

        using var client = new LogcoreClient(
            options.Url,
            options.ApiKey,
            new NoOpHttpMessageHandler()
        );
        using var sink = new LogcoreSink(options, client, onEmit: entry => captured = entry);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            throw new InvalidOperationException("Logcore probe test error message");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Logcore probe test error message");
        }

        if (captured != null)
        {
            Console.WriteLine(JsonSerializer.Serialize(captured));
        }
    }
}
