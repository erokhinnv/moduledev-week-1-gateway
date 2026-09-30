using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Course.Shared;

namespace Course.Workflow.Worker;

/// <summary>
/// Три служебных адреса исполнителя: <c>/health/live</c>, <c>/health/ready</c>, <c>/metrics</c>.
///
/// Почему это сокет, а не веб-фреймворк. Исполнитель не обслуживает внешние запросы и не должен
/// уметь их обслуживать: у него нет ни маршрутов действий, ни границы доверия, ни причины тянуть
/// в образ HTTP-стек приложения. Здесь нужны ровно три ответа фиксированной формы на GET, и
/// поэтому обработчик читает строку запроса, отвечает и закрывает соединение.
///
/// Порт доступен только внутри сети Compose: host-порт не публикует ни один сервис, кроме
/// gateway.
/// </summary>
public sealed class ObservabilityEndpoint(
    string service,
    int port,
    Func<CancellationToken, Task<bool>> readiness,
    Func<string> metrics)
{
    private const int MaxRequestBytes = 8 * 1024;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);

    private static readonly byte[] LiveBody = Encoding.UTF8.GetBytes("{\"status\":\"live\"}");
    private static readonly byte[] ReadyBody = Encoding.UTF8.GetBytes("{\"status\":\"ready\"}");

    private static readonly byte[] NotReadyBody = Encoding.UTF8.GetBytes(
        "{\"status\":\"not_ready\",\"code\":\"" + ContractCodes.DependencyUnavailable + "\"}");

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
        }
        catch (SocketException exception)
        {
            // Занятый служебный порт — ошибка конфигурации контура, а не причина уронить
            // исполнителя: очередь он обслуживать по-прежнему способен.
            StructuredLog.Warning(
                service,
                "observability.bind_failed",
                ("port", port),
                ("reason", exception.SocketErrorCode.ToString()));
            return;
        }

        StructuredLog.Info(service, "observability.started", ("port", port));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                // Probe не имеет права влиять на темп опроса очереди, поэтому соединение
                // обслуживается отдельной задачей и никогда не ждёт цикла исполнителя.
                _ = ServeAsync(client, cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
            StructuredLog.Info(service, "observability.stopped", ("port", port));
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var connection = client;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ConnectionTimeout);

        var buffer = ArrayPool<byte>.Shared.Rent(MaxRequestBytes);
        try
        {
            var stream = connection.GetStream();
            var target = await ReadRequestTargetAsync(stream, buffer, deadline.Token);
            if (target is null)
            {
                await WriteAsync(stream, 400, "application/json; charset=utf-8", "{}", deadline.Token);
                return;
            }

            switch (target)
            {
                case "/health/live":
                    // Живость намеренно не проверяет PostgreSQL: процесс, способный ответить,
                    // не нужно перезапускать из-за недоступной зависимости.
                    await WriteAsync(stream, 200, "application/json; charset=utf-8", LiveBody, deadline.Token);
                    break;

                case "/health/ready":
                    var ready = await readiness(deadline.Token);
                    await WriteAsync(
                        stream,
                        ready ? 200 : 503,
                        "application/json; charset=utf-8",
                        ready ? ReadyBody : NotReadyBody,
                        deadline.Token);
                    break;

                case "/metrics":
                    await WriteAsync(stream, 200, OpenMetrics.MediaType, metrics(), deadline.Token);
                    break;

                default:
                    await WriteAsync(stream, 404, "application/json; charset=utf-8", "{}", deadline.Token);
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException)
        {
            // Оборвавшийся probe не является событием платформы.
        }
        catch (Exception exception)
        {
            StructuredLog.Warning(
                service,
                "observability.request_failed",
                ("failure", Redaction.Describe(exception)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Читает запрос до конца заголовков и возвращает путь GET-запроса. Тело не читается: ни один
    /// служебный адрес его не принимает, а «прочитать, чтобы выбросить» здесь нечего.
    /// </summary>
    private static async Task<string?> ReadRequestTargetAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal) || text.Contains("\n\n", StringComparison.Ordinal))
            {
                return ParseTarget(text);
            }
        }

        return length == 0 ? null : ParseTarget(Encoding.ASCII.GetString(buffer, 0, length));
    }

    private static string? ParseTarget(string request)
    {
        var lineEnd = request.IndexOf('\n');
        var line = (lineEnd < 0 ? request : request[..lineEnd]).TrimEnd('\r');
        var parts = line.Split(' ');
        if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.Ordinal))
        {
            return null;
        }

        var target = parts[1];
        var query = target.IndexOf('?');
        return query < 0 ? target : target[..query];
    }

    private static Task WriteAsync(
        NetworkStream stream,
        int status,
        string contentType,
        string body,
        CancellationToken cancellationToken)
        => WriteAsync(stream, status, contentType, Encoding.UTF8.GetBytes(body), cancellationToken);

    private static async Task WriteAsync(
        NetworkStream stream,
        int status,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            503 => "Service Unavailable",
            _ => "OK",
        };

        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
