using System.Text.Json.Nodes;
using Xunit;

namespace Course.IntegrationTests;

/// <summary>
/// Инварианты workflow-ядра, которые нельзя доказать без живого контура: неизменяемость
/// опубликованной версии карты, append-only история, границы прав сменного исполнителя и
/// поведение конкурентных команд.
///
/// Эти тесты НЕ дублируют <c>./check.sh</c>: открытая проверка гоняет полные аварийные сценарии
/// на своих fixtures, а здесь закрыты те же границы со стороны хранилища и ролей, на встроенной
/// карте <c>workflow-smoke</c>.
/// </summary>
[Collection(ProbeFixture.CollectionName)]
public sealed class WorkflowIntegrationTests
{
    private const string SmokeFlow = "workflow-smoke";

    /// <summary>Ровно четыре функции-границы и ничего больше — контракт роли исполнителя.</summary>
    private static readonly string[] WorkerBoundaries =
    [
        "api.invoke",
        "workflow.claim_jobs",
        "workflow.fail_job",
        "workflow.finish_job",
    ];

    [SkippableFact]
    public void PublishedFlowVersionIsImmutableAndUndeletable()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        // Контрактные колонки опубликованной версии не может изменить даже суперпользователь:
        // запрет держит триггер, а не права роли.
        var update = IntegrationEnvironment.Psql(
            "UPDATE workflow.flow_version SET start_step_key = 'tampered' " +
            "WHERE flow_id = (SELECT flow_id FROM workflow.flow_definition WHERE flow_name = 'workflow-smoke') " +
            "AND version = 1;");
        Assert.NotEqual(0, update.ExitCode);

        var delete = IntegrationEnvironment.Psql(
            "DELETE FROM workflow.flow_version " +
            "WHERE flow_id = (SELECT flow_id FROM workflow.flow_definition WHERE flow_name = 'workflow-smoke') " +
            "AND version = 1;");
        Assert.NotEqual(0, delete.ExitCode);

        // Операционное состояние — единственное, что меняется после публикации.
        var activate = IntegrationEnvironment.Cli(null, "flow", "activate", SmokeFlow, "--version", "1");
        Assert.Equal(0, activate.ExitCode);
    }

    [SkippableFact]
    public void WorkflowHistoryIsAppendOnly()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        // Историю нужно сначала создать: UPDATE, не задевший ни одной строки, не вызывает
        // построчных триггеров и «успешно» проходит, ничего не доказывая.
        var processId = StartSmokeProcess("itest-history");
        Assert.True(
            WaitForState(processId, "WAITING_SIGNAL"),
            $"process {processId} did not reach WAITING_SIGNAL");

        var update = IntegrationEnvironment.Psql(
            $"UPDATE workflow.workflow_event SET event_type = 'Tampered' WHERE process_id = '{processId}';");
        Assert.NotEqual(0, update.ExitCode);

        var delete = IntegrationEnvironment.Psql(
            $"DELETE FROM workflow.workflow_event WHERE process_id = '{processId}';");
        Assert.NotEqual(0, delete.ExitCode);

        // Завершённую попытку тоже нельзя переписать задним числом.
        var attempt = IntegrationEnvironment.Psql(
            "UPDATE workflow.task_attempt SET outcome = 'TAMPERED' WHERE job_id IN " +
            $"(SELECT job_id FROM workflow.workflow_job WHERE process_id = '{processId}');");
        Assert.NotEqual(0, attempt.ExitCode);

        // История на месте: отклонённые попытки её не тронули.
        var events = IntegrationEnvironment.Psql(
            $"SELECT count(*) > 0 FROM autocheck.workflow_events WHERE process_id = '{processId}';");
        Assert.Equal(0, events.ExitCode);
        Assert.Equal("t", events.Output.Trim());
    }

    [SkippableFact]
    public void WorkerRoleReachesOnlyItsFourBoundaries()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        // Разрешённые к выполнению функции схем api и workflow перечисляются по авторитетному
        // состоянию каталога прав, а не по нашему представлению о нём.
        var executable = IntegrationEnvironment.Psql(
            "SELECT string_agg(DISTINCT n.nspname || '.' || p.proname, ',' ORDER BY n.nspname || '.' || p.proname) " +
            "FROM pg_catalog.pg_proc p " +
            "JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace " +
            "WHERE n.nspname IN ('api', 'workflow') " +
            "AND has_function_privilege('workflow_worker', p.oid, 'EXECUTE');");

        Assert.Equal(0, executable.ExitCode);
        Assert.Equal(string.Join(',', WorkerBoundaries), executable.Output.Trim());

        // Ни одной таблицы в любой прикладной схеме, где исполнителю разрешён DML.
        var writable = IntegrationEnvironment.Psql(
            "SELECT count(*) FROM pg_catalog.pg_class c " +
            "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE c.relkind IN ('r', 'p', 'f') " +
            "AND n.nspname <> 'information_schema' AND n.nspname NOT LIKE 'pg\\_%' " +
            "AND (has_table_privilege('workflow_worker', c.oid, 'INSERT') " +
            "  OR has_table_privilege('workflow_worker', c.oid, 'UPDATE') " +
            "  OR has_table_privilege('workflow_worker', c.oid, 'DELETE'));");

        Assert.Equal(0, writable.ExitCode);
        Assert.Equal("0", writable.Output.Trim());

        // И это не теория: прямая попытка дописать историю от лица исполнителя отклоняется.
        var forge = IntegrationEnvironment.Psql(
            "INSERT INTO workflow.workflow_event (process_id, event_type) " +
            "SELECT process_id, 'Forged' FROM workflow.process_instance LIMIT 1;",
            role: "workflow_worker");
        Assert.NotEqual(0, forge.ExitCode);

        // Публикация и запуск процессов исполнителю тоже недоступны: он только исполняет задания.
        var publish = IntegrationEnvironment.Psql(
            "SELECT workflow.publish_flow('{}'::jsonb);", role: "workflow_worker");
        Assert.NotEqual(0, publish.ExitCode);

        var start = IntegrationEnvironment.Psql(
            "SELECT workflow.start_process('workflow-smoke', 'forged', '{}'::jsonb);",
            role: "workflow_worker");
        Assert.NotEqual(0, start.ExitCode);

        // Встроенное действие-проба объявлено SECURITY DEFINER и пишет в таблицу. Права PUBLIC на
        // него отзываются ЯВНО, в той же миграции, где оно создано: общий REVOKE перечисляет схемы
        // поимённо и по построению не может покрыть схему, добавленную следующей миграцией.
        var probeAccess = IntegrationEnvironment.Psql(
            "SELECT has_function_privilege('public', 'training.canary_v1(jsonb,jsonb)', 'EXECUTE')::text " +
            "|| ' ' || has_schema_privilege('public', 'training', 'USAGE')::text;");
        Assert.Equal(0, probeAccess.ExitCode);
        Assert.Equal("false false", LastLine(probeAccess.Output));
    }

    /// <summary>
    /// Определения и исполнение разведены по разным ролям: ни один набор учётных данных не даёт
    /// сразу и публикацию карты, и управление процессами.
    /// </summary>
    [SkippableFact]
    public void PublicationAndOperationRolesAreSeparated()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        // Публикатор не двигает процессы и не завершает задания.
        var publisherStart = IntegrationEnvironment.Psql(
            "SELECT workflow.start_process('workflow-smoke', 'forbidden', '{}'::jsonb);",
            role: "course_publisher");
        Assert.NotEqual(0, publisherStart.ExitCode);

        var publisherSignal = IntegrationEnvironment.Psql(
            "SELECT workflow.accept_signal(gen_random_uuid(), 'training.completed', 'forbidden', '{}'::jsonb);",
            role: "course_publisher");
        Assert.NotEqual(0, publisherSignal.ExitCode);

        var publisherFinish = IntegrationEnvironment.Psql(
            "SELECT workflow.finish_job(gen_random_uuid(), 'worker-a', 1, 'X', '{}'::jsonb, NULL);",
            role: "course_publisher");
        Assert.NotEqual(0, publisherFinish.ExitCode);

        // Оператор двигает процессы, но не меняет определения.
        var operatorPublish = IntegrationEnvironment.Psql(
            "SELECT workflow.publish_flow('{}'::jsonb);", role: "course_operator");
        Assert.NotEqual(0, operatorPublish.ExitCode);

        var operatorActivate = IntegrationEnvironment.Psql(
            "SELECT workflow.activate_flow('workflow-smoke', 1);", role: "course_operator");
        Assert.NotEqual(0, operatorActivate.ExitCode);

        // И при этом каждая роль действительно может делать своё дело.
        var publisherList = IntegrationEnvironment.Psql(
            "SELECT jsonb_array_length(workflow.list_flows());", role: "course_publisher");
        Assert.Equal(0, publisherList.ExitCode);

        var operatorRead = IntegrationEnvironment.Psql(
            "SELECT workflow.describe_process_envelope(gen_random_uuid()) ->> 'status';",
            role: "course_operator");
        Assert.Equal(0, operatorRead.ExitCode);
    }

    /// <summary>
    /// Проба fencing обходит арендатора, поэтому решать, разрешена ли она, обязана база — а не
    /// переменная окружения того, кто запустил клиент.
    /// </summary>
    [SkippableFact]
    public void FencingProbeRequiresTheServerSideTestProfile()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        var profile = IntegrationEnvironment.Psql(
            "SELECT test_profile FROM workflow.runtime_setting;");
        Assert.Equal(0, profile.ExitCode);
        Skip.If(profile.Output.Trim() == "t", "the stack runs under the test profile");

        // Даже от лица роли оператора, которой проба выдана, вне test profile она отказывает.
        var probe = IntegrationEnvironment.Psql(
            "SELECT workflow.test_finish_job(gen_random_uuid(), 'worker-a', 1, 'X', '{}'::jsonb) ->> 'code';",
            role: "course_operator");

        Assert.Equal(0, probe.ExitCode);

        // Запрос от лица роли выполняется как "SET ROLE ...; <sql>", поэтому psql печатает ещё и
        // эхо SET: значимой является последняя непустая строка вывода.
        Assert.Equal("workflow.test_profile_required", LastLine(probe.Output));
    }

    /// <summary>
    /// Бюджет max_attempts тратят только отказы действия, поэтому сбой, не доходящий до fail_job,
    /// иначе перезахватывался бы вечно. Абсолютный потолок делает такое задание наблюдаемо
    /// мёртвым вместо бесконечно живого.
    /// </summary>
    [SkippableFact]
    public void AttemptCeilingKillsAJobThatNeverReportsFailure()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        var businessKey = $"itest-ceiling-{Guid.NewGuid():N}";
        var processId = StartSmokeProcess(businessKey);
        Assert.True(
            WaitForState(processId, "WAITING_SIGNAL"),
            $"process {processId} did not reach WAITING_SIGNAL");

        // Задание уже завершено; возвращаем его в очередь с исчерпанным потолком попыток.
        var seeded = IntegrationEnvironment.Psql(
            "UPDATE workflow.workflow_job SET state = 'READY', attempt_count = 999, " +
            "lease_owner = NULL, lease_until = NULL, next_attempt_at = now() " +
            $"WHERE process_id = '{processId}';");
        Assert.Equal(0, seeded.ExitCode);

        // Кто бы ни забрал его первым — рабочий исполнитель или этот вызов — исход один.
        IntegrationEnvironment.Psql("SELECT jsonb_array_length(workflow.claim_jobs('itest-probe', 1, 1000));");

        var settled = WaitForState(processId, "FAILED");
        Assert.True(settled, $"process {processId} did not settle as FAILED");

        var job = IntegrationEnvironment.Psql(
            $"SELECT state FROM autocheck.jobs WHERE process_id = '{processId}';");
        Assert.Equal("DEAD", job.Output.Trim());

        // Причина смерти обязана отличаться от исчерпанного бюджета отказов: задание не «стабильно
        // отказывает», оно вообще не доходит до отчёта, и путать это в диагностике нельзя.
        var reason = IntegrationEnvironment.Psql(
            "SELECT j.dead_reason FROM workflow.workflow_job j " +
            $"WHERE j.process_id = '{processId}';");
        Assert.Equal("attempt_ceiling", reason.Output.Trim());

        var abandoned = IntegrationEnvironment.Psql(
            "SELECT count(*) FROM autocheck.workflow_events " +
            $"WHERE process_id = '{processId}' AND event_type = 'TaskAbandoned';");
        Assert.NotEqual("0", abandoned.Output.Trim());
    }

    [SkippableFact]
    public void ConcurrentActivateLeavesExactlyOneActiveVersion()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        // Активации обеих версий вперемешку. Частичного состояния «активных нет» или «активных
        // две» наблюдать нельзя ни в один момент: гарантия держится частичным уникальным индексом.
        var commands = new List<Func<(int ExitCode, JsonNode? Envelope)>>();
        for (var round = 0; round < 3; round++)
        {
            commands.Add(() => IntegrationEnvironment.Cli(null, "flow", "activate", SmokeFlow, "--version", "1"));
            commands.Add(() => IntegrationEnvironment.Cli(null, "flow", "activate", SmokeFlow, "--version", "2"));
        }

        var results = commands.Select(command => Task.Run(command)).ToArray();
        Task.WaitAll(results.Cast<Task>().ToArray());

        foreach (var result in results)
        {
            var (exitCode, envelope) = result.Result;
            Assert.True(exitCode == 0, $"a concurrent activate failed: {envelope?.ToJsonString()}");
        }

        var active = IntegrationEnvironment.Psql(
            "SELECT count(*) FROM autocheck.flow_versions WHERE flow_name = 'workflow-smoke' AND is_active;");
        Assert.Equal(0, active.ExitCode);
        Assert.Equal("1", active.Output.Trim());

        // Возвращаем контур к версии 1: остальные тесты рассчитывают на предсказуемый маршрут.
        Assert.Equal(0, IntegrationEnvironment.Cli(null, "flow", "activate", SmokeFlow, "--version", "1").ExitCode);
    }

    [SkippableFact]
    public void ConcurrentIdenticalStartsCreateOneProcess()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        Assert.Equal(0, IntegrationEnvironment.Cli(null, "flow", "activate", SmokeFlow, "--version", "1").ExitCode);

        var fixtures = IntegrationEnvironment.FixturesDirectory;
        var businessKey = $"itest-race-{Guid.NewGuid():N}";

        var starts = Enumerable.Range(0, 6)
            .Select(_ => Task.Run(() => IntegrationEnvironment.Cli(
                fixtures, "flow", "start", SmokeFlow,
                "--business-key", businessKey,
                "--data", "/itest/data/smoke-process.json")))
            .ToArray();
        Task.WaitAll(starts.Cast<Task>().ToArray());

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in starts)
        {
            var (exitCode, envelope) = start.Result;
            Assert.True(exitCode == 0, $"a concurrent start failed: {envelope?.ToJsonString()}");
            var processId = envelope?["result"]?["processId"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(processId));
            identifiers.Add(processId!);
        }

        // Один business key — один процесс, независимо от числа одновременных запусков.
        Assert.Single(identifiers);

        var count = IntegrationEnvironment.Psql(
            $"SELECT count(*) FROM autocheck.processes WHERE business_key = '{businessKey}';");
        Assert.Equal(0, count.ExitCode);
        Assert.Equal("1", count.Output.Trim());

        // Изменённые данные под тем же ключом — конфликт, а не второй процесс.
        var (conflictExit, conflict) = IntegrationEnvironment.Cli(
            fixtures, "flow", "start", SmokeFlow,
            "--business-key", businessKey,
            "--data", "/itest/data/smoke-signal.json");

        Assert.NotEqual(0, conflictExit);
        Assert.Contains("conflict", conflict?["code"]?.GetValue<string>() ?? string.Empty);
    }

    /// <summary>
    /// Одновременная повторная доставка одного сообщения обязана оставаться идемпотентной.
    /// Гонка не должна превращать честный повтор в конфликт: клиент с ретраями увидел бы отказ
    /// там, где контракт обещает duplicate.
    /// </summary>
    [SkippableFact]
    public void ConcurrentIdenticalSignalsStayIdempotent()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        var fixtures = IntegrationEnvironment.FixturesDirectory;
        var businessKey = $"itest-signal-race-{Guid.NewGuid():N}";
        var processId = StartSmokeProcess(businessKey);
        Assert.True(
            WaitForState(processId, "WAITING_SIGNAL"),
            $"process {processId} did not reach WAITING_SIGNAL");

        var messageId = $"{businessKey}-msg";
        var deliveries = Enumerable.Range(0, 6)
            .Select(_ => Task.Run(() => IntegrationEnvironment.Cli(
                fixtures, "flow", "signal", processId,
                "--type", "training.completed",
                "--message-id", messageId,
                "--payload", "/itest/data/smoke-signal.json")))
            .ToArray();
        Task.WaitAll(deliveries.Cast<Task>().ToArray());

        var statuses = new List<string>();
        foreach (var delivery in deliveries)
        {
            var (exitCode, envelope) = delivery.Result;
            Assert.True(exitCode == 0, $"a concurrent signal delivery failed: {envelope?.ToJsonString()}");
            statuses.Add(envelope?["result"]?["status"]?.GetValue<string>() ?? string.Empty);
        }

        // Ровно одна доставка приняла сигнал, остальные увидели повтор.
        Assert.Equal(1, statuses.Count(status => status == "accepted"));
        Assert.Equal(5, statuses.Count(status => status == "duplicate"));

        var stored = IntegrationEnvironment.Psql(
            $"SELECT count(*) FROM autocheck.signals WHERE message_id = '{messageId}';");
        Assert.Equal("1", stored.Output.Trim());
    }

    [SkippableFact]
    public void SmokeFlowReachesCompletedThroughSignal()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        var fixtures = IntegrationEnvironment.FixturesDirectory;
        var businessKey = $"itest-smoke-{Guid.NewGuid():N}";
        var processId = StartSmokeProcess(businessKey);

        // Automatic-шаг исполняет сменный worker, а не тест: ждём устойчивого ожидания сигнала.
        Assert.True(
            WaitForState(processId, "WAITING_SIGNAL"),
            $"process {processId} did not reach WAITING_SIGNAL");

        var executionId = IntegrationEnvironment.Psql(
            $"SELECT execution_id FROM autocheck.jobs WHERE process_id = '{processId}';");
        Assert.Equal(0, executionId.ExitCode);
        var execution = executionId.Output.Trim();
        Assert.NotEmpty(execution);

        // Ровно один предметный эффект и ровно один технический след успешного вызова.
        var effects = IntegrationEnvironment.Psql(
            $"SELECT count(*) FROM training.canary_effect WHERE execution_id = '{execution}';");
        Assert.Equal("1", effects.Output.Trim());

        var dispatches = IntegrationEnvironment.Psql(
            "SELECT count(*) FROM autocheck.action_dispatches " +
            $"WHERE request_id = '{execution}' AND principal = 'workflow-worker' AND status = 'OK';");
        Assert.Equal("1", dispatches.Output.Trim());

        var (signalExit, signal) = IntegrationEnvironment.Cli(
            fixtures, "flow", "signal", processId,
            "--type", "training.completed",
            "--message-id", $"{businessKey}-msg",
            "--payload", "/itest/data/smoke-signal.json");

        Assert.True(signalExit == 0, $"signal was not accepted: {signal?.ToJsonString()}");
        Assert.Equal("accepted", signal?["result"]?["status"]?.GetValue<string>());

        // Сигнал применяется той же транзакцией, что его принимает: процесс уже завершён.
        var (getExit, get) = IntegrationEnvironment.Cli(null, "flow", "get", processId);
        Assert.Equal(0, getExit);
        Assert.Equal("COMPLETED", get?["result"]?["state"]?.GetValue<string>());
        Assert.Equal("done", get?["result"]?["currentStepKey"]?.GetValue<string>());

        // Повтор того же сигнала не создаёт ни строки, ни события.
        var (duplicateExit, duplicate) = IntegrationEnvironment.Cli(
            fixtures, "flow", "signal", processId,
            "--type", "training.completed",
            "--message-id", $"{businessKey}-msg",
            "--payload", "/itest/data/smoke-signal.json");

        Assert.Equal(0, duplicateExit);
        Assert.Equal("duplicate", duplicate?["result"]?["status"]?.GetValue<string>());

        var signals = IntegrationEnvironment.Psql(
            $"SELECT count(*) FROM autocheck.signals WHERE process_id = '{processId}';");
        Assert.Equal("1", signals.Output.Trim());
    }

    /// <summary>
    /// Контракт карты обязан держаться и тогда, когда публикуют минуя CLI. JSON Schema живёт в
    /// клиенте, а workflow.publish_flow — граница, выданная отдельной роли: карта, пришедшая
    /// прямым вызовом SQL, до сих пор не проходила ни одной проверки формы. Automatic-шаг без
    /// task дал бы задание без контракта задачи — исполнитель не может ни выполнить его, ни
    /// отвергнуть, и оно перезахватывалось бы до потолка попыток без единой записи в журнале.
    /// </summary>
    [SkippableTheory]
    [InlineData(
        "an automatic step without a task",
        "{\"contract_version\":\"course-1\",\"flow_name\":\"itest-bad-a\",\"version\":1," +
        "\"start_step\":\"s\",\"steps\":[{\"key\":\"s\",\"type\":\"automatic\"}," +
        "{\"key\":\"e\",\"type\":\"end\",\"outcome\":\"DONE\"}]," +
        "\"transitions\":[{\"from\":\"s\",\"outcome\":\"OK\",\"to\":\"e\"}]}")]
    [InlineData(
        "a task on a step that is not automatic",
        "{\"contract_version\":\"course-1\",\"flow_name\":\"itest-bad-b\",\"version\":1," +
        "\"start_step\":\"s\",\"steps\":[{\"key\":\"s\",\"type\":\"manual\"," +
        "\"allowed_outcomes\":[\"OK\"],\"task\":{}}," +
        "{\"key\":\"e\",\"type\":\"end\",\"outcome\":\"DONE\"}]," +
        "\"transitions\":[{\"from\":\"s\",\"outcome\":\"OK\",\"to\":\"e\"}]}")]
    [InlineData(
        "a start step the map does not declare",
        "{\"contract_version\":\"course-1\",\"flow_name\":\"itest-bad-c\",\"version\":1," +
        "\"start_step\":\"ghost\",\"steps\":[{\"key\":\"s\",\"type\":\"manual\"," +
        "\"allowed_outcomes\":[\"OK\"]},{\"key\":\"e\",\"type\":\"end\",\"outcome\":\"DONE\"}]," +
        "\"transitions\":[{\"from\":\"s\",\"outcome\":\"OK\",\"to\":\"e\"}]}")]
    [InlineData(
        "a transition into a step the map does not declare",
        "{\"contract_version\":\"course-1\",\"flow_name\":\"itest-bad-d\",\"version\":1," +
        "\"start_step\":\"s\",\"steps\":[{\"key\":\"s\",\"type\":\"manual\"," +
        "\"allowed_outcomes\":[\"OK\"]},{\"key\":\"e\",\"type\":\"end\",\"outcome\":\"DONE\"}]," +
        "\"transitions\":[{\"from\":\"s\",\"outcome\":\"OK\",\"to\":\"ghost\"}]}")]
    public void StorageRejectsAMalformedMapEvenWithoutTheClient(string reason, string map)
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        var published = IntegrationEnvironment.Psql(
            $"SELECT workflow.publish_flow($${map}$$::jsonb) ->> 'code';");

        Assert.Equal(0, published.ExitCode);
        Assert.Equal("workflow.map_invalid", LastLine(published.Output));

        // Отклонённая публикация обязана не оставить ни одной строки определения.
        var leftovers = IntegrationEnvironment.Psql(
            "SELECT count(*) FROM autocheck.flow_versions WHERE flow_name LIKE 'itest-bad-%';");

        Assert.Equal(0, leftovers.ExitCode);
        Assert.True("0" == LastLine(leftovers.Output), $"rejecting {reason} left rows behind");
    }

    /// <summary>
    /// Личность состояния неизменяема. На неизменности execution_id держится всё рассуждение о
    /// безопасности доставки at-least-once, на data_hash — идемпотентность повторного запуска, а
    /// на flow_version_id — само закрепление процесса за версией карты. Свойство, которое можно
    /// тихо переписать одним UPDATE от владельца, доказательством не является.
    /// </summary>
    [SkippableTheory]
    [InlineData("UPDATE workflow.workflow_job SET execution_id = gen_random_uuid()")]
    [InlineData("UPDATE workflow.workflow_job SET lease_version = 0 WHERE lease_version > 0")]
    [InlineData("UPDATE workflow.process_instance SET data_hash = repeat('b', 64)")]
    [InlineData("UPDATE workflow.process_instance SET business_key = 'rewritten'")]
    [InlineData("UPDATE workflow.step_instance SET step_key = 'rewritten'")]
    [InlineData("DELETE FROM workflow.process_instance")]
    [InlineData("DELETE FROM workflow.workflow_job")]
    [InlineData("DELETE FROM workflow.worker_principal")]
    [InlineData("UPDATE workflow.step_instance SET step_type = 'MANUAL'")]
    public void StateIdentityCannotBeRewrittenEvenByTheOwner(string statement)
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        // Строковые триггеры не срабатывают на нуле строк, поэтому процесс обязан существовать:
        // иначе тест доказывал бы лишь то, что менять было нечего.
        var processId = StartSmokeProcess($"itest-identity-{Guid.NewGuid():N}");
        Assert.False(string.IsNullOrWhiteSpace(processId));

        var rewrite = IntegrationEnvironment.Psql($"{statement};");

        Assert.NotEqual(0, rewrite.ExitCode);
    }

    /// <summary>
    /// Отказ границы завершения, не связанный с потерей аренды, обязан быть СООБЩЁН, а не пережит
    /// молча.
    ///
    /// Сценарий берётся из реальности: закреплённая версия карты не объявляет перехода для того
    /// исхода, который вернуло действие. Аренда при этом всё ещё наша, и повторы делу не помогут.
    /// Если исполнитель просто выйдет, задание останется LEASED до истечения срока, будет
    /// перезахвачено, и так до абсолютного потолка попыток — то есть умрёт с причиной
    /// `attempt_ceiling`, которая скрывает настоящую («карта и действие не согласованы») и не
    /// оставит в истории ни одного TaskAttemptFailed. Проверяется именно это различие.
    ///
    /// Карта публикуется НАПРЯМУЮ через SQL: полноту покрытия исходов доказывает валидатор карты
    /// в CLI, а он такую карту не пропустит. Хранилище её принимает сознательно — покрытие
    /// исходов это свойство графа, а не строки, и рантайм обязан вести себя честно и на карте,
    /// опубликованной в обход клиента.
    /// </summary>
    [SkippableFact]
    public void AnUnknownOutcomeIsReportedInsteadOfSilentlyReclaimed()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        const string flow = "itest-unknown-outcome";
        var businessKey = $"itest-outcome-{Guid.NewGuid():N}";

        // Действие вернёт ROUTE_SIGNAL (mode отличен от "manual"), а переход объявлен только для
        // ROUTE_MANUAL. Всё остальное в карте корректно, поэтому хранилище её принимает.
        var map =
            "{\"contract_version\":\"course-1\",\"flow_name\":\"" + flow + "\",\"version\":1," +
            "\"start_step\":\"invoke\",\"steps\":[" +
            "{\"key\":\"invoke\",\"type\":\"automatic\",\"task\":{\"service\":\"postgres\"," +
            "\"module\":\"training\",\"action\":\"canary\",\"action_version\":1," +
            "\"required_policy\":[\"workflow:execute\"],\"timeout_ms\":1600," +
            "\"retry\":{\"max_attempts\":1,\"delays_ms\":[]}," +
            "\"input_mapping\":{\"/mode\":\"/mode\",\"/value\":\"/value\"}," +
            "\"input_constants\":{\"marker\":\"itest\"}}}," +
            "{\"key\":\"done\",\"type\":\"end\",\"outcome\":\"COMPLETED\"}]," +
            "\"transitions\":[{\"from\":\"invoke\",\"outcome\":\"ROUTE_MANUAL\",\"to\":\"done\"}]}";

        var published = IntegrationEnvironment.Psql($"SELECT workflow.publish_flow($${map}$$::jsonb) ->> 'status';");
        Assert.Equal(0, published.ExitCode);
        Assert.Equal("ok", LastLine(published.Output));

        var activated = IntegrationEnvironment.Psql(
            $"SELECT workflow.activate_flow('{flow}', 1) ->> 'status';");
        Assert.Equal(0, activated.ExitCode);
        Assert.Equal("ok", LastLine(activated.Output));

        var started = IntegrationEnvironment.Psql(
            $"SELECT workflow.start_process('{flow}', '{businessKey}', " +
            "'{\"mode\":\"signal\",\"value\":\"v\"}'::jsonb) #>> '{result,processId}';");
        Assert.Equal(0, started.ExitCode);
        var processId = LastLine(started.Output);
        Assert.False(string.IsNullOrWhiteSpace(processId));

        // Процесс обязан упасть быстро — по отчёту исполнителя, а не по истечению аренд.
        Assert.True(
            WaitForState(processId, "FAILED"),
            "the process did not fail after the completion boundary refused the outcome");

        // Настоящая причина сохранена: попытка отчиталась своим кодом, а не растворилась.
        var attempt = IntegrationEnvironment.Psql(
            "SELECT a.status || ' ' || coalesce(a.error_code, '-') FROM autocheck.attempts a " +
            $"JOIN autocheck.jobs j ON j.job_id = a.job_id WHERE j.process_id = '{processId}';");
        Assert.Equal(0, attempt.ExitCode);
        Assert.Equal("FAILED workflow.unknown_outcome", LastLine(attempt.Output));

        // И смерть задания отнесена к бюджету отказов, а не к потолку попыток.
        var job = IntegrationEnvironment.Psql(
            "SELECT state || ' ' || coalesce(dead_reason, '-') || ' attempts=' || attempt_count " +
            $"FROM autocheck.jobs WHERE process_id = '{processId}';");
        Assert.Equal(0, job.ExitCode);
        Assert.Equal("DEAD failure_budget attempts=1", LastLine(job.Output));

        // История содержит отчёт о попытке, а не только загадочное исчезновение задания.
        var events = IntegrationEnvironment.Psql(
            "SELECT string_agg(DISTINCT event_type, ',' ORDER BY event_type) " +
            $"FROM autocheck.workflow_events WHERE process_id = '{processId}' " +
            "AND event_type IN ('TaskAttemptFailed', 'TaskFailed', 'TaskAbandoned');");
        Assert.Equal(0, events.ExitCode);
        Assert.Equal("TaskAttemptFailed,TaskFailed", LastLine(events.Output));
    }

    /// <summary>
    /// Закрепление процесса за версией карты — главное обещание версионирования: активация другой
    /// версии меняет выбор только для НОВЫХ экземпляров. Обещание держит триггер, и подменять
    /// нужно на РЕАЛЬНО существующую версию: подмена на случайный uuid упёрлась бы во внешний ключ
    /// и тест проходил бы по неверной причине — даже если бы триггер убрали.
    /// </summary>
    [SkippableFact]
    public void ThePinnedFlowVersionOfALiveProcessCannotBeSwitched()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        var processId = StartSmokeProcess($"itest-pin-{Guid.NewGuid():N}");

        // Обе версии карты опубликованы, поэтому «чужая, но существующая» версия точно есть.
        var other = IntegrationEnvironment.Psql(
            "SELECT count(*) FROM workflow.flow_version v " +
            "JOIN workflow.process_instance p ON p.flow_id = v.flow_id " +
            $"WHERE p.process_id = '{processId}' AND v.flow_version_id <> p.flow_version_id;");
        Assert.Equal(0, other.ExitCode);
        Assert.NotEqual("0", LastLine(other.Output));

        var switched = IntegrationEnvironment.Psql(
            "UPDATE workflow.process_instance p SET flow_version_id = (" +
            "  SELECT v.flow_version_id FROM workflow.flow_version v " +
            "  WHERE v.flow_id = p.flow_id AND v.flow_version_id <> p.flow_version_id LIMIT 1) " +
            $"WHERE p.process_id = '{processId}';");

        Assert.NotEqual(0, switched.ExitCode);
    }

    /// <summary>
    /// Принятый сигнал — доказательство идемпотентной доставки: именно с его отпечатком
    /// сравнивается повтор, чтобы отличить дубликат от конфликта. Переписанный отпечаток превратил
    /// бы конфликт в дубликат, переписанный процесс перенёс бы сообщение на чужой процесс, а
    /// повторное открытие применённого сигнала позволило бы применить его дважды.
    /// </summary>
    [SkippableTheory]
    [InlineData("UPDATE workflow.workflow_signal SET body_hash = repeat('c', 64)")]
    [InlineData("UPDATE workflow.workflow_signal SET process_id = gen_random_uuid()")]
    [InlineData("UPDATE workflow.workflow_signal SET signal_type = 'other.type'")]
    [InlineData("UPDATE workflow.workflow_signal SET status = 'ACCEPTED', applied_at = NULL")]
    [InlineData("DELETE FROM workflow.workflow_signal")]
    public void AnAcceptedSignalCannotBeRewritten(string statement)
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        // Сигнал обязан существовать и быть УЖЕ ПРИМЕНЁННЫМ: только тогда осмысленна и попытка
        // переписать его тело, и попытка открыть его заново.
        var businessKey = $"itest-signal-immutable-{Guid.NewGuid():N}";
        var processId = StartSmokeProcess(businessKey);
        Assert.True(WaitForState(processId, "WAITING_SIGNAL"), "process did not reach WAITING_SIGNAL");

        var messageId = $"itest-immutable-{Guid.NewGuid():N}";
        var accepted = IntegrationEnvironment.Psql(
            $"SELECT workflow.accept_signal('{processId}', 'training.completed', '{messageId}', " +
            "'{}'::jsonb) ->> 'status';");
        Assert.Equal(0, accepted.ExitCode);
        Assert.Equal("ok", LastLine(accepted.Output));

        var applied = IntegrationEnvironment.Psql(
            $"SELECT status FROM autocheck.signals WHERE message_id = '{messageId}';");
        Assert.Equal("APPLIED", LastLine(applied.Output));

        var rewrite = IntegrationEnvironment.Psql($"{statement} WHERE message_id = '{messageId}';");

        Assert.NotEqual(0, rewrite.ExitCode);
    }

    /// <summary>
    /// Форма шага закреплена ограничениями САМОЙ таблицы, а не только проверками функции
    /// публикации: это последний рубеж для строки, вставленной в обход всего остального.
    /// Проверка идёт внутри транзакции с откатом — определения карт удалять нельзя, и тест не
    /// имеет права оставлять за собой опубликованную версию.
    /// </summary>
    [SkippableTheory]
    [InlineData("wait_signal без signal_type", "'WAIT_SIGNAL', NULL, 'GO', '[]'")]
    [InlineData("wait_signal без outcome", "'WAIT_SIGNAL', 'a.b', NULL, '[]'")]
    [InlineData("end без outcome", "'END', NULL, NULL, '[]'")]
    [InlineData("manual с пустым списком исходов", "'MANUAL', NULL, NULL, '[]'")]
    [InlineData("automatic с чужими полями", "'AUTOMATIC', 'a.b', 'GO', '[]'")]
    public void StepShapeIsEnforcedByTheTableItself(string reason, string columns)
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        var name = $"itest-shape-{Guid.NewGuid():N}".Substring(0, 40);
        var probe = IntegrationEnvironment.Psql(
            "BEGIN; " +
            "WITH f AS (" +
            $"  INSERT INTO workflow.flow_definition (flow_name) VALUES ('{name}') RETURNING flow_id), " +
            "v AS (" +
            "  INSERT INTO workflow.flow_version (flow_id, version, start_step_key, map, map_hash) " +
            $"  SELECT flow_id, 1, 's', '{{}}'::jsonb, repeat('a', 64) FROM f RETURNING flow_version_id) " +
            "INSERT INTO workflow.step_definition " +
            "  (flow_version_id, step_key, step_type, signal_type, outcome, allowed_outcomes) " +
            $"SELECT flow_version_id, 's', {columns}::jsonb FROM v; " +
            "ROLLBACK;");

        Assert.True(probe.ExitCode != 0, $"хранилище приняло шаг: {reason}");
    }

    /// <summary>
    /// Абсолютный потолок попыток не имеет права зависеть от того, лежит ли в таблице строка
    /// настроек: предохранитель, исчезающий вместе со строкой данных, — это не предохранитель.
    /// Всё внутри одной транзакции с откатом, поэтому настройки контура остаются нетронутыми.
    /// </summary>
    [SkippableFact]
    public void TheAttemptCeilingSurvivesAMissingSettingsRow()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        var processId = StartSmokeProcess($"itest-ceiling-nosettings-{Guid.NewGuid():N}");
        Assert.True(WaitForState(processId, "WAITING_SIGNAL"), "process did not reach WAITING_SIGNAL");

        var probe = IntegrationEnvironment.Psql(
            "BEGIN; " +
            "DELETE FROM workflow.runtime_setting; " +
            "UPDATE workflow.workflow_job SET state = 'READY', attempt_count = 999, " +
            "  lease_owner = NULL, lease_until = NULL, next_attempt_at = now() " +
            $"WHERE process_id = '{processId}'; " +
            "SELECT jsonb_array_length(workflow.claim_jobs('itest-nosettings', 1, 1000)); " +
            "SELECT state || ' ' || coalesce(dead_reason, '-') FROM workflow.workflow_job " +
            $"WHERE process_id = '{processId}'; " +
            "ROLLBACK;");

        Assert.Equal(0, probe.ExitCode);

        // Последняя строка вывода — тег ROLLBACK, поэтому результат ищется по всему выводу.
        Assert.Contains("DEAD attempt_ceiling", probe.Output, StringComparison.Ordinal);

        // Откат обязан вернуть строку настроек на место: иначе тест сломал бы контур соседям.
        var settings = IntegrationEnvironment.Psql("SELECT count(*) FROM workflow.runtime_setting;");
        Assert.Equal("1", LastLine(settings.Output));
    }

    /// <summary>Последняя непустая строка вывода psql — сам результат, без эха служебных команд.</summary>
    private static string LastLine(string output)
        => output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;

    /// <summary>
    /// Запускает процесс встроенной карты под заданным business key. Запуск идемпотентен, поэтому
    /// повторный прогон тестов на том же контуре возвращает тот же процесс, а не создаёт новый.
    /// </summary>
    private static string StartSmokeProcess(string businessKey)
    {
        Assert.Equal(0, IntegrationEnvironment.Cli(null, "flow", "activate", SmokeFlow, "--version", "1").ExitCode);

        var (exitCode, envelope) = IntegrationEnvironment.Cli(
            IntegrationEnvironment.FixturesDirectory,
            "flow", "start", SmokeFlow,
            "--business-key", businessKey,
            "--data", "/itest/data/smoke-process.json");

        Assert.True(exitCode == 0, $"smoke process could not be started: {envelope?.ToJsonString()}");
        var processId = envelope?["result"]?["processId"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(processId));
        return processId!;
    }

    /// <summary>
    /// Ждёт заявленного состояния процесса. Опрос, а не фиксированная пауза: время исполнения
    /// зависит от профиля worker, и «поспать и понадеяться» — это не доказательство.
    /// </summary>
    private static bool WaitForState(string processId, string state)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = IntegrationEnvironment.Psql(
                $"SELECT state FROM autocheck.processes WHERE process_id = '{processId}';");
            if (current.ExitCode == 0 && current.Output.Trim() == state)
            {
                return true;
            }

            Thread.Sleep(250);
        }

        return false;
    }
}
