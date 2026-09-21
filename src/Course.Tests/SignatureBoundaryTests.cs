using System.Security.Cryptography;
using System.Text;
using Course.Shared;
using Xunit;

namespace Course.Tests;

/// <summary>Общая граница подписи: точные байты, сырой UTF-8 ключ, строгий формат v1 и коды контракта.</summary>
public sealed class SignatureBoundaryTests
{
    private const string Secret = "  synthetic secret with spaces ";

    private static readonly byte[] Body = Encoding.UTF8.GetBytes(
        """{"externalRequestId":"external-123","messageId":"provider-123","occurredAt":"2026-09-04T12:00:00Z","outcome":"COMPLETED","providerPaymentId":"provider-123","version":1}""");

    [Fact]
    public void Absent_header_is_not_an_error_at_the_boundary()
        => Assert.Equal(SignatureVerdict.Absent, new SignatureBoundary(Secret).Verify([], Body));

    [Fact]
    public void Valid_signature_over_exact_bytes_is_verified()
    {
        var boundary = new SignatureBoundary(Secret);
        Assert.Equal(SignatureVerdict.Verified, boundary.Verify([SignatureBoundary.Sign(Secret, Body)], Body));
    }

    [Fact]
    public void Key_is_raw_utf8_without_trim_or_decoding()
    {
        var expected = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Body));
        Assert.Equal(expected, SignatureBoundary.Sign(Secret, Body));

        var trimmed = SignatureBoundary.Sign(Secret.Trim(), Body);
        Assert.Equal(SignatureVerdict.Invalid, new SignatureBoundary(Secret).Verify([trimmed], Body));
    }

    [Fact]
    public void Re_serialized_body_does_not_match_the_signature()
    {
        var signature = SignatureBoundary.Sign(Secret, Body);
        var spaced = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Body).Replace(",", ", "));
        Assert.Equal(SignatureVerdict.Invalid, new SignatureBoundary(Secret).Verify([signature], spaced));
    }

    [Theory]
    [InlineData("v2=")]
    [InlineData("V1=")]
    [InlineData("sha256=")]
    [InlineData("")]
    public void Unknown_version_prefix_is_invalid(string prefix)
    {
        var digest = SignatureBoundary.Sign(Secret, Body)[3..];
        Assert.Equal(SignatureVerdict.Invalid, new SignatureBoundary(Secret).Verify([prefix + digest], Body));
    }

    [Fact]
    public void Uppercase_hex_short_digest_and_multiple_headers_are_invalid()
    {
        var boundary = new SignatureBoundary(Secret);
        var signature = SignatureBoundary.Sign(Secret, Body);

        Assert.Equal(SignatureVerdict.Invalid, boundary.Verify(["v1=" + signature[3..].ToUpperInvariant()], Body));
        Assert.Equal(SignatureVerdict.Invalid, boundary.Verify([signature[..^2]], Body));
        Assert.Equal(SignatureVerdict.Invalid, boundary.Verify([signature, signature], Body));
        Assert.Equal(SignatureVerdict.Invalid, boundary.Verify(["v1=" + new string('0', 64)], Body));
    }

    [Fact]
    public void Missing_secret_rejects_every_signature()
    {
        Assert.Equal(SignatureVerdict.Invalid, new SignatureBoundary(null).Verify([SignatureBoundary.Sign(Secret, Body)], Body));
        Assert.Equal(SignatureVerdict.Invalid, new SignatureBoundary(string.Empty).Verify(["v1=" + new string('a', 64)], Body));
    }

    [Fact]
    public void Transport_marker_carries_only_verification_facts()
    {
        var marker = SignatureBoundary.TransportMarker();
        Assert.Equal(["signatureVerified", "signatureVersion"], marker.Select(pair => pair.Key).Order().ToArray());
        Assert.True(marker["signatureVerified"]!.GetValue<bool>());
        Assert.Equal(1, marker["signatureVersion"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(ContractCodes.SignatureInvalid, 401)]
    [InlineData(ContractCodes.ReceiptSignatureRequired, 403)]
    [InlineData(ContractCodes.WorkflowDecisionConflict, 409)]
    [InlineData(ContractCodes.IdempotencyConflict, 409)]
    [InlineData("receipt.external_request_not_found", 422)]
    public void Perimeter_codes_map_to_contract_statuses(string code, int status)
        => Assert.Equal(status, ContractCodes.HttpStatus(code));
}
