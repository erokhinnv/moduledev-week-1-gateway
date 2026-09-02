using System.Text.Json.Nodes;

namespace Course.Shared;

/// <summary>
/// JSON Pointer по RFC 6901.
///
/// Карта процесса задаёт mapping как «указатель в payload действия → указатель в данных
/// процесса». Оба конца — обычные JSON Pointer, а не выражения: в карте принципиально нельзя
/// записать код, SQL или произвольную подстановку, только адрес значения.
/// </summary>
public static class JsonPointer
{
    /// <summary>
    /// Разбирает строку указателя в последовательность сегментов. Пустая строка означает корень
    /// документа. Экранирование: <c>~1</c> — это «/», <c>~0</c> — это «~»; одинокая «~» ошибочна.
    /// </summary>
    public static bool TryParse(string? pointer, out string[] tokens)
    {
        tokens = [];
        if (pointer is null)
        {
            return false;
        }

        if (pointer.Length == 0)
        {
            return true;
        }

        if (pointer[0] != '/')
        {
            return false;
        }

        var parts = pointer[1..].Split('/');
        var result = new string[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!TryUnescape(parts[index], out var token))
            {
                return false;
            }

            result[index] = token;
        }

        tokens = result;
        return true;
    }

    /// <summary>Находит значение по указателю. Отсутствие любого промежуточного сегмента — это false.</summary>
    public static bool TryResolve(JsonNode? root, IReadOnlyList<string> tokens, out JsonNode? value)
    {
        value = null;
        var current = root;

        foreach (var token in tokens)
        {
            switch (current)
            {
                case JsonObject o when o.TryGetPropertyValue(token, out var child):
                    current = child;
                    break;
                case JsonArray a when TryIndex(token, a.Count, out var index):
                    current = a[index];
                    break;
                default:
                    return false;
            }
        }

        value = current;
        return true;
    }

    /// <summary>
    /// Записывает значение по указателю, создавая недостающие промежуточные объекты. Массивы по
    /// пути не создаются: контракт mapping адресует только свойства объекта payload.
    /// </summary>
    public static bool TrySet(JsonObject root, IReadOnlyList<string> tokens, JsonNode? value)
    {
        if (tokens.Count == 0)
        {
            return false;
        }

        var current = root;
        for (var index = 0; index < tokens.Count - 1; index++)
        {
            var token = tokens[index];
            if (!current.TryGetPropertyValue(token, out var child) || child is null)
            {
                var created = new JsonObject();
                current[token] = created;
                current = created;
                continue;
            }

            if (child is not JsonObject nested)
            {
                return false;
            }

            current = nested;
        }

        current[tokens[^1]] = value;
        return true;
    }

    /// <summary>
    /// Пересечение двух указателей: равенство или отношение предок/потомок ПО СЕГМЕНТАМ.
    /// Именно поэтому «/a» пересекается с «/a/b», но не с «/ab» — сравниваются сегменты, а не
    /// префиксы строк.
    /// </summary>
    public static bool Overlaps(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var shared = Math.Min(left.Count, right.Count);
        for (var index = 0; index < shared; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Собирает строку указателя обратно из сегментов (для сообщений об ошибках).</summary>
    public static string Format(IReadOnlyList<string> tokens)
        => tokens.Count == 0
            ? string.Empty
            : "/" + string.Join('/', tokens.Select(token => token.Replace("~", "~0").Replace("/", "~1")));

    private static bool TryUnescape(string part, out string token)
    {
        token = string.Empty;
        if (part.IndexOf('~') < 0)
        {
            token = part;
            return true;
        }

        var builder = new System.Text.StringBuilder(part.Length);
        for (var index = 0; index < part.Length; index++)
        {
            if (part[index] != '~')
            {
                builder.Append(part[index]);
                continue;
            }

            if (index + 1 >= part.Length)
            {
                return false;
            }

            builder.Append(part[index + 1] switch
            {
                '0' => '~',
                '1' => '/',
                _ => '\0',
            });

            if (builder[^1] == '\0')
            {
                return false;
            }

            index++;
        }

        token = builder.ToString();
        return true;
    }

    private static bool TryIndex(string token, int count, out int index)
    {
        index = 0;
        if (token.Length == 0 || (token.Length > 1 && token[0] == '0'))
        {
            return false;
        }

        return int.TryParse(token, out index) && index >= 0 && index < count;
    }
}
