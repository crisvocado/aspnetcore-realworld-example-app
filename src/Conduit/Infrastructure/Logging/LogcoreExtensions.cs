using System;
using Serilog;
using Serilog.Configuration;

namespace Conduit.Infrastructure.Logging;

public static class LogcoreExtensions
{
    public static LoggerConfiguration Logcore(
        this LoggerSinkConfiguration sinkConfiguration,
        LogcoreOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(sinkConfiguration);
        return sinkConfiguration.Sink(new LogcoreSink(options));
    }
}
