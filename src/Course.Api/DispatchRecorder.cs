using System.Text.Json.Nodes;
using Npgsql;

namespace Course.Api;

/// <summary>Технический след (evidence) одного вызова действия — то, что попадает в проверочную таблицу api.dispatch.</summary>
public sealed class DispatchEvidence
{
    public required Guid CorrelationId { get; init; }
    public required string Module { get; init; }
    public required string Action { get; init; }
    public required string Principal { get; init; }
    public required string Consumer { get; init; }

    /// <summary>SHA-256 хеш тела запроса — само содержимое запроса сюда не попадает, только его отпечаток.</summary>
    public required string PayloadHash { get; init; }
    public string? RequestId { get; set; }
    public int? Version { get; set; }
    public string Status { get; set; } = "ERROR";
    public string? Outcome { get; set; }
    public string? ErrorCode { get; set; }
    public int? DurationMs { get; set; }
}

/// <summary>
/// Записывает технический след вызова действия на ОТДЕЛЬНОМ соединении, вне предметной
/// транзакции. Это осознанное решение: если предметный вызов был откачен (rollback), запись о
/// самой попытке всё равно должна остаться видимой (для отладки и для проверки), поэтому она
/// пишется отдельным, независимым запросом. И наоборот — сам след никогда не содержит исходный
/// payload целиком, только его хеш, чтобы не раскрывать содержимое запроса.
/// </summary>
public sealed class DispatchRecorder(
    NpgsqlDataSource dataSource,
    DependencyState dependencyState,
    ILogger<DispatchRecorder> logger)
{
    public async Task RecordAsync(DispatchEvidence evidence, CancellationToken cancellationToken)
    {
        if (dependencyState.IsKnownUnavailable)
        {
            // Хранилище, куда должен был бы записаться след, — это то же самое хранилище,
            // которое только что доказало свою недоступность. Пытаться писать туда бессмысленно.
            return;
        }

        var payload = new JsonObject
        {
            ["correlationId"] = evidence.CorrelationId.ToString(),
            ["requestId"] = evidence.RequestId,
            ["module"] = evidence.Module,
            ["action"] = evidence.Action,
            ["version"] = evidence.Version,
            ["principal"] = evidence.Principal,
            ["consumer"] = evidence.Consumer,
            ["payloadHash"] = evidence.PayloadHash,
            ["status"] = evidence.Status,
            ["outcome"] = evidence.Outcome,
            ["errorCode"] = evidence.ErrorCode,
            ["durationMs"] = evidence.DurationMs,
        };

        try
        {
            // Запись следа не должна зависеть от того, что клиентское HTTP-соединение уже
            // закрылось (CancellationToken.None вместо токена запроса) — она переживает клиента
            // и ограничена только собственным коротким дедлайном в 5 секунд.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));

            await using var command = dataSource.CreateCommand("SELECT api.record_dispatch($1)");
            command.Parameters.Add(ActionCatalog.Jsonb(payload.ToJsonString()));
            command.CommandTimeout = 5;
            await command.ExecuteNonQueryAsync(deadline.Token);
        }
        catch (Exception exception)
        {
            // Запись следа выполняется по принципу "best effort" (по возможности): она никогда
            // не должна влиять на ответ, который уже получил клиент по самому действию.
            // Если запись не удалась — это просто предупреждение в логах, не более того.
            logger.LogWarning(
                "dispatch evidence was not stored correlationId={CorrelationId} reason={Reason}",
                evidence.CorrelationId,
                exception.GetType().Name);
        }
    }
}
