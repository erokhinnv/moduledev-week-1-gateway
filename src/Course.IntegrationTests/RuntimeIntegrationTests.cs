using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Course.IntegrationTests;

/// <summary>
/// Сценарии исполнения, которые невозможно доказать без живого контура: гонка версий между
/// проверкой и исполнением, и повтор по ключу идемпотентности через смену версии по умолчанию.
/// Оба сценария были найдены ревью статически и до появления этих тестов ничем не покрывались.
/// </summary>
[Collection(ProbeFixture.CollectionName)]
public sealed class RuntimeIntegrationTests(ProbeFixture fixture)
{
    private const string Path = "/api/itest/probe";

    [SkippableFact]
    public async Task ExecutedVersionAlwaysMatchesTheValidatedVersion()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);
        fixture.EnsurePublished();

        using var client = IntegrationEnvironment.CreateClient();
        var token = IntegrationEnvironment.Token("workflow-worker", "internal", "workflow:execute");

        // Пока идёт поток запросов без заголовка версии, версия по умолчанию непрерывно
        // переключается между 1 и 2. Ровно в это окно и попадала прежняя гонка: манифест
        // разрешался один раз в C#, а PostgreSQL разрешала default заново уже при исполнении.
        using var switching = new CancellationTokenSource();
        var flipper = Task.Run(() =>
        {
            var version = 2;
            while (!switching.IsCancellationRequested)
            {
                IntegrationEnvironment.Cli(
                    null, "action", "activate", "itest.probe", "--version", version.ToString());
                version = version == 1 ? 2 : 1;
            }
        });

        var responses = new List<(HttpStatusCode Status, JsonNode? Body)>();
        for (var attempt = 0; attempt < 24; attempt++)
        {
            responses.Add(await SendAsync(client, token, $"race-{Guid.NewGuid():N}", version: null));
        }

        await switching.CancelAsync();
        await flipper;

        foreach (var (status, body) in responses)
        {
            // Допустимы только два исхода: успех — либо чистый отказ по неизвестной версии, если
            // версию успели выключить. Чего быть НЕ должно — успеха с рассогласованием.
            Assert.True(
                status is HttpStatusCode.OK or HttpStatusCode.NotFound,
                $"unexpected status {status}: {body}");

            if (status != HttpStatusCode.OK)
            {
                continue;
            }

            var reportedVersion = body!["meta"]!["actionVersion"]!.GetValue<int>();
            var executedRevision = body["result"]!["revision"]!.GetValue<int>();

            // Главный инвариант: объявленная версия и версия, реально произведшая результат,
            // обязаны совпадать. Схема ответа каждой версии требует свой const revision, поэтому
            // расхождение здесь означало бы, что payload проверялся одной версией, а исполняла
            // его другая.
            Assert.Equal(reportedVersion, executedRevision);
        }
    }

    [SkippableFact]
    public async Task ReplayKeepsTheVersionThatProducedTheResult()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);
        fixture.EnsurePublished();

        using var client = IntegrationEnvironment.CreateClient();
        var token = IntegrationEnvironment.Token("workflow-worker", "internal", "workflow:execute");
        var key = $"replay-{Guid.NewGuid():N}";

        IntegrationEnvironment.Cli(null, "action", "activate", "itest.probe", "--version", "1");

        var (firstStatus, first) = await SendAsync(client, token, key, version: null);
        Assert.Equal(HttpStatusCode.OK, firstStatus);
        Assert.Equal(1, first!["result"]!["revision"]!.GetValue<int>());
        Assert.Equal(1, first["meta"]!["actionVersion"]!.GetValue<int>());

        // Версия по умолчанию меняется уже ПОСЛЕ того, как результат зафиксирован.
        IntegrationEnvironment.Cli(null, "action", "activate", "itest.probe", "--version", "2");

        // Контрольная проверка, без которой тест мог бы пройти вхолостую: убеждаемся, что
        // переключение действительно состоялось и НОВЫЙ ключ теперь исполняет версию 2.
        var (switchedStatus, switched) = await SendAsync(
            client, token, $"after-switch-{Guid.NewGuid():N}", version: null);
        Assert.Equal(HttpStatusCode.OK, switchedStatus);
        Assert.Equal(2, switched!["result"]!["revision"]!.GetValue<int>());

        var (replayStatus, replay) = await SendAsync(client, token, key, version: null);

        // Повтор обязан вернуть исходный результат и исходную версию. Раньше сюда приходила
        // версия 2 при результате версии 1 — а C# мерил такой результат схемой версии 2 (где
        // revision обязан быть 2) и отвечал ложным 500 action.contract_violation.
        Assert.Equal(HttpStatusCode.OK, replayStatus);
        Assert.Equal(1, replay!["result"]!["revision"]!.GetValue<int>());
        Assert.Equal(1, replay["meta"]!["actionVersion"]!.GetValue<int>());
        Assert.Equal(
            first["result"]!.ToJsonString(),
            replay["result"]!.ToJsonString());
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(
        HttpClient client,
        string token,
        string key,
        int? version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new StringContent(
                $$"""{"marker":"{{key}}"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        if (version is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Action-Version", version.Value.ToString());
        }

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
    }
}
