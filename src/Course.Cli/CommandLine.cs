namespace Course.Cli;

/// <summary>
/// Минимальный разборщик аргументов командной строки для контрактных команд Course CLI.
/// Поддерживает как позиционные аргументы (например "action publish"), так и именованные опции
/// вида --version 2 или --version=2.
/// </summary>
public sealed class CommandLine
{
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);

    private CommandLine()
    {
    }

    /// <summary>Список аргументов без опций, в исходном порядке (например ["action", "publish", "manifest.json"]).</summary>
    public IReadOnlyList<string> Positional => _positional;

    /// <summary>Разбирает сырой массив аргументов командной строки в позиционные аргументы и именованные опции.</summary>
    public static CommandLine Parse(IReadOnlyList<string> arguments)
    {
        var line = new CommandLine();
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                line._positional.Add(argument);
                continue;
            }

            // Форма --имя=значение.
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                line._options[argument[..separator]] = argument[(separator + 1)..];
                continue;
            }

            // Форма --имя значение (значение — следующий аргумент, если он сам не похож на опцию).
            if (index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                line._options[argument] = arguments[index + 1];
                index++;
                continue;
            }

            // Флаг без значения, например просто --help.
            line._options[argument] = "true";
        }

        return line;
    }

    /// <summary>Возвращает значение опции по имени или null, если такой опции не было.</summary>
    public string? Value(string name) => _options.TryGetValue(name, out var value) ? value : null;

    /// <summary>Пытается прочитать опцию как целое число.</summary>
    public bool TryInteger(string name, out int value)
    {
        value = 0;
        var raw = Value(name);
        return raw is not null && int.TryParse(raw, out value);
    }

    /// <summary>Проверяет, была ли передана опция с данным именем (в любой форме).</summary>
    public bool Has(string name) => _options.ContainsKey(name);
}
