using System.Runtime.InteropServices;
using Course.Shared;
using Course.Workflow.Worker;

// Точка входа сменного исполнителя процессов. Ни одного host-порта: worker разговаривает только
// с PostgreSQL, и только через четыре функции-границы под минимально привилегированной ролью.

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
    await new WorkflowWorker(options).RunAsync(lifetime.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception exception)
{
    // Наружу уходит только тип сбоя: сообщения PostgreSQL умеют содержать значения данных.
    Console.Error.WriteLine($"worker terminated failure={Redaction.Describe(exception)}");
    return 1;
}
