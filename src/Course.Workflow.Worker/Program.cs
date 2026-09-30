using System.Runtime.InteropServices;
using Course.Shared;
using Course.Workflow.Worker;

// Точка входа сменного исполнителя процессов. Наружу он по-прежнему не публикует ни одного
// host-порта: служебный адрес наблюдаемости доступен только внутри сети Compose, а предметно
// исполнитель разговаривает только с PostgreSQL и только через функции-границы под минимально
// привилегированной ролью.

using var lifetime = new CancellationTokenSource();

// Docker останавливает контейнер сигналом SIGTERM: перехват нужен, чтобы штатная остановка
// закрывала соединение и не выглядела как аварийное завершение.
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    lifetime.Cancel();
});

using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = true;
    lifetime.Cancel();
});

try
{
    var options = WorkerOptions.FromEnvironment();
    var metrics = new WorkerMetrics();
    await using var readiness = new WorkerReadiness();
    var worker = new WorkflowWorker(options, metrics);

    var endpoint = new ObservabilityEndpoint(
        "workflow-worker",
        options.ObservabilityPort,
        readiness.IsReadyAsync,
        metrics.Render);

    // Служебный адрес живёт рядом с циклом, а не вместо него: отказ probe-сокета не имеет права
    // остановить обработку очереди, а остановка процесса обязана закрыть оба.
    var observability = endpoint.RunAsync(lifetime.Token);
    await worker.RunAsync(lifetime.Token);
    await observability;
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception exception)
{
    // Наружу уходит только тип сбоя: сообщения PostgreSQL умеют содержать значения данных.
    StructuredLog.Error("workflow-worker", "worker.terminated", ("failure", Redaction.Describe(exception)));
    return 1;
}
