using System.Text.Json.Nodes;
using Course.Shared;
using Npgsql;

namespace Course.Api;

/// <summary>
/// Обязательные серии OpenMetrics сервиса api.
///
/// Все шесть значений считает одна PostgreSQL-функция по тому же состоянию, которое видит
/// проверка через представления схемы autocheck. Считать их в C# по частям означало бы иметь
/// два разных ответа на вопрос «сколько сейчас в очереди» — и спор о том, который из них верен.
///
/// Уникальные идентификаторы в документ не попадают: метрики публикуются без labels вообще,
/// поэтому кардинальность ограничена самим набором серий.
/// </summary>
public sealed class RuntimeMetrics(
    NpgsqlDataSource dataSource,
    DependencyState dependencyState,
    ILogger<RuntimeMetrics> logger)
{
    /// <summary>Снимок очередей или null, если авторитетное состояние недоступно.</summary>
    public async Task<string?> RenderAsync(CancellationToken cancellationToken)
    {
        JsonNode? snapshot;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));

            await using var command = dataSource.CreateCommand("SELECT api.runtime_metrics()::text");
            command.CommandTimeout = 3;
            var value = await command.ExecuteScalarAsync(deadline.Token) as string;
            snapshot = value is null ? null : JsonNode.Parse(value);
            dependencyState.MarkAvailable();
        }
        catch (Exception exception)
        {
            // Метрика, придуманная вместо снятой, хуже отсутствующей: по ней принимают решения.
            // Поэтому при недоступной базе scrape отвечает отказом, а не нулями.
            logger.LogWarning("metrics snapshot failed reason={Reason}", exception.GetType().Name);
            if (PostgresFailure.IsDependencyOutage(exception))
            {
                dependencyState.MarkUnavailable();
            }

            return null;
        }

        if (snapshot is null)
        {
            return null;
        }

        return new OpenMetrics()
            .Gauge(
                "workflow_jobs_ready",
                Number(snapshot, "workflowJobsReady"),
                "Workflow jobs available for claim right now.")
            .Gauge(
                "workflow_job_oldest_age_seconds",
                Number(snapshot, "workflowJobOldestAgeSeconds"),
                "Age of the oldest claimable workflow job in seconds.")
            .Gauge(
                "workflow_processes_waiting",
                Number(snapshot, "workflowProcessesWaiting"),
                "Processes waiting for a signal or a manual decision.")
            .Gauge(
                "outbox_pending",
                Number(snapshot, "outboxPending"),
                "Outbox deliveries that are still to be sent.")
            .Gauge(
                "outbox_oldest_age_seconds",
                Number(snapshot, "outboxOldestAgeSeconds"),
                "Age of the oldest undelivered Outbox message in seconds.")
            .Counter(
                "workflow_failures",
                Number(snapshot, "workflowFailuresTotal"),
                "Terminal workflow failures recorded in the append-only process history.")
            .Build();
    }

    private static double Number(JsonNode snapshot, string field)
        => snapshot[field]?.GetValue<decimal>() is { } value ? (double)value : 0;
}
