using System.Threading.Channels;

namespace Course.Api;

/// <summary>
/// Очередь технических следов вызовов, снимающая запись следа с пути ответа клиенту.
///
/// Раньше runtime дожидался записи следа ПОСЛЕ коммита и только потом отдавал ответ. Коммит уже
/// состоялся, предметный эффект уже зафиксирован — а клиент всё ещё ждал вспомогательную запись,
/// то есть расходовал на неё свой бюджет времени. При действии с большим timeout_ms это
/// увеличивало и без того реальный риск получить таймаут на шлюзе поверх успешной операции.
///
/// Теперь след кладётся в ограниченную очередь, а пишет его фоновая служба. Потерять след при
/// этом нельзя: если очередь переполнена, запись выполняется синхронно, а при остановке
/// приложения писатель закрывается и очередь дочитывается до конца.
/// </summary>
public sealed class DispatchQueue(DispatchRecorder recorder, ILogger<DispatchQueue> logger)
    : BackgroundService
{
    private readonly Channel<DispatchEvidence> _channel = Channel.CreateBounded<DispatchEvidence>(
        new BoundedChannelOptions(Shared.Env.OptionalInt("COURSE_DISPATCH_QUEUE_CAPACITY", 2048))
        {
            // Читатель ровно один — фоновая служба ниже.
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    /// <summary>
    /// Ставит след в очередь. Возврат практически мгновенный: TryWrite не ждёт ничего, кроме
    /// свободного места. Ожидание возможно только на запасном пути, когда очередь переполнена —
    /// тогда честнее заплатить задержкой, чем молча потерять доказательство вызова.
    /// </summary>
    public async ValueTask EnqueueAsync(DispatchEvidence evidence)
    {
        if (_channel.Writer.TryWrite(evidence))
        {
            return;
        }

        logger.LogWarning(
            "dispatch queue is full, writing evidence inline correlationId={CorrelationId}",
            evidence.CorrelationId);
        await recorder.RecordAsync(evidence);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // stoppingToken намеренно НЕ передаётся в ReadAllAsync: при остановке приложения очередь
        // обязана дочитаться до конца, а не оборваться на середине. Признаком завершения служит
        // закрытие писателя в StopAsync ниже.
        await foreach (var evidence in _channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            // RecordAsync сам подавляет и логирует свои сбои: запись следа не должна ронять
            // фоновую службу, иначе один неудачный след остановил бы запись всех последующих.
            await recorder.RecordAsync(evidence);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Закрываем приём новых следов и ждём, пока фоновый цикл разберёт остаток очереди.
        _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }
}
