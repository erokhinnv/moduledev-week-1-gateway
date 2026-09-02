using System.Text;
using System.Text.Json.Nodes;

namespace Course.Shared;

/// <summary>Как закончился вызов зарегистрированного действия с точки зрения контракта.</summary>
public enum RegisteredActionStatus
{
    /// <summary>Объявленный исход и результат, прошедший схему ответа.</summary>
    Succeeded,

    /// <summary>Действие вернуло объявленную предметную ошибку.</summary>
    DomainError,

    /// <summary>Действие нарушило контракт: конверт, исход или схема результата.</summary>
    ContractViolation,
}

/// <summary>Разобранный конверт одного вызова зарегистрированного действия.</summary>
public sealed record RegisteredActionOutcome(
    RegisteredActionStatus Status,
    string? Outcome,
    JsonNode? Result,
    string Code,
    string Message,
    bool Retryable,
    int? ActionVersion,
    bool Replayed);

/// <summary>
/// Общий разбор границы <c>api.invoke</c>.
///
/// И HTTP-исполнитель, и worker вызывают одну и ту же функцию, получают один и тот же конверт и
/// обязаны одинаково понимать, что считать успехом, предметной ошибкой и нарушением контракта.
/// Держать эти правила в двух местах означало бы рано или поздно разойтись в трактовке — поэтому
/// они живут здесь, в общей библиотеке.
/// </summary>
public static class RegisteredAction
{
    /// <summary>
    /// Строит доверенный (серверный) контекст вызова. Ни одно поле не может прийти из данных
    /// процесса или тела запроса: контекст собирается только из проверенной личности и
    /// идентификаторов, выданных самим движком.
    /// </summary>
    public static JsonObject TrustedContext(
        string principal,
        string consumer,
        IEnumerable<string> scopes,
        Guid correlationId,
        string requestId,
        DateTimeOffset deadline,
        JsonObject? additional = null)
    {
        var context = new JsonObject
        {
            ["principal"] = principal,
            ["consumer"] = consumer,
            ["scopes"] = new JsonArray(scopes.Select(scope => (JsonNode)JsonValue.Create(scope)!).ToArray()),
            ["correlationId"] = correlationId.ToString(),
            ["requestId"] = requestId,
            ["deadline"] = deadline.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        };

        if (additional is not null)
        {
            foreach (var pair in additional)
            {
                context[pair.Key] = pair.Value?.DeepClone();
            }
        }

        return context;
    }

    /// <summary>
    /// Разбирает и проверяет конверт, вернувшийся из api.invoke: форму, объявленность исхода и
    /// соответствие результата схеме ответа. Любое нарушение — это ContractViolation, то есть
    /// повод откатить транзакцию, а не «почти успех».
    /// </summary>
    public static RegisteredActionOutcome Interpret(
        string? envelopeText,
        IReadOnlyList<string> declaredOutcomes,
        JsonNode? responseSchema,
        int maxResultBytes,
        int? expectedVersion = null)
    {
        if (envelopeText is null)
        {
            return Violation("action returned no envelope");
        }

        // Отсечка по размеру ДО разбора: строку драйвер уже материализовал, но построение дерева
        // JsonNode и обход валидатором схемы заведомо дороже.
        if (Encoding.UTF8.GetByteCount(envelopeText) > maxResultBytes)
        {
            return Violation("action result exceeds the configured size limit");
        }

        if (JsonNode.Parse(envelopeText) is not JsonObject envelope)
        {
            return Violation("action returned a non contractual envelope");
        }

        int? version = null;
        if (envelope["meta"]?["actionVersion"] is JsonValue versionValue &&
            versionValue.TryGetValue<int>(out var parsedVersion))
        {
            version = parsedVersion;
        }

        var replayed = envelope["replayed"] is JsonValue replayFlag &&
                       replayFlag.TryGetValue<bool>(out var replayValue) &&
                       replayValue;

        if (envelope["status"]?.GetValue<string>() != "ok")
        {
            return new RegisteredActionOutcome(
                RegisteredActionStatus.DomainError,
                null,
                null,
                envelope["code"]?.GetValue<string>() ?? ContractCodes.InternalError,
                envelope["message"]?.GetValue<string>() ?? "action returned a domain error",
                envelope["retryable"] is JsonValue retryFlag &&
                    retryFlag.TryGetValue<bool>(out var retryable) && retryable,
                version,
                replayed);
        }

        var outcome = envelope["outcome"]?.GetValue<string>();
        if (outcome is null || envelope["result"] is not JsonObject result)
        {
            return Violation("action returned a non contractual result", version);
        }

        // Повтор по ключу идемпотентности исполнен и полностью проверен раньше — возможно, другой
        // версией действия. Мерить его сегодняшним манифестом нельзя.
        if (!replayed)
        {
            // Версия сверяется ПЕРВОЙ. Иначе исполнение чужой версии, вернувшее заодно чужой
            // исход, диагностировалось бы как «необъявленный исход» — и подсказка уводила бы от
            // настоящей причины ровно в том сценарии, ради которого сверка и существует.
            if (expectedVersion is not null && version != expectedVersion)
            {
                return Violation(
                    "action was executed under a different version than the one validated", version);
            }

            if (!declaredOutcomes.Contains(outcome, StringComparer.Ordinal))
            {
                return Violation("action returned an undeclared outcome", version);
            }

            if (responseSchema is not null &&
                !SchemaValidator.TryValidate(responseSchema, result, out _))
            {
                return Violation("action result does not match the response schema", version);
            }
        }

        return new RegisteredActionOutcome(
            RegisteredActionStatus.Succeeded,
            outcome,
            result.DeepClone(),
            "ok",
            "action completed",
            false,
            version,
            replayed);
    }

    private static RegisteredActionOutcome Violation(string message, int? version = null)
        => new(
            RegisteredActionStatus.ContractViolation,
            null,
            null,
            ContractCodes.ContractViolation,
            message,
            false,
            version,
            false);
}
