using System.Text.Json;
using System.Text.Json.Nodes;

namespace Course.Shared;

/// <summary>Тип шага карты процесса в подмножестве course-1.</summary>
public enum WorkflowStepType
{
    Automatic,
    WaitSignal,
    Manual,
    End,
}

/// <summary>Закреплённый контракт вызова зарегистрированного действия из automatic-шага.</summary>
public sealed class WorkflowTask
{
    public required string Service { get; init; }

    public required string Module { get; init; }

    public required string Action { get; init; }

    public required int ActionVersion { get; init; }

    public required IReadOnlyList<string> RequiredPolicy { get; init; }

    public required int TimeoutMs { get; init; }

    public required int MaxAttempts { get; init; }

    public required IReadOnlyList<int> DelaysMs { get; init; }

    /// <summary>Ключ — указатель в payload действия, значение — указатель в данных процесса.</summary>
    public required IReadOnlyDictionary<string, string> InputMapping { get; init; }

    public required JsonObject InputConstants { get; init; }

    public string RouteKey => $"{Module}.{Action}";
}

/// <summary>Один узел карты процесса.</summary>
public sealed class WorkflowStep
{
    public required string Key { get; init; }

    public required WorkflowStepType Type { get; init; }

    public string? SignalType { get; init; }

    public string? Outcome { get; init; }

    public IReadOnlyList<string> AllowedOutcomes { get; init; } = [];

    public WorkflowTask? Task { get; init; }

    /// <summary>
    /// Множество исходов, которые шаг вообще может произвести. Именно оно обязано быть покрыто
    /// переходами ровно один раз: у automatic это outcomes действия, у wait_signal — один
    /// объявленный outcome, у manual — allowed_outcomes, у end — ничего.
    /// </summary>
    public IReadOnlyCollection<string> DeclaredOutcomes(IReadOnlyList<string> actionOutcomes) => Type switch
    {
        WorkflowStepType.Automatic => actionOutcomes,
        WorkflowStepType.WaitSignal => Outcome is null ? [] : new[] { Outcome },
        WorkflowStepType.Manual => AllowedOutcomes,
        _ => [],
    };
}

/// <summary>Переход по конкретному конечному исходу.</summary>
public sealed record WorkflowTransition(string From, string Outcome, string To);

/// <summary>
/// Разобранная карта процесса. Исходный документ сохраняется целиком: именно он публикуется в
/// PostgreSQL и именно из него считается канонический отпечаток версии.
/// </summary>
public sealed class WorkflowMap
{
    public required JsonObject Document { get; init; }

    public required string FlowName { get; init; }

    public required int Version { get; init; }

    public required string StartStep { get; init; }

    public required IReadOnlyList<WorkflowStep> Steps { get; init; }

    public required IReadOnlyList<WorkflowTransition> Transitions { get; init; }

    /// <summary>
    /// Строит модель из документа, УЖЕ прошедшего проверку по JSON Schema course-1: типы и
    /// обязательные поля здесь считаются доказанными, поэтому разбор не занимается диагностикой.
    /// </summary>
    public static WorkflowMap FromDocument(JsonObject document)
    {
        var steps = new List<WorkflowStep>();
        foreach (var item in document["steps"]!.AsArray())
        {
            var step = item!.AsObject();
            var type = step["type"]!.GetValue<string>() switch
            {
                "automatic" => WorkflowStepType.Automatic,
                "wait_signal" => WorkflowStepType.WaitSignal,
                "manual" => WorkflowStepType.Manual,
                _ => WorkflowStepType.End,
            };

            steps.Add(new WorkflowStep
            {
                Key = step["key"]!.GetValue<string>(),
                Type = type,
                SignalType = step["signal_type"]?.GetValue<string>(),
                Outcome = step["outcome"]?.GetValue<string>(),
                AllowedOutcomes = ReadStrings(step["allowed_outcomes"]),
                Task = step["task"] is JsonObject task ? ReadTask(task) : null,
            });
        }

        var transitions = document["transitions"]!.AsArray()
            .Select(item => item!.AsObject())
            .Select(item => new WorkflowTransition(
                item["from"]!.GetValue<string>(),
                item["outcome"]!.GetValue<string>(),
                item["to"]!.GetValue<string>()))
            .ToArray();

        return new WorkflowMap
        {
            Document = document,
            FlowName = document["flow_name"]!.GetValue<string>(),
            Version = document["version"]!.GetValue<int>(),
            StartStep = document["start_step"]!.GetValue<string>(),
            Steps = steps,
            Transitions = transitions,
        };
    }

    private static WorkflowTask ReadTask(JsonObject task)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        if (task["input_mapping"] is JsonObject inputMapping)
        {
            foreach (var pair in inputMapping)
            {
                mapping[pair.Key] = pair.Value?.GetValue<string>() ?? string.Empty;
            }
        }

        return new WorkflowTask
        {
            Service = task["service"]!.GetValue<string>(),
            Module = task["module"]!.GetValue<string>(),
            Action = task["action"]!.GetValue<string>(),
            ActionVersion = task["action_version"]!.GetValue<int>(),
            RequiredPolicy = ReadStrings(task["required_policy"]),
            TimeoutMs = task["timeout_ms"]!.GetValue<int>(),
            MaxAttempts = task["retry"]!["max_attempts"]!.GetValue<int>(),
            DelaysMs = task["retry"]!["delays_ms"] is JsonArray delays
                ? delays.Select(item => item!.GetValue<int>()).ToArray()
                : [],
            InputMapping = mapping,
            InputConstants = task["input_constants"] is JsonObject constants
                ? (JsonObject)constants.DeepClone()
                : new JsonObject(),
        };
    }

    private static string[] ReadStrings(JsonNode? node)
        => node is JsonArray array
            ? array.Select(item => item?.GetValue<string>() ?? string.Empty).ToArray()
            : [];
}

/// <summary>
/// Чтение документа карты из файла или стандартного ввода, в JSON или в YAML. Формат
/// определяется по содержимому, а не по расширению: и то и другое — один и тот же контракт.
/// </summary>
public static class WorkflowMapReader
{
    /// <summary>Путь, означающий «читать документ из стандартного ввода».</summary>
    public static bool IsStandardInput(string path)
        => path is "-" or "/dev/stdin" or "/proc/self/fd/0";

    /// <summary>Читает и разбирает документ. Ошибки формата поднимаются как FormatException.</summary>
    public static JsonObject ReadDocument(string path)
    {
        var text = IsStandardInput(path) ? Console.In.ReadToEnd() : File.ReadAllText(path);
        var trimmed = text.TrimStart();

        // JSON — тоже валидный YAML, но собственный разбор JSON строже и даёт лучшие сообщения,
        // поэтому документ, начинающийся с «{», сначала пробуется как JSON.
        if (trimmed.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(text) is JsonObject json)
                {
                    return json;
                }
            }
            catch (JsonException)
            {
                // Падаем в разбор YAML: возможно, это YAML flow mapping.
            }
        }

        if (YamlDocument.Parse(text) is not JsonObject parsed)
        {
            throw new FormatException("a workflow map must be a JSON or YAML object");
        }

        // Пересборка через JSON-текст нужна не для красоты: узлы, собранные из YAML, хранят
        // числа как CLR-значения (long), и дальнейшее чтение их как int зависело бы от того, в
        // каком формате пришёл документ. После пересборки представление одинаково для обоих
        // форматов — а значит, одинаков и канонический отпечаток опубликованной версии.
        return JsonNode.Parse(parsed.ToJsonString()) as JsonObject
            ?? throw new FormatException("a workflow map must be a JSON or YAML object");
    }
}
