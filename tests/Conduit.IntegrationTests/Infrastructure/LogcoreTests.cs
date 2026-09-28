using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Infrastructure.Logcore;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Conduit.IntegrationTests.Infrastructure;

public class LogcoreTests
{
    [Fact]
    public void Config_SanitizesServiceName()
    {
        Environment.SetEnvironmentVariable("LOGCORE_SERVICE", "My Test App_123");
        var config = LogcoreConfig.LoadFromEnvironment();
        Assert.Matches("^[a-z0-9][a-z0-9._-]{0,62}$", config.Service);
        Assert.Equal("my-test-app_123", config.Service);
        Environment.SetEnvironmentVariable("LOGCORE_SERVICE", null);
    }

    [Fact]
    public void Config_NormalizesEnv()
    {
        Environment.SetEnvironmentVariable("LOGCORE_ENV", "Production");
        var config = LogcoreConfig.LoadFromEnvironment();
        Assert.Equal("prod", config.Env);

        Environment.SetEnvironmentVariable("LOGCORE_ENV", "staging");
        config = LogcoreConfig.LoadFromEnvironment();
        Assert.Equal("staging", config.Env);

        Environment.SetEnvironmentVariable("LOGCORE_ENV", null);
    }

    [Fact]
    public void Config_ParsesIngestUrl()
    {
        Environment.SetEnvironmentVariable("LOGCORE_URL", "https://example.com/api");
        var config = LogcoreConfig.LoadFromEnvironment();
        Assert.Equal("https://example.com/api/v1/logs", config.IngestUrl);
        Environment.SetEnvironmentVariable("LOGCORE_URL", null);
    }

    [Fact]
    public void InsertId_IsDeterministic32HexChars()
    {
        var context = new Dictionary<string, object> { ["key1"] = "val1", ["key2"] = 123 };

        var id1 = LogcoreSink.ComputeInsertId(
            "2026-08-17T18:30:00.000Z",
            "conduit",
            "ERROR",
            "test error",
            context
        );
        var id2 = LogcoreSink.ComputeInsertId(
            "2026-08-17T18:30:00.000Z",
            "conduit",
            "ERROR",
            "test error",
            context
        );

        Assert.Equal(id1, id2);
        Assert.Equal(32, id1.Length);
        Assert.Matches("^[0-9a-f]{32}$", id1);
    }

    [Fact]
    public void ExtractError_CapturesRootInnermostFirstAndInApp()
    {
        Exception? ex = null;
        try
        {
            try
            {
                ThrowAppException();
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("Wrapped error", inner);
            }
        }
        catch (Exception caught)
        {
            ex = caught;
        }

        Assert.NotNull(ex);
        var error = LogcoreExtractors.ExtractError(ex);
        Assert.NotNull(error);
        Assert.Equal("InvalidOperationException", error.Type);
        Assert.Equal("Wrapped error", error.Message);
        Assert.NotNull(error.Stack);
        Assert.NotEmpty(error.Stack);

        var firstFrame = error.Stack[0];
        Assert.Contains(nameof(ThrowAppException), firstFrame.Function);
        Assert.True(firstFrame.InApp);
    }

    private static void ThrowAppException()
    {
        throw new InvalidOperationException("Root app failure");
    }

    [Fact]
    public void CreateEntry_MatchesWireShapeContract()
    {
        var config = new LogcoreConfig { Service = "conduit", Env = "dev" };

        var messageTemplate = new MessageTemplateParser().Parse(
            "Order processing failed for {UserId}"
        );
        var properties = new List<LogEventProperty> { new("UserId", new ScalarValue(42)) };

        Exception? exception = null;
        try
        {
            throw new InvalidOperationException("Something went wrong");
        }
        catch (Exception e)
        {
            exception = e;
        }

        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            exception,
            messageTemplate,
            properties
        );

        var entry = LogcoreSink.CreateEntry(logEvent, config);

        Assert.Equal("ERROR", entry.Severity);
        Assert.Equal("conduit", entry.Service);
        Assert.Equal("dev", entry.Env);
        Assert.Equal("Order processing failed for 42", entry.Message);
        Assert.Matches("^[0-9a-f]{32}$", entry.InsertId);
        Assert.NotNull(entry.Error);
        Assert.Equal("InvalidOperationException", entry.Error.Type);
        Assert.NotNull(entry.Context);
        Assert.Equal(42, Convert.ToInt32(entry.Context["UserId"]));

        var envelope = new LogcoreEnvelope { SchemaVersion = 1, Entries = [entry] };

        var json = JsonSerializer.Serialize(envelope);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        var entries = root.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());

        var firstEntry = entries[0];
        Assert.True(firstEntry.TryGetProperty("timestamp", out _));
        Assert.True(firstEntry.TryGetProperty("severity", out _));
        Assert.True(firstEntry.TryGetProperty("message", out _));
        Assert.True(firstEntry.TryGetProperty("service", out _));
        Assert.True(firstEntry.TryGetProperty("env", out _));
        Assert.True(firstEntry.TryGetProperty("insert_id", out _));
        Assert.True(firstEntry.TryGetProperty("error", out _));
        Assert.True(firstEntry.TryGetProperty("context", out _));
        Assert.False(firstEntry.TryGetProperty("source_project", out _));
        Assert.False(firstEntry.TryGetProperty("service_id", out _));
    }

    [Fact]
    public void Transport_SendsEnvelopeWithApiKeyHeader()
    {
        var handler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(handler);

        var config = new LogcoreConfig
        {
            ApiKey = "test-api-key",
            IngestUrl = "https://mock.logcore.test/v1/logs",
            Enabled = true,
        };

        using var transport = new LogcoreTransport(config, httpClient);

        var entry = new LogcoreEntry
        {
            Timestamp = "2026-08-17T18:30:00.000Z",
            Severity = "ERROR",
            Message = "Test message",
            Service = "conduit",
            Env = "dev",
            InsertId = "7c2e1a0b9d8f6e4c2a0b8d6f4e2c0a8b",
        };

        transport.Enqueue(entry);
        transport.Flush(TimeSpan.FromSeconds(2));

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal(
            "https://mock.logcore.test/v1/logs",
            handler.LastRequest.RequestUri?.ToString()
        );
        Assert.True(handler.LastRequest.Headers.Contains("x-api-key"));
        Assert.Contains("test-api-key", handler.LastRequest.Headers.GetValues("x-api-key"));

        Assert.NotNull(handler.LastRequestBody);
        using var doc = JsonDocument.Parse(handler.LastRequestBody);
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
    }

    [Fact]
    public void Probe_ReturnsTrueForProbeArgs()
    {
        Assert.True(LogcoreProbe.HandleCommandLineArgs(["--logcore-probe"]));
        Assert.True(LogcoreProbe.HandleCommandLineArgs(["--probe"]));
        Assert.False(LogcoreProbe.HandleCommandLineArgs(["--other"]));
        Assert.False(LogcoreProbe.HandleCommandLineArgs([]));
    }

    [Fact]
    public void Probe_Run_EmitsValidLogcoreEntry()
    {
        LogcoreEntry? emitted = null;
        void Handler(LogcoreEntry e) => emitted = e;

        LogcoreSink.Emitted += Handler;
        try
        {
            var config = LogcoreConfig.Current;
            var log = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(new LogcoreSink(config))
                .CreateLogger();

            Serilog.Log.Logger = log;
            LogcoreProbe.Run();

            Assert.NotNull(emitted);
            Assert.Equal("ERROR", emitted.Severity);
            Assert.Equal("Logcore probe test error message", emitted.Message);
            Assert.Equal("conduit", emitted.Service);
            Assert.NotNull(emitted.Error);
            Assert.Equal("InvalidOperationException", emitted.Error.Type);
            Assert.Equal("Logcore probe test exception", emitted.Error.Message);
            Assert.NotNull(emitted.Error.Stack);
            Assert.NotEmpty(emitted.Error.Stack);
            Assert.True(emitted.Error.Stack[0].InApp);
        }
        finally
        {
            LogcoreSink.Emitted -= Handler;
        }
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = request
                    .Content.ReadAsStringAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"ok\"}"),
                }
            );
        }
    }
}
