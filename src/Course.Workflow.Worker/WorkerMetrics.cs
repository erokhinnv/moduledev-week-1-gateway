using Course.Shared;

namespace Course.Workflow.Worker;

/// <summary>
/// Собственные метрики одного исполнителя.
///
/// Обязательные шесть серий курса публикует api как согласованную проекцию PostgreSQL — worker
/// не имеет права публиковать своё мнение о длине общей очереди, иначе два ответа на один вопрос
/// начали бы расходиться. Здесь остаётся только то, чего в базе нет: сколько работы прошло
/// именно через ЭТОТ процесс.
///
/// Labels отсутствуют вовсе, поэтому уникальный идентификатор не может попасть в имя или
/// значение метрики даже случайно.
/// </summary>
public sealed class WorkerMetrics
{
    private long _claimed;
    private long _settled;
    private long _failed;
    private long _leased;

    public void JobClaimed()
    {
        Interlocked.Increment(ref _claimed);
        Interlocked.Increment(ref _leased);
    }

    public void JobSettled(bool succeeded)
    {
        Interlocked.Increment(ref _settled);
        if (!succeeded)
        {
            Interlocked.Increment(ref _failed);
        }

        Interlocked.Decrement(ref _leased);
    }

    /// <summary>Документ OpenMetrics текущего состояния процесса.</summary>
    public string Render()
        => new OpenMetrics()
            .Gauge(
                "workflow_worker_leased_jobs",
                Math.Max(Interlocked.Read(ref _leased), 0),
                "Jobs this worker instance currently holds a lease for.")
            .Counter(
                "workflow_worker_jobs_claimed",
                Interlocked.Read(ref _claimed),
                "Jobs leased by this worker instance since start.")
            .Counter(
                "workflow_worker_attempts_settled",
                Interlocked.Read(ref _settled),
                "Attempts this worker instance reported to the finish boundary since start.")
            .Counter(
                "workflow_worker_attempts_failed",
                Interlocked.Read(ref _failed),
                "Attempts this worker instance reported as failed since start.")
            .Build();
}
