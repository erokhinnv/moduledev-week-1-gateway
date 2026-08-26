using System.Text.Json;
using System.Text.Json.Nodes;

namespace Course.Shared;

/// <summary>
/// Разрешённая (выбранная) версия действия (action).
///
/// Экземпляр этого класса всегда собирается из данных, прочитанных из неизменяемого каталога
/// действий в PostgreSQL, и никогда — из тела HTTP-запроса клиента. Это ключевой инвариант всего
/// решения: клиент выбирает уже зарегистрированное действие по его имени (module + action), но
/// никогда не выбирает и не может повлиять на то, какая именно SQL-функция, схема или база данных
/// будет вызвана. Все эти детали приходят только отсюда — из каталога.
/// </summary>
public sealed class ActionManifest
{
    /// <summary>Модуль действия, например "payment" — верхнеуровневая группа действий.</summary>
    public required string Module { get; init; }

    /// <summary>Имя действия внутри модуля, например "request".</summary>
    public required string Action { get; init; }

    /// <summary>Номер версии действия. У одного действия может быть несколько версий одновременно.</summary>
    public required int Version { get; init; }

    /// <summary>HTTP-метод, которым вызывается действие. Контракт требует только POST.</summary>
    public required string HttpMethod { get; init; }

    /// <summary>Имя схемы PostgreSQL, где физически лежит целевая функция.</summary>
    public required string TargetSchema { get; init; }

    /// <summary>Имя самой целевой SQL-функции, которую вызовет api.invoke.</summary>
    public required string TargetFunction { get; init; }

    /// <summary>JSON Schema, которой должен соответствовать payload запроса, до вызова функции.</summary>
    public required JsonNode RequestSchema { get; init; }

    /// <summary>JSON Schema, которой должен соответствовать result ответа функции.</summary>
    public required JsonNode ResponseSchema { get; init; }

    /// <summary>Список допустимых исходов (outcome) действия, например ["CREATED"].</summary>
    public required IReadOnlyList<string> Outcomes { get; init; }

    /// <summary>Список прав (scopes), которые обязаны присутствовать у вызывающего, чтобы выполнить действие.</summary>
    public required IReadOnlyList<string> RequiredPolicy { get; init; }

    /// <summary>Режим идемпотентности: "none" | "optional" | "required".</summary>
    public required string IdempotencyMode { get; init; }

    /// <summary>Область действия ключа идемпотентности: "none" | "principal_action" | "consumer_action" | "global_action".</summary>
    public required string IdempotencyScope { get; init; }

    /// <summary>Максимально допустимое время выполнения запроса в миллисекундах (дедлайн).</summary>
    public required int TimeoutMs { get; init; }

    /// <summary>Включена ли эта версия действия (можно ли её вообще вызывать).</summary>
    public required bool Enabled { get; init; }

    /// <summary>Является ли эта версия действия версией по умолчанию для своего маршрута.</summary>
    public required bool IsDefault { get; init; }

    /// <summary>Составной ключ маршрута вида "module.action", используемый как идентификатор в CLI.</summary>
    public string RouteKey => $"{Module}.{Action}";

    /// <summary>HTTP-путь действия вида "/api/module/action".</summary>
    public string HttpPath => $"/api/{Module}/{Action}";

    /// <summary>true, если заголовок Idempotency-Key обязателен для этого действия.</summary>
    public bool RequiresIdempotencyKey => IdempotencyMode == "required";

    /// <summary>
    /// Строит объект манифеста из JSON-элемента, полученного из базы данных (из функций каталога
    /// api.resolve_action / api.describe_action / api.default_actions). Именно тут происходит
    /// единственное место в C#-коде, где строка из PostgreSQL превращается в строго типизированный
    /// объект манифеста.
    /// </summary>
    public static ActionManifest FromCatalog(JsonElement value)
    {
        return new ActionManifest
        {
            Module = value.GetProperty("module").GetString()!,
            Action = value.GetProperty("action").GetString()!,
            Version = value.GetProperty("version").GetInt32(),
            HttpMethod = value.GetProperty("httpMethod").GetString()!,
            TargetSchema = value.GetProperty("targetSchema").GetString()!,
            TargetFunction = value.GetProperty("targetFunction").GetString()!,
            RequestSchema = JsonNode.Parse(value.GetProperty("requestSchema").GetRawText())!,
            ResponseSchema = JsonNode.Parse(value.GetProperty("responseSchema").GetRawText())!,
            Outcomes = ReadStringArray(value, "outcomes"),
            RequiredPolicy = ReadStringArray(value, "requiredPolicy"),
            IdempotencyMode = value.GetProperty("idempotencyMode").GetString()!,
            IdempotencyScope = value.GetProperty("idempotencyScope").GetString()!,
            TimeoutMs = value.GetProperty("timeoutMs").GetInt32(),
            Enabled = value.GetProperty("enabled").GetBoolean(),
            IsDefault = value.GetProperty("isDefault").GetBoolean(),
        };
    }

    /// <summary>Безопасно читает JSON-массив строк по имени свойства, игнорируя нестроковые элементы.</summary>
    private static string[] ReadStringArray(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }
}
