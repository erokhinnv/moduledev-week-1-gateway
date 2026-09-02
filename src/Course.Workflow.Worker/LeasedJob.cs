using System.Text.Json.Nodes;

namespace Course.Workflow.Worker;

/// <summary>
/// Арендованное задание в том виде, в каком его отдаёт <c>workflow.claim_jobs</c>.
///
/// Вместе с арендой worker получает ВЕСЬ закреплённый контракт вызова: версию действия, схемы
/// запроса и ответа, объявленные исходы и policy. Отдельного чтения каталога роли исполнителя не
/// требуется — и, что важнее, не разрешается: у неё нет на это прав.
/// </summary>
public sealed class LeasedJob
{
    public required Guid JobId { get; init; }

    public required Guid ExecutionId { get; init; }

    public required Guid AttemptId { get; init; }

    public required int AttemptNumber { get; init; }

    public required long LeaseVersion { get; init; }

    public required Guid ProcessId { get; init; }

    public required string StepKey { get; init; }

    public required string Module { get; init; }

    public required string Action { get; init; }

    public required int ActionVersion { get; init; }

    public required int TimeoutMs { get; init; }

    public required string Principal { get; init; }

    public required string Consumer { get; init; }

    public required IReadOnlyList<string> Scopes { get; init; }

    public required IReadOnlyList<string> Outcomes { get; init; }

    public required bool ActionAvailable { get; init; }

    public JsonNode? RequestSchema { get; init; }

    public JsonNode? ResponseSchema { get; init; }

    public required JsonObject InputConstants { get; init; }

    public required IReadOnlyDictionary<string, string> InputMapping { get; init; }

    public JsonNode? ProcessData { get; init; }

    /// <summary>Разбирает один элемент массива, вернувшегося из workflow.claim_jobs.</summary>
    public static LeasedJob FromDescriptor(JsonObject descriptor) => new()
    {
        JobId = Guid.Parse(descriptor["jobId"]!.GetValue<string>()),
        ExecutionId = Guid.Parse(descriptor["executionId"]!.GetValue<string>()),
        AttemptId = Guid.Parse(descriptor["attemptId"]!.GetValue<string>()),
        AttemptNumber = descriptor["attemptNumber"]!.GetValue<int>(),
        LeaseVersion = descriptor["leaseVersion"]!.GetValue<long>(),
        ProcessId = Guid.Parse(descriptor["processId"]!.GetValue<string>()),
        StepKey = descriptor["stepKey"]?.GetValue<string>() ?? string.Empty,
        Module = descriptor["module"]?.GetValue<string>() ?? string.Empty,
        Action = descriptor["action"]?.GetValue<string>() ?? string.Empty,
        ActionVersion = descriptor["actionVersion"]?.GetValue<int>() ?? 0,
        TimeoutMs = descriptor["timeoutMs"]?.GetValue<int>() ?? 5_000,
        Principal = descriptor["principal"]?.GetValue<string>() ?? "workflow-worker",
        Consumer = descriptor["consumer"]?.GetValue<string>() ?? "workflow-engine",
        Scopes = ReadStrings(descriptor["scopes"]),
        Outcomes = ReadStrings(descriptor["outcomes"]),
        ActionAvailable = descriptor["actionAvailable"]?.GetValue<bool>() ?? false,
        RequestSchema = descriptor["requestSchema"]?.DeepClone(),
        ResponseSchema = descriptor["responseSchema"]?.DeepClone(),
        InputConstants = descriptor["inputConstants"] is JsonObject constants
            ? (JsonObject)constants.DeepClone()
            : new JsonObject(),
        InputMapping = ReadMapping(descriptor["inputMapping"]),
        ProcessData = descriptor["processData"]?.DeepClone(),
    };

    private static string[] ReadStrings(JsonNode? node)
        => node is JsonArray array
            ? array.Select(item => item?.GetValue<string>() ?? string.Empty).ToArray()
            : [];

    private static Dictionary<string, string> ReadMapping(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is not JsonObject mapping)
        {
            return result;
        }

        foreach (var pair in mapping)
        {
            result[pair.Key] = pair.Value?.GetValue<string>() ?? string.Empty;
        }

        return result;
    }
}
