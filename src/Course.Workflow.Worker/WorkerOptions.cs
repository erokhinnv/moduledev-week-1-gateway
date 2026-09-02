using Course.Shared;

namespace Course.Workflow.Worker;

/// <summary>
/// Настройки одного экземпляра исполнителя.
///
/// Профиль исполнения задаётся конфигурацией, а не пересборкой: в test profile аренда короткая, а
/// опрос частый, чтобы аварийные сценарии воспроизводились за секунды; в обычном профиле обе
/// величины консервативнее.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>Идентификатор арендатора. Он же — instanceId в структурных событиях worker'а.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Срок аренды задания в миллисекундах.</summary>
    public required int LeaseMs { get; init; }

    /// <summary>Интервал опроса очереди в миллисекундах.</summary>
    public required int PollMs { get; init; }

    /// <summary>Сколько заданий брать за один заход. Одно — самый честный вариант для двух worker'ов.</summary>
    public required int BatchSize { get; init; }

    /// <summary>Включён ли test profile (короткая аренда, частый опрос, поддержка failpoints).</summary>
    public required bool TestProfile { get; init; }

    /// <summary>Имя активного failpoint или пустая строка.</summary>
    public required string Failpoint { get; init; }

    /// <summary>
    /// ПОТОЛОК бюджета границы завершения, а не сам бюджет: фактический считается из остатка
    /// аренды (см. <see cref="LeaseBudget"/>). Раньше это значение было бюджетом целиком, и в
    /// тестовом профиле оно превышало всю аренду впятеро.
    /// </summary>
    public int FinishTimeoutMs { get; init; } = 10_000;

    /// <summary>Верхняя граница размера конверта, возвращённого api.invoke.</summary>
    public int MaxResultBytes { get; init; } = Env.OptionalInt("COURSE_MAX_RESULT_BYTES", 512 * 1024);

    public static WorkerOptions FromEnvironment()
    {
        var testProfile = Env.Optional("COURSE_TEST_PROFILE", "0") == "1";

        return new WorkerOptions
        {
            InstanceId = Env.Optional("COURSE_WORKER_ID", "worker"),
            // Test profile: аренда 2 секунды, опрос не реже чем раз в 100 мс — таковы условия
            // воспроизводимости аварийных сценариев проверки.
            LeaseMs = Env.OptionalInt("COURSE_WORKER_LEASE_MS", testProfile ? 2_000 : 30_000),
            PollMs = Env.OptionalInt("COURSE_WORKER_POLL_MS", testProfile ? 50 : 1_000),
            BatchSize = Env.OptionalInt("COURSE_WORKER_BATCH", 1),
            TestProfile = testProfile,
            Failpoint = testProfile ? Env.Optional("COURSE_FAILPOINT", string.Empty).Trim() : string.Empty,
        };
    }
}
