using System.Text.Json.Nodes;

namespace Course.Cli;

/// <summary>
/// Контрактный сбой команды CLI: приводит к одному конверту ошибки на stdout и ненулевому
/// коду завершения процесса (exit code). Это единственный "ожидаемый" вид ошибки в CLI — всё,
/// что выбрасывается как CliException, уже классифицировано и понятно, в отличие от
/// непредвиденных исключений (например обрыв связи с базой), которые обрабатываются отдельно.
/// </summary>
public sealed class CliException(string code, string message, JsonNode? details = null) : Exception(message)
{
    /// <summary>Технический код ошибки контракта (см. ContractCodes), например "manifest.invalid".</summary>
    public string Code { get; } = code;

    /// <summary>Необязательные дополнительные подробности, безопасные для вывода в конверте ошибки.</summary>
    public JsonNode? Details { get; } = details;
}
