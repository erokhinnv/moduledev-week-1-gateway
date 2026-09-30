using Course.Shared;
using Npgsql;

namespace Course.Workflow.Worker;

/// <summary>
/// Готовность исполнителя: PostgreSQL — его единственная критическая зависимость.
///
/// Недоступность provider или накопление Outbox на готовность НЕ влияют: это штатный режим
/// контура, и переводить из-за него исполнителя в «не готов» означало бы просить оркестратор
/// снять с него нагрузку в момент, когда работа как раз и накапливается.
///
/// Probe идёт отдельным коротким соединением под ТОЙ ЖЕ минимально привилегированной ролью, что
/// и рабочий цикл, поэтому наблюдаемая роль сессии у сервиса остаётся ровно одна. Результат
/// кешируется на короткое окно: частый health-check не должен превращаться в поток соединений.
/// </summary>
public sealed class WorkerReadiness : IAsyncDisposable
{
    private static readonly TimeSpan CacheWindow = TimeSpan.FromMilliseconds(250);

    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _last;
    private long _lastProbeTicks;

    public WorkerReadiness()
        => _dataSource = CourseDatabase.CreateDataSource(
            connectTimeoutSeconds: 2,
            commandTimeoutSeconds: 3,
            maxPoolSize: 1);

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        var elapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastProbeTicks));
        if (elapsed < CacheWindow)
        {
            return _last;
        }

        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken))
        {
            return _last;
        }

        try
        {
            await using var command = _dataSource.CreateCommand("SELECT 1");
            command.CommandTimeout = 2;
            _last = await command.ExecuteScalarAsync(cancellationToken) is int;
        }
        catch (Exception)
        {
            _last = false;
        }
        finally
        {
            Interlocked.Exchange(ref _lastProbeTicks, DateTime.UtcNow.Ticks);
            _gate.Release();
        }

        return _last;
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await _dataSource.DisposeAsync();
    }
}
