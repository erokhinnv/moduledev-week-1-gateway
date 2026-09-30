using Xunit;

namespace Course.IntegrationTests;

/// <summary>
/// Опубликованные числа профиля доставки, закреплённые как утверждения.
///
/// Прежняя строка профиля читала «максимум попыток 3» как три HTTP-попытки при трёх задержках:
/// третья задержка была недостижима, а provider, вернувшийся к четвёртой попытке, платежа не
/// получал. Число, взятое из контракта, обязано иметь тест, который падает при расхождении на
/// единицу, — иначе ошибка чтения переживает любое количество зелёных прогонов.
/// </summary>
[Collection(ProbeFixture.CollectionName)]
public sealed class DeliveryIntegrationTests
{
    [SkippableFact]
    public void TheTestProfileAllowsFourAttemptsWithThePublishedDelays()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        var policy = IntegrationEnvironment.Psql(
            "SELECT profile || ' ' || max_attempts || ' ' || delays_ms::text || ' ' || lease_ms " +
            "FROM delivery.delivery_policy WHERE profile = 'test';");
        Assert.Equal(0, policy.ExitCode);
        Assert.Equal("test 4 [200, 400, 800] 2000", policy.Output.Trim());

        // Задержек ровно на одну меньше, чем попыток, — у каждого профиля, и это ограничение таблицы,
        // а не договорённость: строка с недостижимой задержкой не сохраняется. Проба откатывается.
        var shape = IntegrationEnvironment.Psql(
            "SELECT count(*) FROM delivery.delivery_policy WHERE jsonb_array_length(delays_ms) <> max_attempts - 1;");
        Assert.Equal("0", shape.Output.Trim());

        var drift = IntegrationEnvironment.Psql(
            "BEGIN; UPDATE delivery.delivery_policy SET max_attempts = 3 WHERE profile = 'test'; ROLLBACK;");
        Assert.NotEqual(0, drift.ExitCode);

        // Ограничение на форму — одно и именованное: прежнее встроенное «не меньше» снято, а не
        // оставлено рядом. Иначе миграция обещала бы равенство, которого таблица не знает по имени.
        var constraints = IntegrationEnvironment.Psql(
            "SELECT string_agg(conname, ',' ORDER BY conname) FROM pg_catalog.pg_constraint " +
            "WHERE conrelid = 'delivery.delivery_policy'::regclass AND contype = 'c' " +
            "AND pg_get_constraintdef(oid) ILIKE '%delays_ms%';");
        Assert.Equal("delivery_policy_delays_shape", constraints.Output.Trim());

        // С недели 4 задержку считает экспонента least(base * 2^(n-1), max), а список остался
        // наглядной записью той же лестницы. Список, не совпадающий с правилом, по которому
        // задержки считаются на самом деле, — это таблица, снова противоречащая самой себе:
        // профиль default хранил 1/5/15/60 с при экспоненте, дающей 1/2/4/8 с.
        var drifted = IntegrationEnvironment.Psql(
            "SELECT coalesce(string_agg(p.profile || ' ' || p.delays_ms::text, '; ' ORDER BY p.profile), '') " +
            "FROM delivery.delivery_policy p WHERE p.delays_ms IS DISTINCT FROM (" +
            "SELECT coalesce(jsonb_agg(least(p.backoff_base_ms::numeric * power(2::numeric, n - 1), " +
            "p.backoff_max_ms::numeric)::integer ORDER BY n), '[]'::jsonb) " +
            "FROM generate_series(1, p.max_attempts - 1) AS n);");
        Assert.Equal(0, drifted.ExitCode);
        Assert.Equal(string.Empty, drifted.Output.Trim());

        var conservative = IntegrationEnvironment.Psql(
            "SELECT max_attempts || ' ' || delays_ms::text || ' ' || backoff_base_ms || '/' || backoff_max_ms " +
            "FROM delivery.delivery_policy WHERE profile = 'default';");
        Assert.Equal("5 [1000, 2000, 4000, 8000] 1000/60000", conservative.Output.Trim());
    }

    /// <summary>
    /// Политика принадлежит базе, а не сессии. Custom-параметр любая роль может переопределить у
    /// себя командой SET, и функция, читающая current_setting, послушно взяла бы значение клиента:
    /// dispatcher сам решал бы, сколько у доставки попыток и сколько живёт аренда. Поэтому
    /// действующая политика читается из настроек уровня базы и сессионного SET не замечает.
    /// </summary>
    [SkippableFact]
    public void ASessionCannotOverrideThePolicyOfTheDatabase()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        const string shape = "max_attempts || ' ' || backoff_base_ms || '/' || backoff_max_ms || '/' || jitter_max_ms || ' ' || lease_ms";
        var database = IntegrationEnvironment.Psql("SELECT " + shape + " FROM delivery.active_policy();");
        Assert.Equal(0, database.ExitCode);

        var session = IntegrationEnvironment.Psql(
            "SET course.outbox_max_attempts = '19'; SET course.outbox_backoff_base_ms = '7'; " +
            "SET course.outbox_backoff_max_ms = '9'; SET course.outbox_jitter_max_ms = '0'; " +
            "SET course.outbox_lease_ms = '599999'; " +
            "SELECT current_setting('course.outbox_max_attempts') || ' ' || " + shape + " FROM delivery.active_policy();");
        Assert.Equal(0, session.ExitCode);

        // Сессия своё значение действительно выставила (первое поле), а политика осталась прежней.
        Assert.Equal("19 " + database.Output.Trim(), LastLine(session.Output));
    }

    /// <summary>psql печатает и статусы команд SET; результат запроса — последняя непустая строка.</summary>
    private static string LastLine(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1];

    /// <summary>
    /// Действующая политика — настройки самой базы (`ALTER DATABASE ... SET course.*`, заданные из
    /// окружения сервиса postgres), а где настройки нет — строка профиля стенда, не строка «по
    /// умолчанию». Ожидание собирается из тех же источников отдельным запросом, поэтому тест падает,
    /// если функция перестанет читать настройку или выберет строку не того профиля.
    /// </summary>
    [SkippableFact]
    public void TheActivePolicyFollowsTheSettingsAndTheProfileOfTheStand()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        const string shape = "max_attempts || ' ' || backoff_base_ms || '/' || backoff_max_ms || '/' || jitter_max_ms || ' ' || lease_ms";
        var expected = IntegrationEnvironment.Psql(
            "SELECT " + shape + " FROM (SELECT " +
            "coalesce(nullif(current_setting('course.outbox_max_attempts', true), '')::int, p.max_attempts) AS max_attempts, " +
            "coalesce(nullif(current_setting('course.outbox_backoff_base_ms', true), '')::int, p.backoff_base_ms) AS backoff_base_ms, " +
            "coalesce(nullif(current_setting('course.outbox_backoff_max_ms', true), '')::int, p.backoff_max_ms) AS backoff_max_ms, " +
            "coalesce(nullif(current_setting('course.outbox_jitter_max_ms', true), '')::int, p.jitter_max_ms) AS jitter_max_ms, " +
            "coalesce(nullif(current_setting('course.outbox_lease_ms', true), '')::int, p.lease_ms) AS lease_ms " +
            "FROM delivery.delivery_policy p WHERE p.profile = CASE WHEN (SELECT test_profile FROM workflow.runtime_setting) " +
            "THEN 'test' ELSE 'default' END) AS s;");
        Assert.Equal(0, expected.ExitCode);
        Assert.Matches(@"^\d+ \d+/\d+/\d+ \d+$", expected.Output.Trim());

        var active = IntegrationEnvironment.Psql("SELECT " + shape + " FROM delivery.active_policy();");
        Assert.Equal(0, active.ExitCode);
        Assert.Equal(expected.Output.Trim(), active.Output.Trim());

        // На стенде проверки профиль закрытый, и числа — опубликованные: четыре попытки, экспонента
        // 200/400/800 мс, jitter до 100 мс, аренда доставки 2 с.
        var profile = IntegrationEnvironment.Psql("SELECT test_profile FROM workflow.runtime_setting;");
        Skip.If(profile.Output.Trim() != "t", "the published numbers are asserted on the test profile only");
        Assert.Equal("4 200/800/100 2000", active.Output.Trim());
    }
}
