using System.Text.Json.Nodes;

namespace Course.Shared;

/// <summary>
/// Строит документы OpenAPI (описание HTTP API в машиночитаемом виде) непосредственно из
/// опубликованных манифестов действий.
///
/// Ключевая идея: здесь НЕТ ни одной строчки, которая генерировала бы документацию из C#-типов
/// или атрибутов контроллеров — потому что в этом проекте нет контроллеров под каждое действие.
/// Вся документация целиком выводится из данных манифеста (JSON Schema запроса/ответа, коды
/// исходов, требуемые права и т.д.), которые лежат в базе данных. Как только новое действие
/// публикуется через CLI, оно сразу же появляется в документации — без пересборки и без
/// перезапуска сервиса api.
/// </summary>
public static class OpenApiBuilder
{
    private const string OpenApiVersion = "3.1.0";

    /// <summary>
    /// Строит документ /openapi/default.json — сводный документ со всеми включёнными действиями,
    /// каждое в своей версии по умолчанию (default).
    /// </summary>
    public static JsonObject DefaultDocument(IReadOnlyList<ActionManifest> manifests)
    {
        var paths = new JsonObject();
        foreach (var manifest in manifests.OrderBy(item => item.HttpPath, StringComparer.Ordinal))
        {
            paths[manifest.HttpPath] = new JsonObject { ["post"] = Operation(manifest) };
        }

        return Document(
            "Course action runtime, enabled default routes",
            "Every enabled route of the immutable action catalog at its default version.",
            paths);
    }

    /// <summary>
    /// Строит документ /openapi/actions/{module}/{action}/{version}.json — описание РОВНО ОДНОЙ
    /// конкретной версии одного действия. В отличие от сводного документа, версии одного и того
    /// же действия никогда не объединяются в один документ через JSON Schema "oneOf": у каждой
    /// версии — свой собственный, полностью независимый документ.
    /// </summary>
    public static JsonObject VersionDocument(ActionManifest manifest)
    {
        var paths = new JsonObject
        {
            [manifest.HttpPath] = new JsonObject { ["post"] = Operation(manifest) },
        };

        return Document(
            $"Course action {manifest.RouteKey} version {manifest.Version}",
            "One exact published version of a single action.",
            paths);
    }

    /// <summary>Общий каркас OpenAPI-документа: версия спецификации, заголовок, пути и общие схемы.</summary>
    private static JsonObject Document(string title, string description, JsonObject paths)
        => new()
        {
            ["openapi"] = OpenApiVersion,
            ["info"] = new JsonObject
            {
                ["title"] = title,
                ["description"] = description,
                ["version"] = ContractCodes.ContractVersion,
            },
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["schemas"] = new JsonObject { ["ErrorEnvelope"] = ErrorEnvelopeSchema() },
            },
        };

    /// <summary>Описание одной HTTP-операции (POST) для одного действия: параметры, тело запроса, ответы.</summary>
    private static JsonObject Operation(ActionManifest manifest)
    {
        var operation = new JsonObject
        {
            ["operationId"] = $"{manifest.Module}_{manifest.Action}_v{manifest.Version}",
            ["summary"] = $"{manifest.RouteKey} version {manifest.Version}",
            ["description"] =
                $"Registered PostgreSQL function published as an action. Required policy: " +
                $"{(manifest.RequiredPolicy.Count == 0 ? "none" : string.Join(", ", manifest.RequiredPolicy))}. " +
                $"Idempotency: {manifest.IdempotencyMode} ({manifest.IdempotencyScope}). " +
                $"Deadline: {manifest.TimeoutMs} ms.",
            ["tags"] = new JsonArray(manifest.Module),
            ["parameters"] = Parameters(manifest),
            ["requestBody"] = new JsonObject
            {
                ["required"] = true,
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject
                    {
                        // Схема запроса берётся напрямую из манифеста — она не описывается заново в C#.
                        ["schema"] = manifest.RequestSchema.DeepClone(),
                    },
                },
            },
            ["responses"] = Responses(manifest),
        };

        // Выключенная версия действия всё ещё документируется (её можно было вызвать раньше),
        // но помечается как устаревшая (deprecated), чтобы это было видно в документации.
        if (!manifest.Enabled)
        {
            operation["deprecated"] = true;
        }

        return operation;
    }

    /// <summary>Заголовки запроса, общие для любого действия: X-Action-Version и Idempotency-Key.</summary>
    private static JsonArray Parameters(ActionManifest manifest)
        => new(
            new JsonObject
            {
                ["name"] = "X-Action-Version",
                ["in"] = "header",
                ["required"] = false,
                ["description"] = "Explicit action version. Without the header the default version is selected.",
                ["schema"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
            },
            new JsonObject
            {
                ["name"] = "Idempotency-Key",
                ["in"] = "header",
                // Обязательность заголовка идемпотентности берётся прямо из режима idempotency_mode
                // манифеста: для действий с required она true, для остальных — false.
                ["required"] = manifest.RequiresIdempotencyKey,
                ["description"] = $"Idempotency key, scope {manifest.IdempotencyScope}.",
                ["schema"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 },
            });

    /// <summary>
    /// Описание возможных HTTP-ответов действия: успех (200, схема которого строится из
    /// response_schema манифеста и списка допустимых outcome) и полный набор кодов ошибок
    /// контракта.
    /// </summary>
    private static JsonObject Responses(ActionManifest manifest)
    {
        var outcomes = new JsonArray();
        foreach (var outcome in manifest.Outcomes)
        {
            outcomes.Add(outcome);
        }

        var success = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("status", "outcome", "result", "meta"),
            ["properties"] = new JsonObject
            {
                ["status"] = new JsonObject { ["const"] = "ok" },
                ["outcome"] = new JsonObject { ["enum"] = outcomes },
                ["result"] = manifest.ResponseSchema.DeepClone(),
                ["meta"] = MetaSchema(),
            },
        };

        var error = new JsonObject
        {
            ["description"] = "Contract error envelope",
            ["content"] = new JsonObject
            {
                ["application/json"] = new JsonObject
                {
                    ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/ErrorEnvelope" },
                },
            },
        };

        return new JsonObject
        {
            ["200"] = new JsonObject
            {
                ["description"] = "Declared outcome with a result valid against the response schema",
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject { ["schema"] = success },
                },
            },
            // Одна и та же схема ошибки переиспользуется для всех кодов ошибок HTTP — глубокая
            // копия нужна, чтобы разные ветки JSON-документа не делили один и тот же объект.
            ["400"] = error.DeepClone(),
            ["401"] = error.DeepClone(),
            ["403"] = error.DeepClone(),
            ["404"] = error.DeepClone(),
            ["409"] = error.DeepClone(),
            ["422"] = error.DeepClone(),
            ["500"] = error.DeepClone(),
            ["503"] = error.DeepClone(),
            ["504"] = error,
        };
    }

    /// <summary>Схема блока meta, общего для успешных и ошибочных ответов.</summary>
    private static JsonObject MetaSchema()
        => new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray("correlationId", "actionVersion"),
            ["properties"] = new JsonObject
            {
                ["correlationId"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
                ["actionVersion"] = new JsonObject { ["type"] = new JsonArray("integer", "null") },
            },
        };

    /// <summary>Схема конверта ошибки, переиспользуемая через $ref во всех кодах ошибок документа.</summary>
    private static JsonObject ErrorEnvelopeSchema()
        => new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray("status", "code", "message", "meta"),
            ["properties"] = new JsonObject
            {
                ["status"] = new JsonObject { ["const"] = "error" },
                ["code"] = new JsonObject { ["type"] = "string" },
                ["message"] = new JsonObject { ["type"] = "string" },
                ["retryable"] = new JsonObject { ["type"] = "boolean" },
                ["details"] = new JsonObject { ["type"] = "object" },
                ["meta"] = MetaSchema(),
            },
        };
}
