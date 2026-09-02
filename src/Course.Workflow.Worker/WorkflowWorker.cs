using System.Data;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Course.Shared;
using Npgsql;
using NpgsqlTypes;

namespace Course.Workflow.Worker;

/// <summary>
/// Сменный исполнитель процессов.
///
/// В этом классе нет ни одной ветки, зависящей от имени карты, шага или действия: он умеет ровно
/// три вещи — арендовать готовое задание, вызвать зарегистрированное действие с доверенным
/// контекстом и сообщить исход. Всё остальное приходит вместе с арендой из опубликованного
/// определения. Поэтому неизвестная на момент сборки карта исполняется без пересборки образа.
/// </summary>
public sealed class WorkflowWorker(WorkerOptions options)
{
    private const string FailpointAfterClaim = "after_job_claim";
    private const string FailpointAfterAction = "after_action_before_finish";

    private NpgsqlConnection? _connection;
    private bool _failpointReached;
    private long _consecutiveFailures;

    /// <summary>
    /// Результат одной попытки: доведена ли она до конца, с каким кодом отказа и с каким
    /// техническим следом. След присутствует только у попыток, в которых действие ДЕЙСТВИТЕЛЬНО
    /// вызывалось: отказ маппинга или недоступность версии вызовом не являются, и запись о них
    /// в журнал вызовов была бы вымыслом.
    /// </summary>
    private sealed record AttemptResult(bool Settled, string Code, bool Retryable, JsonObject? Dispatch = null)
    {
        public static readonly AttemptResult Done = new(true, "ok", false);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Log($"worker started instance={options.InstanceId} lease={options.LeaseMs}ms poll={options.PollMs}ms " +
            $"testProfile={options.TestProfile} failpoint={(options.Failpoint.Length == 0 ? "none" : options.Failpoint)}");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var connection = await ConnectAsync(cancellationToken);
                var jobs = await ClaimAsync(connection, cancellationToken);
                _consecutiveFailures = 0;

                if (jobs.Count == 0)
                {
                    await Task.Delay(options.PollMs, cancellationToken);
                    continue;
                }

                foreach (var job in jobs)
                {
                    // Начатая попытка доводится до конца даже при остановке контейнера: прерваться
                    // посередине означало бы оставить аренду висеть до истечения срока и потратить
                    // лишнюю попытку на ровном месте. Остановка проверяется между заданиями.
                    await ExecuteAsync(connection, job, CancellationToken.None);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (PostgresException exception)
            {
                // Отказ на стороне сервера (например миграции ещё не накатаны) не портит само
                // соединение: его незачем пересоздавать, достаточно подождать и повторить.
                ReportCycleFailure(exception);
                await DelayQuietlyAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                Reset();
                ReportCycleFailure(exception);
                await DelayQuietlyAsync(cancellationToken);
            }
        }

        Reset();
        Log($"worker stopped instance={options.InstanceId}");
    }

    // -----------------------------------------------------------------------------------------
    // Аренда
    // -----------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<LeasedJob>> ClaimAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        // Захват — короткая транзакция сам по себе: одна команда, никакого удержания блокировок
        // на время выполнения действия.
        await using var command = new NpgsqlCommand("SELECT workflow.claim_jobs($1, $2, $3)::text", connection);
        command.Parameters.Add(Text(options.InstanceId));
        command.Parameters.Add(Integer(options.BatchSize));
        command.Parameters.Add(Integer(options.LeaseMs));
        command.CommandTimeout = 15;

        var raw = await command.ExecuteScalarAsync(cancellationToken) as string ?? "[]";
        if (JsonNode.Parse(raw) is not JsonArray array)
        {
            return [];
        }

        return array.OfType<JsonObject>().Select(LeasedJob.FromDescriptor).ToArray();
    }

    // -----------------------------------------------------------------------------------------
    // Выполнение одной попытки
    // -----------------------------------------------------------------------------------------

    private async Task ExecuteAsync(
        NpgsqlConnection connection,
        LeasedJob job,
        CancellationToken cancellationToken)
    {
        // Failpoint «после захвата»: аренда и попытка уже зафиксированы, действие ещё не вызвано.
        if (options.Failpoint == FailpointAfterClaim)
        {
            await ReachFailpointAsync(FailpointAfterClaim);
        }

        if (!job.ActionAvailable || job.RequestSchema is null || job.ResponseSchema is null)
        {
            await FailAsync(connection, job, ContractCodes.WorkflowActionUnavailable, false, null, cancellationToken);
            return;
        }

        // Отсутствующий источник данных — объявленный неповторяемый отказ: действие не вызывается
        // вообще, поэтому никакого частичного эффекта появиться не может.
        if (!WorkflowPayload.TryBuild(job.InputConstants, job.InputMapping, job.ProcessData, out var payload, out var missing))
        {
            Log($"mapping source missing instance={options.InstanceId} job={job.JobId} pointer={missing}");
            await FailAsync(connection, job, ContractCodes.WorkflowMappingMissing, false, null, cancellationToken);
            return;
        }

        // Сформированный payload проходит схему запроса действия ДО обращения к api.invoke.
        if (!SchemaValidator.TryValidate(job.RequestSchema, payload, out _))
        {
            await FailAsync(connection, job, ContractCodes.PayloadInvalid, false, null, cancellationToken);
            return;
        }

        var attempt = await InvokeAsync(connection, job, payload, cancellationToken);
        if (attempt.Settled)
        {
            return;
        }

        // Транзакция действия уже откачена. Отдельная транзакция сохраняет попытку и расписание
        // повтора: сам факт неудачи обязан пережить откат предметного эффекта.
        await FailAsync(connection, job, attempt.Code, attempt.Retryable, attempt.Dispatch, cancellationToken);
    }

    /// <summary>
    /// Предметный эффект и успешное завершение задания — одна транзакция Npgsql. Либо
    /// фиксируются оба, либо не фиксируется ничего.
    /// </summary>
    private async Task<AttemptResult> InvokeAsync(
        NpgsqlConnection connection,
        LeasedJob job,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();
        var payloadText = payload.ToJsonString();
        var payloadHash = Hashing.Sha256Hex(payloadText);
        var started = Stopwatch.GetTimestamp();
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(job.TimeoutMs);

        // executionId служит requestId идемпотентности действия: он один и тот же для всех
        // повторов задания, поэтому повторный вызов не создаёт второго предметного эффекта.
        var context = RegisteredAction.TrustedContext(
            job.Principal,
            job.Consumer,
            job.Scopes,
            correlationId,
            job.ExecutionId.ToString(),
            deadline,
            new JsonObject
            {
                ["processId"] = job.ProcessId.ToString(),
                ["jobId"] = job.JobId.ToString(),
                ["executionId"] = job.ExecutionId.ToString(),
                ["attemptId"] = job.AttemptId.ToString(),
                ["stepKey"] = job.StepKey,
            });

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, transaction, $"SET LOCAL statement_timeout = {job.TimeoutMs}", cancellationToken);

            string? envelope;
            await using (var invoke = new NpgsqlCommand(
                "SELECT api.invoke($1, $2, $3, $4, $5)::text", connection, transaction))
            {
                invoke.Parameters.Add(Text(job.Module));
                invoke.Parameters.Add(Text(job.Action));
                // Передаётся закреплённая картой версия, а не «текущая по умолчанию»: процесс
                // обязан исполняться той версией действия, которую объявила его карта.
                invoke.Parameters.Add(Integer(job.ActionVersion));
                invoke.Parameters.Add(Jsonb(context.ToJsonString()));
                invoke.Parameters.Add(Jsonb(payloadText));
                invoke.CommandTimeout = (int)Math.Ceiling(job.TimeoutMs / 1000.0) + 5;
                envelope = await invoke.ExecuteScalarAsync(cancellationToken) as string;
            }

            var outcome = RegisteredAction.Interpret(
                envelope, job.Outcomes, job.ResponseSchema, options.MaxResultBytes);

            if (outcome.Status != RegisteredActionStatus.Succeeded)
            {
                // Объявленная ошибка или нарушение контракта откатывают предметный эффект целиком.
                // Технический след при этом не теряется: его запишет отдельная транзакция fail_job.
                await transaction.RollbackAsync(CancellationToken.None);
                Log($"attempt rejected instance={options.InstanceId} job={job.JobId} " +
                    $"attempt={job.AttemptNumber} code={outcome.Code} retryable={outcome.Retryable}");
                return new AttemptResult(
                    false,
                    outcome.Code,
                    outcome.Status == RegisteredActionStatus.DomainError && outcome.Retryable,
                    Dispatch(job, correlationId, payloadHash, outcome.ActionVersion, started));
            }

            // Failpoint «после действия, до завершения»: эффект уже выполнен ВНУТРИ транзакции,
            // но ещё не зафиксирован. Гибель здесь обязана не оставить ни одного эффекта.
            if (options.Failpoint == FailpointAfterAction)
            {
                await ReachFailpointAsync(FailpointAfterAction);
            }

            await ExecuteAsync(
                connection, transaction, $"SET LOCAL statement_timeout = {options.FinishTimeoutMs}", cancellationToken);

            var durationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var dispatch = Dispatch(job, correlationId, payloadHash, outcome.ActionVersion, started);

            var finish = await FinishAsync(connection, transaction, job, outcome, dispatch, cancellationToken);
            if (finish?["status"]?.GetValue<string>() != "ok")
            {
                // Право на завершение потеряно, пока действие выполнялось. Откат — единственный
                // корректный исход: задание уже перехвачено другим арендатором.
                await transaction.RollbackAsync(CancellationToken.None);
                Log($"finish refused instance={options.InstanceId} job={job.JobId} " +
                    $"code={finish?["code"]?.GetValue<string>() ?? "unknown"}");
                return AttemptResult.Done;
            }

            await transaction.CommitAsync(CancellationToken.None);
            Log($"attempt succeeded instance={options.InstanceId} job={job.JobId} " +
                $"attempt={job.AttemptNumber} outcome={outcome.Outcome} durationMs={durationMs}");
            return AttemptResult.Done;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            await SafeRollbackAsync(transaction);
            Log($"attempt timed out instance={options.InstanceId} job={job.JobId} attempt={job.AttemptNumber}");
            return new AttemptResult(
                false,
                ContractCodes.ActionTimeout,
                true,
                Dispatch(job, correlationId, payloadHash, job.ActionVersion, started));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await SafeRollbackAsync(transaction);
            return new AttemptResult(
                false,
                ContractCodes.ActionTimeout,
                true,
                Dispatch(job, correlationId, payloadHash, job.ActionVersion, started));
        }
        catch (PostgresException exception)
        {
            // Сервер ответил отказом — соединение цело, и попытку можно честно закрыть. Это
            // важно: сбой ВНУТРИ границы завершения (например несогласованная карта) не тратит
            // бюджет max_attempts, и без явного отчёта задание перезахватывалось бы бесконечно.
            await SafeRollbackAsync(transaction);
            Log($"attempt rejected by database instance={options.InstanceId} job={job.JobId} " +
                $"attempt={job.AttemptNumber} failure={Redaction.Describe(exception)}");
            return new AttemptResult(false, ContractCodes.WorkflowBoundaryFailed, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Сбой самого соединения: состояние транзакции неизвестно, сообщить о нём тем же
            // соединением нельзя. Задание остаётся арендованным и будет перехвачено по истечении
            // срока — теперь под защитой абсолютного потолка попыток в claim_jobs.
            await SafeRollbackAsync(transaction);
            Log($"attempt failed instance={options.InstanceId} job={job.JobId} " +
                $"failure={Redaction.Describe(exception)}");
            throw;
        }
    }

    /// <summary>
    /// Технический след одного вызова зарегистрированного действия. Наружу выходит только
    /// отпечаток payload, но никогда сам payload.
    /// </summary>
    private static JsonObject Dispatch(
        LeasedJob job,
        Guid correlationId,
        string payloadHash,
        int? executedVersion,
        long startedAt)
        => new()
        {
            ["correlationId"] = correlationId.ToString(),
            ["requestId"] = job.ExecutionId.ToString(),
            ["module"] = job.Module,
            ["action"] = job.Action,
            ["version"] = executedVersion ?? job.ActionVersion,
            ["principal"] = job.Principal,
            ["consumer"] = job.Consumer,
            ["payloadHash"] = payloadHash,
            ["durationMs"] = (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
        };

    private async Task<JsonObject?> FinishAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LeasedJob job,
        RegisteredActionOutcome outcome,
        JsonObject dispatch,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT workflow.finish_job($1, $2, $3, $4, $5, $6)::text", connection, transaction);
        command.Parameters.Add(Uuid(job.JobId));
        // Право на завершение предъявляется парой «арендатор + версия аренды»: именно её
        // повторно проверяет граница finish_job, а не доверяет тому, что worker дошёл сюда.
        command.Parameters.Add(Text(options.InstanceId));
        command.Parameters.Add(BigInt(job.LeaseVersion));
        command.Parameters.Add(Text(outcome.Outcome!));
        command.Parameters.Add(Jsonb((outcome.Result ?? new JsonObject()).ToJsonString()));
        command.Parameters.Add(Jsonb(dispatch.ToJsonString()));

        var raw = await command.ExecuteScalarAsync(cancellationToken) as string;
        return raw is null ? null : JsonNode.Parse(raw) as JsonObject;
    }

    private async Task FailAsync(
        NpgsqlConnection connection,
        LeasedJob job,
        string code,
        bool retryable,
        JsonObject? dispatch,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT workflow.fail_job($1, $2, $3, $4, $5, $6)::text", connection);
        command.Parameters.Add(Uuid(job.JobId));
        command.Parameters.Add(Text(options.InstanceId));
        command.Parameters.Add(BigInt(job.LeaseVersion));
        command.Parameters.Add(Text(code));
        command.Parameters.Add(Boolean(retryable));
        command.Parameters.Add(NullableJsonb(dispatch?.ToJsonString()));
        command.CommandTimeout = 15;

        var raw = await command.ExecuteScalarAsync(cancellationToken) as string;
        var envelope = raw is null ? null : JsonNode.Parse(raw) as JsonObject;
        var state = envelope?["result"]?["state"]?.GetValue<string>()
                    ?? envelope?["code"]?.GetValue<string>()
                    ?? "unknown";

        Log($"attempt reported instance={options.InstanceId} job={job.JobId} " +
            $"attempt={job.AttemptNumber} code={code} retryable={retryable} state={state}");
    }

    // -----------------------------------------------------------------------------------------
    // Failpoints, соединение и журнал
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Достижение failpoint. Worker печатает РОВНО ОДНУ структурную строку подтверждения и
    /// блокируется до принудительной остановки — проверка ждёт именно эту строку, а не таймаут,
    /// поэтому граница аварии воспроизводится точно, а не «примерно».
    /// </summary>
    private async Task ReachFailpointAsync(string name)
    {
        if (!_failpointReached)
        {
            _failpointReached = true;
            var acknowledgement = new JsonObject
            {
                ["event"] = "failpoint.reached",
                ["name"] = name,
                ["instanceId"] = options.InstanceId,
            };
            Console.Out.WriteLine(acknowledgement.ToJsonString());
            Console.Out.Flush();
        }

        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }

    private async Task<NpgsqlConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_connection is { State: ConnectionState.Open })
        {
            return _connection;
        }

        Reset();

        // Одно долгоживущее соединение, а не пул: у исполнителя ровно одна последовательная
        // цепочка работы, и наблюдаемая роль сессии в базе тоже должна быть ровно одна.
        var connection = new NpgsqlConnection(CourseDatabase.ConnectionString(
            connectTimeoutSeconds: Env.OptionalInt("COURSE_POSTGRES_CONNECT_TIMEOUT", 5),
            commandTimeoutSeconds: Env.OptionalInt("COURSE_POSTGRES_COMMAND_TIMEOUT", 60),
            maxPoolSize: 2));

        await connection.OpenAsync(cancellationToken);
        _connection = connection;
        return connection;
    }

    private void Reset()
    {
        try
        {
            _connection?.Dispose();
        }
        catch (Exception)
        {
            // Закрытие уже сломанного соединения не должно мешать следующей попытке.
        }

        _connection = null;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SafeRollbackAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // Соединение могло уже умереть: тогда транзакцию откатит сам PostgreSQL.
        }
    }

    private async Task DelayQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Math.Max(options.PollMs, 250), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Остановка во время паузы — обычный путь завершения.
        }
    }

    private void ReportCycleFailure(Exception exception)
    {
        _consecutiveFailures++;

        // Пока база поднимается, повторяющийся отказ — норма: журнал не должен превращаться в шум.
        if (_consecutiveFailures == 1 || _consecutiveFailures % 40 == 0)
        {
            Log($"worker cycle failed instance={options.InstanceId} " +
                $"failure={Redaction.Describe(exception)} consecutive={_consecutiveFailures}");
        }
    }

    /// <summary>
    /// Диагностика идёт в stderr: stdout остаётся чистым каналом структурных событий worker'а.
    /// Ни payload, ни учётные данные, ни текст исключения PostgreSQL в журнал не попадают.
    /// </summary>
    private static void Log(string message)
        => Console.Error.WriteLine($"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff} {message}");

    private static NpgsqlParameter Text(string value)
        => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    private static NpgsqlParameter Integer(int value)
        => new() { NpgsqlDbType = NpgsqlDbType.Integer, Value = value };

    private static NpgsqlParameter BigInt(long value)
        => new() { NpgsqlDbType = NpgsqlDbType.Bigint, Value = value };

    private static NpgsqlParameter Uuid(Guid value)
        => new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = value };

    private static NpgsqlParameter Boolean(bool value)
        => new() { NpgsqlDbType = NpgsqlDbType.Boolean, Value = value };

    private static NpgsqlParameter Jsonb(string value)
        => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };

    private static NpgsqlParameter NullableJsonb(string? value)
        => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)value ?? DBNull.Value };
}
