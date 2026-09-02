using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace Course.Shared;

/// <summary>
/// Чтение карты процесса, записанной в YAML, в то же самое дерево JsonNode, что и её JSON-форма.
///
/// Одна и та же карта в JSON и в YAML обязана быть ОДНИМ контрактом: повторная публикация из
/// другого формата не должна выглядеть как другая версия. Поэтому скаляры разрешаются по core
/// schema YAML: незакавыченные <c>1</c>, <c>true</c> и <c>null</c> становятся числом, булевым
/// значением и null, а закавыченные <c>"1"</c> остаются строкой — ровно как в JSON.
/// </summary>
public static class YamlDocument
{
    /// <summary>Разбирает текст YAML в JsonNode. Пустой документ даёт null.</summary>
    public static JsonNode? Parse(string text)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(text);
        stream.Load(reader);

        if (stream.Documents.Count == 0)
        {
            return null;
        }

        if (stream.Documents.Count > 1)
        {
            throw new FormatException("a workflow map must be a single YAML document");
        }

        return Convert(stream.Documents[0].RootNode);
    }

    private static JsonNode? Convert(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
            {
                var result = new JsonObject();
                foreach (var entry in mapping.Children)
                {
                    if (entry.Key is not YamlScalarNode key)
                    {
                        throw new FormatException("workflow map keys must be scalars");
                    }

                    result[key.Value ?? string.Empty] = Convert(entry.Value);
                }

                return result;
            }

            case YamlSequenceNode sequence:
            {
                var result = new JsonArray();
                foreach (var item in sequence.Children)
                {
                    result.Add(Convert(item));
                }

                return result;
            }

            case YamlScalarNode scalar:
                return ConvertScalar(scalar);

            default:
                throw new FormatException("workflow map contains an unsupported YAML node");
        }
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? string.Empty;

        // Закавыченный скаляр — всегда строка, даже если выглядит как число: это единственный
        // способ записать в YAML строку "1" и не получить из неё целое.
        if (scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted)
        {
            return JsonValue.Create(value);
        }

        if (value.Length == 0 || value is "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        if (value is "true" or "True" or "TRUE")
        {
            return JsonValue.Create(true);
        }

        if (value is "false" or "False" or "FALSE")
        {
            return JsonValue.Create(false);
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return JsonValue.Create(integer);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            value.IndexOfAny(['.', 'e', 'E']) >= 0)
        {
            return JsonValue.Create(number);
        }

        return JsonValue.Create(value);
    }
}
