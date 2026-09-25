using Serilog;
using Serilog.Configuration;

namespace Conduit.Infrastructure.Logging;

public static class LogcoreSinkExtensions
{
    public static LoggerConfiguration Logcore(
        this LoggerSinkConfiguration sinkConfiguration,
        LogcoreOptions? options = null,
        LogcoreClient? client = null
    )
    {
        options ??= LogcoreOptions.FromEnvironment();
        return sinkConfiguration.Sink(new LogcoreSink(options, client));
    }
}
