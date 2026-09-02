using System.Text.Json.Nodes;

namespace Course.Shared;

/// <summary>
/// Машиночитаемая схема карты процесса course-1, встроенная внутрь сборки. Файл контракта не
/// читается с диска во время выполнения: тогда карту можно было бы «разрешить» подменой файла
/// рядом с контейнером.
/// </summary>
public static class WorkflowContracts
{
    private static readonly Lazy<JsonNode> Map = new(() =>
    {
        using var stream = typeof(WorkflowContracts).Assembly
            .GetManifestResourceStream("workflow-map.schema.json")
            ?? throw new InvalidOperationException("embedded workflow map schema is missing");
        using var reader = new StreamReader(stream);
        return JsonNode.Parse(reader.ReadToEnd())!;
    });

    /// <summary>Каноническая JSON Schema карты процесса (contracts/course-1/workflow-map.schema.json).</summary>
    public static JsonNode WorkflowMapSchema => Map.Value;
}

/// <summary>
/// Сборка payload действия из констант и mapping карты.
///
/// Направление намеренно «обратное» интуиции: ключ — это указатель в payload ДЕЙСТВИЯ, значение —
/// указатель в данных ПРОЦЕССА. Так карта описывает, что именно действие получит на вход, не имея
/// никакой возможности выполнить произвольное выражение над данными.
/// </summary>
public static class WorkflowPayload
{
    /// <summary>
    /// Пытается собрать payload. Отсутствие источника — не исключение, а объявленный
    /// неповторяемый отказ: действие в этом случае не вызывается вообще.
    /// </summary>
    public static bool TryBuild(
        JsonObject constants,
        IReadOnlyDictionary<string, string> mapping,
        JsonNode? processData,
        out JsonObject payload,
        out string? missingPointer)
    {
        payload = (JsonObject)constants.DeepClone();
        missingPointer = null;

        // Порядок обхода фиксирован, чтобы одинаковая карта и одинаковые данные всегда давали
        // байт-в-байт одинаковый payload — от этого зависит его отпечаток в техническом следе.
        foreach (var pair in mapping.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!JsonPointer.TryParse(pair.Key, out var target) || target.Length == 0 ||
                !JsonPointer.TryParse(pair.Value, out var source) || source.Length == 0)
            {
                missingPointer = pair.Key;
                return false;
            }

            if (!JsonPointer.TryResolve(processData, source, out var value))
            {
                missingPointer = pair.Value;
                return false;
            }

            if (!JsonPointer.TrySet(payload, target, value?.DeepClone()))
            {
                missingPointer = pair.Key;
                return false;
            }
        }

        return true;
    }
}
