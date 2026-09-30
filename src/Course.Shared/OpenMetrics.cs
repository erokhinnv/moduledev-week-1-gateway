using System.Globalization;
using System.Text;

namespace Course.Shared;

/// <summary>
/// Сборка документа OpenMetrics 1.0.0.
///
/// Формат выглядит простым ровно до первой ошибки: документ обязан заканчиваться строкой
/// <c># EOF</c>, не содержать <c>\r</c>, объявлять counter по имени СЕМЕЙСТВА (без суффикса
/// <c>_total</c>, который есть только у самого значения) и печатать числа без зависимости от
/// текущей культуры. Один класс на весь проект нужен затем, чтобы api и worker не разошлись в
/// трактовке этих правил.
///
/// Уникальные идентификаторы сюда не попадают по построению: метрика добавляется только именем и
/// числом, а значений labels этот тип не принимает вовсе.
/// </summary>
public sealed class OpenMetrics
{
    /// <summary>Media type ответа: проверяется дословно, вместе с версией и кодировкой.</summary>
    public const string MediaType = "application/openmetrics-text; version=1.0.0; charset=utf-8";

    private readonly StringBuilder _builder = new();

    /// <summary>Мгновенное значение: очередь, возраст, число ожидающих процессов.</summary>
    public OpenMetrics Gauge(string name, double value, string help)
        => Append(name, "gauge", help, name, value);

    /// <summary>
    /// Счётчик, растущий только вперёд. Имя семейства передаётся без суффикса: по контракту
    /// OpenMetrics <c># TYPE</c> объявляет семейство, а <c>_total</c> получает только значение.
    /// </summary>
    public OpenMetrics Counter(string family, double value, string help)
        => Append(family, "counter", help, family + "_total", value);

    /// <summary>Завершает документ обязательной строкой <c># EOF</c>.</summary>
    public string Build() => _builder.ToString() + "# EOF\n";

    private OpenMetrics Append(string family, string type, string help, string sample, double value)
    {
        _builder.Append("# HELP ").Append(family).Append(' ').Append(Escape(help)).Append('\n');
        _builder.Append("# TYPE ").Append(family).Append(' ').Append(type).Append('\n');
        _builder.Append(sample).Append(' ').Append(Number(value)).Append('\n');
        return this;
    }

    /// <summary>
    /// Число в инвариантной культуре и без экспоненты. Экспоненциальная запись формату не
    /// противоречит, но читается людьми хуже, а разделитель из локали сделал бы документ
    /// невалидным на машине с русской культурой — то есть ровно там, где он и собирается.
    /// </summary>
    private static string Number(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }

        var rounded = Math.Round(value, 3, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>В строке <c># HELP</c> перевод строки закончил бы документ раньше времени.</summary>
    private static string Escape(string help)
        => help.Replace("\\", "\\\\", StringComparison.Ordinal)
               .Replace("\n", " ", StringComparison.Ordinal)
               .Replace("\r", " ", StringComparison.Ordinal);
}
