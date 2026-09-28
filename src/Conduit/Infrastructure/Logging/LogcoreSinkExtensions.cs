using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace Conduit.Infrastructure.Logging;

public static class LogcoreSinkExtensions
{
    public static LoggerConfiguration Logcore(
        this LoggerSinkConfiguration sinkConfiguration,
        LogcoreOptions? options = null,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Error
    )
    {
        options ??= LogcoreOptions.FromEnvironment();
        if (!options.Enabled)
        {
            return sinkConfiguration.Sink(new NullSink(), restrictedToMinimumLevel);
        }

        var sink = new LogcoreSink(options);
        return sinkConfiguration.Sink(sink, restrictedToMinimumLevel);
    }

    private sealed class NullSink : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent) { }
    }
}
