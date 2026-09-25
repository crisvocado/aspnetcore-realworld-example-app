using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Infrastructure.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Conduit.IntegrationTests.Infrastructure.Logging;

public class LogcoreTests
{
    [Fact]
    public void Expect_LogcoreOptions_Defaults()
    {
        var options = new LogcoreOptions();

        Assert.True(options.Enabled);
        Assert.Equal("https://logcore-dev-352942961463.us-east4.run.app", options.Url);
        Assert.Null(options.ApiKey);
        Assert.Equal("conduit", options.Service);
        Assert.Equal("dev", options.Environment);
        Assert.Equal("ERROR", options.MinSeverity);
    }

    [Theory]
    [InlineData("production", "prod")]
    [InlineData("PROD", "prod")]
    [InlineData("staging", "staging")]
    [InlineData("test", "test")]
    [InlineData("local", "local")]
    [InlineData("development", "dev")]
    [InlineData("unknown", "dev")]
    public void Expect_NormalizeEnvironment(string input, string expected)
    {
        Assert.Equal(expected, LogcoreOptions.NormalizeEnvironment(input));
    }

    [Theory]
    [InlineData("error", "ERROR")]
    [InlineData("fatal", "CRITICAL")]
    [InlineData("critical", "CRITICAL")]
    [InlineData("warn", "WARNING")]
    [InlineData("warning", "WARNING")]
    [InlineData("info", "INFO")]
    [InlineData("information", "INFO")]
    [InlineData("debug", "DEBUG")]
    public void Expect_NormalizeSeverity(string input, string expected)
    {
        Assert.Equal(expected, LogcoreOptions.NormalizeSeverity(input));
    }

    [Fact]
    public void Expect_InsertId_Is_32_Hex_Chars_And_Deterministic()
    {
        var timestamp = "2026-08-17T18:30:00.000Z";
        var service = "conduit";
        var severity = "ERROR";
        var message = "test error";
        var context = new Dictionary<string, object?> { ["key"] = "value", ["num"] = 42 };

        var insertId1 = LogcoreEntryBuilder.ComputeInsertId(
            timestamp,
            service,
            severity,
            message,
            context
        );
        var insertId2 = LogcoreEntryBuilder.ComputeInsertId(
            timestamp,
            service,
            severity,
            message,
            context
        );

        Assert.Equal(32, insertId1.Length);
        Assert.Equal(insertId1, insertId2);
        Assert.Matches("^[0-9a-f]{32}$", insertId1);
    }

    [Fact]
    public void Expect_BuildEntry_Formats_Correctly()
    {
        var options = new LogcoreOptions { Service = "conduit", Environment = "prod" };

        Exception thrownException;
        try
        {
            throw new InvalidOperationException("Inner failure");
        }
        catch (Exception ex)
        {
            thrownException = ex;
        }

        var messageTemplate = new MessageTemplateParser().Parse("Order {OrderId} failed");
        var properties = new List<LogEventProperty>
        {
            new("OrderId", new ScalarValue(12345)),
            new("SourceContext", new ScalarValue("Conduit.Features.Orders")),
        };

        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            thrownException,
            messageTemplate,
            properties
        );

        var entry = LogcoreEntryBuilder.BuildEntry(logEvent, options);

        Assert.Equal("ERROR", entry.Severity);
        Assert.Equal("Order 12345 failed", entry.Message);
        Assert.Equal("conduit", entry.Service);
        Assert.Equal("prod", entry.Env);
        Assert.Null(entry.SourceProject);
        Assert.Matches("^[0-9a-f]{32}$", entry.InsertId);

        Assert.NotNull(entry.Error);
        Assert.Equal("InvalidOperationException", entry.Error.Type);
        Assert.Equal("Inner failure", entry.Error.Message);
        Assert.NotEmpty(entry.Error.Stack);
        Assert.True(entry.Error.Stack.Count <= 50);

        // Innermost frame at index 0
        var topFrame = entry.Error.Stack[0];
        Assert.Contains("Expect_BuildEntry_Formats_Correctly", topFrame.Function);
        Assert.True(topFrame.Line > 0);

        Assert.NotNull(entry.Labels);
        Assert.Equal("12345", entry.Labels["OrderId"]);
        Assert.Equal("Conduit.Features.Orders", entry.Labels["SourceContext"]);

        Assert.NotNull(entry.Context);
        Assert.Equal(12345, Convert.ToInt32(entry.Context["OrderId"]));
    }

    [Fact]
    public void Expect_Envelope_Serialization_Matches_Contract()
    {
        var entry = new LogcoreEntry
        {
            Timestamp = "2026-08-17T18:30:00.000Z",
            Severity = "ERROR",
            Message = "checkout failed",
            Service = "conduit",
            Env = "dev",
            InsertId = "7c2e1a0b9d8f6e4c2a0b8d6f4e2c0a8b",
            Trace = new LogcoreTrace
            {
                TraceId = "4bf92f3577b34da6a3ce929d0e0e4736",
                SpanId = "00f067aa0ba902b7",
                Sampled = true,
            },
            Error = new LogcoreError
            {
                Type = "InvalidOperationException",
                Message = "Operation failed",
                Stack =
                [
                    new LogcoreStackFrame
                    {
                        Function = "Checkout",
                        File = "/workspace/App.cs",
                        Line = 42,
                        Column = 10,
                        InApp = true,
                    },
                ],
            },
        };

        var envelope = new LogcoreEnvelope { SchemaVersion = 1, Entries = [entry] };

        var json = JsonSerializer.Serialize(envelope);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        var entries = root.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());

        var firstEntry = entries[0];
        Assert.Equal("2026-08-17T18:30:00.000Z", firstEntry.GetProperty("timestamp").GetString());
        Assert.Equal("ERROR", firstEntry.GetProperty("severity").GetString());
        Assert.Equal("checkout failed", firstEntry.GetProperty("message").GetString());
        Assert.Equal("conduit", firstEntry.GetProperty("service").GetString());
        Assert.Equal("dev", firstEntry.GetProperty("env").GetString());
        Assert.Equal(
            "7c2e1a0b9d8f6e4c2a0b8d6f4e2c0a8b",
            firstEntry.GetProperty("insert_id").GetString()
        );
        Assert.False(firstEntry.TryGetProperty("source_project", out _));

        var trace = firstEntry.GetProperty("trace");
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", trace.GetProperty("traceId").GetString());
        Assert.Equal("00f067aa0ba902b7", trace.GetProperty("spanId").GetString());
        Assert.True(trace.GetProperty("sampled").GetBoolean());

        var error = firstEntry.GetProperty("error");
        Assert.Equal("InvalidOperationException", error.GetProperty("type").GetString());
        Assert.Equal("Operation failed", error.GetProperty("message").GetString());
        var stack = error.GetProperty("stack");
        Assert.Equal(1, stack.GetArrayLength());
        Assert.Equal("Checkout", stack[0].GetProperty("function").GetString());
        Assert.Equal("/workspace/App.cs", stack[0].GetProperty("file").GetString());
        Assert.Equal(42, stack[0].GetProperty("line").GetInt32());
        Assert.Equal(10, stack[0].GetProperty("column").GetInt32());
        Assert.True(stack[0].GetProperty("inApp").GetBoolean());
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(handler(request));
        }
    }

    [Fact]
    public async Task Expect_LogcoreTransport_Sends_Request_With_Headers()
    {
        HttpRequestMessage? receivedRequest = null;
        string? receivedBody = null;

        var handler = new StubHttpMessageHandler(req =>
        {
            receivedRequest = req;
            receivedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var httpClient = new HttpClient(handler);
        var options = new LogcoreOptions
        {
            Url = "https://logcore.example.com",
            ApiKey = "test-api-key",
            Service = "conduit",
            Environment = "dev",
        };

        await using var transport = new LogcoreTransport(options, httpClient);

        var entry = new LogcoreEntry
        {
            Timestamp = "2026-08-17T18:30:00.000Z",
            Severity = "ERROR",
            Message = "test",
            Service = "conduit",
            Env = "dev",
            InsertId = "7c2e1a0b9d8f6e4c2a0b8d6f4e2c0a8b",
        };

        transport.Enqueue(entry);
        await transport.FlushPendingAsync();

        Assert.NotNull(receivedRequest);
        Assert.Equal("https://logcore.example.com/v1/logs", receivedRequest.RequestUri?.ToString());
        Assert.True(receivedRequest.Headers.Contains("x-api-key"));
        Assert.Equal(
            "test-api-key",
            System.Linq.Enumerable.First(receivedRequest.Headers.GetValues("x-api-key"))
        );

        Assert.NotNull(receivedBody);
        using var doc = JsonDocument.Parse(receivedBody);
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public void Expect_LogcoreSink_Captures_LastEmittedJson()
    {
        var options = new LogcoreOptions { Service = "conduit", Environment = "test" };
        using var sink = new LogcoreSink(options);

        var messageTemplate = new MessageTemplateParser().Parse("Test sink capture");
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            new InvalidOperationException("Test sink error"),
            messageTemplate,
            []
        );

        sink.Emit(logEvent);

        Assert.NotNull(LogcoreSink.LastEmittedEntry);
        Assert.NotNull(LogcoreSink.LastEmittedJson);
        Assert.Equal("ERROR", LogcoreSink.LastEmittedEntry.Severity);
        Assert.Equal("Test sink capture", LogcoreSink.LastEmittedEntry.Message);

        using var doc = JsonDocument.Parse(LogcoreSink.LastEmittedJson);
        Assert.Equal("ERROR", doc.RootElement.GetProperty("severity").GetString());
        Assert.Equal("Test sink capture", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("conduit", doc.RootElement.GetProperty("service").GetString());
    }
}
