using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Course.IntegrationTests;

/// <summary>
/// Граница ответа на единственной публичной точке входа.
///
/// Ограничение результата в api — это обещание api, а не гарантия. Шлюз держит собственную
/// границу, и проверяется она на НАСТОЯЩЕМ ответе api, пропущенном через шлюз с урезанным
/// пределом, а не на подставном потоке: подставной поток проверял бы наши представления о том,
/// как api кодирует тело (на деле — chunked, без Content-Length), а не саму границу.
/// </summary>
public sealed class GatewayIntegrationTests
{
    private const long TinyLimitBytes = 1024;

    [SkippableFact]
    public async Task AResponseOverTheGatewayLimitBecomesAContractEnvelopeNotATruncatedBody()
    {
        Skip.If(!IntegrationEnvironment.Enabled, IntegrationEnvironment.SkipReason);

        // Контроль: штатный шлюз отдаёт документ целиком. Заодно это предусловие — документ
        // обязан быть заведомо больше урезанного предела, иначе тест прошёл бы, ничего не проверив.
        using var client = IntegrationEnvironment.CreateClient();
        using var normal = await client.GetAsync("/openapi/default.json");
        Assert.Equal(HttpStatusCode.OK, normal.StatusCode);

        var document = await normal.Content.ReadAsByteArrayAsync();
        Assert.True(
            document.Length > TinyLimitBytes,
            $"документ OpenAPI ({document.Length} байт) не превышает урезанный предел — тест ничего бы не доказал");

        var (container, port) = IntegrationEnvironment.StartGatewayWithResponseLimit(TinyLimitBytes);
        try
        {
            using var limitedClient = new HttpClient
            {
                BaseAddress = new Uri($"http://localhost:{port}"),
                Timeout = TimeSpan.FromSeconds(30),
            };

            Assert.True(await WaitUntilLiveAsync(limitedClient), "шлюз с урезанным пределом не поднялся");

            using var limited = await limitedClient.GetAsync("/openapi/default.json");
            var text = await limited.Content.ReadAsStringAsync();

            // Главное: клиент получает НЕ усечённый ответ со статусом 200, который выглядел бы полным,
            // а контрактный отказ — тем же кодом, что и остальная матрица ошибок.
            Assert.Equal(HttpStatusCode.InternalServerError, limited.StatusCode);

            var envelope = JsonNode.Parse(text);
            Assert.Equal("error", envelope?["status"]?.GetValue<string>());
            Assert.Equal("action.contract_violation", envelope?["code"]?.GetValue<string>());
            Assert.False(envelope?["retryable"]?.GetValue<bool>() ?? true);

            // И в отказе нет ни байта исходного документа.
            Assert.True(
                text.Length < document.Length,
                "отказ оказался не короче исходного документа — граница не сработала");
            Assert.DoesNotContain("\"openapi\"", text, StringComparison.Ordinal);
        }
        finally
        {
            IntegrationEnvironment.KillContainer(container);
        }
    }

    /// <summary>Опрос готовности, а не пауза: время старта контейнера зависит от машины.</summary>
    private static async Task<bool> WaitUntilLiveAsync(HttpClient client)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var live = await client.GetAsync("/health/live");
                if (live.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
                // Контейнер ещё не принимает соединения — это ожидаемо в первые секунды.
            }

            await Task.Delay(250);
        }

        return false;
    }
}
