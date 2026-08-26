using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Course.Shared;

/// <summary>Одно нарушение JSON Schema: где произошло (Location), какое ключевое слово схемы нарушено (Keyword) и текст сообщения.</summary>
public sealed record SchemaViolation(string Location, string Keyword, string Message);

/// <summary>
/// Проверка данных по JSON Schema версии 2020-12. Используется в трёх местах: проверка тела
/// запроса (request payload) действия, проверка результата (result) действия и проверка манифеста
/// действия при публикации через CLI.
///
/// Скомпилированные схемы кешируются по их текстовому содержимому, поэтому схема одного и того же
/// опубликованного манифеста компилируется единожды, а не при каждом запросе.
/// </summary>
public static class SchemaValidator
{
    private static readonly ConcurrentDictionary<string, JsonSchema> Compiled = new(StringComparer.Ordinal);

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        // В рамках этого курса ключевое слово "format" (например "uuid") — это не просто
        // подсказка, а обязательная проверка (assertion): значение действительно обязано быть
        // валидным UUID, а не просто произвольной строкой.
        RequireFormatValidation = true,
    };

    /// <summary>Проверяет JSON-элемент (System.Text.Json) на соответствие схеме.</summary>
    public static bool TryValidate(JsonNode schema, JsonElement instance, out IReadOnlyList<SchemaViolation> violations)
    {
        var compiled = Compile(schema);
        var results = compiled.Evaluate(instance, Options);
        if (results.IsValid)
        {
            violations = [];
            return true;
        }

        var collected = new List<SchemaViolation>();
        Collect(results, collected);
        violations = collected;
        return false;
    }

    /// <summary>Перегрузка для JsonNode: перегоняет узел в JsonElement и делегирует основной проверке.</summary>
    public static bool TryValidate(JsonNode schema, JsonNode? instance, out IReadOnlyList<SchemaViolation> violations)
    {
        var text = instance?.ToJsonString() ?? "null";
        using var document = JsonDocument.Parse(text);
        return TryValidate(schema, document.RootElement, out violations);
    }

    /// <summary>
    /// Превращает список нарушений в JSON-объект, безопасный для отправки клиенту. Такие детали
    /// описывают ЗАПРОС клиента (что именно в нём не так), а не внутреннее устройство сервера,
    /// поэтому их можно раскрывать без риска утечки информации.
    /// </summary>
    public static JsonObject Details(IReadOnlyList<SchemaViolation> violations)
    {
        var array = new JsonArray();
        foreach (var violation in violations.Take(20))
        {
            array.Add(new JsonObject
            {
                ["location"] = violation.Location,
                ["keyword"] = violation.Keyword,
                ["message"] = violation.Message,
            });
        }

        return new JsonObject { ["violations"] = array };
    }

    /// <summary>Рекурсивно обходит дерево результатов оценки схемы и собирает все найденные нарушения в плоский список.</summary>
    private static void Collect(EvaluationResults results, List<SchemaViolation> collected)
    {
        if (results.Errors is not null)
        {
            foreach (var error in results.Errors)
            {
                collected.Add(new SchemaViolation(
                    results.InstanceLocation.ToString(),
                    error.Key,
                    error.Value));
            }
        }

        if (results.Details is null)
        {
            return;
        }

        foreach (var nested in results.Details)
        {
            Collect(nested, collected);
        }
    }

    /// <summary>Компилирует схему один раз и кеширует результат по её текстовому представлению.</summary>
    private static JsonSchema Compile(JsonNode schema)
    {
        var text = schema.ToJsonString();
        return Compiled.GetOrAdd(text, static value => JsonSchema.FromText(value));
    }
}
