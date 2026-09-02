using System.Text.Json;
using System.Text.Json.Nodes;
using Course.Shared;
using Npgsql;
using NpgsqlTypes;

namespace Course.Cli;

/// <summary>
/// Сторона публикации и trusted-команд процессов.
///
/// Как и в каталоге действий, каждая операция — это ровно один вызов функции PostgreSQL. Ни
/// идемпотентность запуска, ни «ровно одна активная версия», ни fencing завершения не держатся
/// логикой этого класса: он только передаёт аргументы и разбирает конверт ответа.
/// </summary>
public sealed class FlowClient(NpgsqlDataSource dataSource)
{
    /// <summary>Факты каталога и права исполнителя, нужные валидатору карты. Ничего не меняет.</summary>
    public Task<JsonObject> PublicationFactsAsync(JsonObject map, CancellationToken cancellationToken)
        => CallAsync("SELECT workflow.publication_facts($1)::text", cancellationToken, Jsonb(map));

    public Task<JsonObject> PublishAsync(JsonObject map, CancellationToken cancellationToken)
        => CallAsync("SELECT workflow.publish_flow($1)::text", cancellationToken, Jsonb(map));

    public Task<JsonObject> ActivateAsync(string flowName, int version, CancellationToken cancellationToken)
        => CallAsync(
            "SELECT workflow.activate_flow($1, $2)::text",
            cancellationToken,
            Text(flowName),
            Integer(version));

    public async Task<JsonObject> ListAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT workflow.list_flows()::text");
        var raw = await command.ExecuteScalarAsync(cancellationToken) as string ?? "[]";
        var items = JsonNode.Parse(raw) as JsonArray ?? [];

        return new JsonObject
        {
            ["resource"] = "flow",
            ["operation"] = "listed",
            ["items"] = items,
        };
    }

    public Task<JsonObject> StartAsync(
        string flowName,
        string businessKey,
        JsonNode data,
        CancellationToken cancellationToken)
        => CallAsync(
            "SELECT workflow.start_process($1, $2, $3)::text",
            cancellationToken,
            Text(flowName),
            Text(businessKey),
            Jsonb(data));

    public Task<JsonObject> GetAsync(Guid processId, CancellationToken cancellationToken)
        => CallAsync(
            "SELECT workflow.describe_process_envelope($1)::text",
            cancellationToken,
            Uuid(processId));

    public Task<JsonObject> SignalAsync(
        Guid processId,
        string signalType,
        string messageId,
        JsonNode payload,
        CancellationToken cancellationToken)
        => CallAsync(
            "SELECT workflow.accept_signal($1, $2, $3, $4)::text",
            cancellationToken,
            Uuid(processId),
            Text(signalType),
            Text(messageId),
            Jsonb(payload));

    /// <summary>
    /// Проба fencing из test profile. Внутри вызывает ТУ ЖЕ границу завершения, что и worker,
    /// поэтому проверяет настоящий инвариант, а не отдельную тестовую ветку кода. Доступность
    /// самой пробы решает база по серверному признаку test profile, а не переменная окружения
    /// клиента: обход арендатора — это вопрос прав, а не того, кто запустил процесс.
    /// </summary>
    public Task<JsonObject> TestFinishAsync(
        Guid jobId,
        string owner,
        long leaseVersion,
        string outcome,
        JsonNode result,
        CancellationToken cancellationToken)
        => CallAsync(
            "SELECT workflow.test_finish_job($1, $2, $3, $4, $5)::text",
            cancellationToken,
            Uuid(jobId),
            Text(owner),
            BigInt(leaseVersion),
            Text(outcome),
            Jsonb(result));

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
            throw new CliException(ContractCodes.InternalError, "workflow returned a non contractual envelope");
        }

        if (envelope["status"]?.GetValue<string>() != "ok")
        {
            throw new CliException(
                envelope["code"]?.GetValue<string>() ?? ContractCodes.InternalError,
                envelope["message"]?.GetValue<string>() ?? "workflow operation was refused");
        }

        if (envelope["result"] is not JsonObject result)
        {
            throw new CliException(ContractCodes.InternalError, "workflow returned an empty result");
        }

        return (JsonObject)result.DeepClone();
    }

    private static NpgsqlParameter Text(string value)
        => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    private static NpgsqlParameter Integer(int value)
        => new() { NpgsqlDbType = NpgsqlDbType.Integer, Value = value };

    private static NpgsqlParameter BigInt(long value)
        => new() { NpgsqlDbType = NpgsqlDbType.Bigint, Value = value };

    private static NpgsqlParameter Uuid(Guid value)
        => new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = value };

    private static NpgsqlParameter Jsonb(JsonNode value)
        => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value.ToJsonString() };
}

/// <summary>Чтение и проверка карты процесса по машиночитаемой схеме course-1.</summary>
public static class FlowReader
{
    /// <summary>
    /// Читает карту (JSON или YAML, файл или стандартный ввод) и проверяет её по JSON Schema.
    /// Семантика проверяется отдельно — она требует обращения к каталогу.
    /// </summary>
    public static WorkflowMap ReadMap(string path)
    {
        JsonObject document;
        try
        {
            document = WorkflowMapReader.ReadDocument(path);
        }
        catch (FileNotFoundException)
        {
            throw new CliException(ContractCodes.RequestInvalid, $"workflow map file does not exist: {path}");
        }
        catch (DirectoryNotFoundException)
        {
            throw new CliException(ContractCodes.RequestInvalid, $"workflow map file does not exist: {path}");
        }
        catch (Exception exception) when (exception is JsonException or FormatException
                                          or YamlDotNet.Core.YamlException)
        {
            throw new CliException(
                ContractCodes.ManifestInvalid,
                "workflow map is not a valid JSON or YAML document",
                new JsonObject { ["reason"] = exception.Message });
        }

        if (!SchemaValidator.TryValidate(WorkflowContracts.WorkflowMapSchema, document, out var violations))
        {
            throw new CliException(
                ContractCodes.ManifestInvalid,
                "workflow map does not match the course-1 workflow map schema",
                SchemaValidator.Details(violations));
        }

        return WorkflowMap.FromDocument(document);
    }

    /// <summary>Читает произвольный JSON-объект (данные процесса, тело сигнала, результат задания).</summary>
    public static JsonObject ReadJsonObject(string path, string what)
    {
        string text;
        try
        {
            text = WorkflowMapReader.IsStandardInput(path)
                ? Console.In.ReadToEnd()
                : File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliException(ContractCodes.RequestInvalid, $"{what} file cannot be read: {path}");
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject
                ?? throw new CliException(ContractCodes.RequestInvalid, $"{what} must be a JSON object");
        }
        catch (JsonException)
        {
            throw new CliException(ContractCodes.RequestInvalid, $"{what} is not valid JSON");
        }
    }
}
