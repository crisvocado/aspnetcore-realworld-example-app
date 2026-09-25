using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Infrastructure.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Conduit.IntegrationTests.Infrastructure.Logging;

public class LogcoreSinkTests
{
    [Fact]
    public void Options_Normalizes_Service_And_Env()
    {
        Assert.Equal("conduit", LogcoreOptions.NormalizeService(""));
        Assert.Equal("my-service", LogcoreOptions.NormalizeService("MY-SERVICE"));
        Assert.Equal("conduit", LogcoreOptions.NormalizeService("invalid name with spaces"));

        Assert.Equal("prod", LogcoreOptions.NormalizeEnv("production"));
        Assert.Equal("prod", LogcoreOptions.NormalizeEnv("PROD"));
        Assert.Equal("staging", LogcoreOptions.NormalizeEnv("stage"));
        Assert.Equal("dev", LogcoreOptions.NormalizeEnv("development"));
        Assert.Equal("test", LogcoreOptions.NormalizeEnv("TESTING"));
        Assert.Equal("local", LogcoreOptions.NormalizeEnv("local"));
        Assert.Equal("dev", LogcoreOptions.NormalizeEnv("unknown"));
    }

    [Fact]
    public void Transform_Produces_Expected_Wire_Shape()
    {
        var options = new LogcoreOptions
        {
            Service = "conduit-api",
            Env = "dev",
            ApiKey = "test-api-key",
            Url = "https://logcore.example.com",
        };

        using var sink = new LogcoreSink(options);

        Exception exception;
        try
        {
            throw new InvalidOperationException("Inner root failure");
        }
        catch (Exception ex)
        {
            try
            {
                throw new ArgumentException("Outer failure", ex);
            }
            catch (Exception outerEx)
            {
                exception = outerEx;
            }
        }

        var messageTemplate = new MessageTemplateParser().Parse(
            "Processing failed for user {UserId}"
        );
        var properties = new List<LogEventProperty>
        {
            new("UserId", new ScalarValue("user-123")),
            new("SourceContext", new ScalarValue("Conduit.Features.Users.Login")),
        };

        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            exception,
            messageTemplate,
            properties
        );

        var entry = sink.Transform(logEvent);

        Assert.Equal("ERROR", entry.Severity);
        Assert.Equal("Processing failed for user \"user-123\"", entry.Message);
        Assert.Equal("conduit-api", entry.Service);
        Assert.Equal("dev", entry.Env);
        Assert.Null(entry.SourceProject);
        Assert.Matches("^[0-9a-f]{32}$", entry.InsertId);

        Assert.NotNull(entry.Error);
        Assert.Equal("ArgumentException", entry.Error.Type);
        Assert.Equal("Outer failure", entry.Error.Message);
        Assert.NotEmpty(entry.Error.Stack);

        // Verify root exception frame is first (innermost first)
        Assert.Contains(
            entry.Error.Stack,
            f => f.Function.Contains("Transform_Produces_Expected_Wire_Shape")
        );

        Assert.NotNull(entry.Labels);
        Assert.Equal("Conduit.Features.Users.Login", entry.Labels["logger"]);
        Assert.Equal("user-123", entry.Labels["UserId"]);
    }

    [Fact]
    public void InsertId_Is_Deterministic()
    {
        var timestamp = "2026-08-17T18:30:00.000Z";
        var service = "conduit";
        var severity = "ERROR";
        var message = "test error message";
        var context = new Dictionary<string, object> { ["key1"] = "val1", ["key2"] = 42 };

        var id1 = LogcoreSink.ComputeInsertId(timestamp, service, severity, message, context);
        var id2 = LogcoreSink.ComputeInsertId(timestamp, service, severity, message, context);

        Assert.Equal(id1, id2);
        Assert.Equal(32, id1.Length);
        Assert.Matches("^[0-9a-f]{32}$", id1);
    }

    [Fact]
    public async Task Client_Sends_Envelope_With_Correct_Headers_And_Schema()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var handler = new TestHttpMessageHandler(async request =>
        {
            capturedRequest = request;
            if (request.Content != null)
            {
                capturedBody = await request.Content.ReadAsStringAsync();
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = new LogcoreClient(
            "https://logcore.example.com",
            "my-secret-key",
            handler
        );

        var entries = new List<LogcoreEntry>
        {
            new()
            {
                Timestamp = "2026-08-17T18:30:00.000Z",
                Severity = "ERROR",
                Message = "test error",
                Service = "conduit",
                Env = "dev",
                InsertId = "7c2e1a0b9d8f6e4c2a0b8d6f4e2c0a8b",
            },
        };

        var result = await client.SendBatchAsync(entries);

        Assert.True(result);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("https://logcore.example.com/v1/logs", capturedRequest.RequestUri?.ToString());
        Assert.True(capturedRequest.Headers.Contains("x-api-key"));
        Assert.Contains("my-secret-key", capturedRequest.Headers.GetValues("x-api-key"));

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        var jsonEntries = root.GetProperty("entries");
        Assert.Equal(1, jsonEntries.GetArrayLength());
        Assert.Equal("ERROR", jsonEntries[0].GetProperty("severity").GetString());
        Assert.Equal("conduit", jsonEntries[0].GetProperty("service").GetString());
        Assert.Equal("dev", jsonEntries[0].GetProperty("env").GetString());
    }

    [Fact]
    public async Task Client_Drops_On_4xx_Permanent_Rejection()
    {
        var callCount = 0;
        var handler = new TestHttpMessageHandler(_ =>
        {
            callCount++;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"invalid field\"}"),
                }
            );
        });

        using var client = new LogcoreClient("https://logcore.example.com", "key", handler);

        var entries = new List<LogcoreEntry>
        {
            new()
            {
                Timestamp = "2026-08-17T18:30:00.000Z",
                Severity = "ERROR",
                Message = "test",
                Service = "conduit",
                Env = "dev",
                InsertId = "7c2e1a0b9d8f6e4c2a0b8d6f4e2c0a8b",
            },
        };

        var result = await client.SendBatchAsync(entries);

        Assert.False(result);
        // Should not retry permanent 400 rejection
        Assert.Equal(1, callCount);
    }

    private sealed class TestHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFunc
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => handlerFunc(request);
    }
}
