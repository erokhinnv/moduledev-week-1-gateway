using Course.Shared;

namespace Course.Api;

/// <summary>Границы одной транзакции действия, на которых закрытый профиль умеет остановиться.</summary>
public enum ActionPhase
{
    /// <summary>Эффект выполнен и результат проверен, транзакция ещё НЕ зафиксирована.</summary>
    BeforeCommit,

    /// <summary>Транзакция зафиксирована, ответ клиенту ещё не отправлен.</summary>
    AfterCommit,
}

/// <summary>
/// Привязка точек аварийной остановки к границам конкретных действий.
///
/// Почему это отдельный класс, а не условие внутри <see cref="ActionRuntime"/>. Универсальный
/// исполнитель не имеет права ветвиться по имени предметного действия: ровно этим он и ценен.
/// Но контракт закрытого профиля задаёт точки остановки предметно — «после сохранения Inbox» и
/// «после ручного решения, до commit», — и никак иначе: остановка на первой же встреченной
/// границе поймала бы постороннее действие и доказала бы не тот инвариант.
///
/// Компромисс здесь такой: таблица ниже ДЕКЛАРАТИВНА и существует только для профиля проверки.
/// Исполнитель спрашивает «остановиться ли здесь» координатами границы, а сопоставление имён
/// живёт в одном месте, вне пути исполнения. В обычном профиле <see cref="Failpoint"/> пуст, и
/// весь этот класс сводится к одному сравнению с пустой строкой.
/// </summary>
public sealed class ActionFailpoints(Failpoint failpoint)
{
    private static readonly (string Name, string Module, string Action, ActionPhase Phase)[] Points =
    [
        // Остановка внутри незавершённой транзакции: принудительное завершение процесса обязано
        // откатить решение, переход, новый job и успешный результат идемпотентности вместе.
        ("after_manual_decision", "workflow", "manual", ActionPhase.BeforeCommit),

        // Остановка после durable commit Inbox и квитанции, но до HTTP-ответа: сохранённое
        // сообщение обязано пережить смерть api и быть применённым согласователем.
        ("after_inbox_saved", "receipt", "accept", ActionPhase.AfterCommit),
    ];

    /// <summary>
    /// Останавливает процесс, если активная точка привязана именно к этой границе. В остальных
    /// случаях возвращает управление немедленно и ничего не печатает.
    /// </summary>
    public Task StopIfReachedAsync(string module, string action, ActionPhase phase)
    {
        if (failpoint.Name.Length == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var point in Points)
        {
            if (failpoint.Matches(point.Name)
                && point.Phase == phase
                && string.Equals(point.Module, module, StringComparison.Ordinal)
                && string.Equals(point.Action, action, StringComparison.Ordinal))
            {
                return failpoint.ReachAsync(point.Name);
            }
        }

        return Task.CompletedTask;
    }
}
