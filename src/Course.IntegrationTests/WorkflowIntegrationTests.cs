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
