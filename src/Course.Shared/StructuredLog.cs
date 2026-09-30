using System.Text.Json.Nodes;

namespace Course.Shared;

/// <summary>
/// Одна запись журнала — один JSON object в одной строке.
///
/// Так требует контракт наблюдаемости, но ценность не в формате: идентификаторы передаются
/// ПОЛЯМИ, а не вклеиваются в текст сообщения, поэтому путь одной операции находится фильтром по
/// полю, а не разбором строки. Все процессы проекта — C# и Python — пишут одну и ту же форму.
///
/// Что сюда не попадает: JWT, HMAC-секрет и подпись, пароли, полные тела запросов и квитанций,
/// текст provider `message` и причина ручного решения. Это свойство мест вызова, а не форматтера,
/// поэтому здесь нет ни «обогащения» контекстом, ни печати исключения целиком.
/// </summary>
public static class StructuredLog
{
    private static readonly object Gate = new();

    /// <summary>Заготовка записи с обязательными полями.</summary>
    public static JsonObject Record(string service, string level, string @event)
        => new()
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["level"] = level,
            ["service"] = service,
            ["event"] = @event,
        };

    /// <summary>Пишет готовую запись одной строкой. Запись атомарна относительно других потоков.</summary>
    public static void Write(JsonObject record, TextWriter? stream = null)
    {
        var line = record.ToJsonString();
        var target = stream ?? Console.Out;
        lock (Gate)
        {
            target.WriteLine(line);
            target.Flush();
        }
    }

    /// <summary>Собирает и пишет запись уровня <c>info</c> с перечисленными полями.</summary>
    public static void Info(string service, string @event, params (string Name, object? Value)[] fields)
        => Write(Fill(Record(service, "info", @event), fields));

    /// <summary>То же для уровня <c>warning</c>.</summary>
    public static void Warning(string service, string @event, params (string Name, object? Value)[] fields)
        => Write(Fill(Record(service, "warning", @event), fields));

    /// <summary>То же для уровня <c>error</c>; диагностика процессов без HTTP уходит в stderr.</summary>
    public static void Error(string service, string @event, params (string Name, object? Value)[] fields)
        => Write(Fill(Record(service, "error", @event), fields), Console.Error);

    /// <summary>Добавляет поля в запись, приводя значения к JSON-типам без потери смысла.</summary>
    public static JsonObject Fill(JsonObject record, IEnumerable<(string Name, object? Value)> fields)
    {
        foreach (var (name, value) in fields)
        {
            record[name] = Value(value);
        }

        return record;
    }

    /// <summary>Значение поля: число остаётся числом, всё остальное становится строкой.</summary>
    public static JsonNode? Value(object? value) => value switch
    {
        null => null,
        bool boolean => JsonValue.Create(boolean),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        Guid identifier => JsonValue.Create(identifier.ToString()),
        _ => JsonValue.Create(value.ToString()),
    };
}
