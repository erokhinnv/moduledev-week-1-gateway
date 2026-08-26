namespace Course.Shared;

/// <summary>Небольшие помощники для чтения переменных окружения без дублирования проверок.</summary>
public static class Env
{
    /// <summary>Читает обязательную переменную окружения; бросает исключение, если она не задана или пустая.</summary>
    public static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"required environment variable {name} is not set");
        }

        return value;
    }

    /// <summary>Читает необязательную переменную окружения; при отсутствии возвращает значение по умолчанию.</summary>
    public static string Optional(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    /// <summary>То же самое, но сразу разбирает значение как целое число.</summary>
    public static int OptionalInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
}
