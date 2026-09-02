namespace Course.Workflow.Worker;

/// <summary>
/// Сколько времени осталось у права на завершение.
///
/// Понятие вынесено из исполнителя намеренно: это арифметика, от которой зависит, не окажется ли
/// работа исполнителя дольше, чем существует само право её зафиксировать. Такую величину нельзя
/// держать выражением внутри метода — её надо уметь проверить отдельно от Docker, PostgreSQL и
/// расписания опроса.
/// </summary>
public static class LeaseBudget
{
    /// <summary>
    /// Минимальный бюджет границы завершения. Аренда, истёкшая по часам, всё ещё действительна,
    /// пока её никто не перехватил: finish_job сверяет версию аренды, а не время. Поэтому нулевой
    /// остаток означает «шанс мал», а не «шанса нет», и попытка обязана его получить.
    /// </summary>
    public const int MinimumFinishMs = 1_000;

    /// <summary>
    /// Бюджет границы завершения: остаток аренды, зажатый между минимальным шансом и настроенным
    /// потолком. Верхняя граница нужна не меньше нижней — работа сверх остатка идёт уже в окне,
    /// где задание может быть перехвачено, и её результат всё равно будет откачен.
    /// </summary>
    public static int FinishMs(DateTimeOffset leaseUntil, DateTimeOffset now, int maximumMs)
    {
        var remaining = leaseUntil - now;
        var remainingMs = remaining > TimeSpan.FromMilliseconds(int.MaxValue)
            ? int.MaxValue
            : (int)Math.Max(remaining.TotalMilliseconds, 0);

        // Потолок ниже минимума означал бы противоречивую настройку; побеждает потолок, потому что
        // он выражает явное решение эксплуатации, а минимум — лишь наше нежелание отдавать нулевой
        // бюджет.
        return Math.Min(Math.Max(remainingMs, Math.Min(MinimumFinishMs, maximumMs)), maximumMs);
    }
}
