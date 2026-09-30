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
        Assert.Equal("test 4 [200, 400, 800] 3000", policy.Output.Trim());

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
    }

    /// <summary>Действующая политика — та, что объявлена профилем стенда, а не строка «по умолчанию».</summary>
    [SkippableFact]
    public void TheActivePolicyFollowsTheProfileOfTheStand()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        var profile = IntegrationEnvironment.Psql("SELECT test_profile FROM workflow.runtime_setting;");
        Assert.Equal(0, profile.ExitCode);
        var expected = profile.Output.Trim() == "t" ? "4 [200, 400, 800]" : "5 [1000, 5000, 15000, 60000]";

        var active = IntegrationEnvironment.Psql(
            "SELECT max_attempts || ' ' || delays_ms::text FROM delivery.active_policy();");
        Assert.Equal(0, active.ExitCode);
        Assert.Equal(expected, active.Output.Trim());
    }
}
