using System.Text.Json;
using System.Text.Json.Nodes;

namespace Course.Shared;

/// <summary>
/// Единый JSON-конверт (envelope), в который "упаковывается" КАЖДЫЙ HTTP-ответ runtime, и
/// отдельный, более простой конверт для команд CLI. Клиент всегда получает ответ одной и той же
/// формы, независимо от того, какое конкретно действие он вызвал — успех это или ошибка.
/// </summary>
public static class Envelope
{
    /// <summary>Настройки сериализации JSON, общие для всего проекта (без отступов — компактный вывод).</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Строит успешный ответ HTTP-действия: status=ok, объявленный outcome, предметный result и
    /// метаданные (correlationId, версия действия).
    /// </summary>
    public static JsonObject Success(string outcome, JsonNode? result, Guid correlationId, int? actionVersion)
        => new()
        {
            ["status"] = "ok",
            ["outcome"] = outcome,
            ["result"] = result ?? new JsonObject(),
            ["meta"] = Meta(correlationId, actionVersion),
        };

    /// <summary>
    /// Строит ответ об ошибке HTTP-действия: status=error, технический код, человекочитаемое
    /// сообщение (без внутренних деталей!), флаг "можно ли повторить запрос" и опциональные
    /// подробности (details), безопасные для показа клиенту.
    /// </summary>
    public static JsonObject Error(
        string code,
        string message,
        Guid? correlationId,
        int? actionVersion,
        bool retryable = false,
        JsonNode? details = null)
        => new()
        {
            ["status"] = "error",
            ["code"] = code,
            ["message"] = message,
            ["retryable"] = retryable,
            ["details"] = details ?? new JsonObject(),
            ["meta"] = Meta(correlationId, actionVersion),
        };

    /// <summary>
    /// Блок meta, общий для успешного и ошибочного ответа. correlationId генерируется сервером
    /// (никогда не приходит от клиента), actionVersion — фактически выбранная версия действия
    /// (или null, если версию выбрать не удалось, например action.not_found).
    /// </summary>
    public static JsonObject Meta(Guid? correlationId, int? actionVersion)
        => new()
        {
            ["correlationId"] = correlationId?.ToString(),
            ["actionVersion"] = actionVersion,
        };

    /// <summary>
    /// Конверт успешного ответа Course CLI. По контракту CLI обязан вывести в stdout ровно один
    /// такой JSON-документ и ничего больше.
    /// </summary>
    public static JsonObject CliSuccess(JsonNode result)
        => new()
        {
            ["status"] = "ok",
            ["result"] = result,
            ["meta"] = new JsonObject { ["contractVersion"] = ContractCodes.ContractVersion },
        };

    /// <summary>Конверт ответа об ошибке Course CLI: тоже ровно один JSON-документ в stdout, exit code ненулевой.</summary>
    public static JsonObject CliError(string code, string message, JsonNode? details = null)
    {
        var envelope = new JsonObject
        {
            ["status"] = "error",
            ["code"] = code,
            ["message"] = message,
        };
        if (details is not null)
        {
            envelope["details"] = details;
        }

        envelope["meta"] = new JsonObject { ["contractVersion"] = ContractCodes.ContractVersion };
        return envelope;
    }
}
