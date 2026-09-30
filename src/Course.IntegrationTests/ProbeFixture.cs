using Xunit;

namespace Course.IntegrationTests;

/// <summary>
/// Однократная подготовка пробного действия itest.probe: миграция с двумя целевыми функциями и
/// два манифеста. Публикация идемпотентна, поэтому повторный прогон тестов на том же контуре
/// безопасен.
/// </summary>
public sealed class ProbeFixture
{
    public const string CollectionName = "itest-probe";

    private readonly Lock _gate = new();
    private bool _published;

    public void EnsurePublished()
    {
        if (!IntegrationEnvironment.Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_published)
            {
                return;
            }

            var fixtures = IntegrationEnvironment.FixturesDirectory;

            var (migrationExit, migration) = IntegrationEnvironment.Cli(
                fixtures, "migration", "apply", "/itest/migrations");
            Assert.True(
                migrationExit == 0,
                $"probe migration could not be applied: {migration?.ToJsonString()}");

            foreach (var version in new[] { 1, 2 })
            {
                var (publishExit, publish) = IntegrationEnvironment.Cli(
                    fixtures, "action", "publish", $"/itest/manifests/itest-probe-v{version}.action.json");
                Assert.True(
                    publishExit == 0,
                    $"probe manifest v{version} could not be published: {publish?.ToJsonString()}");
            }

            // Медленное действие и его карта: тот же каталог фикстур, та же идемпотентная публикация.
            foreach (var command in new[]
            {
                new[] { "action", "publish", "/itest/manifests/itest-slow-v1.action.json" },
                new[] { "flow", "publish", "/itest/maps/itest-slow-v1.flow.json" },
                new[] { "flow", "activate", "itest-slow", "--version", "1" },
            })
            {
                var (exitCode, envelope) = IntegrationEnvironment.Cli(fixtures, command);
                Assert.True(
                    exitCode == 0,
                    $"slow fixture step '{string.Join(' ', command)}' failed: {envelope?.ToJsonString()}");
            }

            _published = true;
        }
    }
}

/// <summary>
/// Все интеграционные тесты делят одну фикстуру и идут последовательно: они переключают версию по
/// умолчанию одного и того же маршрута, поэтому параллельный запуск сделал бы их взаимно
/// недетерминированными.
/// </summary>
[CollectionDefinition(ProbeFixture.CollectionName, DisableParallelization = true)]
public sealed class ProbeCollection : ICollectionFixture<ProbeFixture>;
