using System.Text.Json;
using System.Text.Json.Nodes;
using Course.Shared;
using Xunit;

namespace Course.Tests;

/// <summary>
/// Request schema receipt.accept — это выданный receipt-v1.schema.json. Provider v0.2.0 написан на
/// Go и сериализует время в RFC3339Nano, поэтому граница обязана принимать любую точность дробной
/// части секунды, а не только миллисекунды.
/// </summary>
public sealed class ReceiptContractTests
{
    private static readonly JsonNode ReceiptSchema = LoadRequestSchema("receipt-accept-v1.action.json");

    [Theory]
    [InlineData("2026-09-04T12:00:00Z")]
    [InlineData("2026-09-04T12:00:00.1Z")]
    [InlineData("2026-09-04T12:00:00.123Z")]
    [InlineData("2026-09-04T12:00:00.123456Z")]
    [InlineData("2026-09-04T12:00:00.123456789Z")]
    public void Receipt_accepts_rfc3339_utc_timestamps_of_any_precision(string occurredAt)
        => Assert.True(Validate(Receipt(occurredAt)), occurredAt);

    [Theory]
    [InlineData("2026-09-04T12:00:00+03:00")]
    [InlineData("2026-13-04T12:00:00Z")]
    [InlineData("not-a-time-Z")]
    public void Receipt_rejects_non_utc_or_malformed_timestamps(string occurredAt)
        => Assert.False(Validate(Receipt(occurredAt)), occurredAt);

    [Fact]
    public void Receipt_rejects_unknown_fields_other_versions_and_crlf()
    {
        var extra = Receipt("2026-09-04T12:00:00Z");
        extra["message"] = "Payment completed";
        Assert.False(Validate(extra));

        var version = Receipt("2026-09-04T12:00:00Z");
        version["version"] = 2;
        Assert.False(Validate(version));

        var crlf = Receipt("2026-09-04T12:00:00Z");
        crlf["messageId"] = "provider\r\n123";
        Assert.False(Validate(crlf));
    }

    private static JsonObject Receipt(string occurredAt) => new()
    {
        ["externalRequestId"] = "external-123",
        ["messageId"] = "provider-123",
        ["occurredAt"] = occurredAt,
        ["outcome"] = "COMPLETED",
        ["providerPaymentId"] = "provider-123",
        ["version"] = 1,
    };

    private static bool Validate(JsonObject receipt)
    {
        using var document = JsonDocument.Parse(receipt.ToJsonString());
        return SchemaValidator.TryValidate(ReceiptSchema, document.RootElement, out _);
    }

    private static JsonNode LoadRequestSchema(string manifest)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "db", "actions")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var text = File.ReadAllText(Path.Combine(directory!.FullName, "db", "actions", manifest));
        return JsonNode.Parse(text)!["request_schema"]!.DeepClone();
    }
}
