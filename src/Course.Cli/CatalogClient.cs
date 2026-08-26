using System.Text.Json;
using System.Text.Json.Nodes;
using Course.Shared;
using Npgsql;
using NpgsqlTypes;

namespace Course.Cli;

/// <summary>
/// Сторона ПУБЛИКАЦИИ каталога действий (в отличие от ActionCatalog в Course.Api, которая только
/// читает). Каждая операция здесь — это ровно один атомарный вызов функции PostgreSQL, поэтому
/// activate и disable никогда не могут оставить маршрут без ровно одной версии по умолчанию:
/// гарантия обеспечивается на уровне базы данных (см. 003_catalog_api.sql), а не в этом C#-коде.
/// </summary>
public sealed class CatalogClient(NpgsqlDataSource dataSource)
{
    /// <summary>Публикует манифест действия. Идентичный повтор безопасен, изменение уже опубликованной версии — конфликт.</summary>
    public Task<JsonObject> PublishAsync(JsonNode manifest, CancellationToken cancellationToken)
        => CallAsync("SELECT api.publish_action($1)::text", cancellationToken, Jsonb(manifest.ToJsonString()));

    /// <summary>Атомарно делает указанную версию действия версией по умолчанию, снимая default с прежней версии.</summary>
    public Task<JsonObject> ActivateAsync(string module, string action, int version, CancellationToken cancellationToken)
        => CallAsync(
            "SELECT api.activate_action($1, $2, $3)::text",
            cancellationToken,
            Text(module),
            Text(action),
            Integer(version));

    /// <summary>Выключает указанную версию действия. Если она была версией по умолчанию — требуется явная замена (replacement).</summary>
    public Task<JsonObject> DisableAsync(
        string module,
        string action,
        int version,
        int? replacement,
        CancellationToken cancellationToken)
        => CallAsync(
            "SELECT api.disable_action($1, $2, $3, $4)::text",
            cancellationToken,
            Text(module),
            Text(action),
            Integer(version),
            Integer(replacement));

    /// <summary>Возвращает список всех когда-либо опубликованных версий всех действий.</summary>
    public async Task<JsonObject> ListAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT api.list_actions()::text");
        var raw = await command.ExecuteScalarAsync(cancellationToken) as string ?? "[]";
        var items = JsonNode.Parse(raw) as JsonArray ?? [];

        return new JsonObject
        {
            ["resource"] = "action",
            ["operation"] = "listed",
            ["items"] = items,
        };
    }

    /// <summary>
    /// Общий вызов функции каталога: выполняет SQL, разбирает вернувшийся JSON-конверт и либо
    /// возвращает его поле "result", либо бросает CliException с кодом и сообщением из
    /// конверта ошибки — благодаря этому вся обработка ошибок публикации сосредоточена в одном месте.
    /// </summary>
    private async Task<JsonObject> CallAsync(
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        var raw = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (raw is null || JsonNode.Parse(raw) is not JsonObject envelope)
        {
            throw new CliException(ContractCodes.InternalError, "catalog returned a non contractual envelope");
        }

        if (envelope["status"]?.GetValue<string>() != "ok")
        {
            throw new CliException(
                envelope["code"]?.GetValue<string>() ?? ContractCodes.InternalError,
                envelope["message"]?.GetValue<string>() ?? "catalog operation was refused");
        }

        if (envelope["result"] is not JsonObject result)
        {
            throw new CliException(ContractCodes.InternalError, "catalog returned an empty result");
        }

        // Возвращаем независимую копию узла (DeepClone), а не сам узел изнутри envelope: узел
        // JsonNode в System.Text.Json может принадлежать только одному родителю одновременно,
        // и без копии попытка вставить result в другой JSON-объект (конверт ответа CLI) привела
        // бы к ошибке "The node already has a parent".
        return (JsonObject)result.DeepClone();
    }

    private static NpgsqlParameter Text(string value)
        => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    private static NpgsqlParameter Integer(int? value)
        => new() { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter Jsonb(string value)
        => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };
}

/// <summary>Чтение и проверка манифеста действия по каноничной машиночитаемой схеме course-1.</summary>
public static class ManifestReader
{
    /// <summary>Читает JSON-файл манифеста с диска и проверяет его по встроенной JSON Schema каталога действий.</summary>
    public static JsonNode Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new CliException(ContractCodes.RequestInvalid, $"manifest file does not exist: {path}");
        }

        JsonNode? manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new CliException(
                ContractCodes.ManifestInvalid,
                "manifest is not valid JSON",
                new JsonObject { ["reason"] = exception.Message });
        }

        if (manifest is not JsonObject)
        {
            throw new CliException(ContractCodes.ManifestInvalid, "manifest must be a JSON object");
        }

        if (!SchemaValidator.TryValidate(ContractSchemas.ActionManifest, manifest, out var violations))
        {
            throw new CliException(
                ContractCodes.ManifestInvalid,
                "manifest does not match the course-1 action manifest schema",
                SchemaValidator.Details(violations));
        }

        return manifest;
    }
}

/// <summary>Доступ к встроенным (embedded) машиночитаемым схемам контракта course-1, зашитым внутрь сборки CLI при публикации.</summary>
public static class ContractSchemas
{
    private static readonly Lazy<JsonNode> Manifest = new(() =>
    {
        using var stream = typeof(ContractSchemas).Assembly
            .GetManifestResourceStream("action-manifest.schema.json")
            ?? throw new InvalidOperationException("embedded action manifest schema is missing");
        using var reader = new StreamReader(stream);
        return JsonNode.Parse(reader.ReadToEnd())!;
    });

    /// <summary>Каноническая JSON Schema манифеста действия (см. contracts/course-1/action-manifest.schema.json), закешированная после первого чтения.</summary>
    public static JsonNode ActionManifest => Manifest.Value;
}
