using Serilog.Configuration;
using Serilog.Events;

namespace Serilog;

public static class LogcoreLoggerConfigurationExtensions
{
    public static LoggerConfiguration Logcore(
        this LoggerSinkConfiguration sinkConfiguration,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Error
    )
    {
        return sinkConfiguration.Sink(
            new Conduit.Infrastructure.Logcore.LogcoreSink(),
            restrictedToMinimumLevel
        );
    }
}
