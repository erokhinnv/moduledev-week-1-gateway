using System.Text.Json.Nodes;
using Course.Workflow.Worker;
using Xunit;

namespace Course.Tests;

/// <summary>
/// Согласование срока аренды и бюджета исполнителя.
///
/// Это единственная часть fencing, которую не видно ни в SQL, ни в интеграционном прогоне: срок
/// аренды приходит числом, а решение «сколько ещё можно работать» принимается арифметикой на
/// стороне исполнителя. Ошибка здесь не падает и не логируется — она просто возвращает поведение,
/// при котором граница завершения работает дольше, чем существует право завершать.
/// </summary>
public sealed class WorkflowLeaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FinishBudgetIsTheRemainingLeaseWhenItFitsTheConfiguredMaximum()
    {
        var budget = LeaseBudget.FinishMs(Now.AddMilliseconds(1_800), Now, maximumMs: 10_000);

        Assert.Equal(1_800, budget);
    }

    /// <summary>
    /// Главный смысл производного бюджета: он не может превысить остаток аренды. Постоянные
    /// 10 секунд поверх аренды в 2 секунды давали границе право работать впятеро дольше, чем
    /// существует само право завершить, — и успешное действие откатывалось по lease_stale.
    /// </summary>
    [Fact]
    public void FinishBudgetNeverExceedsTheRemainingLease()
    {
        var budget = LeaseBudget.FinishMs(Now.AddMilliseconds(1_800), Now, maximumMs: 10_000);

        Assert.True(budget < 2_000, $"budget {budget}ms outlived a 1800ms lease remainder");
    }

    [Fact]
    public void FinishBudgetIsCappedByTheConfiguredMaximum()
    {
        var budget = LeaseBudget.FinishMs(Now.AddMinutes(5), Now, maximumMs: 10_000);

        Assert.Equal(10_000, budget);
    }

    /// <summary>
    /// Истёкшая по часам аренда всё ещё действительна, пока её никто не перехватил: finish_job
    /// сверяет версию аренды, а не время. Поэтому нулевой остаток обязан дать минимальный шанс, а
    /// не нулевой бюджет — иначе исполнитель сам выбрасывал бы уже выполненный предметный эффект.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5_000)]
    public void AnExpiredLeaseStillGetsAMinimalChanceToCommit(int offsetMs)
    {
        var budget = LeaseBudget.FinishMs(Now.AddMilliseconds(offsetMs), Now, maximumMs: 10_000);

        Assert.Equal(LeaseBudget.MinimumFinishMs, budget);
    }

    /// <summary>Потолок ниже минимума — противоречивая настройка; побеждает явно заданный потолок.</summary>
    [Fact]
    public void AnUnusuallySmallMaximumStillWins()
    {
        var budget = LeaseBudget.FinishMs(Now.AddMilliseconds(-1), Now, maximumMs: 250);

        Assert.Equal(250, budget);
    }

    /// <summary>
    /// Срок аренды приходит из PostgreSQL в том виде, в каком его отдаёт jsonb: ISO 8601 со
    /// смещением и микросекундами. Разбор — контракт между claim_jobs и исполнителем, и молчаливая
    /// потеря этого поля вернула бы постоянный бюджет.
    /// </summary>
    [Fact]
    public void TheLeaseDeadlineIsReadFromThePostgresRendering()
    {
        var job = LeasedJob.FromDescriptor(Descriptor("2026-09-02T12:00:01.234567+00:00"));

        Assert.Equal(
            new DateTimeOffset(2026, 9, 2, 12, 0, 1, TimeSpan.Zero).AddTicks(2_345_670),
            job.LeaseUntil);
    }

    [Fact]
    public void AnOffsetOtherThanUtcIsNormalised()
    {
        var job = LeasedJob.FromDescriptor(Descriptor("2026-09-02T15:00:01+03:00"));

        Assert.Equal(new DateTimeOffset(2026, 9, 2, 12, 0, 1, TimeSpan.Zero), job.LeaseUntil);
    }

    /// <summary>
    /// Отсутствующий срок не должен читаться как «аренда уже истекла»: тогда исполнитель урезал бы
    /// бюджет до минимума на ровном месте. Неизвестный срок трактуется как дальний — и об этом
    /// исполнитель сообщает отдельно.
    /// </summary>
    [Fact]
    public void AMissingDeadlineDoesNotLookLikeAnExpiredLease()
    {
        var job = LeasedJob.FromDescriptor(Descriptor(null));

        Assert.Equal(DateTimeOffset.MaxValue, job.LeaseUntil);
        Assert.Equal(10_000, LeaseBudget.FinishMs(job.LeaseUntil, Now, maximumMs: 10_000));
    }

    /// <summary>
    /// Дескриптор собирается РАЗБОРОМ JSON, а не объектом в памяти: именно так он приходит от
    /// claim_jobs, и только так у чисел то же представление. Собранный вручную граф JsonNode
    /// проверял бы другой путь, чем тот, который выполняется в бою.
    /// </summary>
    private static JsonObject Descriptor(string? leaseUntil)
    {
        var lease = leaseUntil is null ? string.Empty : $"\"leaseUntil\": \"{leaseUntil}\",";

        return (JsonObject)JsonNode.Parse($$"""
            {
              "jobId": "11111111-1111-1111-1111-111111111111",
              "executionId": "22222222-2222-2222-2222-222222222222",
              "attemptId": "33333333-3333-3333-3333-333333333333",
              "attemptNumber": 1,
              "leaseVersion": 1,
              {{lease}}
              "processId": "44444444-4444-4444-4444-444444444444",
              "stepKey": "invoke",
              "module": "training",
              "action": "canary",
              "actionVersion": 1,
              "timeoutMs": 1600,
              "actionAvailable": true
            }
            """)!;
    }
}
