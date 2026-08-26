using System.Text.Json.Nodes;
using Course.Shared;
using Npgsql;

namespace Course.Api;

/// <summary>Результат проверки готовности: готов ли сервис, причина (для диагностики) и необязательные детали.</summary>
public sealed record ReadinessState(bool Ready, string Reason, JsonNode? Details);

/// <summary>
/// Проверка готовности (readiness) доказывает готовность ЗАВИСИМОСТИ (PostgreSQL), а не просто
/// то, что процесс api жив. Она реально открывает соединение и читает инициализированный каталог
/// действий — то есть проверяет не только "база отвечает", но и "миграции уже накатаны, каталог
/// не пуст". После восстановления PostgreSQL readiness снова становится зелёной сама по себе,
/// без пересоздания сервиса api.
/// </summary>
public sealed class ReadinessProbe(
    NpgsqlDataSource dataSource,
    DependencyState dependencyState,
    ILogger<ReadinessProbe> logger)
{
    // Короткое окно кеширования результата защищает базу данных от "шторма" проверок: если
    // health-check дёргают очень часто (например средствами оркестрации), не каждый такой запрос
    // будет открывать новое соединение к PostgreSQL.
    private static readonly TimeSpan CacheWindow = TimeSpan.FromMilliseconds(250);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ReadinessState _last = new(false, "not probed yet", null);
    private long _lastProbeTicks;

    public async Task<ReadinessState> CheckAsync(CancellationToken cancellationToken)
    {
        if (dependencyState.IsKnownUnavailable)
        {
            // Недоступность уже доказана недавно другим запросом — отвечаем сразу и повторим
            // попытку только когда короткое окно "известной недоступности" истечёт.
            return new ReadinessState(false, "authoritative state is unavailable", null);
        }

        var elapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastProbeTicks));
        if (elapsed < CacheWindow)
        {
            return _last;
        }

        // Если проверка уже выполняется другим потоком/запросом прямо сейчас — не дублируем её,
        // а просто возвращаем последний известный результат в течение короткого времени ожидания.
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken))
        {
            return _last;
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(1200));

            await using var command = dataSource.CreateCommand("SELECT api.readiness()::text");
            command.CommandTimeout = 2;
            var value = await command.ExecuteScalarAsync(deadline.Token) as string;

            var details = value is null ? null : JsonNode.Parse(value);
            var contractVersion = details?["contractVersion"]?.GetValue<string>();

            // Готовность подтверждена только если функция api.readiness() смогла прочитать строку
            // из autocheck.contract_info с ожидаемой версией контракта — а значит, схема автопроверки
            // уже создана и инициализирована миграциями.
            _last = contractVersion == ContractCodes.ContractVersion
                ? new ReadinessState(true, "ready", details)
                : new ReadinessState(false, "action catalog is not initialised", details);

            dependencyState.MarkAvailable();
        }
        catch (Exception exception)
        {
            logger.LogWarning("readiness probe failed reason={Reason}", exception.GetType().Name);
            if (PostgresFailure.Classify(exception) == ContractCodes.DependencyUnavailable)
            {
                dependencyState.MarkUnavailable();
            }

            _last = new ReadinessState(false, "authoritative state is unavailable", null);
        }
        finally
        {
            Interlocked.Exchange(ref _lastProbeTicks, DateTime.UtcNow.Ticks);
            _gate.Release();
        }

        return _last;
    }
}
