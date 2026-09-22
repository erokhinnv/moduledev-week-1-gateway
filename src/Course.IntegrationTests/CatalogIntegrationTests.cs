using Xunit;

namespace Course.IntegrationTests;

/// <summary>
/// Конкурентная публикация и границы прав ролей — две области, которые ревью отметило как
/// непроверенные, потому что команды каталога прогонялись только последовательно.
/// </summary>
[Collection(ProbeFixture.CollectionName)]
public sealed class CatalogIntegrationTests(ProbeFixture fixture)
{
    [SkippableFact]
    public void ConcurrentCatalogCommandsLeaveExactlyOneDefault()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);
        fixture.EnsurePublished();

        var fixtures = IntegrationEnvironment.FixturesDirectory;

        // Восемь команд по ОДНОМУ маршруту одновременно: активации обеих версий вперемешку с
        // повторной публикацией. До advisory-блокировки здесь были возможны и падение по
        // уникальному индексу единственной default-версии, и взаимная блокировка.
        var commands = new List<Func<(int ExitCode, System.Text.Json.Nodes.JsonNode? Envelope)>>();
        for (var round = 0; round < 3; round++)
        {
            commands.Add(() => IntegrationEnvironment.Cli(null, "action", "activate", "itest.probe", "--version", "1"));
            commands.Add(() => IntegrationEnvironment.Cli(null, "action", "activate", "itest.probe", "--version", "2"));
        }

        commands.Add(() => IntegrationEnvironment.Cli(
            fixtures, "action", "publish", "/itest/manifests/itest-probe-v1.action.json"));
        commands.Add(() => IntegrationEnvironment.Cli(
            fixtures, "action", "publish", "/itest/manifests/itest-probe-v2.action.json"));

        var results = commands
            .Select(command => Task.Run(command))
            .ToArray();
        Task.WaitAll(results.Cast<Task>().ToArray());

        foreach (var result in results)
        {
            var (exitCode, envelope) = result.Result;
            Assert.True(
                exitCode == 0,
                $"a concurrent catalog command failed: {envelope?.ToJsonString()}");
        }

        // Инвариант маршрута: ровно одна включённая версия по умолчанию.
        var (probeExit, defaults) = IntegrationEnvironment.Psql(
            "SELECT count(*) FROM autocheck.action_definitions " +
            "WHERE module = 'itest' AND action = 'probe' AND is_default;");

        Assert.Equal(0, probeExit);
        Assert.Equal("1", defaults.Trim());
    }

    [SkippableFact]
    public void PublicationRoleCannotReachDdlDomainStateOrInvoke()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        // Роль публикации намеренно узкая: ей доступно управление каталогом и ничего сверх того.
        var ddl = IntegrationEnvironment.Psql(
            "CREATE TABLE public.publisher_should_not_create(x int);", role: "course_publisher");
        Assert.NotEqual(0, ddl.ExitCode);

        var domain = IntegrationEnvironment.Psql(
            "SELECT count(*) FROM payment.operation;", role: "course_publisher");
        Assert.NotEqual(0, domain.ExitCode);

        var invoke = IntegrationEnvironment.Psql(
            "SELECT api.invoke('payment', 'request', 1, '{}'::jsonb, '{}'::jsonb);",
            role: "course_publisher");
        Assert.NotEqual(0, invoke.ExitCode);

        // А вот управление каталогом ей доступно — иначе разделение ролей было бы бесполезным.
        var catalog = IntegrationEnvironment.Psql(
            "SELECT jsonb_array_length(api.list_actions());", role: "course_publisher");
        Assert.Equal(0, catalog.ExitCode);

        // И она не состоит ни в одной роли, то есть не может добрать права через SET ROLE.
        var membership = IntegrationEnvironment.Psql(
            "SELECT count(*) FROM pg_auth_members m " +
            "JOIN pg_roles g ON g.oid = m.member WHERE g.rolname = 'course_publisher';");
        Assert.Equal(0, membership.ExitCode);
        Assert.Equal("0", membership.Output.Trim());
    }

    /// <summary>
    /// Семантика повтора закреплена за маршрутом. Раньше версия с другим idempotency_scope
    /// публиковалась, и после activate тот же Idempotency-Key с тем же телом исполнял
    /// зарегистрированную функцию второй раз: запись идемпотентности уникальна по scope ТЕКУЩЕЙ
    /// версии. Проверяются обе половины границы — отказ публикации и отказ вставки в обход неё.
    /// </summary>
    [SkippableFact]
    public void IdempotencySemanticsOfARouteCannotChangeBetweenVersions()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);
        fixture.EnsurePublished();

        const string versionThree =
            "SELECT count(*) FROM api.action_version WHERE module = 'itest' AND action = 'probe' AND version = 3;";

        // Версия 3 отличается от версии 2 только scope — и именно поэтому отклоняется.
        var (exitCode, envelope) = IntegrationEnvironment.Cli(
            IntegrationEnvironment.FixturesDirectory,
            "action", "publish", "/itest/manifests/itest-probe-v3-scope-drift.action.json");

        Assert.NotEqual(0, exitCode);
        Assert.Equal("manifest.conflict", envelope?["code"]?.GetValue<string>());
        Assert.Contains("scope principal_action", envelope?["message"]?.GetValue<string>() ?? string.Empty);
        Assert.Equal("0", IntegrationEnvironment.Psql(versionThree).Output.Trim());

        // Вставка мимо функции публикации. Контроль идёт первым: версия с ТОЙ ЖЕ семантикой
        // обязана пройти триггер — иначе отказ ниже доказывал бы лишь, что триггер не пускает
        // ничего. Её откатывает исключение-маркер. Любой исход, кроме «контроль прошёл, дрейф
        // отклонён именно restrict_violation», завершает psql ненулевым кодом.
        var bypass = IntegrationEnvironment.Psql(
            """
            DO $$
            BEGIN
                BEGIN
                    INSERT INTO api.action_version (module, action, version, http_method, target_schema,
                        target_function, request_schema, response_schema, outcomes, required_policy,
                        idempotency_mode, idempotency_scope, timeout_ms, manifest, manifest_hash)
                    SELECT module, action, 3, http_method, target_schema, target_function, request_schema,
                        response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope,
                        timeout_ms, manifest, manifest_hash
                    FROM api.action_version WHERE module = 'itest' AND action = 'probe' AND version = 1;
                    RAISE EXCEPTION 'itest-control-passed';
                EXCEPTION WHEN raise_exception THEN
                    IF SQLERRM <> 'itest-control-passed' THEN RAISE; END IF;
                END;

                BEGIN
                    INSERT INTO api.action_version (module, action, version, http_method, target_schema,
                        target_function, request_schema, response_schema, outcomes, required_policy,
                        idempotency_mode, idempotency_scope, timeout_ms, manifest, manifest_hash)
                    SELECT module, action, 3, http_method, target_schema, target_function, request_schema,
                        response_schema, outcomes, required_policy, idempotency_mode, 'consumer_action',
                        timeout_ms, manifest, manifest_hash
                    FROM api.action_version WHERE module = 'itest' AND action = 'probe' AND version = 1;
                    RAISE EXCEPTION 'scope drift was inserted past the trigger';
                EXCEPTION WHEN restrict_violation THEN
                    NULL;
                END;
            END $$;
            """.ReplaceLineEndings(" "));

        Assert.True(bypass.ExitCode == 0, "вставка в обход публикации нарушила закрепление семантики");
        Assert.Equal("0", IntegrationEnvironment.Psql(versionThree).Output.Trim());
    }

    [SkippableFact]
    public void RuntimeRoleCannotPublishActions()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        // Обратная граница: runtime исполняет действия, но не управляет их каталогом.
        var publish = IntegrationEnvironment.Psql(
            "SELECT api.publish_action('{}'::jsonb);", role: "course_runtime");
        Assert.NotEqual(0, publish.ExitCode);

        var activate = IntegrationEnvironment.Psql(
            "SELECT api.activate_action('itest', 'probe', 1);", role: "course_runtime");
        Assert.NotEqual(0, activate.ExitCode);
    }
}
