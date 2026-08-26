using System.Text.Json;
using System.Text.Json.Nodes;
using Course.Shared;
using Xunit;

namespace Course.Tests;

/// <summary>Проверка тела запроса обязана отклонять его до любого предметного вызова — включая проверку формата (format), а не только типа данных.</summary>
public sealed class SchemaValidatorTests
{
    // Копии реальных схем контракта course-1, используемые только внутри этого файла для тестов —
    // сами по себе они не влияют на runtime и служат независимой проверкой поведения SchemaValidator.
    private static readonly JsonNode PaymentRequest = JsonNode.Parse("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "additionalProperties": false,
          "required": ["operationKind", "amount", "currency"],
          "properties": {
            "operationKind": {"enum": ["PAYMENT_EXECUTION", "PAYMENT_APPROVAL"]},
            "amount": {
              "type": "string",
              "pattern": "^(?:0\\.0[1-9]|0\\.[1-9][0-9]?|[1-9][0-9]{0,15}(?:\\.[0-9]{1,2})?)$"
            },
            "currency": {"const": "RUB"}
          }
        }
        """)!;

    private static readonly JsonNode OperationGet = JsonNode.Parse("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "additionalProperties": false,
          "required": ["operationId"],
          "properties": {"operationId": {"type": "string", "format": "uuid"}}
        }
        """)!;

    [Theory]
    [InlineData("""{"operationKind":"PAYMENT_EXECUTION","amount":"1000.00","currency":"RUB"}""", true)]
    [InlineData("""{"operationKind":"PAYMENT_APPROVAL","amount":"0.01","currency":"RUB"}""", true)]
    [InlineData("""{"operationKind":"PAYMENT_EXECUTION","amount":"0","currency":"RUB"}""", false)]
    [InlineData("""{"operationKind":"PAYMENT_EXECUTION","amount":"1.234","currency":"RUB"}""", false)]
    [InlineData("""{"operationKind":"PAYMENT_EXECUTION","amount":1,"currency":"RUB"}""", false)] // сумма должна быть строкой, а не числом
    [InlineData("""{"operationKind":"PAYMENT_EXECUTION","amount":"1.00","currency":"USD"}""", false)]
    [InlineData("""{"operationKind":"PAYMENT_EXECUTION","amount":"1.00","currency":"RUB","status":"COMPLETED"}""", false)] // лишнее поле запрещено
    [InlineData("""{"operationKind":"PAYMENT_EXECUTION","amount":"1e3","currency":"RUB"}""", false)] // экспоненциальная запись запрещена
    public void ValidatesPaymentRequestPayloads(string payload, bool expected)
    {
        using var document = JsonDocument.Parse(payload);

        Assert.Equal(expected, SchemaValidator.TryValidate(PaymentRequest, document.RootElement, out _));
    }

    [Fact]
    public void RejectsAPayloadThatTriesToSelectTheTarget()
    {
        // Попытка протащить в payload запроса поле "target_schema" (как если бы клиент пытался
        // сам выбрать целевую SQL-схему) должна быть отклонена уже на уровне JSON Schema —
        // additionalProperties: false запрещает любые поля, не описанные в схеме.
        using var document = JsonDocument.Parse(
            """{"operationKind":"PAYMENT_EXECUTION","amount":"1.00","currency":"RUB","target_schema":"pg_catalog"}""");

        Assert.False(SchemaValidator.TryValidate(PaymentRequest, document.RootElement, out var violations));
        Assert.NotEmpty(violations);
    }

    [Theory]
    [InlineData("""{"operationId":"8c26513d-8441-43ea-b064-3bca8c240052"}""", true)]
    [InlineData("""{"operationId":"not-a-uuid"}""", false)]
    public void AssertsTheUuidFormat(string payload, bool expected)
    {
        // Убеждаемся, что ключевое слово "format": "uuid" реально ПРОВЕРЯЕТСЯ (assertion), а не
        // просто игнорируется как подсказка — SchemaValidator включает RequireFormatValidation.
        using var document = JsonDocument.Parse(payload);

        Assert.Equal(expected, SchemaValidator.TryValidate(OperationGet, document.RootElement, out _));
    }

    [Fact]
    public void PublishesContractDetailsWithoutServerInternals()
    {
        // Подробности о нарушении схемы безопасно показывать клиенту (они описывают его же
        // запрос), но в них не должно быть никаких следов внутреннего устройства сервера —
        // ни имён библиотек (Npgsql), ни слов, похожих на SQL.
        using var document = JsonDocument.Parse("""{"operationId":"not-a-uuid"}""");
        SchemaValidator.TryValidate(OperationGet, document.RootElement, out var violations);

        var details = SchemaValidator.Details(violations).ToJsonString();

        Assert.Contains("violations", details);
        Assert.DoesNotContain("Npgsql", details);
        Assert.DoesNotContain("select", details, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Матрица кодов ошибок — это контракт, общий для всех трёх сервисов (gateway, api, cli).</summary>
public sealed class ContractCodeTests
{
    [Theory]
    [InlineData(ContractCodes.AuthInvalid, 401)]
    [InlineData(ContractCodes.RequestInvalid, 400)]
    [InlineData(ContractCodes.IdempotencyRequired, 400)]
    [InlineData(ContractCodes.AccessDenied, 403)]
    [InlineData(ContractCodes.ActionNotFound, 404)]
    [InlineData(ContractCodes.IdempotencyConflict, 409)]
    [InlineData(ContractCodes.PayloadInvalid, 422)]
    [InlineData(ContractCodes.DependencyUnavailable, 503)]
    [InlineData(ContractCodes.ContractViolation, 500)]
    [InlineData(ContractCodes.ActionTimeout, 504)]
    [InlineData(ContractCodes.InternalError, 500)]
    public void MapsEveryTechnicalCode(string code, int status)
        => Assert.Equal(status, ContractCodes.HttpStatus(code));

    [Fact]
    public void MapsADomainCodeOfARegisteredFunctionToUnprocessable()
        // Код, которого нет в технической матрице (например "operation.not_found" — его выдаёт
        // сама зарегистрированная функция payment.operation_get_v1), обязан попадать на 422,
        // а не на какой-то технический статус.
        => Assert.Equal(422, ContractCodes.HttpStatus("operation.not_found"));

    [Fact]
    public void SuccessEnvelopeCarriesTheServerSideCorrelation()
    {
        var correlationId = Guid.NewGuid();

        var envelope = Envelope.Success("CREATED", new JsonObject { ["operationId"] = "x" }, correlationId, 3);

        Assert.Equal("ok", envelope["status"]!.GetValue<string>());
        Assert.Equal("CREATED", envelope["outcome"]!.GetValue<string>());
        Assert.Equal(correlationId.ToString(), envelope["meta"]!["correlationId"]!.GetValue<string>());
        Assert.Equal(3, envelope["meta"]!["actionVersion"]!.GetValue<int>());
    }

    [Fact]
    public void ErrorEnvelopeKeepsActionVersionNullWhenNoVersionWasSelected()
    {
        // Например action.not_found: версию действия ещё не удалось выбрать, поэтому
        // meta.actionVersion обязан остаться null, а не какое-то произвольное значение.
        var envelope = Envelope.Error(ContractCodes.ActionNotFound, "not published", Guid.NewGuid(), null);

        Assert.Equal("error", envelope["status"]!.GetValue<string>());
        Assert.Null(envelope["meta"]!["actionVersion"]);
        Assert.False(envelope["retryable"]!.GetValue<bool>());
    }
}

public sealed class HashingTests
{
    [Fact]
    public void ProducesLowercaseSha256Hex()
    {
        var digest = Hashing.Sha256Hex("payload");

        Assert.Equal(64, digest.Length);
        Assert.Matches("^[0-9a-f]{64}$", digest);
    }
}
