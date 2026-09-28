using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Conduit.Infrastructure.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Conduit.IntegrationTests.Infrastructure.Logging;

public class LogcoreSinkTests
{
    [Fact]
    public void Format_ProducesCorrectWireShapeAndGoldenProperties()
    {
        var options = new LogcoreOptions { Service = "todo-frontend", Env = "dev" };

        Exception? thrownException = null;
        try
        {
            throw new InvalidOperationException(
                "Cannot read properties of undefined (reading 'total')"
            );
        }
        catch (Exception ex)
        {
            thrownException = ex;
        }

        var parser = new MessageTemplateParser();
        var template = parser.Parse("checkout failed");
        var logEvent = new LogEvent(
            new DateTimeOffset(2026, 8, 17, 18, 30, 0, TimeSpan.Zero),
            LogEventLevel.Error,
            thrownException,
            template,
            [
                new LogEventProperty("OrderId", new ScalarValue("ord_12345")),
                new LogEventProperty("Amount", new ScalarValue(99.95)),
            ]
        );

        var entry = LogcoreFormatter.Format(logEvent, options);

        Assert.Equal("2026-08-17T18:30:00.000Z", entry.Timestamp);
        Assert.Equal("ERROR", entry.Severity);
        Assert.Equal("checkout failed", entry.Message);
        Assert.Equal("todo-frontend", entry.Service);
        Assert.Equal("dev", entry.Env);
        Assert.Null(entry.SourceProject);
        Assert.Matches("^[0-9a-f]{32}$", entry.InsertId);

        Assert.NotNull(entry.Error);
        Assert.Equal("InvalidOperationException", entry.Error.Type);
        Assert.Equal("Cannot read properties of undefined (reading 'total')", entry.Error.Message);
        Assert.NotEmpty(entry.Error.Stack);
        Assert.True(entry.Error.Stack[0].InApp);

        Assert.NotNull(entry.Labels);
        Assert.Equal("ord_12345", entry.Labels["OrderId"]);

        Assert.NotNull(entry.Context);
        Assert.Equal("ord_12345", entry.Context["OrderId"]?.ToString());

        var json = JsonSerializer.Serialize(entry, LogcoreJsonOptions.Default);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("2026-08-17T18:30:00.000Z", root.GetProperty("timestamp").GetString());
        Assert.Equal("ERROR", root.GetProperty("severity").GetString());
        Assert.Equal("checkout failed", root.GetProperty("message").GetString());
        Assert.Equal("todo-frontend", root.GetProperty("service").GetString());
        Assert.Equal("dev", root.GetProperty("env").GetString());
        Assert.False(root.TryGetProperty("source_project", out _));
        Assert.Equal(32, root.GetProperty("insert_id").GetString()?.Length);
        Assert.Equal(
            "InvalidOperationException",
            root.GetProperty("error").GetProperty("type").GetString()
        );
    }

    [Fact]
    public void Format_ExceptionChain_RootCauseFirst()
    {
        var options = new LogcoreOptions();

        Exception? outerException = null;
        try
        {
            try
            {
                throw new ArgumentNullException("item", "Item cannot be null");
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("Failed to process order", inner);
            }
        }
        catch (Exception ex)
        {
            outerException = ex;
        }

        var parser = new MessageTemplateParser();
        var template = parser.Parse("Processing error");
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            outerException,
            template,
            []
        );

        var entry = LogcoreFormatter.Format(logEvent, options);

        Assert.NotNull(entry.Error);
        Assert.Equal("InvalidOperationException", entry.Error.Type);
        Assert.NotEmpty(entry.Error.Stack);
        // The root cause frame should be present
        Assert.Contains(entry.Error.Stack, f => f.InApp);
    }

    [Theory]
    [InlineData("production", "prod")]
    [InlineData("Production", "prod")]
    [InlineData("prod", "prod")]
    [InlineData("staging", "staging")]
    [InlineData("development", "dev")]
    [InlineData("dev", "dev")]
    [InlineData("test", "test")]
    [InlineData("local", "local")]
    public void LogcoreOptions_NormalizeEnv_MapsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, LogcoreOptions.NormalizeEnv(input));
    }

    [Fact]
    public void ComputeInsertId_DeterministicAndMatchesRegex()
    {
        var id1 = LogcoreFormatter.ComputeInsertId(
            "2026-08-17T18:30:00.000Z",
            "conduit",
            "ERROR",
            "msg",
            "{}"
        );
        var id2 = LogcoreFormatter.ComputeInsertId(
            "2026-08-17T18:30:00.000Z",
            "conduit",
            "ERROR",
            "msg",
            "{}"
        );
        var id3 = LogcoreFormatter.ComputeInsertId(
            "2026-08-17T18:30:00.000Z",
            "conduit",
            "ERROR",
            "different",
            "{}"
        );

        Assert.Equal(id1, id2);
        Assert.NotEqual(id1, id3);
        Assert.Matches("^[0-9a-f]{32}$", id1);
    }

    [Fact]
    public void PayloadEnvelope_HasSchemaVersionAndEntries()
    {
        var payload = new LogcorePayload
        {
            SchemaVersion = 1,
            Entries =
            [
                new LogcoreEntry
                {
                    Timestamp = "2026-08-17T18:30:00.000Z",
                    Severity = "ERROR",
                    Message = "test",
                    Service = "conduit",
                    Env = "dev",
                    InsertId = "0123456789abcdef0123456789abcdef",
                },
            ],
        };

        var json = JsonSerializer.Serialize(payload, LogcoreJsonOptions.Default);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(1, root.GetProperty("entries").GetArrayLength());
    }
}
