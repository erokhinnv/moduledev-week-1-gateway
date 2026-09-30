using Microsoft.Extensions.Logging;

namespace Course.Shared;

/// <summary>
/// Провайдер <see cref="ILogger"/>, который пишет записи через <see cref="StructuredLog"/>.
///
/// Нужен затем, чтобы ASP.NET-сервисы вели журнал в той же форме, что процессы без HTTP: одна
/// строка — один JSON object. Именованные placeholder'ы вызова
/// (<c>correlationId={CorrelationId}</c>) становятся полями записи, а не частью текста, поэтому
/// ни один вызов logger.Log* не пришлось переписывать ради формата.
/// </summary>
public sealed class JsonConsoleLoggerProvider(string service) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new JsonConsoleLogger(service, categoryName);

    public void Dispose()
    {
    }
}

internal sealed class JsonConsoleLogger(string service, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var record = StructuredLog.Record(service, Level(logLevel), category);
        record["message"] = formatter(state, exception);

        // Шаблон сообщения ({OriginalFormat}) в запись не идёт: он дублирует уже отформатированный
        // текст. Остальные именованные значения становятся полями.
        if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            foreach (var value in values)
            {
                var name = value.Key == "{OriginalFormat}" ? string.Empty : CamelCase(value.Key);
                if (name.Length > 0 && !record.ContainsKey(name))
                {
                    record[name] = StructuredLog.Value(value.Value);
                }
            }
        }

        // Наружу уходит только тип сбоя: сообщение PostgreSQL умеет содержать значения данных, а
        // трассировка стека не является ни структурой, ни безопасным минимумом.
        if (exception is not null)
        {
            record["failure"] = Redaction.Describe(exception);
        }

        StructuredLog.Write(record);
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        _ => "info",
    };

    private static string CamelCase(string name)
        => name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
